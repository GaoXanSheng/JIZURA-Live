/* 引擎驱动层：replan / 渲染循环 / 播放控制 / 字体预热 / 音频装载。
   原 12_ui.js 的对应逻辑，DOM 操作替换为 bus 事件通知。 */
import { J } from '../engine';
import { analyzeAudioOffThread } from './audio-analyze';
import { S, AP, refreshLoopHold, activeLoopRange } from './state';
import { autosave, flushSave } from './project';
import { bus } from './bus';

const HUD_CHARS = '0123456789:./-_()【】・No.LYRICRECUNTITLEDXYlinebpminterlude—─／ ';

export const fixedMq = window.matchMedia ? window.matchMedia('(min-width: 1181px) and (min-height: 620px)') : null;
document.documentElement.classList.add('fixed-ok');
export function fixedLayout() { return !!(fixedMq && fixedMq.matches) && document.documentElement.classList.contains('fixed-ok'); }

/* ---- 视图挂载（StageCanvas 组件调用） ---- */
let viewEl: HTMLElement | null = null;
let viewCanvas: HTMLCanvasElement | null = null;
export function attachView(el: HTMLElement, canvas: HTMLCanvasElement) { viewEl = el; viewCanvas = canvas; S.need = true; }

export function audioLike() {
  const T = S.project.timing;
  if (S.audio) {
    const a = Object.assign({}, S.audio);
    if (T.bpm > 0) a.beats = J.beatGrid(T.bpm, T.beatOffset || 0, S.audio.duration);
    return a;
  }
  if (T.bpm > 0) return { beats: J.beatGrid(T.bpm, T.beatOffset || 0, 600) };
  return null;
}

export function replan() {
  S.plan = J.plan(S.project, audioLike());
  // 旧项目锁定的行（只有 seed）：现在拍快照，从今往后保持原样
  for (const [i, o] of Object.entries(S.project.overrides || {})) {
    const ov: any = o;
    if (ov && ov.lock && !Array.isArray(ov.lockedCuts)) { const snap = J.lineSnapshot(S.plan, +i); if (snap) ov.lockedCuts = snap; }
  }
  if (S.t > S.plan.duration) S.t = 0;
  refreshLoopHold();
  lastCutIdx = -2;
  S.need = true;
  autosave();
  ensureFonts();
  bus.emit('replan');
  clearTimeout(warmTimer); warmTimer = setTimeout(warm, 450);
}

let replanTimer = 0;
export const replanSoon = (ms = 220) => { clearTimeout(replanTimer); replanTimer = setTimeout(replan, ms); };

/* 预热：空闲时预分解 piece 动画要用的字形，播放不卡顿 */
let warmTimer = 0, warmJob = 0;
function warm() {
  const job = ++warmJob;
  const cuts = S.plan.cuts.filter((c: any) => c.enter === 'assemble' || ['explode', 'fall', 'drift'].includes(c.exit));
  if (!viewCanvas) return;
  const cv = document.createElement('canvas'); cv.width = viewCanvas.width; cv.height = viewCanvas.height;
  const ctx = cv.getContext('2d')!;
  let i = 0;
  const idle = (window as any).requestIdleCallback ? (f: any) => (window as any).requestIdleCallback(f, { timeout: 400 }) : (f: any) => setTimeout(() => f(null), 40);
  const step = (deadline: any) => {
    if (job !== warmJob || S.exporting) return;
    do {
      const c = cuts[i++]; if (!c) break;
      const ts: number[] = [];
      if (c.enter === 'assemble') ts.push(c.start + Math.min(c.inDur * 0.3, c.dur * 0.2));
      if (c.outDur > 0) ts.push(c.end - c.outDur * 0.5);
      for (const t of ts) { try { S.renderer.frame(ctx, S.plan, t, { scale: cv.width / S.plan.W, fast: true, noHud: true, noGhost: true }); } catch {} }
    } while (i < cuts.length && deadline && deadline.timeRemaining() > 10);
    if (i < cuts.length) idle(step);
  };
  idle(step);
}

let fontKey = '';
let thumbFonts = false;
export function invalidateFontKey() { fontKey = ''; }
export async function ensureFonts() {
  const txt = S.project.lyrics + (S.project.title || '') + (S.project.artist || '') + HUD_CHARS;
  const keys = J.fontsOfPlan(S.plan);
  const key = txt + '|' + keys.join(',') + '|' + Object.keys(J.FONTS).length + '|' + J.lang;
  if (key === fontKey) return;
  fontKey = key;
  showMsg('正在加载字体…');
  try { await J.ensureFonts(txt, keys); } catch {}
  showMsg(null); S.need = true; bus.emit('fonts');
  loadThumbFonts();
}
/* 风格缩略图需要每个风格 display 字体的两个字形 — 面板可见时才拉取 */
export function loadThumbFonts() {
  if (thumbFonts) return;
  const visible = document.getElementById('styleGrid');
  if (!visible || !visible.offsetParent) return;
  thumbFonts = true;
  J.ensureFonts('字面', [...new Set(J.STYLE_ORDER.map((k: string) => J.STYLES[k].fonts.display[0]))])
    .then(() => bus.emit('fonts')).catch(() => {});
}

export function showMsg(m: string | null) { bus.emit('msg', m); }

/* ---- 视口尺寸 ---- */
export function sizeViewport() {
  if (!viewEl || !viewCanvas || !S.plan) return;
  const vp = viewEl, c = viewCanvas;
  const ar = S.plan.W / S.plan.H;
  let cssW = vp.clientWidth || 800, cssH = cssW / ar;
  const maxH = fixedLayout() ? Math.max(160, vp.clientHeight - 2) : Math.max(220, window.innerHeight * 0.68);
  if (cssH > maxH) { cssH = maxH; cssW = cssH * ar; }
  const dpr = Math.min(2, window.devicePixelRatio || 1);
  const pw = Math.round(Math.min(S.plan.W, cssW * dpr)), ph = Math.round(pw / ar);
  if (c.width !== pw || c.height !== ph) { c.width = pw; c.height = ph; }
  c.style.width = cssW + 'px'; c.style.height = cssH + 'px';
  S.need = true;
}

/* 中央を空ける的预览参考线（仅预览，导出不含） */
function drawCenterGuide(ctx: CanvasRenderingContext2D, k: number) {
  const P = S.plan, z = P.zones; if (!z || z.length < 2) return;
  const pos = S.project.centerZone || 'center';
  // 留白矩形：单侧留白 = 文字带之外的那条边；中央 = 两条文字带之间的区域
  let r: number[];
  if (pos === 'left') r = [0, 0, z[0].x, P.H];
  else if (pos === 'right') r = [z[0].x + z[0].w, 0, P.W - z[0].x - z[0].w, P.H];
  else if (pos === 'top') r = [0, 0, P.W, z[0].y];
  else if (pos === 'bottom') r = [0, z[0].y + z[0].h, P.W, P.H - z[0].y - z[0].h];
  else if (P.W >= P.H) { const left = z[0].x + z[0].w, right = z[1].x; r = [left, 0, Math.max(2, right - left), P.H]; }
  else { const top = z[0].y + z[0].h, bottom = z[1].y; r = [0, top, P.W, Math.max(2, bottom - top)]; }
  ctx.save(); ctx.setTransform(k, 0, 0, k, 0, 0);
  ctx.setLineDash([14, 10]); ctx.lineWidth = 2 / k * 1.5; ctx.strokeStyle = 'rgba(255,255,255,0.55)';
  ctx.strokeRect(r[0] + 4, r[1] + 4, r[2] - 8, r[3] - 8);
  ctx.setLineDash([]); ctx.restore();
}

export function draw() {
  if (!viewCanvas || !S.plan) return;
  const c = viewCanvas, ctx = c.getContext('2d')!;
  const t0 = performance.now();
  S.renderer.frame(ctx, S.plan, S.t, { scale: c.width / S.plan.W, fast: S.playing && S.slow, transparent: !!window.__jl_spout_transparent });
  if (S.plan.centerFree && !window.__jl_spout_mirror) drawCenterGuide(ctx, c.width / S.plan.W);
  const dt = performance.now() - t0;
  S.slow = S.playing ? (dt > 30 ? true : dt < 14 ? false : S.slow) : false;
  updateCutInfo();
  bus.emit('frame');
}

export function tick(now: number) {
  requestAnimationFrame(tick);
  if (S.exporting) return;
  if (S.playing) {
    // rAF 时间戳可能早于 play()/seek() 打的 t0 → clamp 防负数
    let t = Math.max(0, S.audio ? AP.time() : (now - S.t0) / 1000);
    const range = activeLoopRange();
    if (t >= range.end - 1e-3) {
      if (S.loop && !S.tap) { seek(range.start); t = range.start; }
      else { pause(); t = Math.min(t, S.plan.duration - 1e-3); if (S.tap) bus.emit('tapend'); }
    }
    S.t = t; S.need = true;
    bus.emit('playhead');
  }
  if (S.need) { S.need = false; draw(); }
}

/* ---- 播放控制 ---- */
export function updateTime() { bus.emit('time'); }

export function play() {
  refreshLoopHold();
  if (S.audio) AP.play(S.audio.buffer, S.t);
  else S.t0 = performance.now() - S.t * 1000;
  S.playing = true;
  bus.emit('playstate');
}
export function pause() {
  S.playing = false; AP.stop();
  S.need = true;
  bus.emit('playstate');
}
export function seek(t: number) {
  S.t = J.clamp(t, 0, Math.max(0, S.plan.duration - 1e-3));
  if (S.audio) { if (S.playing) AP.play(S.audio.buffer, S.t); }
  else S.t0 = performance.now() - S.t * 1000;
  refreshLoopHold();
  S.need = true;
  updateCutInfo();
  bus.emit('playhead');
}
export function togglePlay() { S.playing ? pause() : play(); }

/* ---- 当前切分信息（原 updateCutInfo 的非 DOM 部分） ---- */
let lastCutIdx = -2;
export function currentCut() { return J.cutAt(S.plan, S.t); }
export function updateCutInfo() {
  if (!S.plan) return;
  const cut = J.cutAt(S.plan, S.t);
  const idx = cut ? cut.index : -1;
  const li = cut ? cut.line : -1;
  if (li !== S.curLine) { S.curLine = li; followLine(li); }
  if (idx === lastCutIdx) return;
  lastCutIdx = idx;
  bus.emit('cutchange', cut);
}

/* ---- 播放行跟随滚动（组件通过 bus 监听） ---- */
let listTouched = 0;
export function touchList() { listTouched = performance.now(); }
function followLine(li: number) {
  bus.emit('curline', li);
  if (!S.playing || li < 0 || !fixedLayout()) return;
  if (performance.now() - listTouched < 2500) return;
  bus.emit('scrolltoline', li);
}

/* ---- 音频装载 ---- */
let audioSeq = 0;
export async function loadAudioFile(f: File, restored = false) {
  const my = ++audioSeq;   // 只有最新一次选择生效（更早的慢分析直接丢弃）
  bus.emit('audioname', '解析中…');
  try {
    pause();
    const a = await analyzeAudioOffThread(f);   // 数值计算在 Worker：主线程不卡
    if (my !== audioSeq) return false;
    S.audio = a;
    bus.emit('audioname', `${f.name}（${J.fmtTime(S.audio.duration)}・约${S.audio.bpm}BPM）` + (restored ? '・上次的歌曲' : ''));
    S.project.audioName = f.name;
    if (!restored && J.saveSong) J.saveSong(f);   // 存进浏览器：刷新后导出不会丢歌
    S.project.timing.snap = true;
    bus.emit('replan-sync');                      // 让面板上的 BPM 占位等刷新
    replan();
    return true;
  } catch (err: any) {
    if (my !== audioSeq) return false;
    bus.emit('audioname', '无法读取：' + err.message);
    S.audio = null;
    return false;
  }
}

/* ---- 启动 ---- */
export function bootEngine() {
  requestAnimationFrame(tick);
  window.addEventListener('resize', () => { sizeViewport(); bus.emit('viewresize'); });
}
