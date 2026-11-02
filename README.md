# DeployWorker

Worker cho tính năng **Tạo deploy từ repo** của Tool Manager: nhận yêu cầu trong bảng `dbo.ToolManagerDeployRequest` (do nút "Tạo từ repo" ghi vào) và gọi Claude Code tạo `deploy.cmd` / `rollback.cmd` cho repo.

Project riêng, không tham chiếu tới code Tool Manager. Những chỗ phải **giữ khớp với Tool Manager** khi sửa một bên:
- Tên bảng `dbo.ToolManagerDeployRequest` và các cột (script: `ToolManeger\ToolManager\docs\deploy-request.sql`).
- Cách đọc `ports.json`: mục `WebDb.ProtectedConnection`, mã hóa Windows DPAPI (CurrentUser) với entropy `ToolManager.WebDb` (`Db.cs` ở đây, `WebRegistryService.cs` bên Tool Manager).
- Định dạng `ResultJson` (Tool Manager sẽ đọc ở giai đoạn 3).

## Build
```
dotnet build D:\jobs\IT\pcx\manegerLog\DeployWorker\ToolManager.DeployWorker.csproj -c Release
```
Thư mục kết quả cần có `DeployWorker.exe`, `prompt.md` và `Templates\` (prompt và mẫu sửa được mà không cần build lại).

## Cài vào Tool Manager

Chạy như 1 tool trong Tool Manager trên **mỗi máy** cần tạo deploy, bằng **đúng tài khoản Windows đã cấu hình database** (để giải mã `ports.json`).

| Ô trong form tool | Giá trị |
|---|---|
| File chạy | `D:\jobs\IT\pcx\manegerLog\DeployWorker\bin\Release\net8.0-windows\DeployWorker.exe` (sau khi build) |
| Tham số | `--ports "<thư mục ToolManager.exe>\ports.json"` |
| Ẩn cửa sổ cmd | Bật (log `info:` / `warn:` / `error:` hiện trong Tool Manager) |

Tham số khác: `--claude <đường dẫn claude.exe/claude.cmd>` (mặc định tìm trong PATH), `--interval <giây>` (5), `--timeout <phút>` (15), `--once` (xử lý 1 yêu cầu rồi thoát), `--connection "<chuỗi kết nối>"` (thay cho `ports.json`, **chỉ nhận đăng nhập Windows**, có mật khẩu thì bị từ chối).

Cần trên máy: Claude Code đã cài và đăng nhập bằng tài khoản Windows chạy worker (`claude -p "xin chao"` phải trả lời được).

Worker xử lý 1 yêu cầu:
1. Nhận yêu cầu `Pending` cũ nhất của máy mình (khóa dòng, không nhận trùng), chuyển sang `Running`. Lúc khởi động, yêu cầu còn `Running` từ lần chạy trước được đánh dấu `Failed`.
2. Chép repo ra thư mục tạm `%TEMP%\ToolManagerDeploy\` (bản sao): bỏ `.git`, thư mục build / thư viện (`target`, `build`, `bin`, `obj`, `node_modules`, `run`...), file > 1 MB, và **ẩn file có thể chứa mật khẩu** (`application*.yml/properties`, `appsettings*.json`, `launchSettings.json`, `.env`, `*.pfx/*.key/*.pem/*.jks`, `.npmrc`, `settings.xml`...). Claude chỉ được biết tên file đó và port / URL đọc được (vd `server.port = ${SERVER_PORT:9999}`). File source / cấu hình còn lại: giá trị giống mật khẩu / key viết cứng (`Password=...;`, `apiKey = "..."`, `"clientSecret": "..."`) được thay bằng `***` trong bản sao (repo thật không đổi). Đây là lọc theo mẫu, không bắt được mọi kiểu: **không nên để mật khẩu trong source**.
3. Gọi `claude -p --permission-mode acceptEdits` trong bản sao với prompt [`prompt.md`](prompt.md) + script mẫu `Templates\` (BE Spring Boot, FE Flutter web). Chỉ cho Read / Glob / Grep / Write / Edit; chặn Bash, WebFetch, WebSearch. Tool bị chặn quyền (`permission_denials`) được ghi vào cảnh báo / lỗi.
4. Kiểm tra: chỉ nhận file **mới** đuôi `.cmd .bat .js .ps1 .py`, tối đa 6 file, không ghi đè file có sẵn, bắt buộc có `deploy.cmd` + `rollback.cmd`. **Chặn** script có `git push/reset/clean/commit`, tải file (curl, wget, Invoke-WebRequest...), sửa registry / scheduled task / user, lệnh mã hóa; lệnh xóa hàng loạt, taskkill... chỉ cảnh báo.
5. Chép file hợp lệ về repo (CRLF, bỏ BOM), thêm vào `.git\info\exclude` (chỉ máy này, không sửa `.gitignore`), xóa bản sao.
6. Ghi `Done` + `ResultJson` (file đã tạo, cấu hình tool gợi ý: exe đã tìm đường dẫn trên máy, tham số, thư mục, port; cảnh báo; file đã ẩn; chi phí) hoặc `Failed` + `ErrorLog`.

Repo đã có `deploy.cmd` ở thư mục gốc: Claude chỉ đọc và trả cấu hình tool gợi ý, không tạo file.

**Luôn đọc `deploy.cmd` / `rollback.cmd` Claude tạo trước khi chạy.**

## Cấu trúc code
- `Program.cs`: tham số, vòng lặp nhận yêu cầu, log `info:` / `warn:` / `error:`
- `Db.cs`: đọc chuỗi kết nối (ports.json / --connection), nhận yêu cầu, ghi kết quả
- `Sandbox.cs`: bản sao repo, ẩn file cấu hình mật, che mật khẩu viết cứng
- `ClaudeRunner.cs`: tìm và gọi `claude -p`, đọc kết quả JSON
- `RequestProcessor.cs`: dựng prompt, kiểm tra / chép file, tìm đường dẫn exe, tạo ResultJson
- `prompt.md`: prompt chuẩn (có `prompt-version`); `Templates\`: script mẫu BE / FE