/* 性能探针：定位编辑器卡顿。
   激活：URL ?probe=1 或 Ctrl+Alt+P 切换（状态存 localStorage jizura.probe）。
   数据：
     - 埋点聚合 probeSync(name, fn) / probeAsync(name, fn)：count/avg/max/p95
     - rAF 帧间隔（掉帧监控）：frameGap avg/max、丢帧数（>32ms 记一次）
     - LongTask（>50ms 主线程任务）PerformanceObserver
   读取：window.__probe.dump() 返回全部数据；HUD 组件每 500ms 刷新显示。 */

export interface MarkStat { count: number; total: number; max: number; last: number }

const enabled = (() => {
  try {
    if (new URLSearchParams(window.location.search).get('probe') === '1') {
      localStorage.setItem('jizura.probe', '1');
      return true;
    }
    if (new URLSearchParams(window.location.search).get('probe') === '0') {
      localStorage.removeItem('jizura.probe');
      return false;
    }
    return localStorage.getItem('jizura.probe') === '1';
  } catch { return false; }
})();

export const probeEnabled = enabled;

const marks = new Map<string, MarkStat>();

export function probeRecord(name: string, ms: number) {
  let m = marks.get(name);
  if (!m) { m = { count: 0, total: 0, max: 0, last: 0 }; marks.set(name, m); }
  m.count++; m.total += ms; m.last = ms;
  if (ms > m.max) m.max = ms;
}
const record = probeRecord;

export function probeSync<T>(name: string, fn: () => T): T {
  if (!enabled) return fn();
  const t0 = performance.now();
  try { return fn(); } finally { record(name, performance.now() - t0); }
}

export async function probeAsync<T>(name: string, fn: () => Promise<T>): Promise<T> {
  if (!enabled) return fn();
  const t0 = performance.now();
  try { return await fn(); } finally { record(name, performance.now() - t0); }
}

/* 帧间隔/丢帧/LongTask */
export const frame = { count: 0, gapAvg: 0, gapMax: 0, dropped: 0 };
export const longTasks = { count: 0, totalMs: 0, worstMs: 0 };
let lastFrame = 0;
let gapSum = 0;

function rafLoop(now: number) {
  requestAnimationFrame(rafLoop);
  if (!enabled) { lastFrame = now; return; }
  if (lastFrame > 0) {
    const gap = now - lastFrame;
    frame.count++;
    gapSum += gap;
    frame.gapAvg = gapSum / frame.count;
    if (gap > frame.gapMax) frame.gapMax = gap;
    if (gap > 32) frame.dropped++;
  }
  lastFrame = now;
}
requestAnimationFrame(rafLoop);

if (enabled) {
  try {
    new PerformanceObserver(list => {
      for (const e of list.getEntries()) {
        longTasks.count++;
        longTasks.totalMs += e.duration;
        if (e.duration > longTasks.worstMs) longTasks.worstMs = e.duration;
      }
    }).observe({ entryTypes: ['longtask'] });
  } catch { /* 浏览器不支持 */ }
}

/* dump：全部数据 JSON（控制台 / CDP 读取） */
export function reset() {
  marks.clear();
  frame.count = 0; frame.gapAvg = 0; frame.gapMax = 0; frame.dropped = 0; gapSum = 0;
  longTasks.count = 0; longTasks.totalMs = 0; longTasks.worstMs = 0;
}

export function dump() {
  const mem = (performance as any).memory;
  return {
    enabled,
    frame: { ...frame },
    longTasks: { ...longTasks },
    memoryMB: mem ? Math.round(mem.usedJSHeapSize / 1048576) : null,
    marks: [...marks.entries()].map(([name, m]) => ({
      name,
      count: m.count,
      avg: +(m.total / m.count).toFixed(2),
      max: +m.max.toFixed(2),
      last: +m.last.toFixed(2),
    })).sort((a, b) => b.max - a.max),
  };
}

/* 激活时挂全局与快捷键 */
export function installProbe() {
  if (!enabled) return;
  (window as any).__probe = { dump, reset, marks, frame, longTasks };
  window.addEventListener('keydown', e => {
    if (e.ctrlKey && e.altKey && e.code === 'KeyP') {
      try { localStorage.removeItem('jizura.probe'); } catch {}
      window.location.reload();
    }
  });
}

/* Ctrl+Alt+P 开启探针（任意页面可用，切换需刷新） */
export function installProbeHotkey() {
  window.addEventListener('keydown', e => {
    if (e.ctrlKey && e.altKey && e.code === 'KeyP') {
      try {
        if (localStorage.getItem('jizura.probe') === '1') localStorage.removeItem('jizura.probe');
        else localStorage.setItem('jizura.probe', '1');
      } catch {}
      window.location.reload();
    }
  });
}
