/* CDP 性能采样：播放 N 秒并聚合 self-time 热点
   用法: node tools/cdp-profile.mjs [毫秒=4000] */
const MS = +(process.argv[2] || 4000);
const list = await (await fetch('http://127.0.0.1:9222/json/list')).json();
const tgt = list.find(t => t.type === 'page' && !t.url.includes('/render'));
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
    const i = ++id;
    pending.set(i, m => m.error ? rej(new Error(JSON.stringify(m.error))) : res(m.result));
    ws.send(JSON.stringify({id: i, method, params}));
});
await send('Profiler.enable');
await send('Profiler.setSamplingInterval', {interval: 200});
await send('Profiler.start');
await new Promise(r => setTimeout(r, MS));
const {profile} = await send('Profiler.stop');
const nodes = new Map(profile.nodes.map(n => [n.id, n]));
const self = new Map();
const total = profile.samples.length;
for (const sid of profile.samples) {
    const n = nodes.get(sid);
    if (!n) continue;
    const cf = n.callFrame;
    const key = (cf.functionName || '(anon)') + ' @' + (cf.url || '').split('/').pop() + ':' + (cf.lineNumber + 1);
    self.set(key, (self.get(key) || 0) + 1);
}
const top = [...self.entries()].sort((a, b) => b[1] - a[1]).slice(0, 18)
    .map(([k, v]) => ({pct: +(v / total * 100).toFixed(1), fn: k}));
console.log(JSON.stringify(top, null, 1));
ws.close();
