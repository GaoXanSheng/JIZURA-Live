<script setup lang="ts">
/* 导出 tab：参数 + MP4/PNG 导出（进度/取消）+ 编解码提示 */
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { J } from '../../engine';
import { S } from '../../jizura/state';
import { bus } from '../../jizura/bus';
import { exportRangeLines } from '../../jizura/look';
import { flushSave } from '../../jizura/project';
import {
  codecNote, canPickFile, runExport,
  setAspect, setRes, setFps, setQuality, setKeyBg, setIncludeAudio, setCenterFree, setCenterDir,
} from '../../jizura/export';

const aspect = ref('16:9'), res = ref('1080'), fps = ref('30'), quality = ref('high');
const keyBg = ref('off'), includeAudio = ref(true);
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

function lineOpts(first: string) {
  const L = S.plan?.lines || [];
  return [{ v: '-1', t: first }, ...L.map((ln: any, i: number) => ({ v: String(i), t: String(i + 1).padStart(2, '0') + ' ' + ((ln.interlude ? '〔间奏〕' : ln.text) as string).slice(0, 14) }))];
}
const fromOpts = computed(() => lineOpts('全部'));
const toOpts = computed(() => lineOpts('—'));

function sync() {
  if (!S.project) return;
  aspect.value = S.project.aspect; res.value = String(S.project.res); fps.value = String(S.project.fps);
  quality.value = S.project.quality || 'high'; includeAudio.value = S.project.includeAudio !== false;
  const k = J.keyMode(S.project) || 'off';
  keyBg.value = k;
  centerFree.value = !!S.project.centerFree;
  centerDir.value = S.project.centerDir === 'lr' ? 'lr' : 'tb';
  const R = exportRangeLines();
  rangeFrom.value = R ? String(R.from) : '-1';
  rangeTo.value = R ? String(R.to) : '-1';
  const r = rangeInfo2();
  rangeInfo.value = r || `全部（${(S.plan?.duration || 0).toFixed(1)}秒）`;
}
function rangeInfo2() {
  const R = exportRangeLines(); if (!R) return null;
  const L = S.plan.lines, a = L[R.from], b = L[R.to], last = R.to === L.length - 1;
  const t0 = Math.max(0, a.start - 0.25);
  const t1 = last ? S.plan.duration : Math.min(L[R.to + 1].start, (b.interlude ? b.end : b.visEnd) + 0.35);
  return `${J.fmtTime(t0)} 〜 ${J.fmtTime(t1)}（${(t1 - t0).toFixed(1)}秒）`;
}

function onFrom(ev: Event) {
  const v = +(ev.target as HTMLSelectElement).value;
  const R = exportRangeLines();
  S.project.exportRange = v < 0 ? null : { from: v, to: R ? Math.max(v, R.to) : v };
  flushSaveSync(); sync();
}
function onTo(ev: Event) {
  const v = +(ev.target as HTMLSelectElement).value;
  const R = exportRangeLines(); if (!R) return;
  S.project.exportRange = v < 0 ? { from: R.from, to: R.from } : { from: Math.min(R.from, v), to: Math.max(R.from, v) };
  flushSaveSync(); sync();
}
function flushSaveSync() { flushSave(); bus.emit('replan-sync'); }

function onCodec(c: any) { codecOk.value = c.ok; codecText.value = c.text; showMp4File.value = c.ok && canPickFile(); }

onMounted(() => {
  offs.push(bus.on('out', sync));
  offs.push(bus.on('sync-ui', sync));
  offs.push(bus.on('replan', sync));
  offs.push(bus.on('codec', onCodec));
  offs.push(bus.on('export-start', () => { busy.value = true; progress.value = 0; progressText.value = '准备中…'; share.value = null; }));
  offs.push(bus.on('export-progress', (p: number, m?: string) => { progress.value = p; if (m) progressText.value = m; }));
  offs.push(bus.on('export-end', () => { busy.value = false; }));
  offs.push(bus.on('export-share', (s: any) => { share.value = s; }));
  sync();
  codecNote();
});
onUnmounted(() => offs.forEach(f => f()));

function doExport(kind: any) { runExport(kind); }
function abort() { if (S.exporting) S.exporting.abort(); }
async function doShare() { const s = share.value; if (!s) return; try { await (navigator as any).share({ files: [s.file], title: s.name }); } catch {} }
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
          <option value="720">720p</option><option value="1080">1080p</option><option value="1440">1440p</option><option value="2160">4K</option>
        </select>
      </label>
      <label class="field">fps
        <select :value="fps" @change="setFps(+($event.target as HTMLSelectElement).value)">
          <option>24</option><option>30</option><option>60</option>
        </select>
      </label>
      <label class="field">画质
        <select :value="quality" @change="setQuality(($event.target as HTMLSelectElement).value)">
          <option value="standard">标准</option><option value="high">高</option><option value="max">最高</option>
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
    <p class="note">选绿幕/黑幕后，只在单色背景上绘制白色文字与演出（背景花纹、纸质感、粒子、暗角都不含）。绿幕可用剪辑软件的色度键抠除，黑幕用「滤色」或亮度键合成。</p>
    <label class="checkblock my-2.5"><input v-model="centerFree" type="checkbox" @change="setCenterFree(centerFree)"><span>留出画面中央（放角色用）<small>以在中央叠加角色为前提，横屏时文字与演出放左右、竖屏放上下，逐行交替。预览会用虚线标出留空范围</small></span></label>
    <label v-if="centerFree && tall" class="row" style="gap: 6px">竖屏时
      <select :value="centerDir" aria-label="竖屏画面的分列方式" @change="setCenterDir(($event.target as HTMLSelectElement).value)">
        <option value="tb">上下分列</option>
        <option value="lr">左右分列</option>
      </select>
    </label>
    <label class="checkline" style="margin-top: 10px"><input v-model="includeAudio" type="checkbox" @change="setIncludeAudio(includeAudio)"> 把歌曲合进视频</label>
    <div class="flex items-center gap-1.5 flex-wrap my-2.5 text-xs">
      <span class="text-[11px] text-mut">导出范围</span>
      <select :value="rangeFrom" aria-label="导出范围的第一行" @change="onFrom($event)"><option v-for="o in fromOpts" :key="o.v" :value="o.v">{{ o.t }}</option></select>
      <span class="text-mut">〜</span>
      <select :value="rangeTo" :disabled="rangeFrom === '-1'" aria-label="导出范围的最后一行" @change="onTo($event)"><option v-for="o in toOpts" :key="o.v" :value="o.v">{{ o.t }}</option></select>
      <span class="font-mono text-[11px] text-mut">{{ rangeInfo }}</span>
    </div>
    <div class="grid gap-2 mt-3">
      <button class="btn btn-primary !py-2.5" :disabled="busy || !codecOk" title="导出 MP4 视频" @click="doExport('mp4')">导出 MP4</button>
      <button v-if="showMp4File" class="btn btn-ghost !py-2.5" :disabled="busy" title="先选保存位置，边导出边直接写文件。长曲・1080p 以上浏览器内存不足时使用（Chrome / Edge）" @click="doExport('mp4file')">大视频用（直接写入文件）</button>
      <button class="btn !py-2.5" :disabled="busy" @click="doExport('png')">连号 PNG（ZIP）</button>
      <button class="btn !py-2.5" :disabled="busy" @click="doExport('pnga')">透明 PNG（ZIP・无背景）</button>
      <button class="btn !py-2.5" :disabled="busy" title="后景（背景图形与歌词后面的装饰）和前景（歌词与其装饰）分别导出到 back / front 文件夹的透明 PNG" @click="doExport('pngl')">透明 PNG 前景/后景（ZIP）</button>
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
