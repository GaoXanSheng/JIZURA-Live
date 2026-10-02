<script lang="ts" setup>
import {computed, onMounted, onUnmounted, ref} from 'vue';
import {J} from '../../engine';
import {S} from '../../jizura/state';
import {bus} from '../../jizura/bus';
import {exportRange, exportRangeCuts, saveLrc} from '../../jizura/look';
import {flushSave} from '../../jizura/project';
import {
  canPickFile,
  codecNote,
  exportAeJson,
  runExport,
  setAskSaveLocation,
  setAspect,
  setCenterDir,
  setCenterFree,
  setFps,
  setIncludeAudio,
  setKeyBg,
  setQuality,
  setRes,
} from '../../jizura/export';

const aspect = ref('16:9'), res = ref('1080'), fps = ref('30'), quality = ref('high');
const keyBg = ref('off'), includeAudio = ref(true);
const askSave = ref(false);
const centerFree = ref(false), centerDir = ref('tb');
const rangeFrom = ref('-1'), rangeTo = ref('-1');
const rangeInfo = ref('');
const codecText = ref('导出在此浏览器内完成（推荐 Chrome / Edge）。');
const codecOk = ref(true);
const showMp4File = ref(false);
const busy = ref(false);
const progress = ref(0);
const progressText = ref('—');
const share = ref<{ file: File; name: string } | null>(null);
const offs: (() => void)[] = [];

const ASPECTS = ['16:9', '9:16', '4:3', '3:4', '1:1', '4:5', '21:9'];
const tall = computed(() => J.designSize(aspect.value)[1] > J.designSize(aspect.value)[0] * 1.1);

// 导出范围按切分选，和切分循环同粒度
function cutOpts(first: string) {
  const C = S.plan?.cuts || [];
  return [{v: '-1', t: first}, ...C.map((c: any, i: number) => {
    const label = (c.text || '').trim().slice(0, 12) || '〔' + ((J.LAYOUTS[c.layout] || {}).name || c.layout) + '〕';
    return {v: String(i), t: String(i + 1).padStart(2, '0') + ' ' + label};
  })];
}

const fromOpts = ref<{ v: string; t: string }[]>([{v: '-1', t: '全部'}]);
const toOpts = ref<{ v: string; t: string }[]>([{v: '-1', t: '—'}]);

function sync() {
  if (!S.project) return;
  aspect.value = S.project.aspect;
  res.value = String(S.project.res);
  fps.value = String(S.project.fps);
  quality.value = S.project.quality || 'high';
  includeAudio.value = S.project.includeAudio !== false;
  const k = J.keyMode(S.project) || 'off';
  keyBg.value = k;
  centerFree.value = !!S.project.centerFree;
  centerDir.value = S.project.centerDir === 'lr' ? 'lr' : 'tb';
  askSave.value = !!S.project.askSaveLocation;
  fromOpts.value = cutOpts('全部');
  toOpts.value = cutOpts('—');
  const R = exportRangeCuts();
  rangeFrom.value = R ? String(R.from) : '-1';
  rangeTo.value = R ? String(R.to) : '-1';
  const r = rangeInfo2();
  rangeInfo.value = r || `全部（${(S.plan?.duration || 0).toFixed(1)}秒）`;
}

function rangeInfo2() {
  const R = exportRange();
  if (!R) return null;
  return `${J.fmtTime(R.t0)} 〜 ${J.fmtTime(R.t1)}（${(R.t1 - R.t0).toFixed(1)}秒）`;
}

function onFrom(ev: Event) {
  const v = +(ev.target as HTMLSelectElement).value;
  const R = exportRangeCuts();
  S.project.exportRange = v < 0 ? null : {from: v, to: R ? Math.max(v, R.to) : v};
  flushSaveSync();
  sync();
}

function onTo(ev: Event) {
  const v = +(ev.target as HTMLSelectElement).value;
  const R = exportRangeCuts();
  if (!R) return;
  S.project.exportRange = v < 0 ? {from: R.from, to: R.from} : {from: Math.min(R.from, v), to: Math.max(R.from, v)};
  flushSaveSync();
  sync();
}

function flushSaveSync() {
  flushSave();
  bus.emit('replan-sync');
}

function onCodec(c: any) {
  codecOk.value = c.ok;
  codecText.value = c.text;
  showMp4File.value = c.ok && canPickFile();
}

onMounted(() => {
  offs.push(bus.on('out', sync));
  offs.push(bus.on('sync-ui', sync));
  offs.push(bus.on('replan', sync));
  offs.push(bus.on('replan-sync', sync));   // 行列表/时间轴上改了导出范围，这里下拉要跟着对齐
  offs.push(bus.on('codec', onCodec));
  offs.push(bus.on('export-start', () => {
    busy.value = true;
    progress.value = 0;
    progressText.value = '准备中…';
    share.value = null;
  }));
  offs.push(bus.on('export-progress', (p: number, m?: string) => {
    progress.value = p;
    if (m) progressText.value = m;
  }));
  offs.push(bus.on('export-end', () => {
    busy.value = false;
  }));
  offs.push(bus.on('export-share', (s: any) => {
    share.value = s;
  }));
  sync();
  codecNote();
});
onUnmounted(() => offs.forEach(f => f()));

function doExport(kind: any) {
  if (kind === 'ae') {
    exportAeJson();
    return;
  }
  if (kind === 'lrc') {
    saveLrc();
    return;
  }
  runExport(kind);
}

interface ExportItem {
  kind: string;
  label: string;
  title?: string;
  primary?: boolean;
  needCodec?: boolean;
  show?: () => boolean;
}
const GROUP_DEFS: { id: string; label: string; items: ExportItem[] }[] = [
  {
    id: 'video', label: '视频', items: [
      {kind: 'mp4', label: '导出 MP4', title: '导出 MP4 视频', primary: true, needCodec: true},
      {
        kind: 'mp4file', label: '大视频用（直接写入文件）',
        title: '先选保存位置，边导出边直接写文件。长曲・1080p 以上浏览器内存不足时使用（Chrome / Edge）',
        show: () => showMp4File.value,
      },
      {
        kind: 'webm', label: '透明视频（WebM・无背景）',
        title: 'VP9 透明通道视频：无背景、含全部演出与特效、不含音频。剪映・DaVinci Resolve 等可直接叠放；PR/AE 请用透明 PNG 序列。部分播放器不支持透明通道会显示黑底，用 Chrome 打开或导入剪辑软件即可看到透明',
      },
      {
        kind: 'subwebm', label: '字幕渲染视频（WebM・透明）',
        title: '与设置里 Spout 独立字幕通道同一内容：按「字幕」面板样式的翻译/音译字幕，透明底、不含歌词动画与背景。需先在「字幕」面板开启字幕渲染',
      },
      {
        kind: 'webmfront', label: '歌词动画视频（WebM・透明）',
        title: '仅歌词与其前景装饰的透明视频（无背景、无 HUD、不含音频），即分层导出的前景层，用于叠到其它素材上',
      },
    ],
  },
  {
    id: 'png', label: 'PNG 序列', items: [
      {kind: 'png', label: '连号 PNG（ZIP）'},
      {kind: 'pnga', label: '透明 PNG（ZIP・无背景）'},
      {
        kind: 'pngl', label: '透明 PNG 前景/后景（ZIP）',
        title: '后景（背景图形与歌词后面的装饰）和前景（歌词与其装饰）分别导出到 back / front 文件夹的透明 PNG',
      },
      {
        kind: 'pngf', label: '歌词动画 PNG（ZIP・仅前景层）',
        title: '只导出歌词前景层（歌词与其前景装饰，无 HUD、无背景）的透明 PNG 序列；PR/AE 等不吃 WebM 的软件用',
      },
    ],
  },
  {
    id: 'misc', label: '歌词 / 工程', items: [
      {kind: 'lrc', label: '歌词（LRC）', title: '每行带开始时刻（手工 > LRC 标签 > 估算）；选了导出范围时只导那些行'},
      {
        kind: 'ae', label: 'AE 工程（.json）',
        title: '完整 plan（v2）+ AE_MAP 对照表。在 AE 中运行 JIZURA_AE.jsx（上游发布）后选择该文件即可重建合成',
      },
    ],
  },
];
const GROUP_DEFAULTS: Record<string, boolean> = {video: true, png: false, misc: false};
const openGroups = ref<Record<string, boolean>>(loadGroups());

function loadGroups(): Record<string, boolean> {
  try {
    return {...GROUP_DEFAULTS, ...JSON.parse(localStorage.getItem('jizura.out.groups') || '{}')};
  } catch {
    return {...GROUP_DEFAULTS};
  }
}

const groups = computed(() => GROUP_DEFS.map(g => {
  const items = g.items.filter(it => !it.show || it.show());
  return {id: g.id, label: g.label, items, count: items.length};
}));

function toggleGroup(id: string) {
  openGroups.value[id] = !openGroups.value[id];
  try {
    localStorage.setItem('jizura.out.groups', JSON.stringify(openGroups.value));
  } catch { // 私密模式等会抛
  }
}

function abort() {
  if (S.exporting) S.exporting.abort();
}

async function doShare() {
  const s = share.value;
  if (!s) return;
  try {
    await (navigator as any).share({files: [s.file], title: s.name});
  } catch {
  }
}
</script>

<template>
  <div class="px-4 py-3.5">
    <div class="grid grid-cols-3 gap-2">
      <label class="field">画面比例
        <select :value="aspect" @change="setAspect(($event.target as HTMLSelectElement).value)">
          <option v-for="a in ASPECTS" :key="a" :value="a">{{ a }}</option>
        </select>
      </label>
      <label class="field">分辨率
        <select :value="res" @change="setRes(+($event.target as HTMLSelectElement).value)">
          <option value="720">720p</option>
          <option value="1080">1080p</option>
          <option value="1440">1440p</option>
          <option value="2160">4K</option>
        </select>
      </label>
      <label class="field">fps
        <select :value="fps" @change="setFps(+($event.target as HTMLSelectElement).value)">
          <option>24</option>
          <option>30</option>
          <option>60</option>
        </select>
      </label>
      <label class="field">画质
        <select :value="quality" @change="setQuality(($event.target as HTMLSelectElement).value)">
          <option value="standard">标准</option>
          <option value="high">高</option>
          <option value="max">最高</option>
        </select>
      </label>
      <label class="field col-span-2">背景
        <select :value="keyBg" @change="setKeyBg(($event.target as HTMLSelectElement).value)">
          <option value="off">常规（风格背景）</option>
          <option value="green">绿幕（用于合成）</option>
          <option value="black">黑幕（用于合成）</option>
        </select>
      </label>
    </div>
    <p class="note">
      选绿幕/黑幕后，只在单色背景上绘制白色文字与演出（背景花纹、纸质感、粒子、暗角都不含）。绿幕可用剪辑软件的色度键抠除，黑幕用「滤色」或亮度键合成。</p>
    <label class="checkblock my-2.5"><input v-model="centerFree" type="checkbox"
                                            @change="setCenterFree(centerFree)"><span>留出画面中央（放角色用）<small>留白宽度在「设置」页自定义；预览会用虚线标出留空范围</small></span></label>
    <label v-if="centerFree && tall" class="row" style="gap: 6px">竖屏时
      <select :value="centerDir" aria-label="竖屏画面的分列方式"
              @change="setCenterDir(($event.target as HTMLSelectElement).value)">
        <option value="tb">上下分列</option>
        <option value="lr">左右分列</option>
      </select>
    </label>
    <label class="checkline" style="margin-top: 10px"><input v-model="includeAudio" type="checkbox"
                                                             @change="setIncludeAudio(includeAudio)">
      把歌曲合进视频</label>
    <label class="checkline" style="margin-top: 6px" title="导出时弹出「另存为」对话框选择保存位置；关闭则直接下载到下载目录（需 Chrome / Edge）">
      <input v-model="askSave" type="checkbox" :disabled="!canPickFile()" @change="setAskSaveLocation(askSave)">
      导出时询问保存位置（另存为）</label>
    <div class="flex items-center gap-1.5 flex-wrap my-2.5 text-xs">
      <span class="text-[11px] text-mut">导出范围（按切分）</span>
      <select :value="rangeFrom" aria-label="导出范围的第一个切分" @change="onFrom($event)">
        <option v-for="o in fromOpts" :key="o.v" :value="o.v">{{ o.t }}</option>
      </select>
      <span class="text-mut">〜</span>
      <select :disabled="rangeFrom === '-1'" :value="rangeTo" aria-label="导出范围的最后一个切分"
              @change="onTo($event)">
        <option v-for="o in toOpts" :key="o.v" :value="o.v">{{ o.t }}</option>
      </select>
      <span class="font-mono text-[11px] text-mut">{{ rangeInfo }}</span>
    </div>
    <div class="grid gap-1 mt-3">
      <template v-for="g in groups" :key="g.id">
        <button class="btn btn-ghost btn-sm row justify-between" :aria-expanded="!!openGroups[g.id]"
                @click="toggleGroup(g.id)">
          <span>{{ openGroups[g.id] ? '▾' : '▸' }} {{ g.label }}</span>
          <span class="text-[11px] text-mut">{{ g.count }} 项</span>
        </button>
        <div v-show="openGroups[g.id]" class="grid gap-2">
          <button v-for="it in g.items" :key="it.kind" :disabled="busy || (!!it.needCodec && !codecOk)"
                  :class="it.primary ? 'btn btn-primary !py-2.5' : 'btn !py-2.5'" :title="it.title"
                  @click="doExport(it.kind)">{{ it.label }}
          </button>
        </div>
      </template>
    </div>
    <div v-if="busy || progressText !== '—'" class="mt-3">
      <button v-if="share" class="btn btn-primary w-full mb-2" @click="doShare">分享并保存</button>
      <div class="progress"><i :style="{ width: progress.toFixed(1) + '%' }"></i></div>
      <div class="row justify-between mt-1.5">
        <span class="font-mono text-[11px] text-mut truncate">{{ progressText }}</span>
        <button v-if="busy" class="btn btn-ghost btn-sm" @click="abort">中止</button>
      </div>
    </div>
    <p class="note">{{ codecText }}</p>
  </div>
</template>
