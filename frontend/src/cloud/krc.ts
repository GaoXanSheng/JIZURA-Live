/* 歌词解析引擎（原 11b_kggkrc.js 的 krc/网易 LRC 处理，纯函数 TS 化）
   - .krc 浏览器内解密（固定 key 异或 + DecompressionStream inflate）→ 带时间戳 LRC
   - 酷狗 [language:] 块的音译/翻译提取（按字符脚本分类，type 字段不可靠）
   - 网易云主 LRC + 翻译(tlyric)/音译(romalrc) 按时间戳对齐
   输出统一为 JIZURA 语法的 LRC（/ 切镜头转全角、空行=停顿、| 注音） */
const KRC_KEY = new Uint8Array([0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47,
    0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69]);

export interface LyricOptions {
    roma: boolean;
    trans: boolean
}

export interface LyricMapValue {
    roma: string;
    trans: string
}

export type LyricMap = Map<string, LyricMapValue>;

/* ---------- krc 解密 ---------- */

async function inflate(buf: Uint8Array): Promise<Uint8Array> {
    const stream = new Blob([buf as unknown as BlobPart]).stream().pipeThrough(new DecompressionStream('deflate'));
    return new Uint8Array(await new Response(stream).arrayBuffer());
}

export async function decryptKrc(buf: ArrayBuffer): Promise<string> {
    const all = new Uint8Array(buf);
    const head = new Uint8Array(all.subarray(0, 4));
    if (String.fromCharCode(...head) !== 'krc1') throw new Error('不是 .krc 文件（缺少 krc1 魔数）');
    const body = new Uint8Array(all.subarray(4));
    for (let i = 0; i < body.length; i++) body[i] ^= KRC_KEY[i % 16];
    return new TextDecoder('utf-8').decode(await inflate(body));
}

/* ---------- krc → LRC ---------- */

const RE_TIMED = /^\[(\d+),(\d+)\](.*)$/;
const RE_WORD = /<(\d+),(\d+),\d+>/g;

function fmtMs(ms: number): string {
    ms = Math.max(0, Math.floor(ms));
    const cs = Math.floor((ms % 1000) / 10);
    return String(Math.floor(ms / 60000)).padStart(2, '0') + ':' +
        String(Math.floor(ms / 1000) % 60).padStart(2, '0') + '.' +
        String(cs).padStart(2, '0');
}

/* KRC [language:] 块里 type 字段不可靠（同一首歌可能 type:1 是翻译、type:0 是音译），
   按字符脚本分类：拉丁字母为主 → 音译；假名/汉字/谚文为主 → 翻译 */
export function classifyLangRows(rows: string[]): 'roma' | 'trans' | 'none' {
    let latin = 0, cjk = 0;
    for (const r of rows) {
        for (const ch of r) {
            if ((ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z')) latin++;
            else if (/[\u3040-\u30ff\u3400-\u9fff\uac00-\ud7af]/.test(ch)) cjk++;
        }
    }
    if (latin + cjk === 0) return 'none';
    return latin >= cjk ? 'roma' : 'trans';
}

function parseLangBlobs(text: string): { roma: string[] | null; trans: string[] | null } {
    const raw = text.split('\n').find(l => l.startsWith('[language:'));
    if (!raw) return {roma: null, trans: null};
    const line = raw.trim();   // KRC 是 CRLF：不先去 \r 的话结尾 ] 会被误当 base64 一部分
    try {
        const bin = Uint8Array.from(atob(line.slice('[language:'.length, -1)), c => c.charCodeAt(0));
        const j = JSON.parse(new TextDecoder('utf-8').decode(bin));
        let roma: string[] | null = null, trans: string[] | null = null;
        for (const item of j.content || []) {
            const rows = (item.lyricContent || []).map((blocks: any) =>
                Array.isArray(blocks) ? blocks.join('').trim() : String(blocks).trim());
            if (!rows.some((r: string) => r)) continue;
            const kind = classifyLangRows(rows);
            if (kind === 'roma') {
                if (!roma) roma = rows;
            } else if (kind === 'trans') {
                if (!trans) trans = rows;
            }
        }
        return {roma, trans};
    } catch {
        return {roma: null, trans: null};
    }
}

export function krcToLrc(text: string, opts: Partial<LyricOptions>, mapOut?: LyricMap): string {
    const o = opts || {};
    const wantRoma = o.roma !== false, wantTrans = o.trans !== false;
    const blobs = parseLangBlobs(text);
    const roma = wantRoma ? blobs.roma : null;
    const trans = wantTrans ? blobs.trans : null;
    const allRoma = blobs.roma, allTrans = blobs.trans;   // map 不受勾选影响：字幕始终拿得到两轨
    let offsetMs = 0;
    for (const tag of ['offset', 'manualoffset']) {
        const m = text.match(new RegExp('^\\[' + tag + ':(-?\\d+)\\]', 'm'));
        if (m) offsetMs += parseInt(m[1], 10);
    }

    const out: string[] = [];
    let idx = -1;   // 第几条带时间戳的行：音译/翻译按它对齐（间奏空行也占位）
    for (const raw of text.split('\n')) {
        const line = raw.replace(/\r$/, '');
        if (!line.trim()) continue;
        const m = RE_TIMED.exec(line);
        if (!m) continue;
        idx++;
        const start = parseInt(m[1], 10) - offsetMs;
        const words: string[] = [];
        let wm: RegExpExecArray | null;
        RE_WORD.lastIndex = 0;
        const body = m[3];
        while ((wm = RE_WORD.exec(body)) !== null) {
            const after = body.slice(wm.index + wm[0].length);
            const next = after.search(/<\d+,\d+,\d+>/);
            words.push(next === -1 ? after : after.slice(0, next));
        }
        let textLine = words.join('').trim();
        if (!textLine) {
            out.push('');
            continue;
        }        // 空行 = 停顿（JIZURA 语法）
        textLine = textLine.replace(/\//g, '／');          // `/` 是 JIZURA 的切镜头指令
        const notes: string[] = [];
        for (const rows of [roma, trans]) {
            let s = rows && rows[idx];
            if (s) {
                s = s.replace(/\//g, '／').trim();
                if (s && s !== textLine && !notes.includes(s)) notes.push(s);
            }
        }
        if (mapOut) {
            const pick = (rows: string[] | null) => {
                let s = rows && rows[idx];
                return s ? s.replace(/\//g, '／').trim() : '';
            };
            const r = pick(allRoma), tr = pick(allTrans);
            if (r || tr) mapOut.set(textLine, {roma: r, trans: tr});
        }
        out.push('[' + fmtMs(start) + ']' + textLine + (notes.length ? '|' + notes.join('　') : ''));
    }
    return out.join('\n').replace(/\n+$/, '') + '\n';
}

/* ---------- 网易云歌词 ---------- */

export function b64Text(b64: string): string {
    return new TextDecoder('utf-8').decode(Uint8Array.from(atob(b64), c => c.charCodeAt(0)));
}

const NE_STAMP = /\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]/g;

export interface NeRow {
    ms: number | null;
    text: string
}

// 一份 LRC → [{ms(毫秒|null), text}]；多时间戳行展开成多条
export function neParseLrc(src: string): NeRow[] {
    const out: NeRow[] = [];
    for (const raw of String(src || '').split('\n')) {
        const line = raw.replace(/\r$/, '');
        if (!line.trim()) continue;
        if (/^\s*\{"/.test(line)) {
            // 制作信息行（JSON）：转纯文本，不带时间戳
            try {
                const j = JSON.parse(line.trim());
                const text = (j.c || []).map((p: any) => p.tx || '').join('').trim();
                if (text) out.push({ms: null, text});
            } catch { /* 不是合法 JSON 就丢掉 */
            }
            continue;
        }
        NE_STAMP.lastIndex = 0;
        let m: RegExpExecArray | null, last = 0;
        const stamps: number[] = [];
        while ((m = NE_STAMP.exec(line)) !== null && m.index === last) {
            stamps.push((+m[1]) * 60000 + (+m[2]) * 1000 + (m[3] ? +String(m[3]).padEnd(3, '0') : 0));
            last = NE_STAMP.lastIndex;
        }
        if (!stamps.length) {
            // 元数据标签（[ti:..] [offset:..] 等）跳过；其余无时间戳行保留，后面补默认时间
            if (/^\s*\[[a-zA-Z]+:.*]\s*$/.test(line)) continue;
            out.push({ms: null, text: line.trim()});
            continue;
        }
        const text = line.slice(last).trim();
        for (const ms of stamps) out.push({ms, text});
    }
    return out;
}

function neFmtMs(ms: number): string {
    ms = Math.max(0, Math.round(ms));
    return String(Math.floor(ms / 60000)).padStart(2, '0') + ':' +
        String(Math.floor(ms / 1000) % 60).padStart(2, '0') + '.' +
        String(Math.floor(ms % 1000 / 10)).padStart(2, '0');
}

// 按音译/翻译勾选合成 LRC；mapOut 建 歌词原文 → {roma, trans}（字幕用）。
// 无时间戳的行（作词/作曲等）补默认时间：在上一句与下一句之间均摊，作为开场字幕。
// 全曲都没时间戳时不补（交给 JIZURA 的自动排时）。
export function neBuildLrc(lrc: NeRow[], trans: NeRow[], roma: NeRow[], opts: Partial<LyricOptions>, mapOut?: LyricMap): string {
    const at = (rows: NeRow[]) => {
        const m = new Map<number, string>();
        for (const e of rows) if (e.ms != null && e.text) m.set(Math.round(e.ms / 10), e.text);
        return (ms: number) => m.get(Math.round(ms / 10)) || '';
    };
    const getRoma = at(roma), getTrans = at(trans);
    const hasTimed = lrc.some(e => e.ms != null);
    const ms: (number | null)[] = lrc.map(e => e.ms);
    if (hasTimed) {
        for (let i = 0; i < lrc.length; i++) {
            if (ms[i] != null) continue;
            let prev = 0, next: number | null = null, runEnd = i;
            for (let j = i - 1; j >= 0; j--) if (ms[j] != null) {
                prev = ms[j]!;
                break;
            }
            for (let j = i + 1; j < lrc.length; j++) if (ms[j] != null) {
                next = ms[j];
                break;
            }
            while (runEnd < lrc.length && ms[runEnd] == null) runEnd++;
            const run = runEnd - i;
            for (let k = 0; k < run; k++)
                ms[i + k] = next != null ? prev + (next - prev) * (k + 1) / (run + 1)
                    : prev + 2000 * (k + 1);
            i = runEnd - 1;
        }
    }
    const out: string[] = [];
    for (let i = 0; i < lrc.length; i++) {
        const e = lrc[i];
        const ts = hasTimed ? ms[i] : e.ms;
        if (ts == null) {
            if (e.text) out.push(e.text.replace(/\//g, '／'));
            continue;
        }  // 全曲无时间戳的兜底
        const text = e.text.replace(/\//g, '／');
        if (!text) {
            out.push('');
            continue;
        }  // 空行 = 停顿（JIZURA 语法）
        const notes: string[] = [];
        for (const [get, want] of [[getRoma, opts.roma !== false], [getTrans, opts.trans !== false]] as const) {
            if (!want) continue;
            const s = get(ts);
            if (s && s !== text && !notes.includes(s)) notes.push(s);
        }
        if (mapOut) {
            const r = getRoma(ts), t = getTrans(ts);
            if (r || t) mapOut.set(text, {roma: r, trans: t});
        }
        out.push('[' + neFmtMs(ts) + ']' + text + (notes.length ? '|' + notes.join('　') : ''));
    }
    return out.join('\n').replace(/\n+$/, '') + '\n';
}
