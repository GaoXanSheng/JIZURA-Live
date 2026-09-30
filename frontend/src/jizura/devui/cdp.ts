/* CDP（Chrome DevTools Protocol）直连：精确模式的数据源。
   前提：设置 → 勾选「调试端口（CDP）」并重启应用（RemoteDebuggingPort，仅回环）。
   页面直接 WebSocket 连本机 devtools target；/json/list 优先页内 fetch，
   被 CORS 拦截时回退 C# 代理 /api/perf/cdp-targets。
   能力：Overlay.setShowPaintRects（合成器原生 paint 闪烁）、
        Performance.getMetrics（样式/布局/脚本每秒差值）、Tracing（管线突发录制）。 */

import {parseTimelineEvents, type TraceReport} from './traceparse';

export type CdpStatus = 'off' | 'connecting' | 'connected' | 'unavailable';

export interface CdpMetrics {
    layoutPerSec: number;      // 布局次数/秒
    recalcPerSec: number;      // 样式重算次数/秒
    layoutMsPerSec: number;
    recalcMsPerSec: number;
    scriptMsPerSec: number;
    taskMsPerSec: number;
    nodes: number;
    heapMB: number;
}

export const cdpState = {
    status: 'off' as CdpStatus,
    error: '',
    port: 9222,
    metricsOn: false,
    paintFlashOn: false,
    metrics: null as CdpMetrics | null,
};

let ws: WebSocket | null = null;
let msgId = 0;
let want = false;
const pending = new Map<number, { resolve: (v: any) => void; reject: (e: any) => void }>();
let traceBuf: any[] | null = null;
let traceDone: (() => void) | null = null;
let metricsTimer = 0;
let prevMetrics: Map<string, number> | null = null;
let prevAt = 0;

function send(method: string, params?: any): Promise<any> {
    return new Promise((resolve, reject) => {
        if (!ws || ws.readyState !== WebSocket.OPEN) {
            reject(new Error('CDP 未连接'));
            return;
        }
        const id = ++msgId;
        pending.set(id, {resolve, reject});
        ws.send(JSON.stringify({id, method, params: params || {}}));
    });
}

async function discoverWsUrl(): Promise<string> {
    const port = cdpState.port;
    const here = location.origin + location.pathname;
    let list: any = null;
    try {
        list = await fetch(`http://127.0.0.1:${port}/json/list`).then(r => r.json());
    } catch {
        list = await fetch(`/api/perf/cdp-targets?port=${port}`).then(r => {
            if (!r.ok) throw new Error('CDP 端口不可达');
            return r.json();
        });
    }
    const pages = (Array.isArray(list) ? list : []).filter((t: any) => t.type === 'page' && t.webSocketDebuggerUrl);
    const self = pages.find((t: any) => t.url === here)
        || pages.find((t: any) => t.url.startsWith(location.origin))
        || pages[0];
    if (!self) throw new Error('未找到可连接的页面目标');
    return self.webSocketDebuggerUrl as string;
}

function openWs(url: string): Promise<void> {
    return new Promise((resolve, reject) => {
        let sock: WebSocket;
        try {
            sock = new WebSocket(url);
        } catch (e) {
            reject(e);
            return;
        }
        const timer = setTimeout(() => {
            try {
                sock.close();
            } catch {
            }
            reject(new Error('连接超时'));
        }, 4000);
        sock.onopen = () => {
            clearTimeout(timer);
            ws = sock;
            resolve();
        };
        sock.onerror = () => {
            clearTimeout(timer);
            reject(new Error('WebSocket 连接失败'));
        };
        sock.onclose = () => {
            clearTimeout(timer);
            if (ws === sock) {
                ws = null;
                onClosed();
            }
        };
        sock.onmessage = ev => {
            let m: any;
            try {
                m = JSON.parse(ev.data);
            } catch {
                return;
            }
            if (m.id) {
                const p = pending.get(m.id);
                if (p) {
                    pending.delete(m.id);
                    m.error ? p.reject(new Error(m.error.message || 'CDP 调用失败')) : p.resolve(m.result);
                }
            } else if (m.method === 'Tracing.dataCollected' && traceBuf) {
                traceBuf.push(...(m.params?.value || []));
            } else if (m.method === 'Tracing.tracingComplete') {
                traceDone?.();
            }
        };
    });
}

function onClosed() {
    stopMetrics();
    if (want && cdpState.status === 'connected') {
        cdpState.status = 'unavailable';
        cdpState.error = '连接断开，可点「连接」重试';
    }
}

export async function cdpConnect() {
    if (ws || cdpState.status === 'connecting') return;
    cdpState.status = 'connecting';
    cdpState.error = '';
    await loadCdpPort();
    try {
        const url = await discoverWsUrl();
        await openWs(url);
        cdpState.status = 'connected';
        startMetrics();
        if (cdpState.paintFlashOn) applyPaintFlash(true);
    } catch {
        cdpState.status = 'unavailable';
        cdpState.error = `CDP 不可达：设置里勾选「调试端口（CDP）」（端口 ${cdpState.port}）并重启应用后可连接`;
    }
}

export function cdpDisconnect() {
    want = false;
    stopMetrics();
    try {
        ws?.close();
    } catch { /* already closed */
    }
    ws = null;
    cdpState.status = 'off';
    cdpState.error = '';
    cdpState.metrics = null;
    if (cdpState.paintFlashOn) cdpState.paintFlashOn = false;
}

export function cdpSetWant(on: boolean) {
    want = on;
    if (on) cdpConnect();
    else cdpDisconnect();
}

/* ---- 原生 paint 闪烁（合成器绘制，零 JS 开销） ---- */
export async function setPaintFlashing(on: boolean) {
    cdpState.paintFlashOn = on;
    if (!ws || cdpState.status !== 'connected') return;
    try {
        await send('Overlay.enable');
        await send('Overlay.setShowPaintRects', {result: on});
    } catch {
        cdpState.error = 'paint 闪烁开启失败（该页面会话可能不支持 Overlay 域）';
    }
}

async function applyPaintFlash(on: boolean) {
    await setPaintFlashing(on);
}

/* ---- Performance.getMetrics 每秒差值 ---- */
export function startMetrics() {
    cdpState.metricsOn = true;
    if (cdpState.status !== 'connected' || metricsTimer) return;
    prevMetrics = null;
    prevAt = 0;
    send('Performance.enable').catch(() => {
        cdpState.error = 'Performance 域不可用';
    });
    metricsTimer = window.setInterval(pollMetrics, 1000);
}

export function stopMetrics() {
    cdpState.metricsOn = false;
    clearInterval(metricsTimer);
    metricsTimer = 0;
    cdpState.metrics = null;
}

async function pollMetrics() {
    try {
        const r = await send('Performance.getMetrics');
        const now = performance.now();
        const map = new Map<string, number>((r.metrics || []).map((m: any) => [m.name, m.value]));
        if (prevMetrics && prevAt > 0) {
            const dt = Math.max(0.25, (now - prevAt) / 1000);
            const d = (name: string) => Math.max(0, ((map.get(name) || 0) - (prevMetrics!.get(name) || 0)) / dt);
            cdpState.metrics = {
                layoutPerSec: d('LayoutCount'),
                recalcPerSec: d('RecalcStyleCount'),
                layoutMsPerSec: d('LayoutDuration') * 1000,
                recalcMsPerSec: d('RecalcStyleDuration') * 1000,
                scriptMsPerSec: d('ScriptDuration') * 1000,
                taskMsPerSec: d('TaskDuration') * 1000,
                nodes: map.get('Nodes') || 0,
                heapMB: Math.round((map.get('JSHeapUsedSize') || 0) / 1048576),
            };
        }
        prevMetrics = map;
        prevAt = now;
    } catch { /* 断线时静默，onClosed 已更新状态 */
    }
}

/* ---- Tracing 突发录制：录 ms 毫秒渲染管线，返回分阶段报告 ---- */
export async function recordPipeline(ms = 3000): Promise<TraceReport | null> {
    if (!ws || cdpState.status !== 'connected') return null;
    traceBuf = [];
    const done = new Promise<void>(res => {
        traceDone = res;
    });
    try {
        await send('Tracing.start', {
            traceConfig: {includedCategories: ['devtools.timeline']},
            transferMode: 'ReportEvents',
        });
    } catch {
        traceBuf = null;
        traceDone = null;
        cdpState.error = 'Tracing.start 失败';
        return null;
    }
    await new Promise(r => setTimeout(r, ms));
    try {
        await send('Tracing.end');
    } catch { /* 忽略 */
    }
    await Promise.race([done, new Promise(r => setTimeout(r, 4000))]);
    const report = parseTimelineEvents(traceBuf || []);
    traceBuf = null;
    traceDone = null;
    return report;
}

export async function loadCdpPort() {
    try {
        const r = await fetch('/api/app/settings');
        const j = await r.json();
        if (j.ok && j.settings?.cdpPort) cdpState.port = j.settings.cdpPort;
    } catch { /* 服务不在时保持默认 9222 */
    }
}
