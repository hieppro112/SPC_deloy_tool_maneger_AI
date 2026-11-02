using System.Text;
using ToolManager.DeployWorker;

// Worker nhận yêu cầu "Tạo deploy từ repo" của máy này trong dbo.ToolManagerDeployRequest và gọi Claude Code xử lý.
// Chạy như 1 tool trong Tool Manager (bật "Ẩn cửa sổ cmd" để xem log), bằng đúng tài khoản Windows đã cấu hình database.
//
// Tham số:
//   --ports <file>     ports.json của Tool Manager (mặc định: cạnh DeployWorker.exe, rồi thư mục cha)
//   --connection <cs>  dùng chuỗi kết nối này thay cho ports.json; chỉ nhận đăng nhập Windows (không chứa mật khẩu)
//   --claude <file>    đường dẫn claude.exe / claude.cmd (mặc định: tìm trong PATH)
//   --interval <giây>  chu kỳ kiểm tra yêu cầu mới (mặc định 5)
//   --timeout <phút>   thời gian tối đa cho 1 lần gọi Claude (mặc định 15)
//   --once             xử lý tối đa 1 yêu cầu rồi thoát (dùng để thử)

Console.OutputEncoding = Encoding.UTF8;

var opts = Options.Parse(args);
if (opts == null) return 2;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

string cs;
try
{
    if (opts.Connection == null) Log.Info($"Đọc cấu hình database từ {opts.PortsPath}");
    cs = opts.Connection != null ? Db.CheckWindowsAuth(opts.Connection) : Db.LoadConnectionString(opts.PortsPath);
}
catch (Exception ex)
{
    Log.Error(ex.Message);
    return 1;
}

var claude = ClaudeRunner.Find(opts.ClaudePath);
if (claude == null)
{
    Log.Error("Không tìm thấy Claude Code (claude.exe / claude.cmd). Cài Claude Code, đăng nhập bằng tài khoản Windows này, hoặc dùng --claude <đường dẫn>.");
    return 1;
}

var workRoot = Path.Combine(Path.GetTempPath(), "ToolManagerDeploy");
Directory.CreateDirectory(workRoot);
var processor = new RequestProcessor(claude, workRoot, TimeSpan.FromMinutes(opts.TimeoutMinutes));

Log.Info($"DeployWorker v{typeof(Program).Assembly.GetName().Version?.ToString(3)} trên máy {Environment.MachineName}, tài khoản {Environment.UserName}");
Log.Info($"Database: {Describe(cs)} · Claude: {claude} · kiểm tra mỗi {opts.IntervalSeconds}s");

try
{
    var stuck = await Db.FailStuckAsync(cs, cts.Token);
    if (stuck > 0) Log.Warn($"Đánh dấu Lỗi {stuck} yêu cầu đang xử lý dở từ lần chạy trước.");
}
catch (Exception ex)
{
    Log.Error("Không kết nối được database: " + ex.Message);
    return 1;
}

int dbErrors = 0;
while (!cts.IsCancellationRequested)
{
    ClaimedRequest? req = null;
    try
    {
        req = await Db.ClaimNextAsync(cs, cts.Token);
        dbErrors = 0;
    }
    catch (OperationCanceledException)
    {
        break;
    }
    catch (Exception ex)
    {
        // Mất kết nối tạm thời thì thử lại, chỉ ghi lỗi lần đầu để log không bị tràn
        if (dbErrors++ == 0) Log.Error("Lỗi đọc database, sẽ thử lại: " + ex.Message);
    }

    if (req != null)
    {
        Log.Info($"#{req.Id} nhận yêu cầu: {req.FolderPath} (tool \"{req.ToolName}\")");
        ProcessOutcome outcome;
        try
        {
            outcome = await processor.ProcessAsync(req, cts.Token);
        }
        catch (OperationCanceledException)
        {
            outcome = new ProcessOutcome("Worker bị dừng khi đang xử lý yêu cầu này. Hãy gửi lại yêu cầu.", null);
        }
        catch (Exception ex)
        {
            outcome = new ProcessOutcome("Lỗi worker: " + ex.Message, null);
        }

        try
        {
            if (outcome.Error == null)
            {
                await Db.CompleteAsync(cs, req.Id, outcome.ResultJson!, CancellationToken.None);
                Log.Info($"#{req.Id} xong.");
            }
            else
            {
                await Db.FailAsync(cs, req.Id, outcome.Error, outcome.ResultJson, CancellationToken.None);
                Log.Error($"#{req.Id} lỗi: {outcome.Error}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"#{req.Id} không ghi được kết quả vào database: {ex.Message}");
        }

        if (opts.Once) break;
        continue; // có thể còn yêu cầu khác đang chờ
    }

    if (opts.Once) { Log.Info("Không có yêu cầu nào đang chờ."); break; }
    try
    {
        await Task.Delay(TimeSpan.FromSeconds(opts.IntervalSeconds), cts.Token);
    }
    catch (OperationCanceledException)
    {
        break;
    }
}

Log.Info("DeployWorker dừng.");
return 0;

static string Describe(string cs)
{
    try
    {
        var b = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(cs);
        return $"{b.DataSource}/{b.InitialCatalog}";
    }
    catch
    {
        return "(không đọc được)";
    }
}

sealed record Options(string PortsPath, string? Connection, string? ClaudePath, int IntervalSeconds, int TimeoutMinutes, bool Once)
{
    public static Options? Parse(string[] args)
    {
        string? ports = null, connection = null, claude = null;
        int interval = 5, timeout = 15;
        bool once = false;
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i].ToLowerInvariant())
            {
                case "--ports": ports = Next(); break;
                case "--connection": connection = Next(); break;
                case "--claude": claude = Next(); break;
                case "--interval": if (!int.TryParse(Next(), out interval) || interval < 1) return Bad("--interval phải là số giây >= 1"); break;
                case "--timeout": if (!int.TryParse(Next(), out timeout) || timeout < 1) return Bad("--timeout phải là số phút >= 1"); break;
                case "--once": once = true; break;
                default: return Bad($"Tham số không hợp lệ: {args[i]}");
            }
        }

        if (ports == null)
        {
            var baseDir = AppContext.BaseDirectory;
            ports = new[] { Path.Combine(baseDir, "ports.json"), Path.Combine(baseDir, "..", "ports.json") }
                .Select(Path.GetFullPath)
                .FirstOrDefault(File.Exists) ?? Path.Combine(baseDir, "ports.json");
        }
        return new Options(Path.GetFullPath(ports), connection, claude, interval, timeout, once);
    }

    private static Options? Bad(string message)
    {
        Log.Error(message);
        return null;
    }
}

/// <summary>Log ra console: Tool Manager tô đỏ dòng bắt đầu bằng "error:".</summary>
static class Log
{
    public static void Info(string msg) => Console.WriteLine("info: " + msg);
    public static void Warn(string msg) => Console.WriteLine("warn: " + msg);
    public static void Error(string msg) => Console.WriteLine("error: " + msg);
}
