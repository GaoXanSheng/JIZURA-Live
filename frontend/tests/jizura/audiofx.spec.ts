// 音频特效 v2：配置清洗（不信任外部输入）、v1 旧配置迁移、预设完整性、变速钳制。
// 图构建要真实 AudioContext，jsdom 覆盖不了；预览/导出共走同一构建器（10b_audiofx.js）
import {describe, expect, it} from 'vitest';
import {J} from '../../src/engine';

describe('sanitizeAudioFx v2 清洗', () => {
    it('空/非法输入回退默认（空链 + 速度 1）', () => {
        for (const bad of [null, undefined, 'x', 42, {}, {units: 'x'}]) {
            const fx = J.sanitizeAudioFx(bad as any);
            expect(fx.speed).toBe(1);
            expect(fx.units).toEqual([]);
        }
    });

    it('未知单元丢弃，重复单元去重，on 缺省为 true', () => {
        const fx = J.sanitizeAudioFx({
            units: [
                {id: '不存在的'},
                {id: 'reverb', params: {size: 9, wet: 0.5}},
                {id: 'reverb', params: {size: 1}},
                {id: 'echo', on: false, params: {}},
            ],
        });
        expect(fx.units.map((u: any) => u.id)).toEqual(['reverb', 'echo']);
        expect(fx.units[0].on).toBe(true);
        expect(fx.units[0].params.size).toBe(6);      // 越界钳到上限
        expect(fx.units[1].on).toBe(false);           // bypass 保留
    });

    it('数值逐参数钳制，EQ 增益 -12..+12 且补齐 7 段', () => {
        const fx = J.sanitizeAudioFx({units: [{id: 'eq', params: {gains: [-20, 20, 3.5, 'x', NaN]}}]});
        expect(fx.units[0].params.gains).toEqual([-12, 12, 3.5, 0, 0, 0, 0]);
        expect(J.AUDIO_EQ_BANDS).toHaveLength(7);
    });

    it('speed 钳制 0.5..2', () => {
        expect(J.sanitizeAudioFx({speed: 9}).speed).toBe(2);
        expect(J.sanitizeAudioFx({speed: -1}).speed).toBe(0.5);
        expect(J.sanitizeAudioFx({}).speed).toBe(1);
    });
});

describe('v1 旧配置迁移', () => {
    it('单预设搬进等价链（pan/spatial/orbit）', () => {
        const fx = J.sanitizeAudioFx({preset: 'pan', rate: 0.3, depth: 0.4});
        expect(fx.units).toHaveLength(1);
        expect(fx.units[0]).toMatchObject({id: 'pan', on: true, params: {rate: 0.3, depth: 0.4}});

        expect(J.sanitizeAudioFx({preset: 'spatial', depth: 0.2}).units[0].id).toBe('spatial');
        expect(J.sanitizeAudioFx({preset: 'orbit', rate: 0.5}).units[0].id).toBe('orbit');
    });

    it('v1 eq 数组迁为 eq 单元', () => {
        const fx = J.sanitizeAudioFx({preset: 'eq', eq: [1, 2, 3, 4, 5, 6, 7]});
        expect(fx.units[0].id).toBe('eq');
        expect(fx.units[0].params.gains).toEqual([1, 2, 3, 4, 5, 6, 7]);
    });

    it('v1 preset=none 迁为空链', () => {
        expect(J.sanitizeAudioFx({preset: 'none', eq: [1]}).units).toEqual([]);
    });
});

describe('预设包与注册表', () => {
    it('每个预设清洗后仍是合法配置（单元都存在、参数齐全）', () => {
        expect(J.AFX_PRESETS.length).toBeGreaterThanOrEqual(10);
        for (const p of J.AFX_PRESETS) {
            const fx = J.sanitizeAudioFx(p.cfg);
            expect(fx.speed).toBeGreaterThanOrEqual(0.5);
            for (const u of fx.units) {
                expect(J.AFX_UNITS[u.id]).toBeTruthy();
                expect(Object.keys(u.params).sort()).toEqual(Object.keys(J.AFX_UNITS[u.id].defaults).sort());
            }
        }
    });

    it('注册表单元 schema 完整（label/params/defaults/build）', () => {
        for (const [id, def] of Object.entries(J.AFX_UNITS as Record<string, any>)) {
            expect(def.label, id).toBeTruthy();
            expect(Array.isArray(def.params), id).toBe(true);
            expect(typeof def.build, id).toBe('function');
            // defaults 必须能过自己这关（清洗后参数不缺）
            const clean = J.sanitizeAudioFx({units: [{id, params: def.defaults}]});
            expect(Object.keys(clean.units[0].params).sort()).toEqual(Object.keys(def.defaults).sort());
        }
    });

    it('fxSpeed 读 project 并钳制', () => {
        expect(J.fxSpeed(null)).toBe(1);
        expect(J.fxSpeed({})).toBe(1);
        expect(J.fxSpeed({audioFx: {speed: 1.25}})).toBe(1.25);
        expect(J.fxSpeed({audioFx: {speed: 99}})).toBe(2);
    });
});
