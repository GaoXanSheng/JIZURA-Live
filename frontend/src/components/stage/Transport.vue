<script lang="ts" setup>
import {computed, nextTick, onMounted, onUnmounted, ref} from 'vue';
import {useEditor} from '../../stores/editor';
import {AP, LOOP_CYCLE, refreshLoopHold, S, saveVolume} from '../../jizura/state';
import {bus} from '../../jizura/bus';
import {replan, seek, togglePlay} from '../../jizura/engine';
import {commit, histGo, histInfo, omakase, remember} from '../../jizura/look';
import {J} from '../../engine';

const ed = useEditor();
const scrubbing = ref(false);
const hist = ref(histInfo());
const offs: (() => void)[] = [];
const nowEl = ref<HTMLElement | null>(null);
const barEl = ref<HTMLElement | null>(null);
const fillEl = ref<HTMLElement | null>(null);
const thumbEl = ref<HTMLElement | null>(null);

/* 按钮文案映射（内部模式值保持与引擎一致：'line'=整行、'cut'=单个切分）：
   用户术语「切分循环」= 循环这一整行的切分 → 挂在 'line' 上；
   用户术语「行循环」= 只循环单个切分 → 挂在 'cut' 上 */
const LOOP_LABEL: Record<string, { text: string; title: string }> = {
  all: {text: '循环', title: '循环整首'},
  line: {text: '切分循环', title: '循环这一行的完整切分'},
  cut: {text: '行循环', title: '只循环单个切分'},
  off: {text: '循环', title: '不循环'},
};
const loopLabel = computed(() => LOOP_LABEL[ed.loopMode] || LOOP_LABEL.off);

function fmt(t: number) {
  return J.fmtTime(t);
}

const durText = computed(() => fmt(ed.duration));

/* 时间码/进度条不走 Vue：响应式更新会连带整页级重绘（实测 Paint 事件以
   12.5Hz 落在 document/html 根层上）。改为 frame 里命令式写 textContent
   和 transform，Vue 只管播放态/循环/音量这些低频状态 */

function cycleLoop() {
  const i = LOOP_CYCLE.indexOf(S.loop);
  S.loop = LOOP_CYCLE[(i < 0 ? 0 : i + 1) % LOOP_CYCLE.length];
  // 模式一换立即按当前位置取新范围：loopHold 只在 play/seek/replan 刷新，
  // 播放中切到切分循环会沿用行循环留下的整行范围，「切分」就一直切不开
  refreshLoopHold();
  ed.syncPlayState();
  S.need = true;
}

const vol = ref(Math.round(AP.vol * 100));
const muted = ref(AP.muted);
/* 音量条与进度条同款自绘：原生 range 的 :value 绑定在拖动时以指针频率
   触发 Vue 全量 patch（同款文档级失效）。拖动中只写 transform + 引擎音量，
   vol/muted 状态与落盘在松手时一次性提交 */
const volBarEl = ref<HTMLElement | null>(null);
const volFillEl = ref<HTMLElement | null>(null);
const volThumbEl = ref<HTMLElement | null>(null);
let volW = 80;
let volRo: ResizeObserver | null = null;
let volDragging = false;

function setVolBar(f: number) {
  f = Math.min(1, Math.max(0, f));
  if (volFillEl.value) volFillEl.value.style.transform = `scaleX(${f})`;
  if (volThumbEl.value) volThumbEl.value.style.transform = `translateX(${(volW - 12) * f}px)`;
}

function volApply(clientX: number) {
  const r = volBarEl.value!.getBoundingClientRect();
  const f = Math.min(1, Math.max(0, (clientX - r.left) / r.width));
  AP.setVol(f, false);
  setVolBar(f);
}

function volDown(e: PointerEvent) {
  (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
  volDragging = true;
  if (muted.value) muted.value = false;   // 拖动即解除消音（一次渲染）
  volApply(e.clientX);
}

function volMove(e: PointerEvent) {
  if (volDragging) volApply(e.clientX);
}

function volUp() {
  if (!volDragging) return;
  volDragging = false;
  vol.value = Math.round(AP.vol * 100);
  saveVolume();
}

function volKey(e: KeyboardEvent) {
  const cur = Math.round(AP.vol * 100);
  let v: number | null = null;
  if (e.key === 'ArrowRight' || e.key === 'ArrowUp') v = Math.min(100, cur + 5);
  else if (e.key === 'ArrowLeft' || e.key === 'ArrowDown') v = Math.max(0, cur - 5);
  else if (e.key === 'Home') v = 0;
  else if (e.key === 'End') v = 100;
  if (v == null) return;
  e.preventDefault();
  AP.setVol(v / 100, false);
  muted.value = false;
  vol.value = v;
  setVolBar(v / 100);
  saveVolume();
}

function toggleMute() {
  AP.setVol(null, !AP.muted);
  muted.value = AP.muted;
  saveVolume();
}

function onHist() {
  hist.value = histInfo();
}

function doShuffle() {
  remember();
  S.project.seed = (Math.random() * 1e9) | 0;
  bus.emit('sync-ui');
  replan();
  commit();
}

onMounted(() => {
  offs.push(bus.on('hist', onHist));
  offs.push(bus.on('sync-ui', onHist));
  offs.push(bus.on('frame', () => {
    const now = performance.now();
    if (nowEl.value && now - lastSync >= 80) {
      lastSync = now;
      nowEl.value.textContent = fmt(S.t);
    }
    if (!scrubbing.value && now - lastScrub >= 80) {
      lastScrub = now;
      setBar(fracOf());
    }
  }));
  if (nowEl.value) nowEl.value.textContent = fmt(S.t);
  onHist();
  nextTick(() => setBar(fracOf()));
});
let lastSync = 0;
let lastScrub = 0;

/* ---- 自绘进度条：transform 走合成器。原生 range 的 value 写入会触发
   Chromium 文档级失效（contain/will-change/分层均无法隔离，实测 Paint 以
   全视口 clip 落在 document/html/nav 三层、12.5Hz 闪烁全页），故弃用 ---- */
const barW = ref(0);
let barRo: ResizeObserver | null = null;

function fracOf() {
  const dur = S.plan ? S.plan.duration : 0;
  return dur > 0 ? Math.min(1, Math.max(0, S.t / dur)) : 0;
}

function setBar(f: number) {
  if (fillEl.value) fillEl.value.style.transform = `scaleX(${f})`;
  if (thumbEl.value) thumbEl.value.style.transform = `translateX(${(barW.value - 12) * f}px)`;
  if (barEl.value && Math.round(f * 10000) !== Number(barEl.value.getAttribute('aria-valuenow'))) barEl.value.setAttribute('aria-valuenow', String(Math.round(f * 10000)));
}

function barSeek(ev: { clientX: number }) {
  const r = barEl.value!.getBoundingClientRect();
  const f = Math.min(1, Math.max(0, (ev.clientX - r.left) / r.width));
  const dur = S.plan ? S.plan.duration : 0;
  if (dur > 0) seek(f * dur);
  setBar(f);   // 拖拽期间 frame 刷新被 scrubbing 挡住，必须这里立即跟手
}

function barDown(e: PointerEvent) {
  (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
  scrubbing.value = true;
  barSeek(e);
}

function barMove(e: PointerEvent) {
  if (scrubbing.value) barSeek(e);
}

function barUp() {
  scrubbing.value = false;
}

function barKey(e: KeyboardEvent) {
  const step = e.shiftKey ? 5 : 1;
  if (e.key === 'ArrowRight') {
    e.preventDefault();
    seek(S.t + step);
  } else if (e.key === 'ArrowLeft') {
    e.preventDefault();
    seek(S.t - step);
  } else if (e.key === 'Home') {
    e.preventDefault();
    seek(0);
  } else if (e.key === 'End') {
    e.preventDefault();
    seek((S.plan ? S.plan.duration : 0) - 0.001);
  }
}

onMounted(() => {
  barRo = new ResizeObserver(() => {
    barW.value = barEl.value ? barEl.value.clientWidth : 0;
    setBar(fracOf());
  });
  if (barEl.value) barRo.observe(barEl.value);
  volRo = new ResizeObserver(() => {
    if (volBarEl.value) {
      volW = volBarEl.value.clientWidth;
      setVolBar(AP.vol);
    }
  });
  if (volBarEl.value) volRo.observe(volBarEl.value);
});
onUnmounted(() => {
  barRo?.disconnect();
  volRo?.disconnect();
});
onUnmounted(() => offs.forEach(f => f()));
</script>

<template>
  <div class="flex items-center gap-2.5 flex-wrap">
    <button :aria-label="ed.playing ? '暂停' : '播放'" class="btn !w-11 !h-[34px] !p-0 text-[13px]" @click="togglePlay">
      {{ ed.playing ? '❚❚' : '▶' }}
    </button>
    <span ref="nowEl" class="font-mono font-medium text-[13px] tabular-nums">0:00.0</span><span
      class="font-mono text-[13px] text-mut">/</span><span
      class="font-mono text-[13px] text-mut tabular-nums">{{ durText }}</span>
    <div ref="barEl" aria-label="播放位置"
         aria-valuemax="10000" aria-valuemin="0"
         aria-valuenow="0" class="relative flex-[1_1_160px] h-[26px] flex items-center cursor-pointer select-none"
         role="slider" tabindex="0"
         @keydown="barKey" @pointercancel="barUp" @pointerdown="barDown" @pointermove="barMove" @pointerup="barUp">
      <div class="absolute inset-x-0 top-1/2 -translate-y-1/2 h-[5px] rounded bg-line overflow-hidden">
        <div ref="fillEl" class="h-full w-full bg-amber"
             style="transform: scaleX(0); transform-origin: left center;"></div>
      </div>
      <div ref="thumbEl"
           class="absolute left-0 top-1/2 -mt-[6px] w-3 h-3 rounded-full bg-amber shadow-[0_0_4px_rgba(0,0,0,0.6)]"
           style="transform: translateX(0); will-change: transform;"></div>
    </div>
    <button :aria-pressed="ed.loopMode !== 'off'"
            :class="[ed.loopMode === 'off' ? 'opacity-55' : (ed.loopMode !== 'all' ? '!text-amber' : '')]"
            :title="loopLabel.title" class="btn btn-ghost btn-sm" @click="cycleLoop">{{ loopLabel.text }}
    </button>
    <span class="inline-flex items-center gap-1">
      <button :aria-pressed="muted" :class="muted ? '!text-cyan' : ''" class="btn btn-ghost btn-sm"
              title="点击消音／解除（仅预览，导出音量不变）" @click="toggleMute">音量</button>
      <div ref="volBarEl" :aria-valuenow="vol" aria-label="预览音量"
           aria-valuemax="100"
           aria-valuemin="0" class="relative w-20 h-[26px] flex items-center cursor-pointer select-none" role="slider"
           tabindex="0"
           @keydown="volKey" @pointercancel="volUp" @pointerdown="volDown" @pointermove="volMove" @pointerup="volUp">
        <div class="absolute inset-x-0 top-1/2 -translate-y-1/2 h-[5px] rounded bg-line overflow-hidden">
          <div ref="volFillEl" class="h-full w-full bg-amber" style="transform-origin: left center;"></div>
        </div>
        <div ref="volThumbEl"
             class="absolute left-0 top-1/2 -mt-[6px] w-3 h-3 rounded-full bg-amber shadow-[0_0_4px_rgba(0,0,0,0.6)]"
             style="will-change: transform;"></div>
      </div>
    </span>
    <span class="inline-flex gap-1">
      <button :disabled="!hist.canBack" aria-label="上一案" class="btn btn-ghost btn-sm !text-[10px] min-w-7"
              title="回到上一案" @click="histGo(-1)">◀</button>
      <button :disabled="!hist.canFwd" aria-label="下一案" class="btn btn-ghost btn-sm !text-[10px] min-w-7"
              title="前往下一案" @click="histGo(1)">▶</button>
    </span>
    <button class="btn btn-accent" title="设置不变，仅构成重新抽选（锁定的行保留）" @click="doShuffle">重抽构成</button>
    <button class="btn btn-primary" title="风格・氛围・演出・配色整体随机（快捷键 R）" @click="omakase()">一键随机
    </button>
  </div>
</template>
