<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { J } from '../../engine';
import { S } from '../../jizura/state';
import { bus } from '../../jizura/bus';
import { loadAudioFile, replan, replanSoon } from '../../jizura/engine';
import { startTap, stopTap } from '../../jizura/look';
import { useEditor } from '../../stores/editor';
import { flushSave } from '../../jizura/project';

const ed = useEditor();
const audioName = ref('未载入歌曲（载入后自动检测节拍并对齐切分）');
const fileInput = ref<HTMLInputElement | null>(null);
const tapActive = ref(false);
const bpm = ref(''), offset = ref(''), lineScale = ref('');
const snap = ref(false);
const offs: (() => void)[] = [];

const bpmPlaceholder = computed(() => (ed.hasAudio ? `自动 ${ed.audioBpm}` : '无'));

onMounted(() => {
  offs.push(bus.on('audioname', (n: string) => { audioName.value = n; }));
  offs.push(bus.on('replan-sync', syncFields));
  offs.push(bus.on('sync-ui', syncFields));
  offs.push(bus.on('tapstate', () => { tapActive.value = !!S.tap; }));
  offs.push(bus.on('replan', syncFields));
  syncFields();
});
onUnmounted(() => offs.forEach(f => f()));

function syncFields() {
  const T = S.project?.timing || {};
  bpm.value = T.bpm > 0 ? String(T.bpm) : '';
  offset.value = String(T.offset ?? 0.4);
  lineScale.value = String(T.lineScale ?? 1);
  snap.value = !!T.snap;
}

function onFile(e: Event) {
  const f = (e.target as HTMLInputElement).files?.[0];
  if (f) loadAudioFile(f);
}

function onBpm() { S.project.timing.bpm = Math.max(0, parseFloat(bpm.value) || 0); replan(); }
function onOffset() { S.project.timing.offset = Math.max(0, parseFloat(offset.value) || 0); replan(); }
function onLineScale() { S.project.timing.lineScale = J.clamp(parseFloat(lineScale.value) || 1, 0.3, 4); replan(); }
function onSnap() { S.project.timing.snap = snap.value; replan(); }
function onResetTimes() { S.project.timing.lineTimes = {}; replan(); }
function onTapBtn() { S.tap ? stopTap() : startTap(0); }
</script>

<template>
  <div class="sec">
    <div class="sec-h"><h2>歌曲与节拍</h2></div>
    <div class="audio-name muted">{{ audioName }}</div>
    <div class="row">
      <label class="file" title="mp3 / wav / m4a 等">载入歌曲<input ref="fileInput" type="file" accept="audio/*,.mp3,.wav,.m4a,.aac,.ogg,.flac" @change="onFile"></label>
      <button :aria-pressed="tapActive" title="边播放边在每行开头打点" @click="onTapBtn">打点同步</button>
    </div>
    <div class="fields pro-only">
      <label class="field">BPM<input v-model="bpm" type="number" min="0" max="300" step="0.1" :placeholder="bpmPlaceholder" @change="onBpm"></label>
      <label class="field">开始(秒)<input v-model="offset" type="number" min="0" step="0.05" @change="onOffset"></label>
      <label class="field">行长度<input v-model="lineScale" type="number" min="0.4" max="3" step="0.05" @change="onLineScale"></label>
    </div>
    <div class="row pro-only">
      <label class="row" style="gap: 6px"><input v-model="snap" type="checkbox" @change="onSnap"> 对齐节拍</label>
      <button class="ghost small" @click="onResetTimes">清除手动时间</button>
    </div>
  </div>
</template>
