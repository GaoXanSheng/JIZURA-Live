import {describe, expect, it} from 'vitest';
import {J} from '../../src/engine';

describe('J.layoutText', () => {
  it('每个字符产出字形，横排宽度/高度为正', () => {
    const lay = J.layoutText({ text: 'ABあ', size: 100, font: 'sys' });
    expect(lay.length).toBe(3);
    expect(lay.W).toBeGreaterThan(0);
    expect(lay.H).toBeGreaterThan(0);
    expect(lay.map((g: any) => g.ch)).toEqual(['A', 'B', 'あ']);
  });

  it('同一输入两次布局结果一致（独立对象、无缓存干扰）', () => {
    const a = J.layoutText({ text: 'テスト', size: 80, font: 'sys', track: 0.05 });
    const b = J.layoutText({ text: 'テスト', size: 80, font: 'sys', track: 0.05 });
    expect(JSON.stringify(a)).toBe(JSON.stringify(b));
  });

  it('J.measure 返回的 w/h 与布局尺寸一致（sx/sy 缩放）', () => {
    const m1 = J.measure({ text: 'キャッシュ', size: 50, font: 'sys' });
    const m2 = J.measure({ text: 'キャッシュ', size: 50, font: 'sys', sx: 1.5, sy: 2 });
    expect(m1.w).toBeCloseTo(m1.lay.W, 6);
    expect(m2.w).toBeCloseTo(m2.lay.W * 1.5, 6);
    expect(m2.h).toBeCloseTo(m2.lay.H * 2, 6);
  });

  it('多行文本分行布局，行号正确', () => {
    const lay = J.layoutText({ text: '一行\n二行', size: 60, font: 'sys' });
    expect(lay.length).toBe(4);
    expect(new Set(lay.map((g: any) => g.li))).toEqual(new Set([0, 1]));
  });

  it('竖排产出正向宽高', () => {
    const lay = J.layoutText({ text: 'たて書', size: 60, font: 'sys', vertical: true });
    expect(lay.length).toBe(3);
    expect(lay.W).toBeGreaterThan(0);
    expect(lay.H).toBeGreaterThan(0);
  });

  it('对齐方式改变起点但不改变宽度', () => {
    const l = J.layoutText({ text: 'ああ', size: 100, font: 'sys', align: 'left' });
    const c = J.layoutText({ text: 'ああ', size: 100, font: 'sys', align: 'center' });
    const r = J.layoutText({ text: 'ああ', size: 100, font: 'sys', align: 'right' });
    expect(l.W).toBe(c.W);
    expect(c.W).toBe(r.W);
    expect(l.map((g: any) => g.x)).not.toEqual(c.map((g: any) => g.x));
  });
});

describe('J.typesetLine', () => {
  it('输出与输入字符数一致且因子为数值', () => {
    const ts = J.typesetLine([...'アイうA1']);
    expect(ts.length).toBe(5);
    for (const t of ts) {
      expect(typeof t.adv).toBe('number');
      expect(typeof t.f).toBe('number');
      expect(t.f).toBeGreaterThan(0);
    }
  });

  it('默认关闭文字整列：adv/f 全为 1', () => {
    const prev = J.TYPESET;
    J.TYPESET = false;
    try {
      const ts = J.typesetLine([...'あっあ']);
      expect(ts.every(t => t.adv === 1 && t.f === 1)).toBe(true);
    } finally { J.TYPESET = prev; }
  });

  it('开启文字整列后：假名收紧，促音/小书名更紧', () => {
    const prev = J.TYPESET;
    J.TYPESET = true;
    try {
      const ts = J.typesetLine([...'あっあ']);
      expect(ts[0].adv).toBeLessThan(1);
      expect(ts[1].adv).toBeLessThan(ts[0].adv); // っ 是小书名
    } finally { J.TYPESET = prev; }
  });
});

describe('J.metrics', () => {
  it('同字体同字符度量被缓存', () => {
    const a = J.metrics.adv('sys', 'あ');
    const b = J.metrics.adv('sys', 'あ');
    expect(a).toBe(b);
    expect(a).toBeGreaterThan(0);
  });
});
