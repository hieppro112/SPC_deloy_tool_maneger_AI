using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ToolManager.DeployWorker;

/// <summary>Kết quả xử lý 1 yêu cầu: Error = null là thành công.</summary>
public sealed record ProcessOutcome(string? Error, string? ResultJson);

/// <summary>
/// Xử lý 1 yêu cầu: tạo bản sao repo -> gọi Claude -> kiểm tra file Claude tạo -> chép file hợp lệ về repo
/// -> ghi kết quả (file đã tạo + cấu hình tool gợi ý) dạng JSON cho Tool Manager.
/// </summary>
public sealed class RequestProcessor
{
    private static readonly HashSet<string> AllowedExt = new(StringComparer.OrdinalIgnoreCase) { ".cmd", ".bat", ".js", ".ps1", ".py" };
    private const int MaxNewFiles = 6;
    private const long MaxNewFileBytes = 200_000;

    // Lệnh không được có trong script sinh ra (chặn hẳn)
    private static readonly (Regex Pattern, string Reason)[] Forbidden =
    {
        (new Regex(@"\bgit\s+(push|reset\s+--hard|clean|rebase|commit|checkout\s+--)\b", RegexOptions.IgnoreCase), "git thay đổi lịch sử / xóa file (chỉ được git pull)"),
        (new Regex(@"\b(curl|wget|bitsadmin|Invoke-WebRequest|iwr|Start-BitsTransfer)\b|certutil[^\r\n]*-urlcache", RegexOptions.IgnoreCase), "tải file từ internet"),
        (new Regex(@"\breg(\.exe)?\s+(add|delete|import)\b|\bschtasks\b|\bnet\s+(user|localgroup)\b|\bsc(\.exe)?\s+(create|delete|config)\b", RegexOptions.IgnoreCase), "đổi cấu hình Windows"),
        (new Regex(@"-enc(odedcommand)?\s|\bInvoke-Expression\b|\biex\b", RegexOptions.IgnoreCase), "chạy lệnh mã hóa / sinh động"),
        (new Regex(@"\bformat\s+[a-z]:|\bdiskpart\b|\bvssadmin\b|\bbcdedit\b|\bshutdown\b", RegexOptions.IgnoreCase), "lệnh hệ thống nguy hiểm"),
    };

    // Lệnh được dùng nhưng cần người duyệt chú ý
    private static readonly (Regex Pattern, string Reason)[] Suspicious =
    {
        (new Regex(@"\b(rd|rmdir)\s+/s|\bdel\s+(/[a-z]\s+)*/s\b|Remove-Item[^\r\n]*-Recurse", RegexOptions.IgnoreCase), "xóa thư mục / xóa hàng loạt"),
        (new Regex(@"\btaskkill\b|Stop-Process", RegexOptions.IgnoreCase), "tắt process (Tool Manager nên là nơi Stop/Start)"),
        (new Regex(@"%(USERPROFILE|APPDATA|LOCALAPPDATA|ProgramFiles|SystemRoot|windir)%|C:\\Windows", RegexOptions.IgnoreCase), "ghi / đọc ngoài thư mục repo"),
        (new Regex(@"\bnpm\s+(install|i)\s+(-g|--global)\b", RegexOptions.IgnoreCase), "cài package toàn máy"),
        (new Regex(@"child_process|\bsubprocess\b|os\.system", RegexOptions.IgnoreCase), "file phụ chạy lệnh khác"),
    };

    private readonly string _baseDir = AppContext.BaseDirectory;
    private readonly string _claudePath;
    private readonly string _workRoot;
    private readonly TimeSpan _timeout;

    public RequestProcessor(string claudePath, string workRoot, TimeSpan timeout)
    {
        _claudePath = claudePath;
        _workRoot = workRoot;
        _timeout = timeout;
    }

    public async Task<ProcessOutcome> ProcessAsync(ClaimedRequest req, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var warnings = new List<string>();
        var folder = req.FolderPath;
        if (!Directory.Exists(folder)) return new(Error: $"Folder không tồn tại trên máy {Environment.MachineName}: {folder}", null);

        bool existingDeploy = File.Exists(Path.Combine(folder, "deploy.cmd"));

        Log.Info($"#{req.Id} tạo bản sao repo...");
        using var box = Sandbox.Create(folder, _workRoot, req.Id, Path.Combine(_baseDir, "Templates"));
        if (box.HiddenFiles.Count > 0) Log.Info($"#{req.Id} ẩn / che mật khẩu trong {box.HiddenFiles.Count} file: {string.Join(", ", box.HiddenFiles)}");

        var (prompt, promptVersion) = BuildPrompt(req, box, existingDeploy);

        Log.Info($"#{req.Id} gọi Claude ({_claudePath})...");
        var claude = await ClaudeRunner.RunAsync(_claudePath, box.Root, prompt, _timeout, ct);
        Log.Info($"#{req.Id} Claude xong sau {sw.Elapsed.TotalSeconds:0}s" + (claude.CostUsd is { } c ? $", chi phí ~${c:0.###}" : ""));
        if (claude.IsError)
            return new(Error: "Claude báo lỗi: " + claude.Text + (claude.RawTail is { } t ? "\n---\n" + t : ""), null);
        if (claude.Denied is { Count: > 0 } denied)
        {
            // Bash / WebFetch bị chặn là đúng thiết kế; Write / Edit bị chặn thì Claude không tạo được file
            var msg = $"Claude bị chặn quyền dùng: {string.Join(", ", denied)}";
            if (denied.Any(d => d is "Write" or "Edit")) warnings.Add(msg + " (không ghi được file, kiểm tra --permission-mode của Claude Code).");
            else warnings.Add(msg + " (đúng thiết kế, worker không cho chạy lệnh / truy cập web).");
        }

        // Câu trả lời phải kết thúc bằng 1 khối ```json
        JsonObject answer;
        try
        {
            answer = ParseAnswer(claude.Text);
        }
        catch (Exception ex)
        {
            return new(Error: $"Không đọc được khối JSON trong câu trả lời của Claude: {ex.Message}\n---\n{ClaudeRunner.Tail(claude.Text, 1500)}", null);
        }

        // ---- Chọn file mới hợp lệ ----
        var (created, modified) = box.Diff();
        foreach (var m in modified) warnings.Add($"Claude đã sửa file có sẵn {m} trong bản sao; không chép về repo.");

        var toCopy = new List<string>();
        if (existingDeploy)
        {
            if (created.Count > 0) warnings.Add($"Repo đã có deploy.cmd nên bỏ qua file Claude tạo: {string.Join(", ", created)}");
        }
        else
        {
            foreach (var rel in created)
            {
                var ext = Path.GetExtension(rel);
                if (!AllowedExt.Contains(ext)) { warnings.Add($"Bỏ qua {rel}: chỉ nhận file {string.Join(" ", AllowedExt)}."); continue; }
                if (new FileInfo(Path.Combine(box.Root, rel)).Length > MaxNewFileBytes) { warnings.Add($"Bỏ qua {rel}: file quá lớn."); continue; }
                if (File.Exists(Path.Combine(folder, rel))) { warnings.Add($"Bỏ qua {rel}: repo đã có file này, không ghi đè."); continue; }
                toCopy.Add(rel);
            }
            if (toCopy.Count > MaxNewFiles)
                return new(Error: $"Claude tạo {toCopy.Count} file, nhiều hơn giới hạn {MaxNewFiles}: {string.Join(", ", toCopy)}", null);

            foreach (var required in new[] { "deploy.cmd", "rollback.cmd" })
                if (!toCopy.Contains(required, StringComparer.OrdinalIgnoreCase))
                    return new(Error: $"Claude không tạo {required} ở thư mục gốc repo."
                                      + (claude.Denied is { Count: > 0 } d ? $" Claude bị chặn quyền: {string.Join(", ", d)}." : "")
                                      + $"\n---\n{ClaudeRunner.Tail(claude.Text, 1500)}", null);

            // ---- Kiểm tra nội dung script ----
            var blocked = new List<string>();
            foreach (var rel in toCopy)
            {
                var text = box.ReadText(rel);
                foreach (var (pattern, reason) in Forbidden)
                    if (pattern.Match(text) is { Success: true } m) blocked.Add($"{rel}: \"{m.Value.Trim()}\" ({reason})");
                foreach (var (pattern, reason) in Suspicious)
                    if (pattern.Match(text) is { Success: true } m) warnings.Add($"{rel}: có \"{m.Value.Trim()}\" ({reason}), hãy đọc kỹ trước khi chạy.");
                if (IsBatch(rel) && text.Any(ch => ch > 127)) warnings.Add($"{rel}: có ký tự có dấu / ngoài ASCII, cmd có thể hiện sai chữ.");
            }
            if (blocked.Count > 0)
                return new(Error: "Script sinh ra có lệnh bị chặn, không chép về repo:\n" + string.Join("\n", blocked), null);

            // ---- Chép về repo thật ----
            foreach (var rel in toCopy)
            {
                var dest = Path.Combine(folder, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                var text = box.ReadText(rel).TrimStart('\uFEFF'); // cmd đọc sai dòng đầu nếu có BOM
                if (IsBatch(rel)) text = Regex.Replace(text, "\r?\n", "\r\n");
                await File.WriteAllTextAsync(dest, text, new UTF8Encoding(false), ct);
            }
            ExcludeFromGit(folder, toCopy, warnings);
            Log.Info($"#{req.Id} đã tạo: {string.Join(", ", toCopy)}");
        }

        // ---- Cấu hình tool gợi ý ----
        var tool = answer["tool"] as JsonObject ?? new JsonObject();
        var workDir = ResolveWorkDir(folder, Str(tool["workingDirectory"]));
        var exeRaw = Str(tool["exe"]) ?? "";
        var exeResolved = ResolveExe(exeRaw, workDir);

        var result = new JsonObject
        {
            ["promptVersion"] = promptVersion,
            ["projectType"] = answer["projectType"]?.DeepClone(),
            ["summary"] = answer["summary"]?.DeepClone(),
            ["existingDeploy"] = existingDeploy,
            ["files"] = new JsonArray(toCopy.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
            ["stopBeforeDeploy"] = answer["stopBeforeDeploy"]?.DeepClone(),
            ["tool"] = new JsonObject
            {
                ["name"] = req.ToolName,
                ["exe"] = exeRaw,
                ["exePath"] = exeResolved ?? exeRaw,
                ["exeFound"] = exeResolved != null && File.Exists(exeResolved),
                ["arguments"] = Str(tool["arguments"]) ?? "",
                ["workingDirectory"] = workDir,
                ["ports"] = new JsonArray(ParsePorts(tool["ports"]).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray()),
                ["hideWindow"] = tool["hideWindow"] is JsonValue hv && hv.TryGetValue<bool>(out var hide) ? hide : true,
            },
            ["prerequisites"] = answer["prerequisites"]?.DeepClone(),
            ["notes"] = answer["notes"]?.DeepClone(),
            ["warnings"] = new JsonArray(warnings.Select(w => (JsonNode?)JsonValue.Create(w)).ToArray()),
            ["hiddenFiles"] = new JsonArray(box.HiddenFiles.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray()),
            ["costUsd"] = claude.CostUsd,
            ["turns"] = claude.Turns,
            ["durationSec"] = (int)sw.Elapsed.TotalSeconds,
            ["worker"] = typeof(RequestProcessor).Assembly.GetName().Version?.ToString(3),
        };
        foreach (var w in warnings) Log.Warn($"#{req.Id} {w}");
        return new(Error: null, result.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    }

    // ================== Prompt ==================

    private (string Prompt, string Version) BuildPrompt(ClaimedRequest req, Sandbox box, bool existingDeploy)
    {
        var template = File.ReadAllText(Path.Combine(_baseDir, "prompt.md"));
        var version = Regex.Match(template, @"prompt-version:\s*([\w.-]+)") is { Success: true } m ? m.Groups[1].Value : "?";

        var hidden = box.HiddenFiles.Count == 0
            ? "(không có)"
            : string.Join("\n", box.HiddenFiles.Select(h => "- " + h));
        var mode = existingDeploy
            ? "Repo ĐÃ CÓ deploy.cmd ở thư mục gốc. KHÔNG tạo hay sửa file nào. Chỉ đọc deploy.cmd hiện có và project để trả về khối JSON cấu hình tool."
            : "Repo chưa có deploy.cmd. Hãy tạo deploy.cmd và rollback.cmd ở thư mục gốc (thêm file phụ nếu thật sự cần, vd serve-web.js).";
        var note = string.IsNullOrWhiteSpace(req.Note) ? "(không có)" : req.Note.Trim();

        var prompt = template
            .Replace("{{MODE}}", mode)
            .Replace("{{TOOL_NAME}}", req.ToolName)
            .Replace("{{FOLDER_NAME}}", Path.GetFileName(req.FolderPath))
            .Replace("{{HIDDEN_FILES}}", hidden)
            .Replace("{{TEMPLATES_DIR}}", Sandbox.TemplatesDirName)
            .Replace("{{USER_NOTE}}", note);
        return (prompt, version);
    }

    /// <summary>Lấy khối ```json cuối cùng trong câu trả lời.</summary>
    private static JsonObject ParseAnswer(string text)
    {
        var blocks = Regex.Matches(text, @"```json\s*(\{.*?\})\s*```", RegexOptions.Singleline);
        if (blocks.Count == 0) throw new FormatException("không có khối ```json");
        return JsonNode.Parse(blocks[^1].Groups[1].Value) as JsonObject ?? throw new FormatException("khối JSON không phải object");
    }

    // ================== Tiện ích ==================

    private static bool IsBatch(string rel) =>
        rel.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".bat", StringComparison.OrdinalIgnoreCase);

    /// <summary>Thêm file vừa tạo + /run/ vào .git\info\exclude (chỉ có tác dụng trên máy này, không sửa .gitignore của repo).</summary>
    private static void ExcludeFromGit(string folder, List<string> files, List<string> warnings)
    {
        var gitDir = Path.Combine(folder, ".git");
        if (!Directory.Exists(gitDir))
        {
            warnings.Add("Folder không phải repo git (hoặc là git worktree): không thêm file vào .git\\info\\exclude.");
            return;
        }
        try
        {
            var exclude = Path.Combine(gitDir, "info", "exclude");
            Directory.CreateDirectory(Path.GetDirectoryName(exclude)!);
            var existing = File.Exists(exclude) ? File.ReadAllLines(exclude).Select(l => l.Trim()).ToHashSet() : new HashSet<string>();
            var lines = files.Select(f => "/" + f.Replace('\\', '/')).Append("/run/").Where(l => !existing.Contains(l)).ToList();
            if (lines.Count == 0) return;
            File.AppendAllLines(exclude, new[] { "# Tool Manager: file deploy tao tren may nay" }.Concat(lines));
        }
        catch (Exception ex)
        {
            warnings.Add($"Không ghi được .git\\info\\exclude: {ex.Message}");
        }
    }

    /// <summary>Thư mục làm việc: tương đối thì tính từ folder repo, không cho ra ngoài repo.</summary>
    private static string ResolveWorkDir(string folder, string? wd)
    {
        if (string.IsNullOrWhiteSpace(wd) || wd is "." or ".\\" or "./") return folder;
        try
        {
            var full = Path.GetFullPath(Path.IsPathRooted(wd) ? wd : Path.Combine(folder, wd));
            var inside = full.Equals(folder, StringComparison.OrdinalIgnoreCase)
                         || full.StartsWith(folder.TrimEnd('\\') + '\\', StringComparison.OrdinalIgnoreCase);
            return inside ? full.TrimEnd('\\') : folder;
        }
        catch
        {
            return folder;
        }
    }

    /// <summary>
    /// "java" / "node" / "dotnet" => đường dẫn đầy đủ trên máy này (java ưu tiên JAVA_HOME).
    /// Đường dẫn tương đối (vd run\app.exe) => tính từ thư mục làm việc (có thể chưa tồn tại tới khi chạy deploy.cmd).
    /// </summary>
    private static string? ResolveExe(string exe, string workDir)
    {
        exe = exe.Trim().Trim('"');
        if (exe.Length == 0) return null;
        if (Path.IsPathRooted(exe)) return exe;
        if (exe.Contains('\\') || exe.Contains('/')) return Path.GetFullPath(Path.Combine(workDir, exe));

        var names = Path.HasExtension(exe)
            ? new[] { exe }
            : (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries).Select(e => exe + e.ToLowerInvariant()).ToArray();

        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        static bool IsShim(string d) => d.Contains(@"\Oracle\Java\javapath", StringComparison.OrdinalIgnoreCase);

        // Ưu tiên JDK thật (JAVA_HOME, rồi PATH), shim javapath của Oracle để cuối cùng
        var dirs = new List<string>();
        if (exe.Equals("java", StringComparison.OrdinalIgnoreCase) && Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } jh)
            dirs.Add(Path.Combine(jh, "bin"));
        dirs.AddRange(path.Where(d => !IsShim(d)));
        dirs.AddRange(path.Where(IsShim));

        foreach (var dir in dirs)
            foreach (var name in names)
            {
                try
                {
                    var p = Path.Combine(dir, name);
                    if (File.Exists(p)) return p;
                }
                catch
                {
                    // PATH có mục sai định dạng
                }
            }
        return null;
    }

    private static string? Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static List<int> ParsePorts(JsonNode? node)
    {
        var list = new List<int>();
        if (node is JsonArray arr)
            foreach (var n in arr)
                if (n is JsonValue v && v.TryGetValue<int>(out var p) && p is > 0 and < 65536 && !list.Contains(p)) list.Add(p);
        return list;
    }
}
