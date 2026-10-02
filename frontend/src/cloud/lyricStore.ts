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

// 渲染窗口拿不到主窗口内存里的 last，映射和版本候选落 localStorage 两边共享
// 候选不存的话刷新后「歌词版本」下拉会消失（选择器要求 cands ≥ 2）
function persist() {
  try {
    if (last && (last.map.size || last.cands.length)) localStorage.setItem('jizura.kgsubmap', JSON.stringify({
      fmt: last.fmt, text: last.text || '', map: [...last.map], cands: last.cands || [],
    }));
    else localStorage.removeItem('jizura.kgsubmap');
  } catch { // 超配额存不下就算了
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
    } catch {
    }
}

restore();

// 镜像窗口实时跟随主窗口的换歌/改映射
window.addEventListener('storage', e => {
  if (e.key !== 'jizura.kgsubmap') return;
    try {
        last = takeSaved(JSON.parse(e.newValue || 'null'));
    } catch {
    }
});

// 把生成的 LRC 写进项目并触发重排
// deferReplan 给载歌管线中间步用：紧跟着的 loadAudioFile 末尾会 replan 一次到位
function applyLyrics(text: string, deferReplan = false) {
  S.project.lyrics = text;
  bus.emit('lyrics-sync');
    if (!deferReplan) replan();
}

// ly 是 /kg|ne/lyric 的返回；重建映射并按当前勾选生成 LRC
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

// 本地拖入或选择的 .krc 文件
export async function adoptLocalKrc(file: File): Promise<void> {
  const text = await decryptKrc(await file.arrayBuffer());
  const map: LyricMap = new Map();
  last = {fmt: 'krc', text, map, cands: []};
  persist();
  applyLyrics(krcToLrc(text, opts, map));
  toast('歌词已载入：' + file.name);
}

// 音译/翻译勾选变了，对刚载入的歌词立即重新生成
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

// 酷狗同一首歌常有官方/用户上传的多个版本
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
