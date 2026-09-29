<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue';
import { useEditor } from '../../stores/editor';
import { S } from '../../jizura/state';
import { bus } from '../../jizura/bus';
import { remember, commit, toast, rangeSuffix } from '../../jizura/look';
import { replan, replanSoon, pause, seek } from '../../jizura/engine';
import { mergeProject, flushSave } from '../../jizura/project';
import { J } from '../../engine';

const ed = useEditor();
const VERSION = '0.6.0';
const title = ref(S.project?.title || '');
const artist = ref(S.project?.artist || '');
const fileProject = ref<HTMLInputElement | null>(null);
const offs: (() => void)[] = [];

onMounted(() => {
  offs.push(bus.on('sync-ui', syncFromProject));
  offs.push(bus.on('lyrics-sync', syncFromProject));
  syncFromProject();
});
onUnmounted(() => offs.forEach(f => f()));

function syncFromProject() {
  title.value = S.project?.title || '';
  artist.value = S.project?.artist || '';
}

function onTitle() { S.project.title = title.value; replanSoon(300); }
function onArtist() { S.project.artist = artist.value; replanSoon(300); }

function baseName() {
  const k = J.keyMode(S.project);
  return ((S.project.title || 'jizura').replace(/[\\/:*?"<>|]+/g, '_').slice(0, 60) || 'jizura') + (k ? (k === 'green' ? '_greenback' : '_blackback') : '');
}

function onSave() {
  J.saveFile(baseName() + '.jizura.json', JSON.stringify(Object.assign({}, S.project, { appVersion: VERSION }), null, 1));
}

async function onReset() {
  bus.emit('open-reset');   // 原生 <dialog> 确认（Dialogs.vue）
}

/* 配置文件只当样式配置用：歌词/逐字时间/BGM 一律清空，避免"配置是 A 歌、歌词 BGM 还是 B 歌"串台 */
async function onOpenProject(e: Event) {
  const inp = e.target as HTMLInputElement;
  const f = inp.files && inp.files[0];
  if (!f) return;
  try {
    S.project = mergeProject(JSON.parse(await f.text()));
    pause();
    S.project.lyrics = '';
    S.project.timing.lineTimes = {};
    S.project.overrides = {};
    S.project.exportRange = null;
    S.project.audioName = '';
    S.audio = null;
    if (J.forgetSong) await J.forgetSong();
    try { localStorage.removeItem('jizura.kgsubmap'); } catch {}
    bus.emit('audioname', '未载入歌曲（载入后自动检测节拍并对齐切分）');
    bus.emit('sync-ui');
    replan(); flushSave(); seek0();
    toast('配置已载入：歌词与歌曲已清空，请重新载入歌曲');
  } catch (err) {
    bus.emit('msg', '无法读取项目文件');
    setTimeout(() => bus.emit('msg', null), 2500);
  }
  inp.value = '';
}

function seek0() {
  const c0 = S.plan && S.plan.cuts.find((c: any) => c.line >= 0);
  if (c0) seek(c0.start + Math.min(c0.dur * 0.6, c0.inDur + 0.25)); else seek(0);
}

</script>

<template>
  <header class="bar">
    <div class="brand"><span class="mark">字面</span><span class="ver">Live {{ VERSION }}</span></div>
    <div class="proj">
      <input type="text" v-model="title" placeholder="曲名（标题卡・HUD 显示）" aria-label="曲名" @input="onTitle">
      <input type="text" v-model="artist" placeholder="歌手" aria-label="歌手" @input="onArtist">
    </div>
    <div class="acts">
      <label class="file" title="打开保存的项目(.json)">打开<input ref="fileProject" type="file" accept=".json,application/json" @change="onOpenProject"></label>
      <button title="项目保存为 .json" @click="onSave">保存</button>
      <button class="ghost" title="清空歌词、歌曲、设置和历史，回到初始状态" @click="onReset">初始化</button>
      <button class="ghost" title="输出物的权利与许可证" @click="bus.emit('open-terms')">使用说明</button>
    </div>
  </header>
</template>
