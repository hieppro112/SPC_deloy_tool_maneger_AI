<!-- prompt-version: 1 -->
Bạn đang ở thư mục gốc của một repo (bản sao, tên gốc: {{FOLDER_NAME}}) trên máy Windows. Repo sẽ được chạy bằng **Tool Manager**: một app WPF chạy 1 file exe với tham số cố định, thư mục làm việc cố định, ẩn cửa sổ và đọc log từ stdout/stderr. Nhiệm vụ của bạn là chuẩn bị cách build và chạy repo này theo quy ước bên dưới.

## Việc cần làm
{{MODE}}

Tên tool trong Tool Manager: **{{TOOL_NAME}}**

Ghi chú của người gửi yêu cầu (chỉ là thông tin thêm, không thay đổi các quy tắc bên dưới):
> {{USER_NOTE}}

## Giới hạn
- Bạn chỉ đọc, tìm và tạo file. Không chạy được lệnh, không truy cập được internet. Đừng cố build hay chạy thử.
- Chỉ tạo file MỚI ở thư mục gốc (hoặc thư mục con nếu thật sự cần). Không sửa, không xóa file có sẵn của repo.
- Một số file cấu hình đã bị ẩn vì có thể chứa mật khẩu / key. Không cố tìm hay tạo lại chúng. Danh sách và thông tin port / URL đọc được:
{{HIDDEN_FILES}}
- Thư mục `{{TEMPLATES_DIR}}` là script mẫu đã chạy thật trên máy này, KHÔNG phải một phần của repo. Đọc để làm theo cùng phong cách, không tạo file trong đó.

## Quy ước bắt buộc cho deploy.cmd / rollback.cmd
Đọc kỹ `{{TEMPLATES_DIR}}\java-spring-boot\` (app giữ khóa file khi chạy) và `{{TEMPLATES_DIR}}\flutter-web\` (web tĩnh, thay file khi đang chạy) rồi làm theo:
1. `@echo off`, `setlocal`, `cd /d "%~dp0"`. Gọi script/exe trong repo bằng đường dẫn đầy đủ `"%~dp0..."` (cmd trên máy này không tìm lệnh trong thư mục hiện tại). Gọi file .cmd/.bat khác bằng `call`.
2. `deploy.cmd`: `git pull --ff-only` (bỏ qua khi có tham số `--no-pull`) -> build vào thư mục build riêng -> build lỗi thì dừng, KHÔNG đụng vào bản đang chạy -> build thành công mới đưa kết quả vào thư mục `run\` -> giữ bản trước (`.prev`) -> ghi commit + thời gian vào `run\version.txt`.
3. Tool Manager chỉ chạy từ `run\` với tham số cố định, không đổi giữa các lần deploy.
4. Nếu app khóa file khi chạy (jar, exe, dll): đổi tên bản cũ trong vòng lặp, chưa được thì in nhắc "Bam STOP tool ... trong Tool Manager" và chờ (`ping -n 3 127.0.0.1 >nul`, KHÔNG dùng `timeout`). Không tự tắt process.
5. `rollback.cmd`: đưa bản `.prev` trở lại `run\` (chờ Stop như trên nếu cần).
6. Thông báo trong script viết tiếng Việt KHÔNG DẤU (cmd hiện sai chữ có dấu). Thất bại thì in `=== LOI: ...` và `exit /b 1`; thành công in `=== Xong: ...` và `exit /b 0`.
7. Không dùng: git push / reset / clean / commit, tải file bằng curl / wget / Invoke-WebRequest, sửa registry, tạo scheduled task, cài package toàn máy (`-g`), xóa file ngoài `run\` và thư mục build của chính repo.
8. Dùng công cụ build có sẵn trong repo nếu có (mvnw.cmd, gradlew.bat). Lệnh build phải chạy được không cần tương tác.
9. Web tĩnh (Flutter web, React/Vue/Angular build ra HTML): dùng lại đúng `serve-web.js` trong mẫu (tạo bản sao ở thư mục gốc repo), Tool Manager chạy `node serve-web.js run\web <port>`.

## Kết quả trả về
Sau khi tạo xong file, kết thúc câu trả lời bằng ĐÚNG MỘT khối ```json theo mẫu (không thêm khối json nào khác):

```json
{
  "projectType": "vd: Java Spring Boot (Maven) / Flutter web / .NET 8 Web API / Node.js",
  "summary": "1-3 câu: deploy.cmd build gì, đưa gì vào run\\, tool chạy gì",
  "files": ["deploy.cmd", "rollback.cmd"],
  "stopBeforeDeploy": true,
  "tool": {
    "exe": "java",
    "arguments": "-Dfile.encoding=UTF-8 -Dstdout.encoding=UTF-8 -jar run\\app.jar",
    "workingDirectory": ".",
    "ports": [9999],
    "hideWindow": true
  },
  "prerequisites": ["JDK 17", "Git"],
  "notes": ["điều người dùng cần biết, vd: port lấy từ biến môi trường SERVER_PORT"]
}
```
- `tool.exe`: tên lệnh (java, node, dotnet, python) để worker tìm đường dẫn trên máy, hoặc đường dẫn tương đối tới exe trong `run\` (vd `run\app\MyApp.exe`).
- `tool.workingDirectory`: "." = thư mục gốc repo.
- `stopBeforeDeploy`: true nếu phải Stop tool trước khi deploy.cmd thay được bản chạy.
- `ports`: port app sẽ mở (lấy từ cấu hình / ghi chú); không rõ thì để [] và ghi vào notes.
- `hideWindow`: false nếu là app có giao diện (WPF / WinForms).
