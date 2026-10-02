// 字面 Live 工程包（.jizuralive / JZLV 容器）：保存时歌曲、歌词（整个工程）一并带走。
// 不再兼容老格式，老的 .json 只留「打开」当样式配置导入。
// 布局全小端，与 C# 解析器 src/Server/JizuraPackage.cs 逐字节对齐，改动必须两边同步：
//   "JZLV" | u16 version | u16 entryCount | u32 reserved
//   × entryCount: u8 nameLen | name(UTF-8) | u8 codec(0=raw,1=brotli,2=gzip)
//     | u64 rawSize | u64 packSize | u64 offset
//   之后是各条目负载连续排列。
// 压缩策略即压缩率策略：mp3/flac/woff2 本体已是压缩格式，裸存不白费 CPU；
// project.json（歌词/时间/覆盖/配置）走 brotli，浏览器不支持时回退 gzip。
import {J} from '../engine';
import {S} from './state';

export const PKG_VERSION = 1;
export const PROJECT_ENTRY = 'project.json';
export const AUDIO_ENTRY_PREFIX = 'audio.';
export const FONT_ENTRY_PREFIX = 'fonts/';

export interface PkgEntryInput {
    name: string;
    data: Uint8Array
}

export interface PkgEntry {
    name: string;
    codec: 0 | 1 | 2;
    rawSize: number;
    data: Uint8Array
}

const MAGIC = [0x4a, 0x5a, 0x4c, 0x56];   // "JZLV"

let brSupported: boolean | null = null;

function brotliOk(): boolean {
    if (brSupported == null) {
        try {
            new (CompressionStream as any)('br');
            brSupported = true;
        } catch {
            brSupported = false;
        }
    }
    return brSupported;
}

async function pipeFmt(data: Uint8Array, fmt: string, mode: 'c' | 'd'): Promise<Uint8Array> {
    const Ctor: any = mode === 'c' ? CompressionStream : DecompressionStream;
    let stream: ReadableStream;
    try {
        stream = new Blob([data as BlobPart]).stream().pipeThrough(new Ctor(fmt));
    } catch (e) {
        // 本机 CEF 不带 brotli：写入端自动落 gzip；只有别的工具写的 brotli 包会走到这里
        if (fmt === 'br') throw new Error('此工程包使用了本浏览器不支持的 brotli 编码，请用桌面版打开');
        throw e;
    }
    return new Uint8Array(await new Response(stream).arrayBuffer());
}

async function encodeEntry(data: Uint8Array): Promise<{ codec: 0 | 1 | 2; bytes: Uint8Array }> {
    if (brotliOk()) {
        try {
            return {codec: 1, bytes: await pipeFmt(data, 'br', 'c')};
        } catch { // 回退 gzip
        }
    }
    return {codec: 2, bytes: await pipeFmt(data, 'gzip', 'c')};
}

export function buildPackage(entries: PkgEntryInput[]): Promise<Uint8Array> {
    return (async () => {
        const enc = new TextEncoder();
        const head = new Uint8Array(16);
        const hv = new DataView(head.buffer);
        MAGIC.forEach((b, i) => {
            hv.setUint8(i, b);
        });
        hv.setUint16(4, PKG_VERSION, true);
        hv.setUint16(6, entries.length, true);
        const encoded: { name: Uint8Array; codec: 0 | 1 | 2; rawSize: number; bytes: Uint8Array }[] = [];
        for (const e of entries) {
            if (!e.name || e.name.length > 255) throw new Error('bad entry name');
            const {codec, bytes} = await encodeEntry(e.data);
            encoded.push({name: enc.encode(e.name), codec, rawSize: e.data.length, bytes});
        }
        let payload = 0;
        for (const e of encoded) payload += e.bytes.length;
        // 条目头 = nameLen(1) + name + codec(1) + rawSize(8) + packSize(8) + offset(8) = nameLen + 26
        const headerLen = 16 + encoded.reduce((n, e) => n + 2 + e.name.length + 24, 0);
        const out = new Uint8Array(headerLen + payload);
        out.set(head);
        const dv = new DataView(out.buffer);
        let p = 16, off = headerLen;
        for (const e of encoded) {
            dv.setUint8(p, e.name.length);
            p++;
            out.set(e.name, p);
            p += e.name.length;
            dv.setUint8(p, e.codec);
            p++;
            dv.setBigUint64(p, BigInt(e.rawSize), true);
            p += 8;
            dv.setBigUint64(p, BigInt(e.bytes.length), true);
            p += 8;
            dv.setBigUint64(p, BigInt(off), true);
            p += 8;
            out.set(e.bytes, off);
            off += e.bytes.length;
        }
        return out;
    })();
}

export async function readPackage(bytes: Uint8Array): Promise<PkgEntry[]> {
    if (bytes.length < 16 || MAGIC.some((b, i) => bytes[i] !== b)) throw new Error('not a jizuralive package');
    const dv = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    const version = dv.getUint16(4, true);
    if (version > PKG_VERSION) throw new Error('package version too new: ' + version);
    const count = dv.getUint16(6, true);
    const dec = new TextDecoder();
    const out: PkgEntry[] = [];
    let p = 16;
    for (let i = 0; i < count; i++) {
        if (p + 3 > bytes.length) throw new Error('truncated header');
        const nameLen = dv.getUint8(p);
        p++;
        const name = dec.decode(bytes.subarray(p, p + nameLen));
        p += nameLen;
        const codec = dv.getUint8(p) as 0 | 1 | 2;
        p++;
        const rawSize = Number(dv.getBigUint64(p, true));
        p += 8;
        const packSize = Number(dv.getBigUint64(p, true));
        p += 8;
        const offset = Number(dv.getBigUint64(p, true));
        p += 8;
        if (offset < 0 || packSize < 0 || offset + packSize > bytes.length) throw new Error('entry out of bounds: ' + name);
        const packed = bytes.subarray(offset, offset + packSize);
        const data = codec === 0 ? packed : await pipeFmt(packed, codec === 1 ? 'br' : 'gzip', 'd');
        if (data.length !== rawSize) throw new Error('size mismatch: ' + name);
        out.push({name, codec, rawSize, data});
    }
    return out;
}

export const entryText = (e: PkgEntry) => new TextDecoder().decode(e.data);

// 打包当前工程：project.json（全部字段：歌词/逐字时间/覆盖/配置）+ 歌曲（原始编码字节）+ 上传字体
export async function buildProjectPackage(appVersion: string): Promise<Blob> {
    const project = JSON.stringify(Object.assign({}, S.project, {app: 'jizura-live', appVersion}));
    const entries: PkgEntryInput[] = [{name: PROJECT_ENTRY, data: new TextEncoder().encode(project)}];
    if (J.loadSong) {
        try {
            const song = await J.loadSong();
            if (song) entries.push({name: AUDIO_ENTRY_PREFIX + 'bin', data: new Uint8Array(await song.arrayBuffer())});
        } catch { // 歌曲取不到就先不带
        }
    }
    if (J.loadFontData) {
        for (const [key, f] of Object.entries(J.FONTS as Record<string, any>)) {
            if (!f.user || !f.loaded) continue;
            try {
                const buf = await J.loadFontData(key);
                if (buf) entries.push({name: FONT_ENTRY_PREFIX + key, data: new Uint8Array(buf)});
            } catch { // 单个字体缺失不阻断
            }
        }
    }
    return new Blob([await buildPackage(entries) as BlobPart], {type: 'application/octet-stream'});
}
