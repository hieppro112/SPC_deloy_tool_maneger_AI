using System.Text;
using System.Text.RegularExpressions;

namespace ToolManager.DeployWorker;

/// <summary>
/// Bản sao của repo trong thư mục tạm để Claude làm việc:
/// - Không có .git, thư mục build, thư viện tải về.
/// - Không có file cấu hình có thể chứa mật khẩu / key (chỉ báo tên file + port đọc được).
/// - Claude không đụng được vào repo thật; worker tự chọn file mới hợp lệ rồi mới chép về.
/// </summary>
public sealed class Sandbox : IDisposable
{
    public const string TemplatesDirName = ".toolmanager-templates";

    // Thư mục không chép (build, thư viện, IDE, kết quả deploy)
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".svn", ".vs", ".vscode", ".idea", ".gradle", ".dart_tool", ".venv", "venv", "__pycache__",
        "node_modules", "packages", "bin", "obj", "build", "target", "dist", "out", "run", "publish",
    };

    // File có thể chứa mật khẩu / key: không chép, không cho Claude đọc
    private static readonly Regex SecretFile = new(
        @"^(\.env(\..*)?|application.*\.(ya?ml|properties)|appsettings.*\.json|launchSettings\.json|web\.config|app\.config|" +
        @"settings\.xml|\.npmrc|\.pypirc|nuget\.config|key\.properties|local\.properties|id_(rsa|ed25519|ecdsa).*|" +
        @".*\.(pfx|p12|key|pem|jks|keystore|publishsettings|pubxml\.user)|" +
        @".*(secret|credential|password).*\.(json|ya?ml|xml|txt|ini|config|properties|env))$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // File văn bản (source, cấu hình) được quét để che giá trị giống mật khẩu / key viết cứng
    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".vb", ".java", ".kt", ".groovy", ".gradle", ".js", ".mjs", ".cjs", ".ts", ".jsx", ".tsx", ".py", ".go", ".rb", ".php",
        ".dart", ".sql", ".xml", ".json", ".yml", ".yaml", ".properties", ".ini", ".cfg", ".conf", ".config", ".toml", ".txt", ".md",
        ".ps1", ".cmd", ".bat", ".sh", ".csproj", ".vbproj", ".resx", ".settings",
    };

    // Kiểu chuỗi kết nối: Password=abc; / Pwd=abc
    private static readonly Regex ConnPassword = new(
        @"\b(password|pwd|passwd)(\s*=\s*)([^;""'\r\n<>]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Kiểu gán / JSON / YAML có ngoặc: dbPassword = "abc", "apiKey": "abc", secret: 'abc'
    private static readonly Regex QuotedSecret = new(
        @"\b(\w*(?:password|passwd|pwd|secret|api[_-]?key|apikey|access[_-]?key|client[_-]?secret|auth[_-]?token|access[_-]?token|private[_-]?key)\w*)(""?'?\s*[:=]\s*)([""'])([^""'\r\n]+)\3",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const long MaxFileBytes = 1_000_000;
    private const int MaxFiles = 5000;
    private const long MaxTotalBytes = 100_000_000;

    private readonly Dictionary<string, (long Length, DateTime Write)> _copied = new(StringComparer.OrdinalIgnoreCase);

    public string SourceRoot { get; }
    public string Root { get; }

    /// <summary>File bị ẩn (đường dẫn tương đối) + thông tin port / URL đọc được từ file đó.</summary>
    public List<string> HiddenFiles { get; } = new();

    private Sandbox(string sourceRoot, string root)
    {
        SourceRoot = sourceRoot;
        Root = root;
    }

    public static Sandbox Create(string sourceRoot, string workRoot, int requestId, string templatesDir)
    {
        var root = Path.Combine(workRoot, $"req-{requestId}-{DateTime.Now:yyyyMMddHHmmss}");
        if (Directory.Exists(root)) Directory.Delete(root, true);
        Directory.CreateDirectory(root);

        var box = new Sandbox(sourceRoot, root);
        long total = 0;
        box.CopyDir(sourceRoot, root, ref total);

        // Script mẫu cho Claude tham khảo (không chép về repo)
        if (Directory.Exists(templatesDir))
        {
            foreach (var file in Directory.EnumerateFiles(templatesDir, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(root, TemplatesDirName, Path.GetRelativePath(templatesDir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest);
            }
        }
        return box;
    }

    private void CopyDir(string src, string dest, ref long total)
    {
        Directory.CreateDirectory(dest);
        foreach (var file in Directory.EnumerateFiles(src))
        {
            var name = Path.GetFileName(file);
            var rel = Path.GetRelativePath(SourceRoot, file);
            if (SecretFile.IsMatch(name))
            {
                HiddenFiles.Add(DescribeHidden(rel, file));
                continue;
            }

            var info = new FileInfo(file);
            if (info.Length > MaxFileBytes) continue; // file lớn (ảnh, thư viện) không cần để tạo script

            total += info.Length;
            if (_copied.Count >= MaxFiles || total > MaxTotalBytes)
                throw new InvalidOperationException("Folder quá lớn (hơn 5000 file hoặc 100 MB sau khi bỏ thư mục build). Hãy chọn đúng folder gốc của repo.");

            var target = Path.Combine(dest, name);
            File.Copy(file, target);
            if (TextExt.Contains(Path.GetExtension(name)) && Redact(target) is var n and > 0)
                HiddenFiles.Add($"{rel} (vẫn có trong bản sao, đã thay {n} giá trị giống mật khẩu / key bằng ***)");
            var copied = new FileInfo(target);
            _copied[Path.GetRelativePath(Root, target)] = (copied.Length, copied.LastWriteTimeUtc);
        }

        foreach (var dir in Directory.EnumerateDirectories(src))
        {
            var name = Path.GetFileName(dir);
            if (SkipDirs.Contains(name)) continue;
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue; // bỏ symlink / junction
            CopyDir(dir, Path.Combine(dest, name), ref total);
        }
    }

    /// <summary>
    /// Thay giá trị giống mật khẩu / key trong bản sao bằng ***. Giữ nguyên biến chỗ trống
    /// (${DB_PASSWORD}, %PWD%, #{x}) vì đó không phải mật khẩu thật. Trả về số giá trị đã thay.
    /// </summary>
    private static int Redact(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch
        {
            return 0;
        }

        int count = 0;
        static bool IsPlaceholder(string v) => v.TrimStart() is var t && (t.StartsWith("${") || t.StartsWith('%') || t.StartsWith("#{") || t.Length == 0 || t == "***");

        var result = ConnPassword.Replace(text, m =>
        {
            if (IsPlaceholder(m.Groups[3].Value)) return m.Value;
            count++;
            return m.Groups[1].Value + m.Groups[2].Value + "***";
        });
        result = QuotedSecret.Replace(result, m =>
        {
            if (IsPlaceholder(m.Groups[4].Value)) return m.Value;
            count++;
            return m.Groups[1].Value + m.Groups[2].Value + m.Groups[3].Value + "***" + m.Groups[3].Value;
        });

        if (count > 0) File.WriteAllText(path, result, new UTF8Encoding(false));
        return count;
    }

    /// <summary>"src\main\resources\application.yml (server.port = ${SERVER_PORT:9999}; ...)" — chỉ lấy port / URL, không lấy giá trị khác.</summary>
    private static string DescribeHidden(string rel, string path)
    {
        var hints = new List<string>();
        try
        {
            var name = Path.GetFileName(path).ToLowerInvariant();
            var text = File.ReadAllText(path);
            if (name.EndsWith(".yml") || name.EndsWith(".yaml")) hints.AddRange(YamlHints(text));
            else if (name.EndsWith(".properties"))
            {
                foreach (Match m in Regex.Matches(text, @"^\s*(server\.port|server\.servlet\.context-path|spring\.profiles\.active)\s*[=:]\s*(.+?)\s*$", RegexOptions.Multiline))
                    hints.Add($"{m.Groups[1].Value} = {m.Groups[2].Value}");
            }
            else if (name.EndsWith(".json"))
            {
                foreach (Match m in Regex.Matches(text, "\"(applicationUrl|Urls|Url)\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase))
                    hints.Add($"{m.Groups[1].Value} = {m.Groups[2].Value}");
            }
            else if (name.StartsWith(".env"))
            {
                foreach (Match m in Regex.Matches(text, @"^\s*(PORT|HOST|ASPNETCORE_URLS|SERVER_PORT)\s*=\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                    hints.Add($"{m.Groups[1].Value} = {m.Groups[2].Value}");
            }
        }
        catch
        {
            // không đọc được thì chỉ báo tên file
        }
        return hints.Count > 0 ? $"{rel} ({string.Join("; ", hints.Distinct())})" : rel;
    }

    /// <summary>Lấy server.port, server.servlet.context-path, spring.profiles.active trong file YAML (đọc theo thụt lề, đủ dùng cho Spring).</summary>
    private static IEnumerable<string> YamlHints(string text)
    {
        var wanted = new HashSet<string> { "server.port", "server.servlet.context-path", "spring.profiles.active" };
        var stack = new List<(int Indent, string Key)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = Regex.Match(line, @"^(\s*)([A-Za-z0-9_.-]+)\s*:\s*(.*?)\s*$");
            if (!m.Success || line.TrimStart().StartsWith('#')) continue;

            int indent = m.Groups[1].Value.Length;
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);
            stack.Add((indent, m.Groups[2].Value));

            var path = string.Join(".", stack.Select(s => s.Key));
            var value = m.Groups[3].Value;
            if (value.Length > 0 && wanted.Contains(path)) yield return $"{path} = {value}";
        }
    }

    /// <summary>File Claude mới tạo (đường dẫn tương đối), trừ thư mục mẫu. Kèm danh sách file có sẵn bị Claude sửa.</summary>
    public (List<string> Created, List<string> Modified) Diff()
    {
        var created = new List<string>();
        var modified = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(Root, file);
            if (rel.StartsWith(TemplatesDirName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            if (!_copied.TryGetValue(rel, out var before))
            {
                created.Add(rel);
                continue;
            }
            var now = new FileInfo(file);
            if (now.Length != before.Length || now.LastWriteTimeUtc != before.Write) modified.Add(rel);
        }
        return (created, modified);
    }

    public string ReadText(string rel) => File.ReadAllText(Path.Combine(Root, rel), Encoding.UTF8);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
        catch
        {
            // file đang bị khóa thì để lại, lần sau dọn
        }
    }
}
