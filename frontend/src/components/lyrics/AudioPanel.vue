<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { J } from '../../engine';
import { S } from '../../jizura/state';
import { bus } from '../../jizura/bus';
import { loadAudioFile, replan } from '../../jizura/engine';
import { startTap, stopTap } from '../../jizura/look';
import { useEditor } from '../../stores/editor';
import { useCloud } from '../../stores/cloud';
import { adoptLocalKrc } from '../../cloud/lyricStore';

const ed = useEditor();
const cloud = useCloud();
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
  // 整页拖拽：.krc → 歌词
  window.addEventListener('dragover', onDragOver);
  window.addEventListener('drop', onDrop);
  syncFields();
});
onUnmounted(() => {
  offs.forEach(f => f());
  window.removeEventListener('dragover', onDragOver);
  window.removeEventListener('drop', onDrop);
});

function syncFields() {
  const T = S.project?.timing || {};
  bpm.value = T.bpm > 0 ? String(T.bpm) : '';
  offset.value = String(T.offset ?? 0.4);
  lineScale.value = String(T.lineScale ?? 1);
  snap.value = !!T.snap;
}

function onDragOver(e: DragEvent) {
  if (e.dataTransfer && [...e.dataTransfer.types].includes('Files')) e.preventDefault();
}
async function onDrop(e: DragEvent) {
  const f = e.dataTransfer?.files?.[0];
  if (!f) return;
  if (f.name.toLowerCase().endsWith('.krc')) {
    e.preventDefault();
    try { await adoptLocalKrc(f); } catch (err: any) { alert('krc 解析失败：' + (err.message || err)); }
  }
}

async function onFile(e: Event) {
  const inp = e.target as HTMLInputElement;
  const f = inp.files?.[0];
  if (!f) return;
  if (f.name.toLowerCase().endsWith('.krc')) {
    try { await adoptLocalKrc(f); } catch (err: any) { alert('krc 解析失败：' + (err.message || err)); }
    return;
  }
  await loadAudioFile(f);
}

function onBpm() { S.project.timing.bpm = Math.max(0, parseFloat(bpm.value) || 0); replan(); }
function onOffset() { S.project.timing.offset = Math.max(0, parseFloat(offset.value) || 0); replan(); }
function onLineScale() { S.project.timing.lineScale = J.clamp(parseFloat(lineScale.value) || 1, 0.3, 4); replan(); }
function onSnap() { S.project.timing.snap = snap.value; replan(); }
function onResetTimes() { S.project.timing.lineTimes = {}; replan(); }
function onTapBtn() { S.tap ? stopTap() : startTap(0); }
function openCloud() { cloud.open = true; }
</script>

<template>
  <div class="sec">
    <div class="sec-h"><h2>歌曲与节拍</h2></div>
    <div class="audio-name muted">{{ audioName }}</div>
    <div class="row">
      <label class="file" title="mp3 / wav / m4a 等，也支持 .krc 歌词文件">载入歌曲<input ref="fileInput" type="file" accept="audio/*,.mp3,.wav,.m4a,.aac,.ogg,.flac,.krc" @change="onFile"></label>
      <button :aria-pressed="tapActive" title="边播放边在每行开头打点" @click="onTapBtn">打点同步</button>
      <button class="cloud-btn" title="在线搜索 / 我的歌单 / 扫码登录（酷狗・网易云）" @click="openCloud">☁ 云端音乐</button>
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

<style scoped>
.cloud-btn { border-color: var(--amber-line); color: var(--amber); background: transparent; }
.cloud-btn:hover { background: var(--amber-soft); border-color: var(--amber); }
</style>

