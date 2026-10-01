/* 歌词源状态（原 kgLastLyric 体系）：当前载入的 KRC/LRC/网易云歌词、
   音译/翻译映射（字幕用，跨窗口镜像共享经 localStorage jizura.kgsubmap）、
   多歌词版本候选。载入新歌词统一走这里，再同步进项目。 */
import {S} from '../jizura/state';
import {bus} from '../jizura/bus';
import {replan} from '../jizura/engine';
import {toast} from '../jizura/look';
import {api} from './api';
import type {LyricCandidate, LyricResponse, Source} from './types';
import {b64Text, decryptKrc, krcToLrc, type LyricMap, type LyricOptions, neBuildLrc, neParseLrc} from './krc';

export interface LyricSource {
  fmt: 'krc' | 'lrc' | 'ne';
  text: string;
  map: LyricMap;
  cands: LyricCandidate[];
  raw?: {
    lrc: ReturnType<typeof neParseLrc>;
    trans: ReturnType<typeof neParseLrc>;
    roma: ReturnType<typeof neParseLrc>
  };
}

let last: LyricSource | null = null;
let opts: LyricOptions = {roma: true, trans: true};

export function lyricOptions(): LyricOptions {
  return {...opts};
}

export function setLyricOptions(p: Partial<LyricOptions>) {
  opts = {...opts, ...p};
  localStorage.setItem('jizura.kglyricopts', JSON.stringify(opts));
}

try {
  const o = JSON.parse(localStorage.getItem('jizura.kglyricopts') || 'null');
  if (o) opts = {roma: o.roma !== false, trans: o.trans !== false};
} catch {
}

export function currentLyric(): LyricSource | null {
  return last;
}

/* 镜像（渲染进程）浏览器拿不到主页面内存里的 last，
   映射+歌词版本候选持久化到 localStorage（两边共享），
   载入时还原、storage 事件实时跟随。候选不存的话，刷新后
   「歌词版本」下拉就消失（选择器要求 cands ≥ 2） */
function persist() {
  try {
    if (last && (last.map.size || last.cands.length)) localStorage.setItem('jizura.kgsubmap', JSON.stringify({
      fmt: last.fmt, text: last.text || '', map: [...last.map], cands: last.cands || [],
    }));
    else localStorage.removeItem('jizura.kgsubmap');
  } catch { /* 映射过大存不下就算了 */
  }
}

function takeSaved(saved: any): LyricSource | null {
    return saved && ((saved.map && saved.map.length) || (saved.cands && saved.cands.length))
        ? {fmt: saved.fmt || 'krc', text: saved.text || '', map: new Map(saved.map || []), cands: saved.cands || []}
        : null;
}

function restore() {
    try {
        last = takeSaved(JSON.parse(localStorage.getItem('jizura.kgsubmap') || 'null'));
    } catch { /* 忽略 */
    }
}

restore();

/* 镜像窗口：主窗口换了歌/改了映射时实时跟随 */
window.addEventListener('storage', e => {
  if (e.key !== 'jizura.kgsubmap') return;
    try {
        last = takeSaved(JSON.parse(e.newValue || 'null'));
    } catch { /* 忽略 */
    }
});

/* 把生成的 LRC 文本写进项目并触发重排（等价旧版的写 #lyrics + input 事件）
   deferReplan: 歌曲载入管线的中间步跳过同步重排——紧随其后的 loadAudioFile 会 replan 一次到位 */
function applyLyrics(text: string, deferReplan = false) {
  S.project.lyrics = text;
  bus.emit('lyrics-sync');
    if (!deferReplan) replan();
}

/* 载入一份歌词源（ly = /kg|ne/lyric 的返回），重建映射并按当前勾选生成 LRC */
export async function adoptKgLyric(ly: LyricResponse, keepCands = false, deferReplan = false): Promise<'krc' | 'lrc'> {
  const bin = Uint8Array.from(atob(ly.content!), c => c.charCodeAt(0));
  const cands = ly.candidates || (keepCands && last && last.cands) || [];
  if (ly.fmt === 'krc') {
    const text = await decryptKrc(bin.buffer as ArrayBuffer);
    const map: LyricMap = new Map();
    last = {fmt: 'krc', text, map, cands};
    persist();
    applyLyrics(krcToLrc(text, opts, map), deferReplan);
  } else {
    last = {fmt: 'lrc', text: new TextDecoder('utf-8').decode(bin), map: new Map(), cands};
    persist();
    applyLyrics(last.text, deferReplan);
  }
  bus.emit('lyric-versions');
  return ly.fmt as 'krc' | 'lrc';
}

export async function adoptNeLyric(ly: LyricResponse, deferReplan = false): Promise<void> {
  const lrc = neParseLrc(b64Text(ly.content!));
  const trans = ly.trans ? neParseLrc(b64Text(ly.trans)) : [];
  const roma = ly.roma ? neParseLrc(b64Text(ly.roma)) : [];
  const map: LyricMap = new Map();
  const text = neBuildLrc(lrc, trans, roma, opts, map);
  last = {fmt: 'ne', text, map, raw: {lrc, trans, roma}, cands: []};
  persist();
    applyLyrics(text, deferReplan);
  bus.emit('lyric-versions');
}

/* 本地 .krc 文件（拖入 / 文件选择） */
export async function adoptLocalKrc(file: File): Promise<void> {
  const text = await decryptKrc(await file.arrayBuffer());
  const map: LyricMap = new Map();
  last = {fmt: 'krc', text, map, cands: []};
  persist();
  applyLyrics(krcToLrc(text, opts, map));
  toast('歌词已载入：' + file.name);
}

/* 勾选变化：对刚载入的歌词立即重新生成 */
export function applyLyricOptions(): void {
  if (!last) return;
  if (last.fmt === 'krc') {
    applyLyrics(krcToLrc(last.text, opts, last.map));
  } else if (last.fmt === 'ne' && last.raw) {
    const map: LyricMap = new Map();
    applyLyrics(neBuildLrc(last.raw.lrc, last.raw.trans, last.raw.roma, opts, map));
    last.map = map;
    persist();
  }
}

/* ---------- 歌词版本（酷狗同一首歌常有官方/用户上传的多个版本） ---------- */

export function versionLabel(c: LyricCandidate): string {
  return (c.from || '其他版本') + (c.nick && c.nick !== '热心用户' ? ' · ' + c.nick : '');
}

export function versions(): LyricCandidate[] {
  return (last && last.cands) || [];
}

export async function switchVersion(source: Source, idx: number): Promise<void> {
  const c = versions()[idx];
  if (!c) return;
  toast('切换歌词版本…');
  try {
    const ly = await api.lyricById(source, c.id, c.accesskey);
    if (!ly.ok) throw new Error(ly.error || '下载失败');
    const fmt = await adoptKgLyric(ly, true);
    const bad = fmt === 'krc' && !/^\[\d\d:\d\d[.:]/m.test(S.project.lyrics);
    toast(bad ? '该版本格式暂不支持，请换个版本试试'
        : '歌词版本：' + versionLabel(c) + (fmt === 'lrc' ? '（普通 LRC，没有音译/翻译）' : ''));
    bus.emit('lyric-versions');
  } catch (e: any) {
    toast('切换失败：' + (e.message || e));
    bus.emit('lyric-versions');
  }
}
