// 驱动一次 JS 黄金 run：node tools/parity/run-js.cjs <case名> [--noframes]
// 起服务（若未起）→ 无头 Edge 加载 harness → 等 obj/parity/<case>.js.json 出现
const { execFileSync, spawn } = require('child_process');
const fs = require('fs');
const path = require('path');
const net = require('net');

const caseName = process.argv[2] || 'smoke';
const noFrames = process.argv.includes('--noframes');
const ROOT = path.resolve(__dirname, '..', '..');
const sys = process.argv.includes('--famsys');
const w400 = process.argv.includes('--w400');
const OUT = path.join(ROOT, 'obj', 'parity', caseName + (sys ? '.sys' : '') + (w400 ? '.w4' : '') + '.js.json');
const EDGE = 'C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe';
const sleep = ms => new Promise(r => setTimeout(r, ms));

(async () => {
  if (fs.existsSync(OUT)) fs.unlinkSync(OUT);

  // 服务探活，没起就拉一个
  const up = await new Promise(res => {
    const s = net.connect(8765, '127.0.0.1', () => { s.end(); res(true); });
    s.on('error', () => res(false));
  });
  if (!up) {
    const srv = spawn('node', [path.join(__dirname, 'serve.cjs')], { detached: true, stdio: 'ignore' });
    srv.unref();
    await sleep(800);
  }

  const args = [
    '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check',
    '--virtual-time-budget=30000', '--timeout=45000',
    '--dump-dom',
    `http://127.0.0.1:8765/harness.html?case=${caseName}${noFrames ? '&frames=0' : ''}${process.argv.includes('--famsys') ? '&famsys=1' : ''}${w400 ? '&w400=1' : ''}`
  ];
  let dom = '';
  try {
    dom = execFileSync(EDGE, args, { maxBuffer: 1 << 28, timeout: 120000 }).toString();
  } catch (e) {
    console.error('edge failed:', e.message);
  }

  const status = (dom.match(/<title>([^<]*)<\/title>/) || [])[1] || '?';
  console.log('[harness status]', status);
  for (let i = 0; i < 40 && !fs.existsSync(OUT); i++) await sleep(500);

  if (fs.existsSync(OUT)) {
    console.log('[ok]', OUT, (fs.statSync(OUT).size / 1e6).toFixed(1) + 'MB');
  } else {
    console.error('[fail] 未产出', OUT);
    const m = dom.match(/ERROR[\s\S]{0,600}/);
    if (m) console.error(m[0]);
    process.exit(1);
  }
})();
