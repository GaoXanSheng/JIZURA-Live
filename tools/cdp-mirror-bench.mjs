/* 在指定页面里基准测试 renderer.frame 的单帧耗时（只读，不改引擎状态）
   用法: node tools/cdp-mirror-bench.mjs <targetId前8位> */
const PREFIX = process.argv[2];
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
  const stage = window.J.ui;
  const plan = stage.plan;
  if (!plan) return 'no plan';
  const r = window.__benchRenderer || (window.__benchRenderer = new window.J.Renderer());
  const cv = document.createElement('canvas');
  cv.width = 1920; cv.height = 1080;
  const ctx = cv.getContext('2d');
  const scale = cv.width / plan.W;
  const rows = [];
  for (const t of [10.9, 11.0, 11.1, 11.3, 11.5, 11.9, 12.0, 12.1]) {
    const dts = [];
    for (let k = 0; k < 3; k++) {
      const t0 = performance.now();
      r.frame(ctx, plan, t, { scale, fast: false, transparent: !!window.__jl_spout_transparent });
      dts.push(performance.now() - t0);
    }
    dts.shift();
    rows.push({t, ms: +(dts.reduce((a,b)=>a+b,0)/dts.length).toFixed(1)});
  }
  return JSON.stringify({url: location.pathname.slice(0, 30), rows});
})()`;
const r = await send('Runtime.evaluate', {expression: expr, returnByValue: true, awaitPromise: true});
if (r.exceptionDetails) console.error('PAGE ERR:', r.exceptionDetails.exception?.description?.slice(0, 300));
else console.log(JSON.stringify(r.result.value));
ws.close();
