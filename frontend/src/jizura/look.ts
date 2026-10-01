/* 外观/覆盖层：双历史栈（外观◀▶ + 歌词编辑Ctrl+Z）、行覆盖、切分指定、
   导出范围、锁。原 12_ui.js 对应逻辑原样移植。 */
import {J} from '../engine';
import {S} from './state';
import {autosave, flushSave} from './project';
import {bus} from './bus';
import {invalidateFontKey, pause, play, replan, seek} from './engine';

export {flushSave};

/* ---- toast（组件监听渲染） ---- */
export function toast(m: string, cols?: string[]) {
  bus.emit('toast', m, cols);
}

/* ---- 行覆盖 ---- */
export function setOv(i: number, patch: Record<string, any>) {
  const cur = Object.assign({}, S.project.overrides[i] || {}, patch);
  for (const k of Object.keys(cur)) if (cur[k] === undefined || cur[k] === false || cur[k] === '') delete cur[k];
  if (Object.keys(cur).length) S.project.overrides[i] = cur; else delete S.project.overrides[i];
}

export function lyricCutK(cut: any) {
  if (!cut || cut.line < 0) return -1;
  let k = 0;
  for (const c of S.plan.cuts) {
    if (c.line !== cut.line) continue;
    if (!J.LAYOUTS[c.layout] || J.LAYOUTS[c.layout].special) continue;
    if (c.index === cut.index) return k;
    k++;
  }
  return -1;
}

export function cutQuietSlot(line: number, k: number) {
  const o = (S.project.overrides || {})[line] || {};
  return (o.cutQuiet && (o.cutQuiet[k] || o.cutQuiet[String(k)])) || {};
}

export function markCutQuiet(i: number | null, k: number | null, groups: string[], on: boolean) {
  if (i == null || i < 0 || k == null || k < 0) return;
  const cur = Object.assign({}, S.project.overrides[i] || {});
  const cutQuiet = Object.assign({}, cur.cutQuiet || {});
  const q = Object.assign({}, cutQuiet[k] || cutQuiet[String(k)] || {});
  delete cutQuiet[String(k)];
  groups.forEach(g => {
    if (on) q[g] = true; else delete q[g];
  });
  if (Object.keys(q).length) cutQuiet[k] = q; else delete cutQuiet[k];
  if (Object.keys(cutQuiet).length) cur.cutQuiet = cutQuiet; else delete cur.cutQuiet;
  if (Object.keys(cur).length) S.project.overrides[i] = cur; else delete S.project.overrides[i];
}

export function cutTechSlot(line: number, k: number) {
  const o = (S.project.overrides || {})[line] || {};
  const t = (o.cutTech && (o.cutTech[k] || o.cutTech[String(k)])) || {};
  const lay = o.cutLayouts && (o.cutLayouts[k] || o.cutLayouts[String(k)]);
  return lay && t.layout == null ? Object.assign({layout: lay}, t) : t;
}

export function cutGroupVal(cut: any, g: string) {
  if (g === 'layout') return cut.layout || '';
  if (g === 'enter') return cut.enter || '';
  if (g === 'hold') return cut.hold || '';
  if (g === 'exit') return cut.exit || '';
  if (g === 'treat') return cut.treat || 'none';
  if (g === 'bg') return cut.bg || 'none';
  if (g === 'cam') return cut.cam || 'push';
  if (g === 'trans') return cut.trans || '';
  if (g === 'decor') return (cut.decor && cut.decor[0] && cut.decor[0].id) || '';
  return '';
}

export function setCutTech(i: number | null, k: number | null, group: string, key: string) {
  if (i == null || i < 0 || k == null || k < 0) return;
  const cur = Object.assign({}, S.project.overrides[i] || {});
  const cutTech = Object.assign({}, cur.cutTech || {});
  const slot = Object.assign({}, cutTech[k] || cutTech[String(k)] || {});
  delete cutTech[String(k)];
  if (!key) delete slot[group];
  else slot[group] = key;
  if (Object.keys(slot).length) cutTech[k] = slot; else delete cutTech[k];
  if (Object.keys(cutTech).length) cur.cutTech = cutTech; else delete cur.cutTech;
  const cutQuiet = Object.assign({}, cur.cutQuiet || {});
  const q = Object.assign({}, cutQuiet[k] || cutQuiet[String(k)] || {});
  delete cutQuiet[String(k)];
  delete q[group];
  if (Object.keys(q).length) cutQuiet[k] = q; else delete cutQuiet[k];
  if (Object.keys(cutQuiet).length) cur.cutQuiet = cutQuiet; else delete cur.cutQuiet;
  if (group === 'layout') {
    const cutLayouts = Object.assign({}, cur.cutLayouts || {});
    if (!key) delete cutLayouts[k]; else cutLayouts[k] = key;
    if (Object.keys(cutLayouts).length) cur.cutLayouts = cutLayouts; else delete cur.cutLayouts;
  }
  if (Object.keys(cur).length) S.project.overrides[i] = cur; else delete S.project.overrides[i];
}

/* ---- 手法候选挑选（当前切分重抽用） ---- */
export function pickEnabledTech(g: string, opts: { n?: number; allowNone?: boolean; avoid?: string } = {}) {
  const tbl = J.registry(g) || {};
  const en = (S.project.enabled || {})[g] || {};
  let keys: string[] = J.order(g).filter((key: string) => {
    const def = tbl[key];
    if (!def || def.special) return false;
    if (en[key] === false) return false;
    return !J.randomOk || J.randomOk(S.project, g, key);
  });
  if (!keys.length) keys = J.order(g).filter((key: string) => tbl[key] && !tbl[key].special && en[key] !== false);
  if (g === 'layout' && opts.n != null) {
    const fit = keys.filter((key: string) => !J.LAYOUTS[key].fits || J.LAYOUTS[key].fits(opts.n));
    if (fit.length) keys = fit;
  }
  if (opts.allowNone && !keys.includes('none')) keys = ['none'].concat(keys);
  if (opts.avoid && keys.length > 1) keys = keys.filter((key: string) => key !== opts.avoid);
  if (!keys.length) return '';
  return keys[(Math.random() * keys.length) | 0];
}

/* ---- 歌词/时间编辑历史（Ctrl+Z，独立于外观◀▶） ---- */
const ED = {undo: [] as string[], redo: [] as string[]};
const edSnap = () => JSON.stringify({lyrics: S.project.lyrics, lineTimes: S.project.timing.lineTimes || {}});

export function pushEdit() {
  const s = edSnap();
  if (ED.undo[ED.undo.length - 1] !== s) ED.undo.push(s);
  if (ED.undo.length > 60) ED.undo.shift();
  ED.redo = [];
}

export function edGo(d: number) {
  const from = d < 0 ? ED.undo : ED.redo, to = d < 0 ? ED.redo : ED.undo;
  if (!from.length) return;
  const o = JSON.parse(from.pop()!), cur = JSON.parse(edSnap());
  if ('ov' in o) {
    cur.ov = S.project.overrides;
    cur.range = S.project.exportRange || null;
  }
  to.push(JSON.stringify(cur));
  S.project.lyrics = o.lyrics;
  S.project.timing.lineTimes = o.lineTimes;
  if ('ov' in o) {
    S.project.overrides = o.ov || {};
    S.project.exportRange = o.range || null;
  }
  replan();
  flushSave();
  bus.emit('lyrics-sync');
  toast(d < 0 ? '已撤销' : '已重做');
}

/* 换歌（网络载入新歌词）前调用：旧行号绑定的东西全部清掉，
   否则旧歌词的逐行时间/逐行覆盖/导出范围会叠到新歌上。推入撤销栈可 Ctrl+Z 找回 */
export function resetLyricBinding() {
    const P = S.project;
    if (S.exporting) return;
    const snap = JSON.parse(edSnap());
    snap.ov = P.overrides || {};
    snap.range = P.exportRange || null;
    if (!P.lyrics.trim() && !Object.keys(P.timing.lineTimes || {}).length && !Object.keys(P.overrides || {}).length && !P.exportRange) return;
    ED.undo.push(JSON.stringify(snap));
    if (ED.undo.length > 60) ED.undo.shift();
    ED.redo = [];
    P.timing.lineTimes = {};
    P.overrides = {};
    P.exportRange = null;
}

/* 清空歌词：歌词 + 一切绑定行号的东西（时间/逐行设置/导出范围）；可撤销 */
export function clearLyrics() {
  if (S.tap || S.exporting) return;
  const P = S.project;
  if (!P.lyrics.trim() && !Object.keys(P.timing.lineTimes || {}).length) {
    bus.emit('focus-lyrics');
    return;
  }
  const snap = JSON.parse(edSnap());
  snap.ov = P.overrides || {};
  snap.range = P.exportRange || null;
  ED.undo.push(JSON.stringify(snap));
  if (ED.undo.length > 60) ED.undo.shift();
  ED.redo = [];
  pause();
  P.lyrics = '';
  P.timing.lineTimes = {};
  P.overrides = {};
  P.exportRange = null;
  replan();
  flushSave();
  seek(0);
  bus.emit('lyrics-sync');
  toast('歌词已清空（可用「撤销」或 Ctrl+Z 恢复）');
}

/* ---- 导出范围（按切分：和切分循环同一粒度，而不是整行） ---- */
export function exportRangeCuts() {
    const r = S.project.exportRange, n = S.plan ? S.plan.cuts.length : 0;
  if (!r || !n || !(r.from >= 0)) return null;
  const from = Math.min(n - 1, r.from | 0), to = Math.min(n - 1, Math.max(from, r.to | 0));
  return {from, to};
}

export function exportRange() {
    const R = exportRangeCuts();
    if (!R) return null;
    const C = S.plan.cuts, a = C[R.from], b = C[R.to], last = R.to === C.length - 1;
  const t0 = Math.max(0, a.start - 0.25);
    const t1 = last ? S.plan.duration : Math.min(C[R.to + 1].start, b.end + 0.35);
  return {t0, t1};
}

export function rangeSuffix() {
    const R = exportRangeCuts();
    if (!R) return '';
  const f = (n: number) => String(n + 1).padStart(2, '0');
    return '_C' + f(R.from) + (R.to > R.from ? '-' + f(R.to) : '');
}

/* 行按钮：把这一行首尾切分设为范围（Shift 扩大；再点一次取消） */
export function setExportRange(i: number, extend: boolean) {
    const cs = S.plan.cuts.filter((c: any) => c.line === i);
    if (!cs.length) {
        toast('这一行没有切分');
        return;
    }
    const from = cs[0].index, to = cs[cs.length - 1].index;
    const R = exportRangeCuts();
    if (extend && R) S.project.exportRange = {from: Math.min(R.from, from), to: Math.max(R.to, to)};
    else if (R && R.from === from && R.to === to) S.project.exportRange = null;
    else S.project.exportRange = {from, to};
  flushSave();
  bus.emit('replan-sync');   // 行列表的重渲染
    const R2 = exportRangeCuts();
    toast(R2 ? `导出范围：切分 ${R2.from + 1}${R2.to > R2.from ? '〜' + (R2.to + 1) : ''}` : '导出范围：全部');
}

/* ---- 外观历史（◀ ▶）：只跟踪"外观"，歌词/时间/输出设置永不回滚 ---- */
const HKEYS = ['style', 'mood', 'seed', 'fx', 'enabled', 'fonts', 'colors', 'overrides', 'locks'];
const H = {list: [] as string[], i: -1};
const lookSnap = () => JSON.stringify(Object.fromEntries(HKEYS.map(k => [k, S.project[k] ?? null])));

export function remember() {          // 改外观之前调用：确保当前外观已入栈
  const s = lookSnap();
  if (H.i >= 0 && H.list[H.i] === s) return;
  H.list = H.list.slice(0, H.i + 1);
  H.list.push(s);
  H.i = H.list.length - 1;
}

export function commit() {            // 改外观之后调用
  const s = lookSnap();
  if (H.list[H.i] !== s) {
    H.list = H.list.slice(0, H.i + 1);
    H.list.push(s);
    H.i = H.list.length - 1;
  }
  if (H.list.length > 80) {
    H.list.splice(0, H.list.length - 80);
    H.i = H.list.length - 1;
  }
  bus.emit('hist');
}

export function histInfo() {
  return {i: H.i, len: H.list.length, canBack: H.i > 0, canFwd: H.i < H.list.length - 1};
}

export function histGo(d: number) {
  if (S.exporting) return;
  remember();
  const j = H.i + d;
  if (j < 0 || j >= H.list.length) return;
  H.i = j;
  Object.assign(S.project, JSON.parse(H.list[j]));
  invalidateFontKey();
  bus.emit('sync-ui');
  replan();
  bus.emit('hist');
  toast(`第 ${j + 1} / ${H.list.length} 案`);
  restartPreview();
}

export function restartPreview() {
  seek(0);
}

/* ---- 锁（随机/重抽不能碰的东西） ---- */
export function locksOf() {
  const P = S.project;
  if (!P.locks) P.locks = {tech: {}, params: {}};
  if (!P.locks.tech) P.locks.tech = {};
  if (!P.locks.params) P.locks.params = {};
  return P.locks;
}

export function lockOn(k: string) {
  return !!locksOf().params[k];
}

export function lockedEnabled() {
  const P = S.project, out: any = {};
  for (const g of Object.keys(locksOf().tech)) if (P.enabled && P.enabled[g]) out[g] = Object.assign({}, P.enabled[g]);
  return out;
}

export function restoreEnabled(keep: any) {
  const P = S.project;
  for (const g of Object.keys(keep || {})) {
    P.enabled = P.enabled || {};
    P.enabled[g] = keep[g];
  }
}

export function lockedParams() {
  const out: any = {}, L = locksOf(), F = S.project.fx;
  for (const k of Object.keys(L.params)) {
    if (!L.params[k]) continue;
    if (k === 'koma') out[k] = J.komaOf(F);
    else if (k === 'flash') out[k] = !!F.flash;
    else if (F[k] != null) out[k] = F[k];
  }
  return out;
}

export function restoreParams(keep: any) {
  const P = S.project;
  for (const k of Object.keys(keep || {})) P.fx[k] = keep[k];
}

export function toggleTechLock(g: string) {
  const L = locksOf();
  remember();
  if (L.tech[g]) {
    delete L.tech[g];
    toast('已解锁：' + g);
  } else {
    L.tech[g] = true;
    toast('已锁定：' + g);
  }
  commit();
  autosave();
  bus.emit('sync-ui');
}

export function toggleParamLock(k: string) {
  const L = locksOf();
  remember();
  if (L.params[k]) {
    delete L.params[k];
    toast('已解锁：' + k);
  } else {
    L.params[k] = true;
    toast('已锁定：' + k);
  }
  commit();
  autosave();
  bus.emit('sync-ui');
}

/* ---- 配色随机 ---- */
export function randomPalette() {
  remember();
  const c = S.project.colors;
  const sc0 = J.STYLES[S.project.style].schemes[0];
  const bg = c.enabled && c.bg ? c.bg : sc0.bg;
  let p: any, guard = 0;
  do {
    p = J.randomPalette(bg);
  } while (guard++ < 6 && p.ghostA === c.ghostA && p.ghostB === c.ghostB);
  Object.assign(c, {accent: p.accent, ghostA: p.ghostA, ghostB: p.ghostB, accentOn: true});
  bus.emit('sync-ui');
  replan();
  commit();
  toast('配色：强调色・错位色A/B 已更换', [p.accent, p.ghostA, p.ghostB]);
}

/* ---- おまかせ（一键随机） ---- */
export function omakase() {
  if (S.exporting || S.tap) return;
  remember();
  const keepE = lockedEnabled(), keepP = lockedParams();
  const r = J.omakase(S.project);
  Object.assign(S.project, r);
  restoreEnabled(keepE);
  restoreParams(keepP);
  invalidateFontKey();
  bus.emit('sync-ui');
  replan();
  commit();
  toast(`一键随机：${J.STYLES[r.style].name} × ${J.MOODS[r.mood].name}`, r.colors.accentOn ? [r.colors.accent, r.colors.ghostA, r.colors.ghostB] : undefined);
  restartPreview();
}

/* ---- TAP 打点同步 ----
   从任意行开始：播放从该行稍前开始，更早的行保持原时间 */
export function startTap(from = 0) {
  if (!S.plan.lines.length) return;
  from = J.clamp(from | 0, 0, S.plan.lines.length - 1);
  pushEdit();
  S.tap = {i: from, from, done: []};
  if (!S.project.timing.lineTimes) S.project.timing.lineTimes = {};
  const prev = from > 0 ? S.plan.lines[from - 1] : null, cur = S.plan.lines[from];
  const t0 = from === 0 ? 0 : Math.max(0, prev.start + 0.01, cur.start - 2.5);   // 比该行稍早，但不早于上一行
  seek(t0);
  play();
  bus.emit('tapstate');
  if (from > 0) toast(`从第 ${from + 1} 行开始打点同步（${J.fmtTime(t0)} 起播放）`);
}

export function tapNow() {
  if (!S.tap) return;
  const LT = S.project.timing.lineTimes, i = S.tap.i, t = +S.t.toFixed(3);
  S.tap.done.push({i, had: LT[i]});
  LT[i] = t;
  // 后面的行若在之前一轮被打过且早于本时间，清掉防止倒挂
  for (const k of Object.keys(LT)) if (+k > i && LT[k] <= t + 0.2) delete LT[k];
  S.tap.i++;
  replan();
  if (S.tap.i >= S.plan.lines.length) stopTap(); else bus.emit('tapstate');
}

export function tapBack() {   // 回退一次打点并跳回一点
  if (!S.tap || !S.tap.done.length) return;
  const d = S.tap.done.pop()!, LT = S.project.timing.lineTimes;
  if (d.had != null) LT[d.i] = d.had; else delete LT[d.i];
  S.tap.i = d.i;
  replan();
  bus.emit('tapstate');
  seek(Math.max(0, S.t - 3));
  if (!S.playing) play();
}

export function stopTap() {
  S.tap = null;
  replan();
  flushSave();
  bus.emit('tapstate');
}

/* 播放到循环终点时打断点同步（engine.tick 经 bus 通知，避免模块循环） */
bus.on('tapend', () => {
  if (S.tap) stopTap();
});

/* ---- 初始化：回到空白项目（歌曲/设置/两套历史全清） ---- */
export async function resetAll() {
  if (S.exporting) return;
  if (S.tap) stopTap();
  pause();
  const {resetAllProject} = await import('./project');
  await resetAllProject();
  ED.undo = [];
  ED.redo = [];
  H.list = [];
  H.i = -1;
  invalidateFontKey();
  bus.emit('audioname', '未载入歌曲（载入后自动检测节拍并对齐切分）');
  bus.emit('sync-ui');
  bus.emit('hist');
  replan();
  commit();
  seek(0);
  flushSave();
  bus.emit('lyrics-sync');
  toast('已初始化');
}

/* ---- 当前切分重抽 ---- */
const CHIP_GROUPS: [string, string, string][] = [
  ['layout', 'l', '布局'], ['enter', 'e', '入场'], ['hold', 'h', '保持'], ['exit', 'x', '退场'],
  ['decor', '', '装饰'], ['treat', 't', '文字处理'], ['bg', 'b', '背景'], ['cam', 'c', '运镜'], ['trans', 'c', '转场'],
];
export {CHIP_GROUPS};

export function groupName(g: string, k: string) {
  if (!k) return '无';
  const tbl = J.registry(g);
  return (tbl && tbl[k] && tbl[k].name) || k;
}

export function rerollCurrentCut(kind: 'shuffle' | 'omakase') {
  if (S.exporting || S.tap) return;
  const cut = J.cutAt(S.plan, S.t);
  const k = lyricCutK(cut);
  if (!cut || k < 0) {
    toast('这个位置的切分无法重抽');
    return;
  }
  remember();
  const n = [...String(cut.text || '').replace(/\s+/g, '')].length;
  const groups = kind === 'omakase' ? CHIP_GROUPS.map(x => x[0]) : ['layout', 'enter', 'hold', 'exit', 'cam', 'trans'];
  const t0 = S.t;
  groups.forEach(g => {
    const allowNone = g === 'decor' || g === 'trans';
    const key = pickEnabledTech(g, {avoid: kind === 'omakase' ? cutGroupVal(cut, g) : undefined, allowNone, n});
    if (key) setCutTech(cut.line, k, g, key);
  });
  markCutQuiet(cut.line, k, groups, true);
  bus.emit('close-cutpick');
  replan();
  commit();
  seek(t0);
  toast(kind === 'omakase' ? '此切分已一键随机' : '此切分已重抽');
}
