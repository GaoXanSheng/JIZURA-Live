/* 引擎驱动层：replan / 渲染循环 / 播放控制 / 字体预热 / 音频装载。
   原 12_ui.js 的对应逻辑，DOM 操作替换为 bus 事件通知。 */
import {J} from '../engine';
import {analyzeAudioOffThread} from './audio-analyze';
import {activeLoopRange, AP, refreshLoopHold, S} from './state';
import {autosave} from './project';
import {bus} from './bus';

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

/* ---- 临近 morph 档位预热 ----
   seek/play 后，播放头前方 3 秒内的第一个 morph 切分：后台把它的全部 e 档位
   位图提前画好（idle 分档让出）。循环/行循环反复经过同一 morph 时首圈即命中；
   线性播放时每次进入新 morph 前 ~0.5s 就绪。miss 时回退现绘（同旧路径成本）。 */
let nearWarmTimer = 0, nearWarmJob = 0;

function warmupNearMorphs() {
  clearTimeout(nearWarmTimer);
  nearWarmTimer = setTimeout(() => {
    if (!S.plan || !viewCanvas) return;
    const c = S.plan.cuts.find((c: any) => c.morph && c.index > 0 && S.plan.cuts[c.index - 1] && c.start >= S.t - 0.05 && c.start - S.t < 3);
    if (!c) return;
    const job = ++nearWarmJob;
    const A = S.plan.cuts[c.index - 1];
    const cw = viewCanvas.width, ch = viewCanvas.height, scale = cw / S.plan.W;
    const idle = (window as any).requestIdleCallback ? (f: any) => (window as any).requestIdleCallback(f, {timeout: 200}) : (f: any) => setTimeout(() => f(null), 20);
    let q = 0;
    const step = (deadline: any) => {
      if (job !== nearWarmJob || S.exporting) return;
      try {
        const L = S.renderer.morphLogs(S.plan, A, c, cw, ch, scale, {});
        while (q <= 9 && (!deadline || deadline.timeRemaining() > 4)) {
          S.renderer.morphShotBuild(S.plan, A, c, cw, ch, scale, q / 9, true, L);
          q++;
        }
      } catch { /* 兜底 */
      }
      if (q <= 9) idle(step);
    };
    idle(step);
  }, 60);
}

/* 预热：空闲时预分解 piece 动画要用的字形 + morph 帧位图（全部 e 档位），
   播放/镜像不卡顿。morph 缓存是首次播放卡顿的大头：morphLogs 递归两整帧 +
   每个档位的逐字重绘，正好落在播放里。
   播放中不跑全局预热：新 plan 下的冷渲染（morphLogs 递归两整帧 30-80ms、
   逐字光栅化）作为 idle 块落地时会直接顶掉播放帧预算（实测切换特效后 rAF
   p50 26.7ms→最大 386ms）。播放时只留 warmupNearMorphs 的预算化预热，
   全局预热挪到 pause() 补跑。 */
let warmTimer = 0, warmJob = 0;
function warm() {
  if (S.playing || S.exporting) return;   // 播放/导出中：全局预热让路，pause() 里补跑
  const job = ++warmJob;
  const cuts = S.plan.cuts.filter((c: any) => c.enter === 'assemble' || ['explode', 'fall', 'drift'].includes(c.exit));
  const morphs = S.plan.cuts.filter((c: any) => c.morph && c.index > 0 && S.plan.cuts[c.index - 1]);
  if (!viewCanvas) return;
  const cv = document.createElement('canvas'); cv.width = viewCanvas.width; cv.height = viewCanvas.height;
  const ctx = cv.getContext('2d')!;
  const scale = cv.width / S.plan.W;
  let i = 0, mi = 0;
  const idle = (window as any).requestIdleCallback ? (f: any) => (window as any).requestIdleCallback(f, { timeout: 400 }) : (f: any) => setTimeout(() => f(null), 40);
  const step = (deadline: any) => {
    if (job !== warmJob || S.exporting) return;
    // 字体没就绪就预热的话，位图里嵌的是 fallback 字形且不会自己失效——等就绪
    if (morphs.length && document.fonts && document.fonts.status !== 'loaded') {
      idle(step);
      return;
    }
    do {
      if (mi < morphs.length) {
        const c = morphs[mi++];
        // 只预热 morphLogs（字形起止）：首次递归渲染两整帧+字形光栅化是首播大卡。
        // 位图档位不在这里全量预热（LRU 装不下全歌档位），由 warmupNearMorphs 按需填
        try {
          S.renderer.morphLogs(S.plan, S.plan.cuts[c.index - 1], c, cv.width, cv.height, scale, {});
        } catch { /* 兜底 */
        }
      } else {
        const c = cuts[i++];
        if (!c) break;
        const ts: number[] = [];
        if (c.enter === 'assemble') ts.push(c.start + Math.min(c.inDur * 0.3, c.dur * 0.2));
        if (c.outDur > 0) ts.push(c.end - c.outDur * 0.5);
        for (const t of ts) {
          try {
            S.renderer.frame(ctx, S.plan, t, {scale: cv.width / S.plan.W, fast: true, noHud: true, noGhost: true});
          } catch {
          }
        }
      }
    } while ((mi < morphs.length || i < cuts.length) && deadline && deadline.timeRemaining() > 10);
    if (mi < morphs.length || i < cuts.length) idle(step);
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
  // 字体就绪前若做过渲染/预热，产物里嵌的是 fallback 字形——morph 缓存作废重建
  S.renderer.morphCache = null;
  S.renderer.morphShotCache = null;
  showMsg(null); S.need = true; bus.emit('fonts');
  clearTimeout(warmTimer);
  warmTimer = setTimeout(warm, 120);   // 新字体下重跑预热
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
export function play() {
  refreshLoopHold();
  if (S.audio) AP.play(S.audio.buffer, S.t);
  else S.t0 = performance.now() - S.t * 1000;
  S.playing = true;
  warmupNearMorphs();   // 即将经过的 morph 档位提前备好，首播不现绘
  bus.emit('playstate');
}
export function pause() {
  S.playing = false; AP.stop();
  S.need = true;
  // 播放期间被搁置的全局预热现在补跑（replan 触发的那次已在 warm() 里让路跳过）
  clearTimeout(warmTimer);
  warmTimer = setTimeout(warm, 250);
  bus.emit('playstate');
}
export function seek(t: number) {
  S.t = J.clamp(t, 0, Math.max(0, S.plan.duration - 1e-3));
  if (S.audio) { if (S.playing) AP.play(S.audio.buffer, S.t); }
  else S.t0 = performance.now() - S.t * 1000;
  refreshLoopHold();
  S.need = true;
  updateCutInfo();
  warmupNearMorphs();
  bus.emit('playhead');
}
export function togglePlay() { S.playing ? pause() : play(); }

/* ---- 当前切分信息（原 updateCutInfo 的非 DOM 部分） ---- */
let lastCutIdx = -2;
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
