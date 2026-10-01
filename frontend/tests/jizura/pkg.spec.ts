/* 专用工程包（jizura/pkg）单元测试：JZLV 容器布局、往返一致性、坏输入拒绝。
   Node 没有 CompressionStream('br')，这里自动落 gzip 路径；brotli 路径由
   CEF（Chrome 152+）承担，并与 C# 解析器 src/Server/JizuraPackage.cs 做交叉验证。 */
import {describe, expect, it} from 'vitest';
import {J} from '../../src/engine';
import {S} from '../../src/jizura/state';
import {
    AUDIO_ENTRY_PREFIX,
    buildPackage,
    buildProjectPackage,
    entryText,
    FONT_ENTRY_PREFIX,
    PKG_VERSION,
    PROJECT_ENTRY,
    readPackage,
} from '../../src/jizura/pkg';

const anyJ = J as any;

function bytes(n: number, seed = 1): Uint8Array {
    const out = new Uint8Array(n);
    let s = seed >>> 0;
    for (let i = 0; i < n; i++) {
        s = (s * 1103515245 + 12345) >>> 0;
        out[i] = s & 0xff;
    }
    return out;
}

describe('JZLV 容器', () => {
    it('往返：多条目（文本 + 二进制）字节级一致', async () => {
        const text = new TextEncoder().encode('{"lyrics":"忆华年","n":3}');
        const audio = bytes(65536, 7);
        const font = bytes(1024, 9);
        const pkg = await buildPackage([
            {name: PROJECT_ENTRY, data: text},
            {name: AUDIO_ENTRY_PREFIX + 'bin', data: audio},
            {name: FONT_ENTRY_PREFIX + 'user_UF_x', data: font},
        ]);
        expect(pkg.length).toBeGreaterThan(16);
        const entries = await readPackage(pkg);
        expect(entries.map(e => e.name)).toEqual([PROJECT_ENTRY, AUDIO_ENTRY_PREFIX + 'bin', FONT_ENTRY_PREFIX + 'user_UF_x']);
        expect(entryText(entries[0])).toBe('{"lyrics":"忆华年","n":3}');
        expect([...entries[1].data]).toEqual([...audio]);
        expect([...entries[2].data]).toEqual([...font]);
        for (const e of entries) expect(e.rawSize).toBe(e.data.length);
    });

    it('头部布局：magic/version/entryCount + 条目字段小端', async () => {
        const text = new TextEncoder().encode('abc');
        const pkg = await buildPackage([{name: 'a.json', data: text}]);
        const dv = new DataView(pkg.buffer);
        expect([pkg[0], pkg[1], pkg[2], pkg[3]]).toEqual([0x4a, 0x5a, 0x4c, 0x56]);
        expect(dv.getUint16(4, true)).toBe(PKG_VERSION);
        expect(dv.getUint16(6, true)).toBe(1);
        const nameLen = dv.getUint8(16);
        expect(new TextDecoder().decode(pkg.subarray(17, 17 + nameLen))).toBe('a.json');
        const codec = dv.getUint8(17 + nameLen);
        expect([0, 1, 2]).toContain(codec);
        const rawSize = Number(dv.getBigUint64(18 + nameLen, true));
        expect(rawSize).toBe(3);
    });

    it('紧凑布局：负载紧跟头部，无空洞（与 C# 顺序写入端对齐）', async () => {
        const pkg = await buildPackage([
            {name: 'project.json', data: new TextEncoder().encode('{"a":1}')},
            {name: 'audio.bin', data: bytes(2048, 3)},
            {name: 'fonts/user_UF_x', data: bytes(512, 5)},
        ]);
        const dv = new DataView(pkg.buffer);
        const count = dv.getUint16(6, true);
        // 走一遍条目头，最后一个条目的 offset+packSize 必须正好等于文件长度
        let p = 16, lastEnd = 0;
        for (let i = 0; i < count; i++) {
            const nameLen = dv.getUint8(p);
            p += 1 + nameLen + 1;
            const packSize = Number(dv.getBigUint64(p + 8, true));
            const offset = Number(dv.getBigUint64(p + 16, true));
            p += 24;
            lastEnd = offset + packSize;
        }
        expect(lastEnd).toBe(pkg.length);
    });

    it('拒绝：坏魔数 / 截断头', async () => {
        await expect(readPackage(new TextEncoder().encode('{"json":1}'))).rejects.toThrow();
        const pkg = await buildPackage([{name: 'x', data: new Uint8Array(16)}]);
        await expect(readPackage(pkg.subarray(0, 18))).rejects.toThrow();
    });

    it('buildProjectPackage：至少带 project.json，且 app 标记在', async () => {
        S.project = {lyrics: '[00:01.00]忆华年', title: '青丝', artist: '苏瑾'} as any;
        anyJ.loadSong = undefined;   // 无歌环境：包里只有工程
        const blob = await buildProjectPackage('0.0.0-test');
        const entries = await readPackage(new Uint8Array(await blob.arrayBuffer()));
        expect(entries).toHaveLength(1);
        expect(entries[0].name).toBe(PROJECT_ENTRY);
        const project = JSON.parse(entryText(entries[0]));
        expect(project.app).toBe('jizura-live');
        expect(project.appVersion).toBe('0.0.0-test');
        expect(project.lyrics).toContain('忆华年');
    });
});
