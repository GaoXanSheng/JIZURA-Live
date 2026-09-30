/* 解析 CDP Tracing（devtools.timeline 类目）事件 → 渲染管线分阶段耗时报告。
   纯函数，可单测。trace 事件的 ts/dur 单位是微秒，这里统一换算为毫秒。 */

export interface TraceStage {
    name: string;
    label: string;
    count: number;
    totalMs: number;
    maxMs: number;
}

export interface TraceReport {
    stages: TraceStage[];
    frames: number;      // DrawFrame 次数（合成器提交帧）
    spanMs: number;      // 事件时间跨度
}

const STAGE_LABELS: Record<string, string> = {
    UpdateLayoutTree: '样式重算',
    Layout: '布局',
    PrePaint: '预绘制',
    Layerize: '分层',
    Paint: '绘制',
    RasterTask: '光栅化',
    FireAnimationFrame: 'rAF 回调',
    FunctionCall: 'JS 调用',
    TimerFire: '定时器',
    EventDispatch: '事件分发',
    HitTest: '命中测试',
};

export function parseTimelineEvents(events: any[]): TraceReport {
    const map = new Map<string, TraceStage>();
    let t0 = Infinity, t1 = -Infinity, frames = 0;
    for (const e of events || []) {
        if (!e || typeof e.name !== 'string') continue;
        const cat = String(e.cat || '');
        if (!cat.includes('devtools.timeline')) continue;
        if (e.name === 'DrawFrame') {
            frames++;
            continue;
        }
        if (e.ph !== 'X') continue;
        const durMs = (Number(e.dur) || 0) / 1000;
        let st = map.get(e.name);
        if (!st) {
            st = {name: e.name, label: STAGE_LABELS[e.name] || e.name, count: 0, totalMs: 0, maxMs: 0};
            map.set(e.name, st);
        }
        st.count++;
        st.totalMs += durMs;
        if (durMs > st.maxMs) st.maxMs = durMs;
        const ts = Number(e.ts);
        if (Number.isFinite(ts)) {
            if (ts < t0) t0 = ts;
            const end = ts + (Number(e.dur) || 0);
            if (end > t1) t1 = end;
        }
    }
    return {
        stages: [...map.values()].sort((a, b) => b.totalMs - a.totalMs),
        frames,
        spanMs: t1 > t0 ? (t1 - t0) / 1000 : 0,
    };
}
