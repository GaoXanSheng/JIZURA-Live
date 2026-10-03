// 导出勾选模型：校验矩阵 + 勾选→spec 映射 + 旧 kind 兼容对齐
import {describe, expect, it} from 'vitest';
import {
    buildSpec,
    DEFAULT_EXPORT_CFG,
    type ExportCfg,
    normalizeCfg,
    specFromLegacyKind
} from '../../src/jizura/export-spec';

const ctxOn = {subOn: true, audioLoaded: true};
const ctxSubOff = {subOn: false, audioLoaded: true};

const cfg = (p: Partial<ExportCfg>): ExportCfg => ({...DEFAULT_EXPORT_CFG, ...p});

describe('buildSpec 校验', () => {
    it('渲染画面与字幕都没勾 → 拒绝', () => {
        const r = buildSpec(cfg({visuals: false, subs: false}), ctxOn);
        expect(r.ok).toBe(false);
        if (!r.ok) expect(r.error).toContain('至少勾选');
    });

    it('勾了字幕但字幕面板是关 → 拒绝', () => {
        const r = buildSpec(cfg({subs: true}), ctxSubOff);
        expect(r.ok).toBe(false);
        if (!r.ok) expect(r.error).toContain('字幕渲染未开启');
    });

    it('MP4 + 无背景（透明）→ 拒绝并指向 WebM/PNG', () => {
        const r = buildSpec(cfg({bg: false}), ctxOn);
        expect(r.ok).toBe(false);
        if (!r.ok) expect(r.error).toContain('WebM');
    });

    it('MP4 + 纯字幕（无渲染画面）→ 同样拒绝', () => {
        const r = buildSpec(cfg({visuals: false, subs: true}), ctxOn);
        expect(r.ok).toBe(false);
    });
});

describe('buildSpec 勾选 → spec 映射', () => {
    it('默认勾选 = 不透明 MP4 + 音频（字幕默认不勾，不烤）', () => {
        const r = buildSpec(DEFAULT_EXPORT_CFG, ctxOn);
        expect(r.ok).toBe(true);
        if (r.ok) {
            expect(r.spec).toMatchObject({
                kind: 'mp4', transparent: false, layer: null,
                bakeSubs: false, embedAudio: true, keyBg: 'off',
            });
            expect(r.wavToo).toBe(false);
        }
    });

    it('MP4 勾字幕 → bakeSubs（overlay 烤入）', () => {
        const r = buildSpec(cfg({subs: true}), ctxOn);
        if (r.ok) expect(r.spec.bakeSubs).toBe(true);
    });

    it('MP4 大视频直存 → kind=mp4file', () => {
        const r = buildSpec(cfg({toFile: true}), ctxOn);
        if (r.ok) expect(r.spec.kind).toBe('mp4file');
    });

    it('MP4 + 绿幕 → keyBg 透传（抠像用）', () => {
        const r = buildSpec(cfg({bgStyle: 'green'}), ctxOn);
        if (r.ok) expect(r.spec.keyBg).toBe('green');
    });

    it('WebM 不勾背景 = 透明前景层（front、无 HUD），音频转 wavToo', () => {
        const r = buildSpec(cfg({format: 'webm', bg: false, subs: true}), ctxOn);
        expect(r.ok).toBe(true);
        if (r.ok) {
            expect(r.spec).toMatchObject({kind: 'webm', transparent: true, layer: 'front', noHud: true, bakeSubs: true});
            expect(r.wavToo).toBe(true);
        }
    });

    it('WebM 勾背景 = 不透明整帧（带背景与 HUD，layer=null）', () => {
        const r = buildSpec(cfg({format: 'webm'}), ctxOn);
        if (r.ok) expect(r.spec).toMatchObject({transparent: false, layer: null, noHud: false});
    });

    it('WebM 纯字幕 = customDrawOnly，不走 overlay', () => {
        const r = buildSpec(cfg({format: 'webm', visuals: false, subs: true}), ctxOn);
        expect(r.ok).toBe(true);
        if (r.ok) {
            expect(r.spec).toMatchObject({customDrawOnly: true, bakeSubs: false, transparent: true});
            expect(r.wavToo).toBe(true);
        }
    });

    it('PNG 勾背景+分层 = pngl 等价（layers，双层）', () => {
        const r = buildSpec(cfg({format: 'png', layered: true, subs: true}), ctxOn);
        expect(r.ok).toBe(true);
        if (r.ok) expect(r.spec).toMatchObject({layers: true, transparent: true, layer: null});
    });

    it('PNG 不勾背景 = 透明前景（frontOnly）', () => {
        const r = buildSpec(cfg({format: 'png', bg: false}), ctxOn);
        if (r.ok) expect(r.spec).toMatchObject({transparent: true, layer: 'front', noHud: true, layers: false});
    });

    it('PNG 纯字幕 = 透明底 customDrawOnly', () => {
        const r = buildSpec(cfg({format: 'png', visuals: false, subs: true}), ctxOn);
        if (r.ok) expect(r.spec).toMatchObject({customDrawOnly: true, transparent: true});
    });
});

describe('specFromLegacyKind 与旧按钮行为对齐', () => {
    it('mp4/mp4file：烤字幕 + includeAudio 门控音频 + keyBg 透传', () => {
        expect(specFromLegacyKind('mp4', {includeAudio: false, keyBg: 'green'})).toMatchObject({
            kind: 'mp4', bakeSubs: true, embedAudio: false, keyBg: 'green',
        });
        expect(specFromLegacyKind('mp4', {includeAudio: true})).toMatchObject({embedAudio: true, keyBg: 'off'});
        expect(specFromLegacyKind('mp4file', {})).toMatchObject({kind: 'mp4file', bakeSubs: true});
    });

    it('webm/webmfront/subwebm：透明、字幕只走 subwebm 的 customDraw', () => {
        expect(specFromLegacyKind('webm', {})).toMatchObject({kind: 'webm', transparent: true, layer: null, bakeSubs: false});
        expect(specFromLegacyKind('webmfront', {})).toMatchObject({layer: 'front', noHud: true, bakeSubs: false});
        expect(specFromLegacyKind('subwebm', {})).toMatchObject({customDrawOnly: true, bakeSubs: true});
    });

    it('png 家族：不透明/透明/分层/前景 四态', () => {
        expect(specFromLegacyKind('png', {keyBg: 'black'})).toMatchObject({transparent: false, keyBg: 'black'});
        expect(specFromLegacyKind('pnga', {})).toMatchObject({transparent: true, layer: null, layers: false});
        expect(specFromLegacyKind('pngl', {})).toMatchObject({transparent: true, layers: true});
        expect(specFromLegacyKind('pngf', {})).toMatchObject({transparent: true, layer: 'front', noHud: true});
    });

    it('未知 kind → null（调用方维持原报错）', () => {
        expect(specFromLegacyKind('lrc', {})).toBeNull();
        expect(specFromLegacyKind('wav', {})).toBeNull();
    });
});

describe('normalizeCfg 脏数据回退', () => {
    it('非法值逐字段回默认', () => {
        expect(normalizeCfg(null)).toEqual(DEFAULT_EXPORT_CFG);
        expect(normalizeCfg({format: 'avi', bgStyle: 'red', visuals: 'yes'})).toMatchObject({
            format: 'mp4', bgStyle: 'off', visuals: true,
        });
        // 旧工程迁移：includeAudio=false → audio=false
        expect(normalizeCfg({audio: false})).toMatchObject({audio: false});
    });
});
