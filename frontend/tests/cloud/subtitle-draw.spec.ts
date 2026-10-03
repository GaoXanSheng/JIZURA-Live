// 字幕绘制核心（subtitle-draw.ts）：不依赖 DOM，用桩 ctx 验证行定位、
// 载荷下标对应与开关短路。排版数值不在这里复刻断言，只看「画没画、画的哪行」
import {describe, expect, it} from 'vitest';
import {drawSubtitleAt, makeSubtitleOverlay} from '../../src/cloud/subtitle-draw';

// 记录调用的桩 ctx：measureText 给固定宽度触发折行路径，其余只计数
function stubCtx() {
    const calls: string[] = [];
    return {
        calls,
        canvas: {width: 1920, height: 1080},
        save: () => calls.push('save'),
        restore: () => calls.push('restore'),
        setTransform: () => calls.push('setTransform'),
        measureText: (s: string) => ({width: s.length * 10}),
        fillText: (s: string) => calls.push('fill:' + s),
        strokeText: (s: string) => calls.push('stroke:' + s),
        fillRect: () => calls.push('bar'),
        font: '', textAlign: '', textBaseline: '',
        lineWidth: 0, strokeStyle: '', fillStyle: '', lineJoin: '',
    } as any;
}

const plan = {
    W: 1920, H: 1080,
    lines: [
        {text: '一', start: 0, end: 2},
        {text: '二', start: 2, end: 4},
        {text: '', start: 4, end: 6, interlude: true},
    ],
};

describe('drawSubtitleAt', () => {
    it('按时间命中行，textOf 收到行对象与下标', () => {
        const ctx = stubCtx();
        drawSubtitleAt(ctx, plan, 2.5, {scale: 1},
            {content: 'trans'} as any, (line, idx) => `T${idx}:${line.text}`);
        expect(ctx.calls.filter(c => c.startsWith('fill:'))).toEqual(['fill:T1:二']);
    });

    it('textOf 返回空串（间奏/无翻译）不画任何字', () => {
        const ctx = stubCtx();
        drawSubtitleAt(ctx, plan, 5, {scale: 1}, {content: 'trans'} as any, () => '');
        expect(ctx.calls.filter(c => c.startsWith('fill:'))).toEqual([]);
    });

    it('content=off 与 noTrans/layer 直接短路', () => {
        for (const opt of [{scale: 1}, {scale: 1, noTrans: true}, {scale: 1, layer: 'front'}]) {
            const ctx = stubCtx();
            drawSubtitleAt(ctx, plan, 1, opt, {content: 'off'} as any, () => '字');
            expect(ctx.calls).toEqual([]);
        }
    });

    it('makeSubtitleOverlay 按 texts 下标取词，与 plan 行序一致', () => {
        const overlay = makeSubtitleOverlay({
            cfg: {content: 'both'} as any,
            texts: ['甲', '乙', ''],
        });
        const ctx = stubCtx();
        overlay(ctx, plan, 0.5, {scale: 1});
        overlay(ctx, plan, 3.5, {scale: 1});
        expect(ctx.calls.filter(c => c.startsWith('fill:'))).toEqual(['fill:甲', 'fill:乙']);
    });
});
