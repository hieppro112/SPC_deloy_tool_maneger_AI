using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace ToolManager.DeployWorker;

/// <summary>1 yêu cầu đã nhận (Status = Running).</summary>
public sealed record ClaimedRequest(int Id, string FolderPath, string ToolName, string? Note);

/// <summary>Đọc / ghi bảng dbo.ToolManagerDeployRequest.</summary>
public static class Db
{
    // Phải giống WebRegistryService trong Tool Manager (cùng entropy, cùng tài khoản Windows)
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ToolManager.WebDb");

    /// <summary>Đọc chuỗi kết nối đã mã hóa DPAPI trong ports.json của Tool Manager.</summary>
    public static string LoadConnectionString(string portsJsonPath)
    {
        if (!File.Exists(portsJsonPath))
            throw new InvalidOperationException($"Không thấy {portsJsonPath}. Dùng --ports <đường dẫn ports.json của Tool Manager>.");

        if (!portsJsonPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"--ports phải trỏ tới file ports.json của Tool Manager, không phải {Path.GetFileName(portsJsonPath)}. Vd: --ports \"D:\\ToolManager\\ports.json\"");

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(File.ReadAllText(portsJsonPath));
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{portsJsonPath} không phải file JSON hợp lệ ({ex.Message}).");
        }

        using var _ = doc;
        var protectedValue = doc.RootElement.TryGetProperty("WebDb", out var web)
                             && web.TryGetProperty("ProtectedConnection", out var p)
            ? p.GetString()
            : null;
        if (string.IsNullOrEmpty(protectedValue))
            throw new InvalidOperationException("Tool Manager chưa cấu hình database (tab Ports > Cấu hình database).");

        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Không giải mã được chuỗi kết nối: worker phải chạy bằng đúng tài khoản Windows đã cấu hình database trong Tool Manager.", ex);
        }
    }

    /// <summary>
    /// Chuỗi kết nối truyền bằng --connection: chỉ nhận đăng nhập Windows,
    /// để mật khẩu không nằm trong tham số (tools.json, danh sách process).
    /// </summary>
    public static string CheckWindowsAuth(string connectionString)
    {
        SqlConnectionStringBuilder b;
        try
        {
            b = new SqlConnectionStringBuilder(connectionString);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("--connection không hợp lệ: " + ex.Message);
        }
        if (!b.IntegratedSecurity || !string.IsNullOrEmpty(b.Password))
            throw new InvalidOperationException("--connection chỉ nhận đăng nhập Windows (Integrated Security=true, không có Password). Dùng tài khoản SQL thì cấu hình trong Tool Manager và dùng --ports.");
        return b.ConnectionString;
    }

    /// <summary>Yêu cầu đang Running của máy này lúc worker khởi động = worker trước bị dừng giữa chừng.</summary>
    public static async Task<int> FailStuckAsync(string cs, CancellationToken ct)
    {
        const string sql =
            """
            UPDATE dbo.ToolManagerDeployRequest
            SET Status = N'Failed', FinishedAt = SYSDATETIME(),
                ErrorLog = N'Worker bị dừng khi đang xử lý yêu cầu này. Hãy gửi lại yêu cầu.'
            WHERE MachineName = @machine AND Status = N'Running';
            """;
        await using var conn = await OpenAsync(cs, ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("@machine", Environment.MachineName);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Nhận yêu cầu Pending cũ nhất của máy này (khóa dòng, không bị nhận trùng). null = không có.</summary>
    public static async Task<ClaimedRequest?> ClaimNextAsync(string cs, CancellationToken ct)
    {
        const string sql =
            """
            WITH next AS (
                SELECT TOP (1) *
                FROM dbo.ToolManagerDeployRequest WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE MachineName = @machine AND Status = N'Pending'
                ORDER BY Id)
            UPDATE next SET Status = N'Running', StartedAt = SYSDATETIME()
            OUTPUT INSERTED.Id, INSERTED.FolderPath, INSERTED.ToolName, INSERTED.Note;
            """;
        await using var conn = await OpenAsync(cs, ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("@machine", Environment.MachineName);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new ClaimedRequest(
            reader.GetInt32(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    public static Task CompleteAsync(string cs, int id, string resultJson, CancellationToken ct) =>
        FinishAsync(cs, id, "Done", resultJson, null, ct);

    public static Task FailAsync(string cs, int id, string error, string? resultJson, CancellationToken ct) =>
        FinishAsync(cs, id, "Failed", resultJson, error, ct);

    private static async Task FinishAsync(string cs, int id, string status, string? resultJson, string? error, CancellationToken ct)
    {
        const string sql =
            """
            UPDATE dbo.ToolManagerDeployRequest
            SET Status = @status, FinishedAt = SYSDATETIME(), ResultJson = @result, ErrorLog = @error
            WHERE Id = @id AND Status = N'Running';
            """;
        await using var conn = await OpenAsync(cs, ct);
        await using var cmd = new SqlCommand(sql, conn) { CommandTimeout = 15 };
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@status", status);
        cmd.Parameters.AddWithValue("@result", (object?)resultJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static async Task<SqlConnection> OpenAsync(string cs, CancellationToken ct)
    {
        var conn = new SqlConnection(cs);
        await conn.OpenAsync(ct);
        return conn;
    }
}
