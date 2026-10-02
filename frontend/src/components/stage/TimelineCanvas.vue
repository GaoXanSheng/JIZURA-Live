<script lang="ts" setup>
// 三块独立 canvas，各画各的、可以单独探查：
// 上条视图滑动（缩放后平移可视窗口），中条播放头（自由定位、不吸附），
// 下条是时间轴本体（波形/节拍/切分块/行界拖动手柄/缩放范围）。
// 早先三块挤在同一个 canvas 的三条横带里，调试得靠像素坐标去抠横带。
import {onMounted, onUnmounted, ref} from 'vue';
import {J} from '../../engine';
import {activeLoopRange, S} from '../../jizura/state';
import {bus} from '../../jizura/bus';
import {beginScrub, endScrub, pause, replan, seek} from '../../jizura/engine';
import {edGo, exportRange, flushSave, pushEdit} from '../../jizura/look';

const canvas = ref<HTMLCanvasElement | null>(null);     // 下条：时间轴本体
const panCanvas = ref<HTMLCanvasElement | null>(null);  // 上条：视图滑动
const phCanvas = ref<HTMLCanvasElement | null>(null);   // 中条：播放头
const offs: (() => void)[] = [];

const layoutHue = (k: string) => (J.LAYOUT_ORDER.indexOf(k) * 37 + 30) % 360;
type Hover = number;
const TL: { z: number; off: number; hover: Hover; drag: number } = {z: 1, off: 0, hover: -1, drag: -1};

// 尺寸缓存：tlFrame 每帧 ×3 块 canvas 读 clientWidth/Height 会强制重排
// （CPU 剖面里 get clientWidth 占 774ms）。CSS 尺寸只在 resize 时变，读一次缓存
let dimsDirty = true;
const dimCache = new WeakMap<HTMLCanvasElement, { w: number; h: number; dpr: number }>();

function fitCanvas(cv: HTMLCanvasElement): { w: number; h: number; dpr: number } {
  const cached = dimCache.get(cv);
  if (!dimsDirty && cached) return cached;
  const dpr = Math.min(2, window.devicePixelRatio || 1);
  const w = Math.max(10, Math.round(cv.clientWidth * dpr)), h = Math.max(10, Math.round(cv.clientHeight * dpr));
  if (cv.width !== w || cv.height !== h) {
    cv.width = w;
    cv.height = h;
  }
  const out = {w, h, dpr};
  dimCache.set(cv, out);
  dimsDirty = false;
  return out;
}

function tlView() {
  const D = Math.max(0.001, S.plan.duration), vd = D / TL.z;
  TL.off = J.clamp(TL.off, 0, Math.max(0, D - vd));
  return {D, vd, off: TL.off};
}

function tlZoom(f: number, tc?: number) {
  const {D, vd} = tlView();
  const z = J.clamp(TL.z * f, 1, Math.max(1, D / 2));
  const c = tc == null ? S.t : tc, frac = (c - TL.off) / vd;
  TL.z = z;
  TL.off = c - frac * (D / z);
  tlView();
  drawTimeline();
}

// 放大播放时播放头出可视区就重绘，视图跟着走
function followTlPlayhead() {
  if (!(TL.z > 1) || TL.drag >= 0) return;
  const {vd, off} = tlView();
  if (S.t < off || S.t > off + vd * 0.92) drawTimeline();
}

function tlTimeOf(ev: { clientX: number }, r: DOMRect) {
  const {vd, off} = tlView();
  return off + J.clamp((ev.clientX - r.left) / r.width, 0, 1) * vd;
}

function timelineSeek(ev: { clientX: number }) {
  seek(tlTimeOf(ev, phCanvas.value!.getBoundingClientRect()));
}

function drawPanBar() {
  const c = panCanvas.value;
  if (!c || !S.plan) return;
  const {w, h, dpr} = fitCanvas(c);
  const x = c.getContext('2d')!;
  const {D, vd, off} = tlView();
  x.fillStyle = '#131316';
  x.fillRect(0, 0, w, h);
  x.fillStyle = 'rgba(255,255,255,0.045)';
  x.fillRect(0, 0, w, h);
  if (TL.z > 1) {
    x.fillStyle = 'rgba(245,165,12,0.45)';
    x.fillRect(off / D * w, dpr * 1.5, Math.max(6 * dpr, vd / D * w), h - 3 * dpr);
  } else {
    x.fillStyle = 'rgba(255,255,255,0.09)';
    x.fillRect(0, dpr * 1.5, w, h - 3 * dpr);
  }
}

let panDrag = false, panLastX = 0;

function onPanDown(e: PointerEvent) {
  try {
    panCanvas.value!.setPointerCapture(e.pointerId);
  } catch { // 合成事件/无效指针，不影响后续
  }
  panDrag = true;
  panLastX = e.clientX;
  // 音量条语义：点哪，视窗块中心瞬移到哪，之后块跟手 1:1 拖
  const r = panCanvas.value!.getBoundingClientRect();
  const frac = J.clamp((e.clientX - r.left) / r.width, 0, 1);
  const {D, vd} = tlView();
  TL.off = frac * D - vd / 2;
  tlView();
  drawTimeline();
  panCanvas.value!.style.cursor = 'grabbing';
}

function onPanMove(e: PointerEvent) {
  if (!panDrag) return;
  const r = panCanvas.value!.getBoundingClientRect(), {D} = tlView();
  // 滚动条语义：握住视窗块直接拖，块跟手 1:1。拖内容语义是 off -= dx，块会反向走
  TL.off += (e.clientX - panLastX) / r.width * D;
  panLastX = e.clientX;
  tlView();
  drawTimeline();
}

function onPanUp() {
  panDrag = false;
  panCanvas.value!.style.cursor = 'grab';
}

function onPanWheel(e: WheelEvent) {
  e.preventDefault();
  const {vd} = tlView();
  if (e.shiftKey || Math.abs(e.deltaX) > Math.abs(e.deltaY)) {
    TL.off += (e.shiftKey ? e.deltaY : e.deltaX) / panCanvas.value!.clientWidth * vd;
    tlView();
    drawTimeline();
  } else tlZoom(Math.exp(-e.deltaY * (e.ctrlKey ? 0.01 : 0.0025)));
}

function drawPhBar() {
  const c = phCanvas.value;
  if (!c || !S.plan) return;
  const {w, h, dpr} = fitCanvas(c);
  const x = c.getContext('2d')!;
  const {vd, off} = tlView();
  x.fillStyle = '#131316';
  x.fillRect(0, 0, w, h);
  x.fillStyle = 'rgba(255,255,255,0.02)';
  x.fillRect(0, 0, w, h);
  x.fillStyle = 'rgba(255,255,255,0.07)';
  x.fillRect(0, h / 2 - dpr / 2, w, dpr);
  const px = Math.round((S.t - off) / vd * w);
  x.fillStyle = '#f5a50c';
  x.fillRect(px - dpr, 0, 2 * dpr, h);
  x.beginPath();
  x.moveTo(px - 5 * dpr, 0);
  x.lineTo(px + 5 * dpr, 0);
  x.lineTo(px, h * 0.55);
  x.closePath();
  x.fill();
}

let phDrag = false;

function onPhDown(e: PointerEvent) {
  try {
    phCanvas.value!.setPointerCapture(e.pointerId);
  } catch { // 合成事件/无效指针，不影响后续
  }
  phDrag = true;
  beginScrub();   // 拖动中音频静默跟随，松手恢复
  timelineSeek(e);
}

function onPhMove(e: PointerEvent) {
  if (phDrag) timelineSeek(e);
}

function onPhUp() {
  phDrag = false;
  endScrub();
}

// 静态层缓存：波形/节拍/切分/行界很少变，播放中每帧只叠播放头线；
// 视图或内容一变（缩放/平移/拖动/replan/导出范围/循环模式/换音频）就整幅重建
let tlStatic: HTMLCanvasElement | null = null;
let tlStaticKey = '';

function tlCacheKey(w: number, h: number, off: number, vd: number): string {
  const R = exportRange();
  const loopR = S.loop === 'line' || S.loop === 'cut' ? activeLoopRange() : null;
  return [w, h, off.toFixed(3), vd.toFixed(3), TL.z, TL.drag, TL.hover, S.loop,
    R ? R.t0.toFixed(2) + '~' + R.t1.toFixed(2) : '',
    loopR ? loopR.start.toFixed(2) + '~' + loopR.end.toFixed(2) : '',
    S.audio ? S.audio.duration.toFixed(2) : '',
    S.plan.cuts.length, S.plan.lines.length].join('|');
}

function drawPlayheadLine(x: CanvasRenderingContext2D, w: number, h: number, dpr: number, off: number, vd: number) {
  const px = Math.round((S.t - off) / vd * w);
  x.fillStyle = '#f5a50c';
  x.fillRect(px - dpr, 0, 2 * dpr, h);
}

function drawTimeline() {
  const c = canvas.value;
  if (!c || !S.plan) return;
  const {w, h, dpr} = fitCanvas(c);
  let {D, vd, off} = tlView();
  if (TL.z > 1 && S.playing && TL.drag < 0 && (S.t < off || S.t > off + vd * 0.92)) {
    TL.off = S.t - vd * 0.1;
    ({D, vd, off} = tlView());
  }
  if (!tlStatic || tlStatic.width !== w || tlStatic.height !== h) {
    tlStatic = document.createElement('canvas');
    tlStatic.width = w;
    tlStatic.height = h;
  }
  drawStatic(tlStatic.getContext('2d')!, w, h, dpr, vd, off, D);
  tlStaticKey = tlCacheKey(w, h, off, vd);
  const x = c.getContext('2d')!;
  x.drawImage(tlStatic, 0, 0);
  drawPlayheadLine(x, w, h, dpr, off, vd);
  drawPanBar();
}

/* 播放帧：静态层命中就只叠加播放头线；视图被自动跟随挪动或缓存失效才整幅重画 */
function tlFrame() {
  const c = canvas.value;
  if (!c || !S.plan) return;
  const {w, h, dpr} = fitCanvas(c);
  let {vd, off} = tlView();
  if (TL.z > 1 && S.playing && TL.drag < 0 && (S.t < off || S.t > off + vd * 0.92)) {
    drawTimeline();
    drawPhBar();
    return;
  }
  if (!tlStatic || tlStaticKey !== tlCacheKey(w, h, off, vd)) {
    drawTimeline();
    drawPhBar();
    return;
  }
  const x = c.getContext('2d')!;
  x.drawImage(tlStatic, 0, 0);
  drawPlayheadLine(x, w, h, dpr, off, vd);
  drawPhBar();
}

function drawStatic(x: CanvasRenderingContext2D, w: number, h: number, dpr: number, vd: number, off: number, D: number) {
  const X = (t: number) => (t - off) / vd * w, T = (px: number) => off + px / w * vd;
  x.fillStyle = '#131316';
  x.fillRect(0, 0, w, h);
  // 行界（最上带）：刻度线 + 三角手柄 + 行号
  const mono = getComputedStyle(document.body).getPropertyValue('--mono') || 'monospace';
  const top = 16 * dpr, bot = h - 8 * dpr;
  const LT: Record<string, number> = S.project.timing.lineTimes || {};
  x.font = `${10 * dpr}px ${mono}`;
  for (const ln of S.plan.lines) {
    const lx = X(ln.start);
    if (lx < -20 * dpr || lx > w + 2) continue;
    const on = ln.index === TL.drag || ln.index === TL.hover, man = LT[ln.index] != null;
    x.fillStyle = on ? '#f5a50c' : man ? '#6fb7c8' : '#5d5a63';
    x.fillRect(lx - (on ? dpr : 0), 0, on ? 2 * dpr : 1, top);
    x.beginPath();
    x.moveTo(lx - 5 * dpr, 0);
    x.lineTo(lx + 5 * dpr, 0);
    x.lineTo(lx, 7 * dpr);
    x.closePath();
    x.fill();
    x.fillStyle = on ? '#f5a50c' : '#8e8a94';
    x.fillText(String(ln.index + 1).padStart(2, '0') + (ln.interlude ? ' 间奏' : ''), lx + 4 * dpr, 14 * dpr);
  }
  // 波形（垫底）
  if (S.audio && S.audio.peaks) {
    const pk: Float32Array = S.audio.peaks, n = pk.length, sd = S.audio.duration;
    x.fillStyle = '#2b2b33';
    for (let i = 0; i < w; i += 2) {
      const t = T(i);
      if (t > sd) break;
      if (t < 0) continue;
      const v = pk[Math.min(n - 1, Math.floor(t / sd * n))];
      const hh = v * (bot - top) * 0.85;
      x.fillRect(i, top + (bot - top) / 2 - hh / 2, 1.5, hh);
    }
  }
  // 节拍刻度（底缘）
  const beats: number[] = S.plan.beats || [];
  x.fillStyle = '#3a3a44';
  for (const b of beats) {
    if (b < off) continue;
    if (b > off + vd) break;
    x.fillRect(Math.round(X(b)), h - 6 * dpr, 1, 6 * dpr);
  }
  // 导出范围底色 + 切分块
  const R = exportRange();
  if (R) {
    x.fillStyle = 'rgba(245,165,12,0.10)';
    x.fillRect(X(R.t0), 0, X(R.t1) - X(R.t0), h);
  }
  for (const cut of S.plan.cuts) {
    const x0 = X(cut.start), x1 = X(cut.end);
    if (x1 < 0 || x0 > w) continue;
    const hue = layoutHue(cut.layout);
    x.fillStyle = `hsla(${hue},70%,58%,0.28)`;
    x.fillRect(x0, top, Math.max(1, x1 - x0 - 1), bot - top);
    x.fillStyle = `hsla(${hue},80%,62%,0.95)`;
    x.fillRect(x0, top, Math.max(1, 2 * dpr), bot - top);
    if (x1 - x0 > 34 * dpr) {
      x.fillStyle = 'rgba(236,231,225,0.85)';
      x.font = `${10 * dpr}px ${mono}`;
      x.save();
      x.beginPath();
      x.rect(x0, top, x1 - x0 - 3, bot - top);
      x.clip();
      x.fillText(TL.z > 3 && cut.text ? cut.text : ((J.LAYOUTS[cut.layout] || {}).name || cut.layout), x0 + 5 * dpr, top + 13 * dpr);
      x.restore();
    }
  }
  if (TL.z > 1) {
    x.fillStyle = 'rgba(255,255,255,0.18)';
    x.fillRect(0, h - 2 * dpr, w, 2 * dpr);
    x.fillStyle = '#f5a50c';
    x.fillRect(off / D * w, h - 2 * dpr, Math.max(4, vd / D * w), 2 * dpr);
  }
  if (S.loop === 'line' || S.loop === 'cut') {
    const r = activeLoopRange();
    x.fillStyle = 'rgba(245,165,12,0.16)';
    x.fillRect(X(r.start), 0, Math.max(2 * dpr, X(r.end) - X(r.start)), h);
  }
}

// 指针下的行界手柄
function tlHandleAt(ev: { clientX: number; clientY: number }) {
  const c = canvas.value!, r = c.getBoundingClientRect(), {vd, off} = tlView();
  if (ev.clientY - r.top > r.height * 0.4) return -1;
  let best = -1, bd = 8;
  for (const ln of S.plan.lines) {
    const d = Math.abs((ln.start - off) / vd * r.width - (ev.clientX - r.left));
    if (d < bd) {
      bd = d;
      best = ln.index;
    }
  }
  return best;
}

function tlDragTo(i: number, ev: { clientX: number; shiftKey: boolean }) {
  let t = tlTimeOf(ev, canvas.value!.getBoundingClientRect());
  if (!ev.shiftKey && S.plan.beats && S.plan.beats.length) {   // 吸附最近节拍（Shift：自由）
    let bt: number | null = null, bd = 0.12;
    for (const b of S.plan.beats) {
      const d = Math.abs(b - t);
      if (d < bd) {
        bd = d;
        bt = b;
      }
      if (b > t + 0.2) break;
    }
    if (bt != null) t = bt;
  }
  const L = S.plan.lines, lo = i > 0 ? L[i - 1].start + 0.2 : 0,
      hi = i < L.length - 1 ? L[i + 1].start - 0.2 : S.plan.duration - 0.2;
  t = +J.clamp(t, lo, Math.max(lo, hi)).toFixed(3);
  if (!S.project.timing.lineTimes) S.project.timing.lineTimes = {};
  // 一并钉住相邻行界，移动一个不会带动后面的行
  L.forEach((ln: any) => {
    if (S.project.timing.lineTimes[ln.index] == null) S.project.timing.lineTimes[ln.index] = +ln.start.toFixed(3);
  });
  S.project.timing.lineTimes[i] = t;
  replan();
  seek(t + 0.001);
}

let scrubDrag = false, dragRaf = 0;

function onDown(e: PointerEvent) {
  const c = canvas.value!;
  try {
    c.setPointerCapture(e.pointerId);
  } catch { // 合成事件/无效指针，不影响后续
  }
  if (!(S.tap || S.exporting)) {
    const h = tlHandleAt(e);
    if (h >= 0) {
      pushEdit();
      TL.drag = h;
      pause();
      c.style.cursor = 'ew-resize';
      return;
    }
  }
  scrubDrag = true;
  timelineSeek(e);
}

function onMove(e: PointerEvent) {
  if (TL.drag >= 0) {
    const ev = {clientX: e.clientX, clientY: e.clientY, shiftKey: e.shiftKey};
    cancelAnimationFrame(dragRaf);
    dragRaf = requestAnimationFrame(() => tlDragTo(TL.drag, ev));
    return;
  }
  if (scrubDrag) {
    timelineSeek(e);
    return;
  }
  const hv: Hover = !(S.tap || S.exporting) ? tlHandleAt(e) : -1;
  if (hv !== TL.hover) {
    TL.hover = hv;
    canvas.value!.style.cursor = hv >= 0 ? 'ew-resize' : 'pointer';
    drawTimeline();
  }
}

function endDrag() {
  if (TL.drag >= 0) {
    TL.drag = -1;
    flushSave();
    bus.emit('replan-sync');
    drawTimeline();
  }
  scrubDrag = false;
}

function onLeave() {
  if (TL.hover !== -1 && TL.drag < 0) {
    TL.hover = -1;
    drawTimeline();
  }
}

function onWheel(e: WheelEvent) {
  e.preventDefault();
  const {vd} = tlView();
  if (e.shiftKey || Math.abs(e.deltaX) > Math.abs(e.deltaY)) {
    TL.off += (e.shiftKey ? e.deltaY : e.deltaX) / canvas.value!.clientWidth * vd;
    tlView();
    drawTimeline();
  } else tlZoom(Math.exp(-e.deltaY * (e.ctrlKey ? 0.01 : 0.0025)), tlTimeOf(e, canvas.value!.getBoundingClientRect()));
}

function undoEdit() {
  edGo(-1);
}

onMounted(() => {
  offs.push(bus.on('frame', tlFrame));
  offs.push(bus.on('playhead', followTlPlayhead));
  offs.push(bus.on('replan', drawTimeline));
  offs.push(bus.on('replan-sync', drawTimeline));
  offs.push(bus.on('viewresize', () => {
    dimsDirty = true;
    drawTimeline();
  }));
  const onWinResize = () => {
    dimsDirty = true;
    drawTimeline();
  };
  window.addEventListener('resize', onWinResize);
  offs.push(() => window.removeEventListener('resize', onWinResize));
  drawTimeline();
  drawPhBar();
});
onUnmounted(() => {
  endScrub();   // 拖动中卸载：不兜底会永久停掉播放推进与音频
  offs.forEach(f => f());
});
</script>

<template>
  <div class="flex justify-between items-center gap-2 my-2 mb-1 flex-wrap">
    <span class="text-[11px] text-mut">上条左右滑视图，中条拖动播放头；行界刻度可拖动，<span class="kbd">Shift</span> 不吸附节拍；底部黄条为缩放范围</span>
    <span class="flex gap-1 ml-auto">
      <button class="btn btn-ghost btn-sm" title="撤销歌词・时间修改（Ctrl+Z）" @click="undoEdit">撤销</button>
      <button aria-label="缩小时间轴" class="btn btn-ghost btn-sm min-w-7" title="缩小"
              @click="tlZoom(1 / 1.6)">−</button>
      <button aria-label="放大时间轴" class="btn btn-ghost btn-sm min-w-7" title="放大（时间轴上滚轮亦可）"
              @click="tlZoom(1.6)">＋</button>
      <button class="btn btn-ghost btn-sm" title="显示整首" @click="TL.z = 1; TL.off = 0; drawTimeline()">全部</button>
    </span>
  </div>
  <div class="flex flex-col">
    <canvas
        ref="panCanvas"
        aria-label="视图滑动条（左右拖动平移）"
        class="w-full h-[16px] block bg-panel cursor-grab touch-none"
        @pointercancel="onPanUp"
        @pointerdown="onPanDown"
        @pointermove="onPanMove"
        @pointerup="onPanUp"
        @wheel="onPanWheel"
    ></canvas>
    <canvas
        ref="phCanvas"
        aria-label="播放头条（拖动定位）"
        class="w-full h-[20px] block bg-panel cursor-ew-resize touch-none"
        @pointercancel="onPhUp"
        @pointerdown="onPhDown"
        @pointermove="onPhMove"
        @pointerup="onPhUp"
    ></canvas>
    <canvas
        ref="canvas"
        aria-label="时间轴（点击移动・行界可拖动调整）"
        class="w-full h-[60px] block bg-panel border border-line cursor-pointer touch-none"
        @pointercancel="endDrag"
        @pointerdown="onDown"
        @pointerleave="onLeave"
        @pointermove="onMove"
        @pointerup="endDrag"
        @wheel="onWheel"
    ></canvas>
  </div>
</template>
