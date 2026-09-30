import {describe, expect, it} from 'vitest';
import {parseTimelineEvents} from '../../src/jizura/devui/traceparse';

/* devtools.timeline 事件样例：ts/dur 单位微秒，ph 'X' 为完成事件 */
const ev = (name: string, durUs: number, ts = 1000, cat = 'devtools.timeline', ph = 'X') =>
    ({name, cat, ph, ts, dur: durUs});

describe('parseTimelineEvents', () => {
    it('按阶段聚合次数与耗时（微秒→毫秒）', () => {
        const r = parseTimelineEvents([
            ev('UpdateLayoutTree', 1000),            // 1ms
            ev('UpdateLayoutTree', 3000),            // 3ms
            ev('Layout', 2000),                      // 2ms
            ev('Paint', 500, 2000),                  // 0.5ms
        ]);
        expect(r.frames).toBe(0);
        expect(r.stages).toHaveLength(3);
        const style = r.stages.find(s => s.name === 'UpdateLayoutTree')!;
        expect(style.label).toBe('样式重算');
        expect(style.count).toBe(2);
        expect(style.totalMs).toBeCloseTo(4);
        expect(style.maxMs).toBeCloseTo(3);
        const layout = r.stages.find(s => s.name === 'Layout')!;
        expect(layout.totalMs).toBeCloseTo(2);
        // 按 totalMs 降序：样式重算在前
        expect(r.stages[0].name).toBe('UpdateLayoutTree');
    });

    it('DrawFrame 计入帧数（不分相位）', () => {
        const r = parseTimelineEvents([
            {name: 'DrawFrame', cat: 'devtools.timeline', ph: 'I', ts: 1},
            {name: 'DrawFrame', cat: 'devtools.timeline', ph: 'I', ts: 2},
            ev('Paint', 100),
        ]);
        expect(r.frames).toBe(2);
        expect(r.stages).toHaveLength(1);
    });

    it('忽略非 timeline 类目与非 X 相位事件', () => {
        const r = parseTimelineEvents([
            ev('FunctionCall', 5000, 1000, 'v8.execute'),       // 类目不符
            ev('Layout', 1000, 1000, 'devtools.timeline', 'B'), // 未完成事件
            ev('Paint', 1000),
        ]);
        expect(r.stages).toHaveLength(1);
        expect(r.stages[0].name).toBe('Paint');
    });

    it('空输入返回空报告，spanMs 为 0', () => {
        expect(parseTimelineEvents([])).toEqual({stages: [], frames: 0, spanMs: 0});
        const r = parseTimelineEvents([ev('Paint', 1000, 5000), ev('Layout', 2000, 1000)]);
        expect(r.spanMs).toBeCloseTo((5000 + 1000 - 1000) / 1000);
    });

    it('未知阶段保留原名', () => {
        const r = parseTimelineEvents([ev('SomeNewStage', 100)]);
        expect(r.stages[0].label).toBe('SomeNewStage');
    });
});
