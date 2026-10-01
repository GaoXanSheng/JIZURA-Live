/* 被动测量镜像页自身帧间隔（不改任何状态）
   用法: node tools/cdp-mirror-frames.mjs <targetId前8位> [毫秒=6000] */
const PREFIX = process.argv[2];
const MS = +(process.argv[3] || 6000);
const list = await (await fetch('http://127.0.0.1:9222/json/list')).json();
const tgt = list.find(t => t.id.startsWith(PREFIX));
if (!tgt) {
    console.error('no target', PREFIX);
    process.exit(1);
}
const ws = new WebSocket(tgt.webSocketDebuggerUrl);
await new Promise((res, rej) => {
    ws.onopen = res;
    ws.onerror = rej;
});
let id = 0;
const pending = new Map();
ws.onmessage = ev => {
    const m = JSON.parse(ev.data);
    if (m.id && pending.has(m.id)) {
        pending.get(m.id)(m);
        pending.delete(m.id);
    }
};
const send = (method, params = {}) => new Promise((res, rej) => {
    const k = ++id;
    pending.set(k, m => m.error ? rej(new Error(JSON.stringify(m.error))) : res(m.result));
    ws.send(JSON.stringify({id: k, method, params}));
});
const expr = `(async () => {
  const frames = []; let last = performance.now(); const t0 = last;
  await new Promise(done => {
    const tick = () => {
      const now = performance.now();
      frames.push(+(now - last).toFixed(1)); last = now;
      if (now - t0 >= ${MS}) return done();
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
  });
  const s = window.J.ui;
  return JSON.stringify({n: frames.length, avg: +(frames.reduce((a,b)=>a+b,0)/frames.length).toFixed(1), max: Math.max(...frames), over30: frames.filter(x=>x>30).length, t: +s.t.toFixed(2), need: !!s.need, memMB: performance.memory ? +(performance.memory.usedJSHeapSize/1048576).toFixed(1) : null});
})()`;
const r = await send('Runtime.evaluate', {expression: expr, returnByValue: true, awaitPromise: true});
console.log(tgt.url.slice(0, 60), '=>', r.result.value);
ws.close();
