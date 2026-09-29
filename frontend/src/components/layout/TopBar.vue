<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue';
import { J } from '../../engine';
import { S } from '../../jizura/state';
import { bus } from '../../jizura/bus';
import { toast, rangeSuffix, exportRange } from '../../jizura/look';
import { replan, replanSoon, pause, seek } from '../../jizura/engine';
import { mergeProject, flushSave } from '../../jizura/project';

const VERSION = '0.10.0';
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
  return ((S.project.title || 'jizuralive').replace(/[\\/:*?"<>|]+/g, '_').slice(0, 60) || 'jizuralive') + (k ? (k === 'green' ? '_greenback' : '_blackback') : '');
}

/* 保存：字面 Live 自己的格式（app 标记 + 可自由携带扩展字段）；
   读取仍然兼容原版 JIZURA 工程文件（mergeProject 统一清洗） */
function onSave() {
  J.saveFile(baseName() + '.jizuralive.json', JSON.stringify(Object.assign({}, S.project, {
    app: 'jizura-live', appVersion: VERSION,
  }), null, 1));
}

function onAE() {
  J.saveFile(baseName() + rangeSuffix() + '_ae.json', JSON.stringify(J.planForAE(S.plan, S.project, exportRange()), null, 1));
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
  <header class="flex items-center gap-3.5 flex-wrap py-2 px-4 border-b border-line bg-panel">
    <div class="flex items-baseline gap-2.5 min-w-0">
      <span class="brand-mark">字面</span>
      <span class="font-mono text-[10px] text-mut opacity-80 tracking-wide">Live {{ VERSION }}</span>
    </div>
    <div class="flex gap-2 flex-[1_1_260px] min-w-0">
      <input v-model="title" type="text" class="flex-[1_1_0] min-w-0" placeholder="曲名（标题卡・HUD 显示）" aria-label="曲名" @input="onTitle">
      <input v-model="artist" type="text" class="flex-[1_1_0] min-w-0" placeholder="歌手" aria-label="歌手" @input="onArtist">
    </div>
    <div class="flex gap-2 flex-wrap">
      <label class="btn-file" title="打开工程文件（兼容原版 JIZURA 的 .json）">打开<input ref="fileProject" type="file" accept=".json,application/json" @change="onOpenProject"></label>
      <button class="btn" title="保存为字面 Live 工程（.jizuralive.json）" @click="onSave">保存</button>
      <button class="btn" title="导出可供 After Effects 面板使用的构成数据" @click="onAE">导出到AE</button>
      <button class="btn btn-ghost" title="清空歌词、歌曲、设置和历史，回到初始状态" @click="onReset">初始化</button>
      <button class="btn btn-ghost" title="输出物的权利与许可证" @click="bus.emit('open-terms')">使用说明</button>
    </div>
  </header>
</template>
