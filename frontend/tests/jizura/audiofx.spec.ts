// 只测配置清洗（不信任外部输入）和 EQ 频段
// 图构建要真实 AudioContext，jsdom 覆盖不了；预览/导出共走同一构建器
import {describe, expect, it} from 'vitest';
import {J} from '../../src/engine';

describe('sanitizeAudioFx 配置清洗', () => {
    it('空/非法输入回退默认（none + 默认速度/强度 + EQ 归零）', () => {
        for (const bad of [null, undefined, 'x', 42, {}, {preset: '不存在'}]) {
            const fx = J.sanitizeAudioFx(bad as any);
            expect(fx.preset).toBe('none');
            expect(fx.rate).toBeCloseTo(0.125);
            expect(fx.depth).toBeCloseTo(0.7);
            expect(fx.eq).toEqual([0, 0, 0, 0, 0, 0, 0]);
        }
    });

    it('合法预设透传，数值越界钳制', () => {
        const fx = J.sanitizeAudioFx({preset: 'orbit', rate: 99, depth: -3});
        expect(fx.preset).toBe('orbit');
        expect(fx.rate).toBe(2);       // 上限 2Hz
        expect(fx.depth).toBe(0);      // 下限 0
    });

    it('eq 增益逐段钳制 -12..+12，长度补齐到 7 段', () => {
        const fx = J.sanitizeAudioFx({preset: 'eq', eq: [-20, 20, 3.5, 'x', NaN]});
        expect(fx.preset).toBe('eq');
        expect(fx.eq).toEqual([-12, 12, 3.5, 0, 0, 0, 0]);   // 越界钳制 + 缺位补 0
        expect(J.AUDIO_EQ_BANDS).toHaveLength(7);
    });
});
