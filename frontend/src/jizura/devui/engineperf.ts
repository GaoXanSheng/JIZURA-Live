// 引擎帧归因：Renderer.frame() 的分段耗时环形缓冲（打点在 09_render.js）。
// 面板开着才 on——渲染器每帧只做一次 `J.PERF && J.PERF.on` 判断，关=零开销；
// 非 stage（手法缩略图等小画布）的帧不采集，只记主舞台大画布。
export interface EngineFrameRow {
    total: number;
    bg: number;
    pass: number;
    morph: number;
    trans: number;
    post: number;
    hit: boolean;
    stage: boolean;
    layout: string;
    cam: string;
    bgId: string;
    decor: number;
    cut: number;
    tq: number;
    t: number;
}

const RING = 240;
const rows: EngineFrameRow[] = [];

export const enginePerf = {
    on: false,
    push(r: EngineFrameRow) {
        if (!r.stage) return;
        rows.push(r);
        if (rows.length > RING) rows.shift();
    },
    snapshot(): EngineFrameRow[] {
        return rows.slice();
    },
};

export function setEnginePerfOn(on: boolean) {
    enginePerf.on = on;
    if (!on) rows.length = 0;
    const J = (globalThis as any).J;
    if (J) J.PERF = on ? enginePerf : null;
}
