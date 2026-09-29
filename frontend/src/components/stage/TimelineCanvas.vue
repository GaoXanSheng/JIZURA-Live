<script setup lang="ts">
/* 时间轴（原 12_ui.js timeline 体系）：音频波形 + 节拍 + 切分块 + 行界拖动手柄
   缩放/平移（滚轮）、点击定位、拖动行界（Shift 自由，默认吸附节拍） */
import { onMounted, onUnmounted, ref } from 'vue';
import { J } from '../../engine';
import { S, activeLoopRange } from '../../jizura/state';
import { bus } from '../../jizura/bus';
import { seek, pause, replan } from '../../jizura/engine';
import { pushEdit, flushSave, exportRangeLines, exportRange, edGo } from '../../jizura/look';

const canvas = ref<HTMLCanvasElement | null>(null);
const offs: (() => void)[] = [];

const layoutHue = (k: string) => (J.LAYOUT_ORDER.indexOf(k) * 37 + 30) % 360;
const TL = { z: 1, off: 0, hover: -1, drag: -1 };

function tlView() {
  const D = Math.max(0.001, S.plan.duration), vd = D / TL.z;
  TL.off = J.clamp(TL.off, 0, Math.max(0, D - vd));
  return { D, vd, off: TL.off };
}
function tlZoom(f: number, tc?: number) {
  const { D, vd } = tlView();
  const z = J.clamp(TL.z * f, 1, Math.max(1, D / 2));
  const c = tc == null ? S.t : tc, frac = (c - TL.off) / vd;
  TL.z = z; TL.off = c - frac * (D / z); tlView(); drawTimeline();
}
/* 放大播放时，播放头离开可视区就重绘（视图跟随） */
function followTlPlayhead() {
  if (!(TL.z > 1) || TL.drag >= 0) return;
  const { vd, off } = tlView();
  if (S.t < off || S.t > off + vd * 0.92) drawTimeline();
}

function drawTimeline() {
  const c = canvas.value; if (!c || !S.plan) return;
  const dpr = Math.min(2, window.devicePixelRatio || 1);
  const w = Math.max(10, Math.round(c.clientWidth * dpr)), h = Math.max(10, Math.round(c.clientHeight * dpr));
  if (c.width !== w || c.height !== h) { c.width = w; c.height = h; }
  let { D, vd, off } = tlView();
  if (TL.z > 1 && S.playing && TL.drag < 0 && (S.t < off || S.t > off + vd * 0.92)) { TL.off = S.t - vd * 0.1; ({ D, vd, off } = tlView()); }
  const x = c.getContext('2d')!;
  const X = (t: number) => (t - off) / vd * w, T = (px: number) => off + px / w * vd;
  x.fillStyle = '#131316'; x.fillRect(0, 0, w, h);
  if (S.audio && S.audio.peaks) {
    const pk: Float32Array = S.audio.peaks, n = pk.length, sd = S.audio.duration;
    x.fillStyle = '#2b2b33';
    for (let i = 0; i < w; i += 2) { const t = T(i); if (t > sd) break; if (t < 0) continue; const v = pk[Math.min(n - 1, Math.floor(t / sd * n))]; const hh = v * h * 0.8; x.fillRect(i, h * 0.6 - hh / 2, 1.5, hh); }
  }
  const beats: number[] = S.plan.beats || [];
  x.fillStyle = '#3a3a44';
  for (const b of beats) { if (b < off) continue; if (b > off + vd) break; x.fillRect(Math.round(X(b)), h - 6 * dpr, 1, 6 * dpr); }
  const top = h * 0.3, bot = h - 8 * dpr;
  const R = exportRange();
  if (R) { x.fillStyle = 'rgba(245,165,12,0.10)'; x.fillRect(X(R.t0), 0, X(R.t1) - X(R.t0), h); }
  for (const cut of S.plan.cuts) {
    const x0 = X(cut.start), x1 = X(cut.end);
    if (x1 < 0 || x0 > w) continue;
    const hue = layoutHue(cut.layout);
    x.fillStyle = `hsla(${hue},70%,58%,0.28)`; x.fillRect(x0, top, Math.max(1, x1 - x0 - 1), bot - top);
    x.fillStyle = `hsla(${hue},80%,62%,0.95)`; x.fillRect(x0, top, Math.max(1, 2 * dpr), bot - top);
    if (x1 - x0 > 34 * dpr) {
      x.fillStyle = 'rgba(236,231,225,0.85)'; x.font = `${10 * dpr}px ${getComputedStyle(document.body).getPropertyValue('--mono') || 'monospace'}`;
      x.save(); x.beginPath(); x.rect(x0, top, x1 - x0 - 3, bot - top); x.clip();
      x.fillText(TL.z > 3 && cut.text ? cut.text : ((J.LAYOUTS[cut.layout] || {}).name || cut.layout), x0 + 5 * dpr, top + 13 * dpr); x.restore();
    }
  }
  x.font = `${10 * dpr}px monospace`;
  const LT: Record<string, number> = S.project.timing.lineTimes || {};
  for (const ln of S.plan.lines) {
    const lx = X(ln.start);
    if (lx < -20 * dpr || lx > w + 2) continue;
    const on = ln.index === TL.drag || ln.index === TL.hover, man = LT[ln.index] != null;
    x.fillStyle = on ? '#f5a50c' : man ? '#6fb7c8' : '#5d5a63'; x.fillRect(lx - (on ? dpr : 0), 0, on ? 2 * dpr : 1, top);
    x.beginPath(); x.moveTo(lx - 5 * dpr, 0); x.lineTo(lx + 5 * dpr, 0); x.lineTo(lx, 7 * dpr); x.closePath(); x.fill();
    x.fillStyle = on ? '#f5a50c' : '#8e8a94'; x.fillText(String(ln.index + 1).padStart(2, '0') + (ln.interlude ? ' 间奏' : ''), lx + 4 * dpr, 17 * dpr);
  }
  if (TL.z > 1) {
    x.fillStyle = 'rgba(255,255,255,0.18)'; x.fillRect(0, h - 2 * dpr, w, 2 * dpr);
    x.fillStyle = '#f5a50c'; x.fillRect(off / D * w, h - 2 * dpr, Math.max(4, vd / D * w), 2 * dpr);
  }
  if (S.loop === 'line' || S.loop === 'cut') {
    const r = activeLoopRange();
    x.fillStyle = 'rgba(245,165,12,0.16)';
    x.fillRect(X(r.start), 0, Math.max(2 * dpr, X(r.end) - X(r.start)), h);
  }
  const px = X(S.t);
  x.fillStyle = '#f5a50c'; x.fillRect(Math.round(px) - dpr, 0, 2 * dpr, h);
}

function tlTime(ev: { clientX: number }) {
  const r = canvas.value!.getBoundingClientRect(), { vd, off } = tlView();
  return off + J.clamp((ev.clientX - r.left) / r.width, 0, 1) * vd;
}
function timelineSeek(ev: { clientX: number }) { seek(tlTime(ev)); }
/* 指针下的行界手柄（顶带，±7px） */
function tlHandleAt(ev: { clientX: number; clientY: number }) {
  const c = canvas.value!, r = c.getBoundingClientRect(), { vd, off } = tlView();
  if (ev.clientY - r.top > r.height * 0.45) return -1;
  let best = -1, bd = 8;
  for (const ln of S.plan.lines) { const d = Math.abs((ln.start - off) / vd * r.width - (ev.clientX - r.left)); if (d < bd) { bd = d; best = ln.index; } }
  return best;
}
function tlDragTo(i: number, ev: { clientX: number; shiftKey: boolean }) {
  let t = tlTime(ev);
  if (!ev.shiftKey && S.plan.beats && S.plan.beats.length) {   // 吸附最近节拍（Shift：自由）
    let bt: number | null = null, bd = 0.12;
    for (const b of S.plan.beats) { const d = Math.abs(b - t); if (d < bd) { bd = d; bt = b; } if (b > t + 0.2) break; }
    if (bt != null) t = bt;
  }
  const L = S.plan.lines, lo = i > 0 ? L[i - 1].start + 0.2 : 0, hi = i < L.length - 1 ? L[i + 1].start - 0.2 : S.plan.duration - 0.2;
  t = +J.clamp(t, lo, Math.max(lo, hi)).toFixed(3);
  if (!S.project.timing.lineTimes) S.project.timing.lineTimes = {};
  // 一并钉住相邻行界，移动一个不会带动后面的行
  L.forEach((ln: any) => { if (S.project.timing.lineTimes[ln.index] == null) S.project.timing.lineTimes[ln.index] = +ln.start.toFixed(3); });
  S.project.timing.lineTimes[i] = t;
  replan(); seek(t + 0.001);
}

/* ---- 指针交互 ---- */
let scrubDrag = false, dragRaf = 0;
function onDown(e: PointerEvent) {
  const c = canvas.value!; c.setPointerCapture(e.pointerId);
  const h = S.tap || S.exporting ? -1 : tlHandleAt(e);
  if (h >= 0) { pushEdit(); TL.drag = h; pause(); c.style.cursor = 'ew-resize'; return; }
  scrubDrag = true; timelineSeek(e);
}
function onMove(e: PointerEvent) {
  if (TL.drag >= 0) { const ev = { clientX: e.clientX, clientY: e.clientY, shiftKey: e.shiftKey }; cancelAnimationFrame(dragRaf); dragRaf = requestAnimationFrame(() => tlDragTo(TL.drag, ev)); return; }
  if (scrubDrag) { timelineSeek(e); return; }
  const hv = tlHandleAt(e);
  if (hv !== TL.hover) { TL.hover = hv; canvas.value!.style.cursor = hv >= 0 ? 'ew-resize' : 'pointer'; drawTimeline(); }
}
function endDrag() {
  if (TL.drag >= 0) { TL.drag = -1; flushSave(); bus.emit('replan-sync'); drawTimeline(); }
  scrubDrag = false;
}
function onLeave() { if (TL.hover >= 0 && TL.drag < 0) { TL.hover = -1; drawTimeline(); } }
function onWheel(e: WheelEvent) {
  e.preventDefault();
  const { vd } = tlView();
  if (e.shiftKey || Math.abs(e.deltaX) > Math.abs(e.deltaY)) { TL.off += (e.shiftKey ? e.deltaY : e.deltaX) / canvas.value!.clientWidth * vd; tlView(); drawTimeline(); }
  else tlZoom(Math.exp(-e.deltaY * (e.ctrlKey ? 0.01 : 0.0025)), tlTime(e));
}

function undoEdit() { edGo(-1); }

onMounted(() => {
  offs.push(bus.on('frame', drawTimeline));
  offs.push(bus.on('playhead', followTlPlayhead));
  offs.push(bus.on('replan', drawTimeline));
  offs.push(bus.on('replan-sync', drawTimeline));
  offs.push(bus.on('viewresize', drawTimeline));
  drawTimeline();
});
onUnmounted(() => offs.forEach(f => f()));
</script>

<template>
  <div class="flex justify-between items-center gap-2 my-2 mb-1 flex-wrap">
    <span class="text-[11px] text-mut">行界（上排刻度）可拖动。<span class="kbd">Shift</span> 不吸附节拍</span>
    <span class="flex gap-1 ml-auto">
      <button class="ghost small" title="撤销歌词・时间修改（Ctrl+Z）" @click="undoEdit">撤销</button>
      <button class="ghost small icon-txt" title="缩小" aria-label="缩小时间轴" @click="tlZoom(1 / 1.6)">−</button>
      <button class="ghost small icon-txt" title="放大（时间轴上滚轮亦可）" aria-label="放大时间轴" @click="tlZoom(1.6)">＋</button>
      <button class="ghost small" title="显示整首" @click="TL.z = 1; TL.off = 0; drawTimeline()">全部</button>
    </span>
  </div>
  <canvas
    ref="canvas"
    class="w-full h-[76px] block bg-panel border border-line rounded-md cursor-pointer touch-none"
    aria-label="时间轴（点击移动・行界可拖动调整）"
    @pointerdown="onDown"
    @pointermove="onMove"
    @pointerup="endDrag"
    @pointercancel="endDrag"
    @pointerleave="onLeave"
    @wheel="onWheel"
  ></canvas>
</template>
