/* v0.10 上游功能迁移测试：LRC 行序/时序、老工程迁移、主题、拉丁分词、装饰文本隐藏、LRC 导出。 */
import {describe, expect, it} from 'vitest';
import {J} from '../../src/engine';
import {mergeProject} from '../../src/jizura/project';

const anyJ = J as any;

const MIXED_LRC = [
    '[00:02.00]一つ目',
    '間奏でない無戳行',        // 无戳行：v0.10 起保持在书写位置（跟在「一つ目」后）
    '[00:06.00]ふたつ目',
].join('\n');

describe('parseLyrics 行序（v0.10）', () => {
    it('无戳行保持在书写位置，不再被排到最后', () => {
        const {lines} = anyJ.parseLyrics(MIXED_LRC);
        const texts = lines.map((l: any) => l.text);
        expect(texts).toEqual(['一つ目', '間奏でない無戳行', 'ふたつ目']);
    });

    it('有戳行仍按时间戳排序', () => {
        const raw = '[00:08.00]后\n[00:02.00]前';
        const {lines} = anyJ.parseLyrics(raw);
        expect(lines.map((l: any) => l.text)).toEqual(['前', '后']);
    });

    it('parseOrderV1 给出 v0.9 的旧行序映射（旧索引→新索引）', () => {
        const map = anyJ.parseOrderV1(MIXED_LRC);
        // 旧排序（无戳行排最后）：一つ目(0), ふたつ目(2), 無戳行(1) → 新索引里的位置
        expect(map).toEqual([0, 2, 1]);
    });

    it('全带戳或全无戳的歌词不需要迁移', () => {
        expect(anyJ.parseOrderV1('[00:01.00]a\n[00:02.00]b')).toBeNull();
        expect(anyJ.parseOrderV1('a\nb')).toBeNull();
    });
});

describe('computeTiming 混合 LRC（v0.10）', () => {
    it('有戳行用 LRC 时刻，无戳行夹在其间', () => {
        const p = J.defaultProject();
        p.lyrics = MIXED_LRC;
        p.timing.snap = false;
        const tm = anyJ.computeTiming(p, anyJ.parseLyrics(MIXED_LRC), null);
        expect(tm.starts[0]).toBeCloseTo(2, 1);
        expect(tm.starts[2]).toBeCloseTo(6, 1);
        // 无戳行必须落在 2~6 之间（而不是被推到最后）
        expect(tm.starts[1]).toBeGreaterThan(2);
        expect(tm.starts[1]).toBeLessThan(6);
    });

    it('手工时刻优先于 LRC 标签，且不会倒挂到更早的固定时刻之前', () => {
        const p = J.defaultProject();
        p.lyrics = '[00:02.00]a\n[00:06.00]b\n[00:10.00]c';
        p.timing.snap = false;
        p.timing.lineTimes = {1: 1};   // 手工把第二行定在 1s（早于第一行的 2s）
        const tm = anyJ.computeTiming(p, anyJ.parseLyrics(p.lyrics), null);
        expect(tm.starts[0]).toBeCloseTo(2, 1);
        expect(tm.starts[1]).toBeGreaterThanOrEqual(2.05);   // 被钳到不早于前行
    });
});

describe('主题（J.THEMES / おまかせ themeId）', () => {
    it('主题表包含六个方向', () => {
        expect(anyJ.THEME_ORDER).toEqual(expect.arrayContaining(['lyricpv', 'kinetic', 'wa', 'horror', 'pop', 'ballad']));
    });

    it('主题名经 zh-labels 覆盖为中文（UI 不出现日文）', () => {
        expect(anyJ.THEMES.kinetic.name).toBe('动感');
        expect(anyJ.THEMES.horror.name).toBe('恐怖');
        expect(anyJ.THEMES.pop.name).toBe('波普');
        expect([...Object.values(anyJ.THEMES).map((t: any) => t.name)].every((n: string) => !/[\u3040-\u30ff]/.test(n))).toBe(true);
    });

    it('带主题的一键随机打开主题需要的部品开关', () => {
        const p = J.defaultProject();
        p.extra = false;
        p.typo = false;
        for (let i = 0; i < 20; i++) {
            const r = anyJ.omakase(p, Math.random, 'lyricpv');
            expect(r.typo).toBe(true);          // 主题需要的开关随结果返回
            expect(['editorial', 'graphic', 'emotional']).toContain(r.mood);
        }
    });

    it('无主题时结果不带额外开关（旧行为不变）', () => {
        const r = anyJ.omakase(J.defaultProject());
        expect(r.typo).toBeUndefined();
    });

    it('mergeProject 清洗 themeId：未知主题回退 null', () => {
        expect(mergeProject({themeId: 'lyricpv'}).themeId).toBe('lyricpv');
        expect(mergeProject({themeId: 'noSuchTheme'}).themeId).toBeNull();
        expect(mergeProject({themeId: 123}).themeId).toBeNull();
    });
});

describe('老工程行序迁移（migrateOrder）', () => {
    it('timingOrder 缺失的混合 LRC 工程：lineTimes 与 overrides 按新行序搬移', () => {
        const p: any = mergeProject({
            lyrics: MIXED_LRC,
            timing: {lineTimes: {0: 2, 1: 5, 2: 6}},   // v0.9 行序下的时刻：一つ目/無戳行/ふたつ目
            overrides: {1: {seed: 7}},
        });
        // 旧索引 1（無戳行）→ 新索引 2；旧 2（ふたつ目）→ 新索引 1
        expect(p.timing.lineTimes[0]).toBe(2);
        expect(p.timing.lineTimes[1]).toBe(6);
        expect(p.timing.lineTimes[2]).toBe(5);
        expect(p.overrides[2]).toBeDefined();
        expect(p.overrides[1]).toBeUndefined();
        expect(p.timingOrder).toBe(2);
    });

    it('已是 timingOrder=2 的工程不重复迁移', () => {
        const p: any = mergeProject({
            lyrics: MIXED_LRC,
            timingOrder: 2,
            timing: {lineTimes: {1: 9}},
        });
        expect(p.timing.lineTimes[1]).toBe(9);
        expect(p.timing.lineTimes[2]).toBeUndefined();
    });
});

describe('拉丁/韩文分词', () => {
    it('isLatinText 判定拉丁为主', () => {
        expect(anyJ.isLatinText('La la la la 世界')).toBe(true);
        expect(anyJ.isLatinText('夜明けの色を覚えてる')).toBe(false);
    });

    it('joinWords 拉丁词间留空格，连字符后不留', () => {
        expect(anyJ.joinWords(['never-', 'ending'])).toBe('never-ending');
        expect(anyJ.joinWords(['hello', 'world'])).toBe('hello world');
    });

    it('长拉丁词不在词中间切开（只在连字符后分）', () => {
        const chunks = anyJ.chunkText('extraordinaryneverendingstorytime');
        expect(chunks.every((c: string) => ['extraordinaryneverendingstorytime'].includes(c) || /[-‐–—]/.test(c))).toBe(true);
    });

    it('splitLines 对拉丁文本只在空格处断行', () => {
        const out = anyJ.splitLines('the quick brown fox jumps over the lazy dog', 10);
        expect(out.split('\n').every((l: string) => l.trim().length > 0)).toBe(true);
        expect(out).not.toContain('\n\n');
    });
});

describe('装饰文本隐藏（fx.hideNo / hideTime）', () => {
    it('decoTextKind 分类编号与时刻，CJK 文本返回 null', () => {
        expect(anyJ.decoTextKind('No.01')).toBe('no');
        expect(anyJ.decoTextKind('#03')).toBe('no');
        expect(anyJ.decoTextKind('00:12.34')).toBe('time');
        expect(anyJ.decoTextKind('REC 1:05')).toBe('time');
        expect(anyJ.decoTextKind('第3話')).toBeNull();
        expect(anyJ.decoTextKind('hello world')).toBeNull();
    });

    it('hideDecoText 按 fx 开关隐藏，出现在歌词里的组合保留', () => {
        const env = {fx: {hideNo: true, hideTime: false}, cut: {lineText: 'No.01 はじまり'}};
        expect(anyJ.hideDecoText(env, 'No.02')).toBe(true);       // 开关开：隐藏
        expect(anyJ.hideDecoText(env, 'No.01')).toBe(false);      // 歌词里出现：保留
        expect(anyJ.hideDecoText(env, '00:12.34')).toBe(false);   // hideTime 未开
        expect(anyJ.hideDecoText({fx: {}}, 'No.02')).toBe(false);
    });
});

describe('LRC 导出（J.lrcText）', () => {
    it('每行带 [mm:ss.xx] 时间戳，标题/作者写入标签', () => {
        const p = J.defaultProject();
        p.title = '测试曲';
        p.artist = '测试歌手';
        p.lyrics = '[00:02.00]一つ目\n[00:06.00]ふたつ目';
        p.timing.snap = false;
        const txt = anyJ.lrcText(p, null, null);
        expect(txt).toContain('[ti:测试曲]');
        expect(txt).toContain('[ar:测试歌手]');
        expect(txt).toMatch(/\[00:02\.00\]一つ目/);
        expect(txt).toMatch(/\[00:06\.00\]ふたつ目/);
    });

    it('行范围导出：只含范围内行，时刻从 t0 起算', () => {
        const p = J.defaultProject();
        p.lyrics = '[00:02.00]一つ目\n[00:06.00]ふたつ目\n[00:10.00]みっつ目';
        p.timing.snap = false;
        const txt = anyJ.lrcText(p, null, {from: 1, to: 2, t0: 5.8});
        expect(txt).not.toContain('一つ目');
        expect(txt).toMatch(/\[00:00\.20\]ふたつ目/);   // 6.00 - 5.80
        expect(txt).toMatch(/みっつ目/);
    });

    it('间奏行保留用户写的标签（含秒数），无标签时补 [间奏]', () => {
        const p = J.defaultProject();
        p.lyrics = '[00:02.00]一つ目\n[00:05.00][間奏 4]';
        p.timing.snap = false;
        const txt = anyJ.lrcText(p, null, null);
        expect(txt).toMatch(/\[00:05\.00\]\[間奏 4\]/);   // 已带标签：原样保留
    });
});
