// Web server tĩnh cho bản Flutter web, chạy trong Tool Manager.
// Dùng: node serve-web.js <thư mục web> <port>   (mặc định run\web do deploy.cmd tạo, port 5050)
// Không cần cài package nào, chỉ dùng module có sẵn của Node.
const http = require('http');
const fs = require('fs');
const path = require('path');

const root = path.resolve(process.argv[2] || path.join(__dirname, 'run', 'web'));
const port = Number(process.argv[3] || 5050);

const types = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.wasm': 'application/wasm',
  '.png': 'image/png',
  '.jpg': 'image/jpeg',
  '.jpeg': 'image/jpeg',
  '.gif': 'image/gif',
  '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon',
  '.ttf': 'font/ttf',
  '.otf': 'font/otf',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2',
};

if (!fs.existsSync(path.join(root, 'index.html'))) {
  console.error(`error: không thấy ${path.join(root, 'index.html')}. Chạy "flutter build web --release" trước.`);
  process.exit(1);
}

function send(req, res, file, st) {
  // Flutter không đổi tên main.dart.js sau mỗi lần build, nên trình duyệt phải hỏi lại mỗi lần.
  // File không đổi thì trả 304 (không tải lại), build mới thì có hiệu lực ngay.
  const lastModified = new Date(Math.floor(st.mtimeMs / 1000) * 1000);
  const since = Date.parse(req.headers['if-modified-since'] || '');
  const headers = {
    'Content-Type': types[path.extname(file).toLowerCase()] || 'application/octet-stream',
    'Cache-Control': 'no-cache',
    'Last-Modified': lastModified.toUTCString(),
  };
  if (!Number.isNaN(since) && lastModified.getTime() <= since) {
    res.writeHead(304, headers).end();
    return;
  }
  res.writeHead(200, headers);
  fs.createReadStream(file)
    .on('error', () => res.destroy())
    .pipe(res);
}

function sendFile(req, res, file) {
  fs.stat(file, (err, st) => {
    if (err || !st.isFile()) {
      // Đang deploy (robocopy đang chép) có thể thiếu file trong chốc lát
      console.log(`warn: 503 ${req.url}`);
      res.writeHead(503).end();
      return;
    }
    send(req, res, file, st);
  });
}

http.createServer((req, res) => {
  let rel;
  try {
    rel = decodeURIComponent(new URL(req.url, 'http://x').pathname);
  } catch {
    res.writeHead(400).end();
    return;
  }
  const file = path.join(root, rel);
  if (file !== root && !file.startsWith(root + path.sep)) {
    res.writeHead(403).end();
    return;
  }
  fs.stat(file, (err, st) => {
    if (!err && st.isFile()) return send(req, res, file, st);
    if (!err && st.isDirectory() && fs.existsSync(path.join(file, 'index.html'))) {
      return sendFile(req, res, path.join(file, 'index.html'));
    }
    // Đường dẫn của app (route Flutter) thì trả index.html, file thiếu thì 404
    if (path.extname(rel)) {
      console.log(`warn: 404 ${req.url}`);
      res.writeHead(404).end();
      return;
    }
    sendFile(req, res, path.join(root, 'index.html'));
  });
}).on('error', (e) => {
  console.error(`error: ${e.message}`);
  process.exit(1);
}).listen(port, '0.0.0.0', () => {
  console.log(`FE web: http://localhost:${port}  (thư mục ${root})`);
});
