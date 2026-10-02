import {describe, expect, it} from 'vitest';
import type {LyricMap} from '../../src/cloud/krc';
import {classifyLangRows, decryptKrc, krcToLrc, neBuildLrc, neParseLrc} from '../../src/cloud/krc';

const KRC_KEY = [0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47, 0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69];

async function deflate(buf: Uint8Array): Promise<Uint8Array> {
  const stream = new Blob([buf as unknown as BlobPart]).stream().pipeThrough(new CompressionStream('deflate'));
  return new Uint8Array(await new Response(stream).arrayBuffer());
}

async function makeKrc(text: string): Promise<ArrayBuffer> {
  // KRC 格式 = 'krc1' + XOR(deflate(文本))，先压缩后异或，顺序和 decryptKrc 相反
  const zipped = await deflate(new TextEncoder().encode(text));
  for (let i = 0; i < zipped.length; i++) zipped[i] ^= KRC_KEY[i % 16];
  const out = new Uint8Array(4 + zipped.length);
  out.set(new TextEncoder().encode('krc1'), 0);
  out.set(zipped, 4);
  return out.buffer;
}

function b64(json: any): string {
  return btoa(String.fromCharCode(...new TextEncoder().encode(JSON.stringify(json))));
}

describe('classifyLangRows', () => {
  it('拉丁为主 → 音译', () => {
    expect(classifyLangRows(['konnichiha sekai'])).toBe('roma');
  });
  it('假名/汉字为主 → 翻译', () => {
    expect(classifyLangRows(['こんにちは世界'])).toBe('trans');
  });
  it('空行 → none', () => {
    expect(classifyLangRows([])).toBe('none');
    expect(classifyLangRows(['', '  '])).toBe('none');
  });
});

describe('decryptKrc', () => {
  it('krc1 魔数 + XOR + deflate 回环还原原文', async () => {
    const src = '[0,2000]<0,2000,0>こんにちは\n[2500,1500]<0,1500,0>世界';
    const text = await decryptKrc(await makeKrc(src));
    expect(text).toBe(src);
  });

  it('缺魔数报错', async () => {
    const bad = new Uint8Array(8);
    await expect(decryptKrc(bad.buffer)).rejects.toThrow('krc1');
  });
});

describe('krcToLrc', () => {
  const langLine = '[language:' + b64({ content: [
    { lyricContent: [['konnitiha']] },
    { lyricContent: [['你好']] },
  ] }) + ']';
  const KRC = [
    '[offset:0]',
    '[0,2000]<0,2000,0>こんにちは',
    '[2500,1500]<0,1500,0>世界/!',
    langLine,
  ].join('\n');

  it('逐字时间压缩为 [mm:ss.cc] 行，音译/翻译拼到 | 后', () => {
    const out = krcToLrc(KRC, { roma: true, trans: true });
    const lines = out.trim().split('\n');
    expect(lines[0]).toBe('[00:00.00]こんにちは|konnitiha　你好');
    expect(lines[1]).toBe('[00:02.50]世界／!'); // 注音行不足两条时，只对齐到存在的行
  });

  it('`/` 被转成全角（JIZURA 切镜头指令保护）', () => {
    const out = krcToLrc(KRC, { roma: false, trans: false });
    expect(out).toContain('世界／!');
    expect(out).not.toContain('世界/!');
  });

  it('mapOut 记录 原文 → {音译, 翻译}（不受勾选影响）', () => {
    const map: LyricMap = new Map();
    krcToLrc(KRC, { roma: false, trans: false }, map);
    expect(map.get('こんにちは')).toEqual({ roma: 'konnitiha', trans: '你好' });
  });

  it('offset 标签平移时间且不为负', () => {
    const out = krcToLrc('[offset:500]\n[100,1000]<0,1000,0>あ', {});
    expect(out).toContain('[00:00.00]あ');
  });
});

describe('neParseLrc', () => {
  it('解析时间戳为毫秒，多时间戳行展开', () => {
    const rows = neParseLrc('[00:01.00]あ\n[00:05.00][00:09.50]い');
    expect(rows).toEqual([
      { ms: 1000, text: 'あ' },
      { ms: 5000, text: 'い' },
      { ms: 9500, text: 'い' },
    ]);
  });

  it('元数据标签跳过，JSON 制作行转纯文本', () => {
    const rows = neParseLrc('[ti:タイトル]\n{"c":[{"tx":"作詞"},{"tx":"誰か"}]}\n[00:02.00]う');
    expect(rows[0]).toEqual({ ms: null, text: '作詞誰か' });
    expect(rows[1]).toEqual({ ms: 2000, text: 'う' });
  });
});

describe('neBuildLrc', () => {
  it('按时间戳对齐音译/翻译，写入 mapOut', () => {
    const map: LyricMap = new Map();
    const main = neParseLrc('[00:01.00]こんにちは\n[00:04.00]世界');
    const roma = neParseLrc('[00:01.00]konnitiha\n[00:04.00]sekai');
    const trans = neParseLrc('[00:01.00]你好\n[00:04.00]世界');
    const out = neBuildLrc(main, trans, roma, { roma: true, trans: true }, map);
    const lines = out.trim().split('\n');
    expect(lines[0]).toBe('[00:01.00]こんにちは|konnitiha　你好');
    expect(lines[1]).toBe('[00:04.00]世界|sekai');
    expect(map.get('こんにちは')).toEqual({ roma: 'konnitiha', trans: '你好' });
  });

  it('无时间戳行在前句与后句之间均摊时间', () => {
    const main = neParseLrc('[00:00.00]はじめ\n作詞：誰か\n[00:06.00]おわり');
    const out = neBuildLrc(main, [], [], {});
    const lines = out.trim().split('\n');
    expect(lines[1]).toMatch(/^\[00:03\.00\]作詞：誰か$/); // prev 0 + (next-prev) * 1/(run+1) = 6000/2
  });

  it('关掉勾选后不拼注音（map 不受影响）', () => {
    const main = neParseLrc('[00:01.00]あ');
    const roma = neParseLrc('[00:01.00]a');
    const out = neBuildLrc(main, [], roma, { roma: false, trans: false });
    expect(out.trim()).toBe('[00:01.00]あ');
  });
});
