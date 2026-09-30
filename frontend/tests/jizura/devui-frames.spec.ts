import {describe, expect, it} from 'vitest';
import {gapStats} from '../../src/jizura/devui/frames';

describe('gapStats', () => {
    it('空窗口返回全 0', () => {
        expect(gapStats([])).toEqual({avg: 0, max: 0, p95: 0, fps: 0});
    });

    it('avg/p95/max 与 fps 推导', () => {
        // 20 帧：16ms×19 + 100ms×1（一次卡顿）
        const gaps = [...Array(19).fill(16), 100];
        const s = gapStats(gaps);
        expect(s.avg).toBeCloseTo((19 * 16 + 100) / 20);
        expect(s.max).toBe(100);
        expect(s.p95).toBe(16);          // 前 95% 都在 16ms 档
        expect(s.fps).toBeCloseTo(1000 / s.avg);
    });

    it('p95 取上分位档（全高帧间隔时）', () => {
        const s = gapStats([...Array(10).fill(50)]);
        expect(s.p95).toBe(50);
        expect(s.fps).toBeCloseTo(20);
    });
});
