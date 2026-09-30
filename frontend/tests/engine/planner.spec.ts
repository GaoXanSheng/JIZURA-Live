/* planner 单元测试：确定性、切分结构不变量、歌词行覆盖、标题/间奏、节拍网格。
   全部跑在 jsdom + 假 canvas 度量上（见 tests/setup.ts），完全离线可复现。 */
import { describe, it, expect } from 'vitest';
import { J } from '../../src/engine';

const LYRICS = [
  '# 测试歌词',
  '[00:02.00]こんにちは世界',
  '[00:06.50]ふたつめの*ライン*!',
  '[00:10.00]三行目 / 二段目',
  '[00:13.00][間奏 3]',   // 有时间戳的间奏；computeTiming 要求全行都有时间戳才采用 LRC 时间
  '[00:16.00]ラスト',
].join('\n');

function makeProject(over: any = {}) {
  const p = J.defaultProject();
  p.lyrics = LYRICS;
  p.title = 'テスト曲';
  p.artist = 'テスト歌手';
  // snap=false：行时间直接用 LRC 时间戳（默认会吸附到节拍/能量，时间不可预测）
  p.timing = Object.assign(p.timing, { snap: false });
  return Object.assign(p, over);
}

function makeAudio(duration = 120) {
  return {
    duration,
    bpm: 120,
    beats: J.beatGrid(120, 0, duration),
    energy: Float32Array.from({ length: duration * 50 }, (_v, i) => Math.abs(Math.sin(i / 8))),
    energyRate: 50,
    peaks: Float32Array.from({ length: 400 }, (_v, i) => Math.sin(i / 5)),
  };
}

describe('J.plan', () => {
  it('同一输入产出完全一致的 plan（确定性）', () => {
    const a = J.plan(makeProject(), makeAudio());
    const b = J.plan(makeProject(), makeAudio());
    expect(JSON.stringify(a.cuts)).toBe(JSON.stringify(b.cuts));
    expect(JSON.stringify(a.lines)).toBe(JSON.stringify(b.lines));
  });

  it('不同 seed 产出不同编排', () => {
    const a = J.plan(makeProject({ seed: 1 }), makeAudio());
    const b = J.plan(makeProject({ seed: 999999 }), makeAudio());
    expect(JSON.stringify(a.cuts)).not.toBe(JSON.stringify(b.cuts));
  });

  it('切分按时间有序且时长为正', () => {
    const plan = J.plan(makeProject(), makeAudio());
    expect(plan.cuts.length).toBeGreaterThan(0);
    for (let i = 0; i < plan.cuts.length; i++) {
      const c = plan.cuts[i];
      expect(c.end).toBeGreaterThan(c.start);
      if (i > 0) expect(c.start).toBeGreaterThanOrEqual(plan.cuts[i - 1].start - 1e-6);
    }
  });

  it('非间奏歌词行全部进入 plan.lines（强调/冲击标记被解析）', () => {
    const plan = J.plan(makeProject(), makeAudio());
    const textLines = plan.lines.filter((l: any) => l.text);
    expect(textLines.map((l: any) => l.text)).toEqual(['こんにちは世界', 'ふたつめのライン', '三行目二段目', 'ラスト']);
    const emphLine = textLines[1];
    expect(emphLine.emph).toContain('ライン');
    expect(emphLine.impact).toBe(true);
  });

  it('`/` 手切标记把一行拆成多个切分', () => {
    const plan = J.plan(makeProject(), makeAudio());
    const texts = plan.cuts.map((c: any) => c.text);
    expect(texts).toContain('三行目');
    expect(texts).toContain('二段目');
  });

  it('间奏标记生成 interlude 行（时长取自标记）', () => {
    const plan = J.plan(makeProject(), makeAudio());
    const inst = plan.lines.filter((l: any) => l.interlude);
    expect(inst.length).toBeGreaterThan(0);
    expect(inst.some((l: any) => l.secs === 3)).toBe(true);
  });

  it('标题切分在开头且带曲名', () => {
    const plan = J.plan(makeProject(), makeAudio());
    const title = plan.cuts.find((c: any) => c.layout === 'title');
    expect(title).toBeTruthy();
    expect(title.text).toBe('テスト曲');
    expect(title.line).toBe(-1);
  });

  it('时长以音频为准（useAudioLength 默认开）', () => {
    const plan = J.plan(makeProject(), makeAudio(200));
    expect(plan.duration).toBeGreaterThanOrEqual(199.5);
  });

  it('音频节拍进入 plan；能量表按引用传递', () => {
    const audio = makeAudio();
    const plan = J.plan(makeProject(), audio);
    expect(plan.beats).toEqual(audio.beats);
    expect(plan.energy).toBe(audio.energy);
  });
});

describe('J.beatGrid', () => {
  it('按 BPM 生成等间隔节拍', () => {
    const beats = J.beatGrid(120, 0, 60);
    expect(beats.length).toBeGreaterThan(100);
    expect(beats[1] - beats[0]).toBeCloseTo(0.5, 5);
    expect(beats.every((b: number) => b >= 0 && b <= 60)).toBe(true);
  });

  it('beatOffset 平移网格', () => {
    const a = J.beatGrid(120, 0, 30);
    const b = J.beatGrid(120, 0.25, 30);
    expect(b[0]).toBeCloseTo(a[0] + 0.25, 5);
  });
});
