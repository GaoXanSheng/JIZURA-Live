// 帧监控（渲染调试面板数据源）：rAF 帧间隔 + longtask + Vue 组件渲染耗时 + 堆/DOM 规模。
// 面板打开时 start，关闭时 stop，采集器随之全停，零常驻开销。
// Vue 组件耗时依赖 app.config.performance（dev 构建才产生 User Timing measure）。

export interface FrameSnapshot {
    fps: number;
    gapAvg: number;
    gapP95: number;
    gapMax: number;
    dropped: number;
    longTasks: number;
    longTaskWorstMs: number;
    heapMB: number | null;
    nodes: number;
    vueUpdates: number;
    vueUpdateAvgMs: number;
    vueUpdateWorstMs: number;
    fpsHistory: number[];
}

const GAPS_MAX = 240;          // 统计窗口：最近 240 帧（≈4s @60fps）
const FPS_HISTORY = 60;        // sparkline：最近 60 个 1s 采样点

let running = false;
let raf = 0, last = 0, framesThisSec = 0, secTimer = 0;
let dropped = 0, longTasks = 0, ltWorst = 0;
const gaps: number[] = [];
const fpsHistory: number[] = [];
let ltObs: PerformanceObserver | null = null;
let measureObs: PerformanceObserver | null = null;
let vueWin = {count: 0, total: 0, worst: 0};   // 进行中的 1s 窗口
let vueLast = {count: 0, avg: 0, worst: 0};    // 秒采样时落盘的上一窗口

function loop(now: number) {
    raf = requestAnimationFrame(loop);
    if (last > 0) {
        const gap = now - last;
        gaps.push(gap);
        if (gaps.length > GAPS_MAX) gaps.shift();
        framesThisSec++;
        if (gap > 32) dropped++;
    }
    last = now;
}

function sampleSecond() {
    fpsHistory.push(framesThisSec);
    if (fpsHistory.length > FPS_HISTORY) fpsHistory.shift();
    framesThisSec = 0;
    vueLast = {count: vueWin.count, avg: vueWin.count ? vueWin.total / vueWin.count : 0, worst: vueWin.worst};
    vueWin = {count: 0, total: 0, worst: 0};
}

export function startFrames() {
    if (running) return;
    running = true;
    last = 0;
    framesThisSec = 0;
    dropped = 0;
    longTasks = 0;
    ltWorst = 0;
    gaps.length = 0;
    fpsHistory.length = 0;
    vueWin = {count: 0, total: 0, worst: 0};
    raf = requestAnimationFrame(loop);
    secTimer = window.setInterval(sampleSecond, 1000);
    try {
        ltObs = new PerformanceObserver(list => {
            for (const e of list.getEntries()) {
                longTasks++;
                if (e.duration > ltWorst) ltWorst = e.duration;
            }
        });
        ltObs.observe({entryTypes: ['longtask']});
    } catch { // 浏览器不支持
    }
    try {
        // Vue 组件渲染耗时：app.config.performance 开启时产生 "Vue:<组件> patch/mount" measure
        measureObs = new PerformanceObserver(list => {
            for (const e of list.getEntries()) {
                if (!e.name.startsWith('Vue:')) continue;
                vueWin.count++;
                vueWin.total += e.duration;
                if (e.duration > vueWin.worst) vueWin.worst = e.duration;
            }
        });
        measureObs.observe({type: 'measure', buffered: false} as any);
    } catch { // 浏览器不支持
    }
}

export function stopFrames() {
    if (!running) return;
    running = false;
    cancelAnimationFrame(raf);
    clearInterval(secTimer);
    ltObs?.disconnect();
    ltObs = null;
    measureObs?.disconnect();
    measureObs = null;
}

export function isFramesRunning() {
    return running;
}

// 帧间隔窗口统计：avg/p95/max 与推导 fps，纯函数便于单测
export function gapStats(gaps: number[]) {
    const n = gaps.length;
    if (!n) return {avg: 0, max: 0, p95: 0, fps: 0};
    const sorted = [...gaps].sort((a, b) => a - b);
    const avg = sorted.reduce((s, g) => s + g, 0) / n;
    return {
        avg,
        max: sorted[n - 1],
        p95: sorted[Math.min(n - 1, Math.max(0, Math.ceil(n * 0.95) - 1))],
        fps: 1000 / avg,
    };
}

export function framesSnapshot(): FrameSnapshot {
    const {avg, max, p95, fps} = gapStats(gaps);
    const mem = (performance as any).memory;
    return {
        fps,
        gapAvg: avg,
        gapP95: p95,
        gapMax: max,
        dropped,
        longTasks,
        longTaskWorstMs: ltWorst,
        heapMB: mem ? Math.round(mem.usedJSHeapSize / 1048576) : null,
        nodes: document.getElementsByTagName('*').length,
        vueUpdates: vueLast.count,
        vueUpdateAvgMs: vueLast.avg,
        vueUpdateWorstMs: vueLast.worst,
        fpsHistory: [...fpsHistory],
    };
}
