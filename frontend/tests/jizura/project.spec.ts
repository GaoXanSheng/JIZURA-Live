// mergeProject 是导出文件/云端数据进引擎的唯一入口，清洗（安全不变量）全在这
import {describe, expect, it} from 'vitest';
import {J} from '../../src/engine';
import {LS_KEY, mergeProject} from '../../src/jizura/project';

const anyJ = J as any;

describe('mergeProject 清洗', () => {
  it('null → 完整默认工程', () => {
    const p = mergeProject(null);
    expect(p.aspect).toBe('16:9');
    expect(p.style).toBe('noir');
    expect(p.seed).toBeGreaterThan(0);
    expect(p.fx).toHaveProperty('motion');
    expect(p.timing).toHaveProperty('bpm');
    expect(p.locks).toEqual({ tech: {}, params: {} });
  });

  it('丢弃 appVersion（版本由宿主带）', () => {
    const p = mergeProject({ appVersion: '9.9.9' } as any);
    expect(p.appVersion).toBeUndefined();
  });

  it('colors 只留 enabled 布尔与 #hex 颜色，其余丢弃', () => {
    const p = mergeProject({
      colors: { enabled: true, accent: '#ff0044', evil: 'javascript:alert(1)', weight: 'bold' },
    } as any);
    expect(p.colors.enabled).toBe(true);
    expect(p.colors.accent).toBe('#ff0044');
    expect(p.colors.evil).toBeUndefined();
    expect(p.colors.weight).toBeUndefined();
  });

  it('locks 只收 plain key 且只允许开', () => {
    const p = mergeProject({
      locks: {
        tech: { layout: true, 'x;alert(1)': true, bad: false },
        params: { glitch: true, '../evil': true },
      },
    } as any);
    expect(p.locks.tech).toEqual({ layout: true });
    expect(p.locks.params).toEqual({ glitch: true });
  });

  it('fonts 角色必须是已知字体 key、角色名必须 plain', () => {
    const known = Object.keys(anyJ.FONTS)[0];
    const p = mergeProject({ fonts: { main: known, main2: 'NoSuchFont', '../x': known } } as any);
    expect(p.fonts.main).toBe(known);
    expect(p.fonts.main2).toBeUndefined();
    expect(p.fonts['../x']).toBeUndefined();
  });

  it('userFonts 按 SAFE_FONT_KEY 过滤并注册进字体表', () => {
    const key = 'user_test_ok';
    const p = mergeProject({
      userFonts: [
        { key, label: '测试字体', family: key.slice(6), weight: 700 },
        { key: 'bad;key', label: 'x', family: 'x', weight: 400 },
      ],
    } as any);
    expect(p.userFonts.length).toBe(1);
    expect(p.userFonts[0].key).toBe(key);
    expect(anyJ.FONTS[key]).toBeTruthy();
  });

  it('enabled 组开关与默认表合并，未知组不进来', () => {
    const p = mergeProject({ enabled: { layout: { columns: false }, noSuchGroup: { x: false } } } as any);
    expect(p.enabled.layout.columns).toBe(false);
    expect(p.enabled.noSuchGroup).toBeUndefined();
  });

  it('overrides 原样保留（内部自清洗，含 lockedCuts 快照路径）', () => {
    const ov = { '3': { lock: true, layout: 'huge' } };
    const p = mergeProject({ overrides: ov } as any);
    expect(p.overrides['3'].layout).toBe('huge');
  });

  it('持久化键名不回归（老项目兼容契约）', () => {
    expect(LS_KEY).toBe('jizura.project.v1');
  });
});
