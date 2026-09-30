/* 应用相关进程的系统指标：1s 轮询 GET /api/perf/sys（C# 侧 PerfController，
   主进程 + CefSharp.BrowserSubprocess 的 CPU/GPU/内存；GPU 走 pdh 系统计数器）。
   cpu/gpu 为 -1 表示该值未知（首帧无差值 / GPU 实例未出现）。 */

export interface SysProc {
    pid: number;
    name: string;
    type: string;       // main | cef
    cpu: number;
    gpu: number;
    memMB: number;
}

export interface SysSnapshot {
    total: { cpu: number; gpu: number; memMB: number };
    procs: SysProc[];
    error: string | null;
    at: number;
}

let timer = 0;
let snap: SysSnapshot | null = null;
let inFlight = false;

export function startSys(intervalMs = 1000) {
    if (timer) return;
    poll();
    timer = window.setInterval(poll, intervalMs);
}

export function stopSys() {
    clearInterval(timer);
    timer = 0;
}

export function sysSnapshot(): SysSnapshot | null {
    return snap;
}

async function poll() {
    if (inFlight) return;
    inFlight = true;
    try {
        const r = await fetch('/api/perf/sys');
        if (!r.ok) throw new Error(`HTTP ${r.status}`);
        const j = await r.json();
        snap = {
            total: {
                cpu: j.total?.cpu ?? -1,
                gpu: j.total?.gpu ?? -1,
                memMB: j.total?.memMB ?? 0,
            },
            procs: Array.isArray(j.procs) ? j.procs.map((p: any) => ({
                pid: p.pid, name: p.name, type: p.type,
                cpu: p.cpu ?? -1, gpu: p.gpu ?? -1, memMB: p.memMB ?? 0,
            })) : [],
            error: null,
            at: Date.now(),
        };
    } catch {
        snap = {
            total: {cpu: -1, gpu: -1, memMB: 0},
            procs: [],
            error: '本机服务未提供 /api/perf/sys（需重启应用加载新版本）',
            at: Date.now(),
        };
    } finally {
        inFlight = false;
    }
}
