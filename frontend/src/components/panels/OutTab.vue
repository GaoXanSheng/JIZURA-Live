<script lang="ts" setup>
import {computed, onMounted, onUnmounted, ref} from 'vue';
import {J} from '../../engine';
import {S} from '../../jizura/state';
import {bus} from '../../jizura/bus';
import {exportRange, exportRangeCuts, saveLrc, toast} from '../../jizura/look';
import {flushSave} from '../../jizura/project';
import {askConfirm} from '../../jizura/confirm';
import {EXPORT_BG_STYLES, type ExportCfg} from '../../jizura/export-spec';
import {
  canPickFile,
  codecNote,
  exportAeJson,
  exportCfg,
  runExportCfg,
  runExportWav,
  setAskSaveLocation,
  setAspect,
  setCenterDir,
  setCenterFree,
  setExportCfg,
  setFps,
  setQuality,
  setRes,
} from '../../jizura/export';

const aspect = ref('16:9'), res = ref('1080'), fps = ref('30'), quality = ref('high');
const cfg = ref<ExportCfg>(exportCfg());
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
  cfg.value = exportCfg();
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

const FORMATS: { v: ExportCfg['format']; t: string }[] = [
  {v: 'mp4', t: 'MP4 视频'}, {v: 'webm', t: 'WebM 视频'}, {v: 'png', t: 'PNG 序列'},
];

function setFormat(v: string) {
  setExportCfg({format: v as ExportCfg['format']});
}

function setBgStyle(v: string) {
  setExportCfg({bgStyle: v as ExportCfg['bgStyle']});
}

function setCheck(k: 'visuals' | 'subs' | 'bg' | 'audio' | 'layered' | 'toFile') {
  return (e: Event) => setExportCfg({[k]: (e.target as HTMLInputElement).checked});
}

// 勾选组合在 UI 侧就能判定的硬伤（按钮禁用 + 提示）；字幕面板未开启这类
// 运行时校验交给 runExportCfg 的 toast
const invalidMsg = computed(() => {
  const c = cfg.value;
  if (!c.visuals && !c.subs) return '至少勾选「渲染画面」或「字幕」，否则没有可导出的内容';
  if (c.format === 'mp4' && (!c.visuals || !c.bg)) return 'MP4 不支持透明/无背景导出：请勾选背景（可用绿幕/黑幕形态），或改用 WebM / PNG 序列';
  return '';
});

const fmtName = computed(() => cfg.value.format === 'mp4' ? 'MP4 视频' : cfg.value.format === 'webm' ? 'WebM 视频' : 'PNG 序列');
const showLayered = computed(() => cfg.value.format === 'png' && cfg.value.bg && cfg.value.visuals);
const showToFile = computed(() => cfg.value.format === 'mp4' && showMp4File.value);
const audioNote = computed(() => {
  const c = cfg.value;
  if (c.format === 'mp4' || !c.audio) return '';
  return `「${fmtName.value}」无法内嵌音轨：导出时会询问是否另存一个 WAV 音频文件（也可用下方「导出音频（WAV）」按钮单独导出）`;
});

// 主导出：WebM/PNG 勾了音频 → 现场确认是否另存 WAV（确认要在点击手势内完成）
async function doExport() {
  const c = cfg.value;
  if (c.audio && c.format !== 'mp4') {
    const ok = await askConfirm(
        `「${fmtName.value}」无法内嵌音轨。\n是否在导出画面之外，另存一个 WAV 音频文件？`,
        '音频导出');
    if (!ok) {
      const err = await runExportCfg({...c, audio: false});
      if (err) toast(err);
      return;
    }
    const err = await runExportCfg(c, {wav: true});
    if (err) toast(err);
    return;
  }
  const err = await runExportCfg(c);
  if (err) toast(err);
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
    <div class="grid grid-cols-2 gap-2">
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
    </div>
    <label class="checkblock my-2.5"><input v-model="centerFree" type="checkbox"
                                            @change="setCenterFree(centerFree)"><span>留出画面中央（放角色用）<small>留白宽度在「设置」页自定义；预览会用虚线标出留空范围</small></span></label>
    <label v-if="centerFree && tall" class="row" style="gap: 6px">竖屏时
      <select :value="centerDir" aria-label="竖屏画面的分列方式"
              @change="setCenterDir(($event.target as HTMLSelectElement).value)">
        <option value="tb">上下分列</option>
        <option value="lr">左右分列</option>
      </select>
    </label>

    <!-- 导出内容勾选：格式 × 内容 × 背景形态，一个「导出」按钮按组合落盘 -->
    <div class="mt-3 pt-3 border-t border-line">
      <div class="row text-xs gap-1.5 mb-2.5">
        <span class="text-[11px] text-mut">格式</span>
        <div class="row gap-1">
          <button v-for="f in FORMATS"
                  :key="f.v" class="btn btn-sm"
                  :class="cfg.format === f.v ? 'btn-primary' : 'btn-ghost'"
                  :disabled="busy"
                  @click="setFormat(f.v)">{{ f.t }}
          </button>
        </div>
      </div>
      <div class="grid grid-cols-2 gap-x-4 gap-y-1.5 text-[13px]">
        <label class="checkline">
          <input :checked="cfg.visuals" type="checkbox" :disabled="busy" @change="setCheck('visuals')($event)">
          渲染画面<small>（歌词动画）</small></label>
        <label class="checkline">
          <input :checked="cfg.subs" type="checkbox" :disabled="busy"
                 title="按「字幕」面板的样式，把翻译/音译字幕烤进导出画面；需先在「字幕」面板开启字幕渲染"
                 @change="setCheck('subs')($event)">
          字幕<small>（云字幕）</small></label>
        <label class="checkline">
          <input :checked="cfg.bg" type="checkbox" :disabled="busy || cfg.format === 'mp4'"
                 title="不勾 = 透明底导出（仅 WebM / PNG 序列；MP4 需勾上并用绿幕/黑幕抠像）"
                 @change="setCheck('bg')($event)">
          背景</label>
        <label class="checkline">
          <input :checked="cfg.audio" type="checkbox" :disabled="busy"
                 title="MP4：歌曲内嵌进视频；WebM / PNG：无法内嵌，导出时询问另存 WAV"
                 @change="setCheck('audio')($event)">
          音频<small>（歌曲）</small></label>
      </div>
      <div class="row gap-1.5 mt-2 text-xs flex-wrap">
        <template v-if="cfg.bg">
          <span class="text-[11px] text-mut">背景形态</span>
          <select :value="cfg.bgStyle" aria-label="背景形态" :disabled="busy" @change="setBgStyle(($event.target as HTMLSelectElement).value)">
            <option v-for="b in EXPORT_BG_STYLES" :key="b.value" :value="b.value">{{ b.label }}</option>
          </select>
          <span v-if="cfg.bgStyle !== 'off'" class="text-[11px] text-mut">单色底上只画白色文字与演出，剪辑软件色度键/滤色合成</span>
        </template>
        <template v-else>
          <span class="text-[11px] text-cyan">透明底（无背景）</span>
          <span v-if="cfg.format === 'mp4'" class="text-[11px] text-amber">MP4 不支持透明，请勾上背景或改用 WebM / PNG</span>
        </template>
      </div>
      <div class="grid gap-1 mt-2 text-[13px]">
        <label v-if="showLayered" class="checkline">
          <input :checked="cfg.layered" type="checkbox" :disabled="busy"
                 title="后景（背景图形与歌词后面的装饰）和前景（歌词与其装饰）分别导出到 back / front 文件夹的透明 PNG"
                 @change="setCheck('layered')($event)">
          分层输出<small>（back / front 双目录）</small></label>
        <label v-if="showToFile" class="checkline">
          <input :checked="cfg.toFile" type="checkbox" :disabled="busy"
                 title="先选保存位置，边导出边直接写文件。长曲・1080p 以上浏览器内存不足时使用（Chrome / Edge）"
                 @change="setCheck('toFile')($event)">
          大视频直接写入文件<small>（不占内存）</small></label>
        <label class="checkline" title="导出时弹出「另存为」对话框选择保存位置；关闭则直接下载到下载目录（需 Chrome / Edge）">
          <input v-model="askSave" type="checkbox" :disabled="!canPickFile() || busy" @change="setAskSaveLocation(askSave)">
          导出时询问保存位置（另存为）</label>
      </div>
      <!-- 单行不换行：标签收短，两个下拉弹性收缩，时长信息放不下时省略号（悬停看全） -->
      <div class="flex items-center gap-1.5 mt-2.5 text-xs" title="按切分选择导出的行范围；未选时导出整曲">
        <span class="text-[11px] text-mut shrink-0">导出范围</span>
        <select class="flex-1 min-w-0 max-w-[104px]" :value="rangeFrom" aria-label="导出范围的第一个切分"
                @change="onFrom($event)">
          <option v-for="o in fromOpts" :key="o.v" :value="o.v">{{ o.t }}</option>
        </select>
        <span class="text-mut shrink-0">〜</span>
        <select class="flex-1 min-w-0 max-w-[104px]" :disabled="rangeFrom === '-1'" :value="rangeTo"
                aria-label="导出范围的最后一个切分" @change="onTo($event)">
          <option v-for="o in toOpts" :key="o.v" :value="o.v">{{ o.t }}</option>
        </select>
        <span class="font-mono text-[11px] text-mut truncate min-w-0 text-right" :title="rangeInfo">{{ rangeInfo }}</span>
      </div>
      <p v-if="invalidMsg" class="note !text-amber">{{ invalidMsg }}</p>
      <p v-else-if="audioNote" class="note">{{ audioNote }}</p>
      <p v-else-if="cfg.subs" class="note">字幕按「字幕」面板当前样式与内容烤入；只勾字幕时导出的是透明底纯字幕层（可与画面分层素材叠放）。</p>
      <button class="btn btn-primary w-full !py-3 mt-2.5 text-[15px]" :disabled="busy || !!invalidMsg"
              title="按上方勾选导出；范围用上面的「导出范围（按切分）」"
              @click="doExport">导　出
      </button>
      <div class="row gap-1.5 mt-2 flex-wrap">
        <button class="btn btn-sm btn-ghost" :disabled="busy" title="单独导出歌曲音频（WAV，含音频特效）" @click="runExportWav()">导出音频（WAV）</button>
        <button class="btn btn-sm btn-ghost" :disabled="busy" title="每行带开始时刻（手工 > LRC 标签 > 估算）；选了导出范围时只导那些行" @click="saveLrc()">歌词（LRC）</button>
        <button class="btn btn-sm btn-ghost" :disabled="busy" title="完整 plan（v2）+ AE_MAP 对照表。在 AE 中运行 JIZURA_AE.jsx（上游发布）后选择该文件即可重建合成" @click="exportAeJson()">AE 工程（.json）</button>
      </div>
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
