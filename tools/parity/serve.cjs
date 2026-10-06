// 对抗验证静态服务：serve 引擎 JS / 字体 / harness 页；POST /save 落盘结果。
// 用法：node tools/parity/serve.cjs  （端口 8765，仅 127.0.0.1）
const http = require('http');
const fs = require('fs');
const path = require('path');

const ROOT = path.resolve(__dirname, '..', '..');
const ENGINE = path.join(ROOT, 'frontend', 'src', 'engine');
const FONTS = path.join(ROOT, 'frontend', 'public', 'fonts');
const CASES = path.join(__dirname, 'cases');
const OUT = path.join(ROOT, 'obj', 'parity');
fs.mkdirSync(OUT, { recursive: true });

const MIME = {
  '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8',
  '.json': 'application/json', '.css': 'text/css', '.woff2': 'font/woff2',
  '.woff': 'font/woff', '.ttf': 'font/ttf', '.otf': 'font/otf', '.png': 'image/png'
};

http.createServer((req, res) => {
  const url = new URL(req.url, 'http://127.0.0.1');
  const p = url.pathname;

  if (p === '/save' && req.method === 'POST') {
    let body = '';
    req.on('data', c => { body += c; if (body.length > 200 * 1024 * 1024) req.destroy(); });
    req.on('end', () => {
      try {
        const name = (url.searchParams.get('name') || 'result').replace(/[^\w.-]/g, '_');
        fs.writeFileSync(path.join(OUT, name + '.json'), body);
        res.writeHead(200); res.end('ok');
        console.log('[save]', name, (body.length / 1e6).toFixed(1) + 'MB');
      } catch (e) { res.writeHead(500); res.end(String(e)); }
    });
    return;
  }

  let file = null;
  if (p === '/' || p === '/harness.html') file = path.join(__dirname, 'harness.html');
  else if (p.startsWith('/engine/')) file = path.join(ENGINE, p.slice('/engine/'.length));
  else if (p.startsWith('/fonts/')) file = path.join(FONTS, decodeURIComponent(p.slice('/fonts/'.length)));
  else if (p.startsWith('/cases/')) file = path.join(CASES, p.slice('/cases/'.length));

  if (!file || !file.startsWith(ROOT) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
    res.writeHead(404); res.end('nf: ' + p); return;
  }
  res.writeHead(200, { 'Content-Type': MIME[path.extname(file)] || 'application/octet-stream' });
  fs.createReadStream(file).pipe(res);
}).listen(8765, '127.0.0.1', () => console.log('parity serve on http://127.0.0.1:8765'));
