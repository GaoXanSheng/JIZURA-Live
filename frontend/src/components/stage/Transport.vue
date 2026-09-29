<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { useEditor } from '../../stores/editor';
import { S, AP, LOOP_CYCLE, saveVolume } from '../../jizura/state';
import { bus } from '../../jizura/bus';
import { play, pause, seek, togglePlay, replan } from '../../jizura/engine';
import { remember, commit, omakase, histGo, histInfo } from '../../jizura/look';
import { J } from '../../engine';

const ed = useEditor();
const scrubbing = ref(false);
const hist = ref(histInfo());
const offs: (() => void)[] = [];

const LOOP_LABEL: Record<string, { text: string; title: string }> = {
  all: { text: '循环', title: '循环整首' },
  line: { text: '行循环', title: '循环这一行' },
  cut: { text: '切分循环', title: '循环这一切分' },
  off: { text: '循环', title: '不循环' },
};
const loopLabel = computed(() => LOOP_LABEL[ed.loopMode] || LOOP_LABEL.off);

function fmt(t: number) { return J.fmtTime(t); }
const nowText = computed(() => fmt(ed.t));
const durText = computed(() => fmt(ed.duration));
const scrubVal = computed(() => Math.round(ed.t / Math.max(0.001, ed.duration) * 10000));

function onScrub(e: Event) {
  const v = +(e.target as HTMLInputElement).value;
  scrubbing.value = true;
  seek(v / 10000 * ed.duration);
}
function onScrubEnd() { scrubbing.value = false; }

function cycleLoop() {
  const i = LOOP_CYCLE.indexOf(S.loop);
  S.loop = LOOP_CYCLE[(i < 0 ? 0 : i + 1) % LOOP_CYCLE.length];
  ed.syncPlayState();
  S.need = true;
}

const vol = ref(Math.round(AP.vol * 100));
const muted = ref(AP.muted);
function onVol(e: Event) { vol.value = +(e.target as HTMLInputElement).value; AP.setVol(vol.value / 100, false); muted.value = false; saveVolume(); }
function toggleMute() { AP.setVol(null, !AP.muted); muted.value = AP.muted; saveVolume(); }

function onHist() { hist.value = histInfo(); }
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
  onHist();
});
onUnmounted(() => offs.forEach(f => f()));
</script>

<template>
  <div class="flex items-center gap-2.5 flex-wrap">
    <button class="btn !w-11 !h-[34px] !p-0 text-[13px]" :aria-label="ed.playing ? '暂停' : '播放'" @click="togglePlay">{{ ed.playing ? '❚❚' : '▶' }}</button>
    <span class="font-mono font-medium text-[13px] tabular-nums">{{ nowText }}</span><span class="font-mono text-[13px] text-mut">/</span><span class="font-mono text-[13px] text-mut tabular-nums">{{ durText }}</span>
    <input class="flex-[1_1_160px]" type="range" min="0" max="10000" :value="scrubVal" aria-label="播放位置" @input="onScrub" @change="onScrubEnd">
    <button class="btn btn-ghost btn-sm" :class="[ed.loopMode === 'off' ? 'opacity-55' : (ed.loopMode !== 'all' ? '!text-amber' : '')]"
            :aria-pressed="ed.loopMode !== 'off'" :title="loopLabel.title" @click="cycleLoop">{{ loopLabel.text }}</button>
    <span class="inline-flex items-center gap-1">
      <button class="btn btn-ghost btn-sm" :class="muted ? '!text-cyan' : ''" :aria-pressed="muted" title="点击消音／解除（仅预览，导出音量不变）" @click="toggleMute">音量</button>
      <input type="range" min="0" max="100" :value="vol" class="w-20" aria-label="预览音量" @input="onVol">
    </span>
    <span class="inline-flex gap-1">
      <button class="btn btn-ghost btn-sm !text-[10px] min-w-7" :disabled="!hist.canBack" title="回到上一案" aria-label="上一案" @click="histGo(-1)">◀</button>
      <button class="btn btn-ghost btn-sm !text-[10px] min-w-7" :disabled="!hist.canFwd" title="前往下一案" aria-label="下一案" @click="histGo(1)">▶</button>
    </span>
    <button class="btn btn-accent" title="设置不变，仅构成重新抽选（锁定的行保留）" @click="doShuffle">重抽构成</button>
    <button class="btn btn-primary" title="风格・氛围・演出・配色整体随机（快捷键 R）" @click="omakase()">一键随机</button>
  </div>
</template>
