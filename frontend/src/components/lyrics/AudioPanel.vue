<script lang="ts" setup>
import {computed, onMounted, onUnmounted, ref} from 'vue';
import {J} from '../../engine';
import {S} from '../../jizura/state';
import {bus} from '../../jizura/bus';
import {loadAudioFile, replan} from '../../jizura/engine';
import {startTap, stopTap} from '../../jizura/look';
import {useEditor} from '../../stores/editor';
import {useCloud} from '../../stores/cloud';
import {adoptLocalKrc} from '../../cloud/lyricStore';

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
  offs.push(bus.on('audioname', (n: string) => {
    audioName.value = n;
  }));
  offs.push(bus.on('replan-sync', syncFields));
  offs.push(bus.on('sync-ui', syncFields));
  offs.push(bus.on('tapstate', () => {
    tapActive.value = !!S.tap;
  }));
  offs.push(bus.on('replan', syncFields));
  // 整页拖拽：.krc 文件当歌词导入
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
    try {
      await adoptLocalKrc(f);
    } catch (err: any) {
      alert('krc 解析失败：' + (err.message || err));
    }
  }
}

async function onFile(e: Event) {
  const f = (e.target as HTMLInputElement).files?.[0];
  if (!f) return;
  if (f.name.toLowerCase().endsWith('.krc')) {
    try {
      await adoptLocalKrc(f);
    } catch (err: any) {
      alert('krc 解析失败：' + (err.message || err));
    }
    return;
  }
  await loadAudioFile(f);
}

function onBpm() {
  S.project.timing.bpm = Math.max(0, parseFloat(bpm.value) || 0);
  replan();
}

function onOffset() {
  S.project.timing.offset = Math.max(0, parseFloat(offset.value) || 0);
  replan();
}

function onLineScale() {
  S.project.timing.lineScale = J.clamp(parseFloat(lineScale.value) || 1, 0.3, 4);
  replan();
}

function onSnap() {
  S.project.timing.snap = snap.value;
  replan();
}

function onResetTimes() {
  S.project.timing.lineTimes = {};
  replan();
}

function onTapBtn() {
  S.tap ? stopTap() : startTap(0);
}

function openCloud() {
  cloud.open = true;
}
</script>

<template>
  <div class="sec">
    <div class="sec-h"><span class="sec-title">歌曲与节拍</span></div>
    <div class="text-xs break-all text-mut">{{ audioName }}</div>
    <div class="row">
      <label class="btn-file" title="mp3 / wav / m4a 等，也支持 .krc 歌词文件">载入歌曲<input ref="fileInput"
                                                                                             accept="audio/*,.mp3,.wav,.m4a,.aac,.ogg,.flac,.krc"
                                                                                             type="file"
                                                                                             @change="onFile"></label>
      <button :aria-pressed="tapActive" class="btn" title="边播放边在每行开头打点" @click="onTapBtn">打点同步</button>
      <button class="btn btn-cloud" title="在线搜索 / 我的歌单 / 扫码登录（酷狗・网易云）" @click="openCloud">云端音乐
      </button>
    </div>
    <div class="grid grid-cols-3 gap-2">
      <label class="field">BPM<input v-model="bpm" :placeholder="bpmPlaceholder" max="300" min="0" step="0.1"
                                     type="number" @change="onBpm"></label>
      <label class="field">开始(秒)<input v-model="offset" min="0" step="0.05" type="number" @change="onOffset"></label>
      <label class="field">行时长<input v-model="lineScale" max="3" min="0.4" step="0.05" type="number"
                                        @change="onLineScale"></label>
    </div>
    <div class="row">
      <label class="checkline"><input v-model="snap" type="checkbox" @change="onSnap"> 对齐节拍</label>
      <button class="btn btn-ghost btn-sm" @click="onResetTimes">清除手动时间</button>
    </div>
  </div>
</template>
