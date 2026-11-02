using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ToolManager.DeployWorker;

/// <summary>Denied: tên tool Claude định dùng nhưng bị chặn quyền (vd Write, Bash).</summary>
public sealed record ClaudeResult(bool IsError, string Text, double? CostUsd, int? Turns, string? RawTail, IReadOnlyList<string>? Denied = null);

/// <summary>
/// Gọi Claude Code chế độ không tương tác: claude -p, prompt đưa qua stdin.
/// Chỉ cho đọc / tìm / ghi file trong thư mục làm việc (bản sao repo); không cho chạy lệnh, không cho truy cập web.
/// </summary>
public static class ClaudeRunner
{
    // acceptEdits: tự cho phép ghi / sửa file trong thư mục làm việc (bản sao repo).
    // Chỉ có --allowedTools thì chế độ -p vẫn chặn Write (đã thử với Claude Code thật).
    private const string Args =
        "-p --output-format json --max-turns 40 --permission-mode acceptEdits " +
        "--allowedTools \"Read,Glob,Grep,Write,Edit\" " +
        "--disallowedTools \"Bash,WebFetch,WebSearch,NotebookEdit\"";

    /// <summary>Tìm claude: tham số --claude, rồi claude.exe / claude.cmd trong PATH và chỗ cài mặc định.</summary>
    public static string? Find(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured)) return File.Exists(configured) ? Path.GetFullPath(configured) : null;

        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"));
        foreach (var name in new[] { "claude.exe", "claude.cmd" })
        {
            foreach (var dir in dirs)
            {
                try
                {
                    var p = Path.Combine(dir, name);
                    if (File.Exists(p)) return p;
                }
                catch
                {
                    // PATH có mục sai định dạng thì bỏ qua
                }
            }
        }
        return null;
    }

    public static async Task<ClaudeResult> RunAsync(string claudePath, string workDir, string prompt, TimeSpan timeout, CancellationToken ct)
    {
        bool isScript = claudePath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                        || claudePath.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);
        var psi = isScript
            ? new ProcessStartInfo("cmd.exe", $"/d /s /c \"\"{claudePath}\" {Args}\"")
            : new ProcessStartInfo(claudePath, Args);
        psi.WorkingDirectory = workDir;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;
        psi.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";

        using var p = new Process { StartInfo = psi };
        p.Start();

        var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = p.StandardError.ReadToEndAsync(ct);

        // Ghi prompt dạng UTF-8 (không BOM) rồi đóng stdin
        using (var stdin = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)))
        {
            await stdin.WriteAsync(prompt);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { /* đã thoát */ }
            if (ct.IsCancellationRequested) throw;
            return new ClaudeResult(true, $"Claude chạy quá {timeout.TotalMinutes:0} phút nên đã bị dừng.", null, null, null);
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return Parse(stdout, stderr, p.ExitCode);
    }

    /// <summary>--output-format json in ra 1 object: { type, is_error, result, total_cost_usd, num_turns, ... }.</summary>
    private static ClaudeResult Parse(string stdout, string stderr, int exitCode)
    {
        var tail = Tail(stdout + "\n" + stderr, 2000);
        var json = stdout.Trim();
        int start = json.LastIndexOf("\n{", StringComparison.Ordinal);
        if (start >= 0) json = json[(start + 1)..];

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            var text = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "";
            double? cost = root.TryGetProperty("total_cost_usd", out var c) && c.TryGetDouble(out var cv) ? cv : null;
            int? turns = root.TryGetProperty("num_turns", out var t) && t.TryGetInt32(out var tv) ? tv : null;
            if (root.TryGetProperty("subtype", out var st) && st.GetString() is { } sub && sub != "success")
            {
                isError = true;
                if (text.Length == 0) text = $"Claude dừng với trạng thái {sub} (vd: hết số lượt --max-turns).";
            }
            var denied = new List<string>();
            if (root.TryGetProperty("permission_denials", out var pd) && pd.ValueKind == JsonValueKind.Array)
                foreach (var d in pd.EnumerateArray())
                    if (d.TryGetProperty("tool_name", out var tn) && tn.GetString() is { } name && !denied.Contains(name)) denied.Add(name);
            return new ClaudeResult(isError, text, cost, turns, isError ? tail : null, denied);
        }
        catch (JsonException)
        {
            return new ClaudeResult(true, $"Không đọc được kết quả của Claude (mã thoát {exitCode}).", null, null, tail);
        }
    }

    public static string Tail(string s, int max) => s.Length <= max ? s.Trim() : "..." + s[^max..].Trim();
}
