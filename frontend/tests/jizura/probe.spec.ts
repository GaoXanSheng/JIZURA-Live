/* 性能探针（jizura/probe）单元测试：埋点聚合与 dump 结构。
   rAF 循环/LongTask 观察是浏览器运行时的职责，这里只测纯逻辑。 */
import { describe, it, expect } from 'vitest';
import { probeRecord, probeSync, reset, dump } from '../../src/jizura/probe';

describe('probeRecord / dump', () => {
  it('聚合 count/avg/max/last，按 max 降序', () => {
    reset();
    probeRecord('a', 10);
    probeRecord('a', 30);
    probeRecord('a', 20);
    probeRecord('b', 1);
    const d = dump();
    const a = d.marks.find((m: any) => m.name === 'a');
    const b = d.marks.find((m: any) => m.name === 'b');
    expect(a.count).toBe(3);
    expect(a.avg).toBe(20);
    expect(a.max).toBe(30);
    expect(a.last).toBe(20);
    expect(b.max).toBe(1);
    expect(d.marks[0].name).toBe('a');   // max 更大的排前面
    expect(d.frame).toHaveProperty('dropped');
    expect(d.longTasks).toHaveProperty('worstMs');
  });

  it('reset 清空一切', () => {
    probeRecord('x', 5);
    reset();
    const d = dump();
    expect(d.marks).toEqual([]);
    expect(d.frame.count).toBe(0);
    expect(d.longTasks.count).toBe(0);
  });
});

describe('probeSync / probeAsync', () => {
  it('透传返回值并记录耗时', () => {
    reset();
    const v = probeSync('sync', () => 42);
    expect(v).toBe(42);
    const m = dump().marks.find((x: any) => x.name === 'sync');
    expect(m.count).toBe(1);
    expect(m.last).toBeGreaterThanOrEqual(0);
  });

  it('函数抛错也照样记录（finally 语义）', () => {
    reset();
    expect(() => probeSync('boom', () => { throw new Error('x'); })).toThrow('x');
    expect(dump().marks.find((x: any) => x.name === 'boom')?.count).toBe(1);
  });
});
