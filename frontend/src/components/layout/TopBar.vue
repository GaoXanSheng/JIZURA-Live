<script lang="ts" setup>
import {onMounted, onUnmounted, ref} from 'vue';
import {J} from '../../engine';
import {S} from '../../jizura/state';
import {bus} from '../../jizura/bus';
import {exportRange, rangeSuffix, toast} from '../../jizura/look';
import {invalidateFontKey, loadAudioFile, pause, replan, replanSoon, seek} from '../../jizura/engine';
import {flushSave, mergeProject} from '../../jizura/project';
import {
  AUDIO_ENTRY_PREFIX,
  buildProjectPackage,
  entryText,
  FONT_ENTRY_PREFIX,
  PROJECT_ENTRY,
  readPackage
} from '../../jizura/pkg';

const VERSION = __APP_VERSION__;   // 来自 package.json，vite define 注入
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

function onTitle() {
  S.project.title = title.value;
  replanSoon(300);
}

function onArtist() {
  S.project.artist = artist.value;
  replanSoon(300);
}

function baseName() {
  const k = J.keyMode(S.project);
  return ((S.project.title || 'jizuralive').replace(/[\\/:*?"<>|]+/g, '_').slice(0, 60) || 'jizuralive') + (k ? (k === 'green' ? '_greenback' : '_blackback') : '');
}

// .jizuralive（JZLV 容器）：工程 + 歌曲 + 上传字体打成一个包
// 老格式只读不写，.json 兼容只留在「打开」一侧
async function onSave() {
  bus.emit('msg', '正在打包工程（含歌曲）…');
  try {
    const blob = await buildProjectPackage(VERSION);
    const r = await J.saveFile(baseName() + '.jizuralive', blob);
    if (r === 'declined') toast('已取消保存');
  } catch (err) {
    bus.emit('msg', '打包失败：' + ((err as any) && (err as any).message || err));
    setTimeout(() => bus.emit('msg', null), 4000);
    return;
  }
  bus.emit('msg', null);
}

function onAE() {
  J.saveFile(baseName() + rangeSuffix() + '_ae.json', JSON.stringify(J.planForAE(S.plan, S.project, exportRange()), null, 1));
}

async function onReset() {
  bus.emit('open-reset');   // 确认框在 Dialogs.vue
}

const JZLV_MAGIC = [0x4a, 0x5a, 0x4c, 0x56];
const mimeOf = (name: string) => (/\.flac$/i.test(name) ? 'audio/flac' : /\.wav$/i.test(name) ? 'audio/wav' : /\.m4a|\.mp4$/i.test(name) ? 'audio/mp4' : /\.ogg$/i.test(name) ? 'audio/ogg' : 'audio/mpeg');

async function onOpenProject(e: Event) {
  const inp = e.target as HTMLInputElement;
  const f = inp.files && inp.files[0];
  if (!f) return;
  // 先让「载入中」提示画出来再解析，大工程同步规划会卡几百毫秒
  bus.emit('msg', '正在载入工程…');
  await new Promise(r => requestAnimationFrame(() => requestAnimationFrame(r)));
  try {
    const head = new Uint8Array(await f.slice(0, 4).arrayBuffer());
    const isPkg = head.length === 4 && JZLV_MAGIC.every((b, i) => head[i] === b);
    if (isPkg) await openPackage(f); else await openLegacyJson(f);
  } catch (err) {
    bus.emit('msg', '无法读取项目文件');
    setTimeout(() => bus.emit('msg', null), 2500);
  }
  inp.value = '';
}

// 老 .json 只当样式配置用，歌词/逐字时间/BGM 一律清空
// 免得配置是 A 歌、歌词和 BGM 还是 B 歌的串台
async function openLegacyJson(f: File) {
  S.project = mergeProject(JSON.parse(await f.text()));
  pause();
  S.project.lyrics = '';
  S.project.timing.lineTimes = {};
  S.project.overrides = {};
  S.project.exportRange = null;
  S.project.audioName = '';
  S.audio = null;
  if (J.forgetSong) await J.forgetSong();
  try {
    localStorage.removeItem('jizura.kgsubmap');
  } catch {
  }
  bus.emit('audioname', '未载入歌曲（载入后自动检测节拍并对齐切分）');
  bus.emit('sync-ui');
  replan();
  flushSave();
  seek0();
  toast('配置已载入：歌词与歌曲已清空，请重新载入歌曲');
}

// .jizuralive：全量恢复，工程 + 歌曲 + 上传字体
async function openPackage(f: File) {
  const entries = await readPackage(new Uint8Array(await f.arrayBuffer()));
  const pj = entries.find(en => en.name === PROJECT_ENTRY);
  if (!pj) throw new Error('no project.json');
  S.project = mergeProject(JSON.parse(entryText(pj)));
  pause();
  const audio = entries.find(en => en.name.startsWith(AUDIO_ENTRY_PREFIX));
  if (audio) {
    const name = S.project.audioName || 'song.' + (audio.name.split('.').pop() || 'mp3');
    await loadAudioFile(new File([audio.data as BlobPart], name, {type: mimeOf(name)}));
  } else {
    S.audio = null;
    if (J.forgetSong) await J.forgetSong();
    bus.emit('audioname', '包内没有歌曲，请重新载入');
  }
  for (const en of entries) {
    if (!en.name.startsWith(FONT_ENTRY_PREFIX)) continue;
    const key = en.name.slice(FONT_ENTRY_PREFIX.length);
    if (!J.FONTS[key] || !J.SAFE_FONT_KEY.test(key)) continue;
    if (J.saveFontData) await J.saveFontData(key, en.data);
    J.FONTS[key].loaded = true;
  }
  J.glyphs.clear();
  J.metrics.clear();
  invalidateFontKey();
  bus.emit('sync-ui');
  replan();
  flushSave();
  seek0();
  toast(audio ? '工程已载入：歌曲与歌词一并恢复' : '工程已载入（包内无歌曲）');
}

function seek0() {
  const c0 = S.plan && S.plan.cuts.find((c: any) => c.line >= 0);
  if (c0) seek(c0.start + Math.min(c0.dur * 0.6, c0.inDur + 0.25)); else seek(0);
}

</script>

<template>
  <header class="flex items-center gap-3.5 flex-wrap py-2 px-4 border-b border-line bg-panel">
    <div class="flex gap-2 flex-[1_1_260px] min-w-0">
      <input v-model="title" aria-label="曲名" class="flex-[1_1_0] min-w-0" placeholder="曲名（标题卡・HUD 显示）"
             type="text" @input="onTitle">
      <input v-model="artist" aria-label="歌手" class="flex-[1_1_0] min-w-0" placeholder="歌手" type="text"
             @input="onArtist">
    </div>
    <div class="flex gap-2 flex-wrap">
      <label class="btn-file" title="打开工程（.jizuralive 全量恢复；老 .json 仅作样式配置导入）">打开<input
          ref="fileProject" accept=".jizuralive,.json,application/json" type="file" @change="onOpenProject"></label>
      <button class="btn" title="保存为字面 Live 专用工程包（.jizuralive：含歌曲与歌词）" @click="onSave">保存</button>
      <button class="btn btn-ghost" title="清空歌词、歌曲、设置和历史，回到初始状态" @click="onReset">初始化</button>
      <button class="btn btn-ghost" title="输出物的权利与许可证" @click="bus.emit('open-terms')">使用说明</button>
    </div>
  </header>
</template>
