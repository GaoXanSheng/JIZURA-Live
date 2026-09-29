<script setup lang="ts">
/* かんたん模式面板：一键随机大按钮 + 套装开关 + 当前方案 + 单项替换 + 导出 */
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { J } from '../../engine';
import { S } from '../../jizura/state';
import { bus } from '../../jizura/bus';
import { replan } from '../../jizura/engine';
import { remember, commit, omakase, rerollPart, randomPalette, histGo, histInfo, toast, exportRangeLines } from '../../jizura/look';
import { flushSave } from '../../jizura/project';
import { swatchHTML } from '../../jizura/ui-helpers';
import { codecNote, canPickFile, runExport, setAspect, setRes, setFps, setKeyBg, setCenterFree } from '../../jizura/export';

const offs: (() => void)[] = [];
const hist = ref(histInfo());
const nowRows = ref<{ k: string; html: string }[]>([]);
const busy = ref(false);
const progress = ref(0);
const progressText = ref('—');
const codecOk = ref(true);
const share = ref<{ file: File; name: string } | null>(null);

const aspect = ref('16:9'), res = ref('1080'), fps = ref('30'), keyBg = ref('off'), centerFree = ref(false);
const rangeFrom = ref('-1'), rangeTo = ref('-1'), rangeInfo = ref('');
const ASPECTS = ['16:9', '9:16', '4:3', '3:4', '1:1', '4:5', '21:9'];

const toggles = computed(() => [
  { cls: 'extra', label: '使用追加的演出', sub: '最初公开版之后追加的约 350 种演出・12 套配色・6 种字体，也纳入一键随机/重抽的候选', value: S.project?.extra === true },
  { cls: 'wa', label: '使用和风演出', sub: '灯笼・信笺・障子・扇・家纹・青海波・樱花瓣等和风图形。关闭后一键随机/重抽不再选中', value: S.project?.wa !== false },
  { cls: 'typo', label: '使用文字PV部件', sub: '只用线条・数字・字组呈现的文字PV风部件（约 50）', value: J.setOn(S.project, 'typo') },
  { cls: 'kinetic', label: '使用动感部件', sub: '逐词运动・弹跳・堆叠等重视动态的部件（约 50）', value: J.setOn(S.project, 'kinetic') },
  { cls: 'horror', label: '使用恐怖演出', sub: '诡异氛围的部件（约 50）与 3 套配色。开启后一键随机的氛围会加入「恐怖」', value: J.setOn(S.project, 'horror') },
  { cls: 'unify', label: '统一感', value: S.project?.unify === true },
  { cls: 'typeset', label: '文字对齐', value: S.project?.typeset === true },
]);

const lineOpts = computed(() => {
  const L = S.plan?.lines || [];
  return [{ v: '-1', t: '全部' }, ...L.map((ln: any, i: number) => ({ v: String(i), t: String(i + 1).padStart(2, '0') + ' ' + ((ln.interlude ? '〔间奏〕' : ln.text) as string).slice(0, 14) }))];
});

function esc(s: string) { return String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c] as string)); }
function sw(cols: string[]) { return swatchHTML(cols).map(c => `<i style="background:${c}" title="${c}"></i>`).join(''); }

function showNow() {
  if (!S.plan) return;
  const P = S.project, sc = S.plan.style.schemes[0];
  const moodName = P.mood && J.MOODS[P.mood] ? J.MOODS[P.mood].name : '自定义';
  const fk = S.plan.style.fonts.display[0];
  const fontName = J.FONTS[fk] ? (J.faceOf ? J.faceOf(fk) : J.FONTS[fk]).label : fk;
  const cuts = S.plan.cuts.filter((c: any) => c.line >= 0 && c.layout !== 'interlude');
  const kinds = new Set(cuts.map((c: any) => c.layout)).size;
  const row = (k: string, v: string) => ({ k, html: v });
  nowRows.value = [
    row('风格', `<b>${esc(J.STYLES[P.style].name)}</b>`),
    row('氛围', esc(moodName)),
    row('配色', `<span class="swatches">${sw([sc.bg, sc.fg, sc.accent, sc.ghostA, sc.ghostB])}</span>${P.colors.accentOn ? '<span class="tagl">随机</span>' : ''}`),
    row('标题字体', esc(fontName)),
    row('构成', `${cuts.length} 切分・布局 ${kinds} 种`),
    row('演出', `加工 ${cuts.filter((c: any) => c.treat && c.treat !== 'none').length}・背景 ${new Set(cuts.map((c: any) => c.bg).filter((b: any) => b && b !== 'none')).size} 种・镜头 ${cuts.filter((c: any) => c.cam && c.cam !== 'push').length}`),
  ];
}

function onToggle(cls: string, ev: Event) {
  const on = (ev.target as HTMLInputElement).checked;
  remember();
  // 与旧版 setSwitch 一致：全部直接写 project 字段（typo/kinetic/horror 由引擎 J.setOn 读取）
  S.project[cls] = on;
  bus.emit('sync-ui');
  replan(); commit(); flushSave();
}

function syncOut() {
  aspect.value = S.project.aspect; res.value = String(S.project.res); fps.value = String(S.project.fps);
  const k = J.keyMode(S.project) || 'off';
  keyBg.value = k;
  centerFree.value = !!S.project.centerFree;
  const R = exportRangeLines();
  rangeFrom.value = R ? String(R.from) : '-1';
  rangeTo.value = R ? String(R.to) : '-1';
  rangeInfo.value = R ? `${R.from + 1}〜${R.to + 1} 行` : `全部（${(S.plan?.duration || 0).toFixed(1)}秒）`;
}

function onHist() { hist.value = histInfo(); showNow(); }

onMounted(() => {
  offs.push(bus.on('sync-ui', () => { syncOut(); showNow(); onHist(); }));
  offs.push(bus.on('replan', () => { showNow(); syncOut(); }));
  offs.push(bus.on('hist', onHist));
  offs.push(bus.on('codec', (c: any) => { codecOk.value = c.ok; }));
  offs.push(bus.on('export-start', () => { busy.value = true; progress.value = 0; progressText.value = '准备中…'; share.value = null; }));
  offs.push(bus.on('export-progress', (p: number, m?: string) => { progress.value = p; if (m) progressText.value = m; }));
  offs.push(bus.on('export-end', () => { busy.value = false; }));
  offs.push(bus.on('export-share', (s: any) => { share.value = s; }));
  syncOut(); showNow(); onHist();
});
onUnmounted(() => offs.forEach(f => f()));

function onFrom(ev: Event) {
  const v = +(ev.target as HTMLSelectElement).value;
  const R = exportRangeLines();
  S.project.exportRange = v < 0 ? null : { from: v, to: R ? Math.max(v, R.to) : v };
  flushSave(); bus.emit('replan-sync');
}
function onTo(ev: Event) {
  const v = +(ev.target as HTMLSelectElement).value;
  const R = exportRangeLines(); if (!R) return;
  S.project.exportRange = v < 0 ? { from: R.from, to: R.from } : { from: Math.min(R.from, v), to: Math.max(R.from, v) };
  flushSave(); bus.emit('replan-sync');
}
function doExport() { runExport('mp4'); }
function abort() { if (S.exporting) S.exporting.abort(); }
async function doShare() { const s = share.value; if (!s) return; try { await (navigator as any).share({ files: [s.file], title: s.name }); } catch {} }
</script>

<template>
  <div class="easy">
    <div class="easy-hero">
      <button class="omakase" title="风格・氛围・演出・配色整体随机" @click="omakase()">一键随机生成</button>
      <p class="hint">每按一次，风格・氛围・动效・配色・构成都焕然一新。键盘 <span class="kbd">R</span> 也行。</p>
      <div class="hist-row">
        <button class="ghost" :disabled="!hist.canBack" @click="histGo(-1)">◀ 上一案</button>
        <span class="mono muted hist-pos">{{ hist.len > 1 ? `${hist.i + 1} / ${hist.len}` : '' }}</span>
        <button class="ghost" :disabled="!hist.canFwd" @click="histGo(1)">下一案 ▶</button>
      </div>
      <div class="pick-sets">
        <label v-for="t in toggles" :key="t.cls" class="check">
          <input type="checkbox" :checked="t.value" @change="onToggle(t.cls, $event)">
          <span>{{ t.label }}<small>{{ t.sub }}</small></span>
        </label>
      </div>
    </div>
    <div class="easy-sec">
      <h3>当前方案</h3>
      <div class="now">
        <div v-for="r in nowRows" :key="r.k" class="now-row">
          <span class="k">{{ r.k }}</span>
          <span class="v" v-html="r.html"></span>
        </div>
      </div>
    </div>
    <div class="easy-sec">
      <h3>只改这里</h3>
      <div class="easy-grid">
        <button title="只换风格" @click="rerollPart('style')">风格</button>
        <button title="随机换强调色・错位色A/B" @click="randomPalette(); rerollPart('mood')">配色</button>
        <button title="只变动效・故障・手法氛围" @click="rerollPart('mood')">氛围</button>
        <button title="只重抽布局与动效的组合" @click="rerollPart('cut')">构成</button>
      </div>
    </div>
    <div class="easy-sec">
      <h3>导出</h3>
      <div class="fields">
        <label class="field">画面比例<select :value="aspect" @change="setAspect(($event.target as HTMLSelectElement).value)"><option v-for="a in ASPECTS" :key="a" :value="a">{{ a }}</option></select></label>
        <label class="field">分辨率<select :value="res" @change="setRes(+($event.target as HTMLSelectElement).value)"><option value="720">720p</option><option value="1080">1080p</option><option value="1440">1440p</option><option value="2160">4K</option></select></label>
        <label class="field">fps<select :value="fps" @change="setFps(+($event.target as HTMLSelectElement).value)"><option>24</option><option>30</option><option>60</option></select></label>
        <label class="field field-wide">背景<select :value="keyBg" @change="setKeyBg(($event.target as HTMLSelectElement).value)"><option value="off">常规（风格配色）</option><option value="green">绿幕</option><option value="black">黑幕</option></select></label>
      </div>
      <label class="check"><input v-model="centerFree" type="checkbox" @change="setCenterFree(centerFree)"><span>留空中央（给角色用）</span></label>
      <div class="rng-row">
        <span class="rng-lab">导出范围</span>
        <select :value="rangeFrom" @change="onFrom($event)"><option v-for="o in lineOpts" :key="o.v" :value="o.v">{{ o.t }}</option></select>
        <span class="muted">〜</span>
        <select :value="rangeTo" :disabled="rangeFrom === '-1'" @change="onTo($event)"><option v-for="o in lineOpts" :key="o.v" :value="o.v">{{ o.t }}</option></select>
        <span class="rng-info mono muted">{{ rangeInfo }}</span>
      </div>
      <div class="outbtns"><button class="primary" :disabled="busy || !codecOk" @click="doExport">导出 MP4</button></div>
      <div v-if="busy || progressText !== '—'" class="exp-box" style="margin-top: 10px">
        <button v-if="share" class="primary" style="width: 100%; margin-bottom: 8px" @click="doShare">分享并保存</button>
        <div class="progress"><i :style="{ width: progress.toFixed(1) + '%' }"></i></div>
        <div class="row" style="justify-content: space-between; margin-top: 6px">
          <span class="mono muted exp-text">{{ progressText }}</span>
          <button v-if="busy" class="ghost small" @click="abort">中止</button>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.easy { padding: 16px 16px 28px; display: grid; gap: 18px; }
.easy h3 { margin: 0 0 8px; }
.easy-hero { display: grid; gap: 10px; }
button.omakase {
  position: relative; padding: 18px 14px; font: 900 22px/1 var(--brand); letter-spacing: 0.12em;
  background: var(--text); color: var(--ink); border: 0; border-radius: var(--r);
  box-shadow: -3px -2px 0 var(--cyan), 3px 2px 0 var(--amber);
  transition: transform 0.12s ease, box-shadow 0.12s ease, background 0.12s ease;
  min-height: 0;
}
button.omakase:hover { background: #fff; box-shadow: -5px -3px 0 var(--cyan), 5px 3px 0 var(--amber); }
button.omakase:active { transform: translateY(1px) scale(0.99); box-shadow: -1px -1px 0 var(--cyan), 1px 1px 0 var(--amber); }
.hist-row { display: grid; grid-template-columns: 1fr auto 1fr; gap: 8px; align-items: center; }
.hist-pos { font-size: 11px; text-align: center; min-width: 44px; }
.pick-sets { margin: 12px 0 6px; text-align: left; }
.now { display: grid; gap: 1px; background: var(--line); border: 1px solid var(--line); border-radius: var(--r-sm); overflow: hidden; }
.now-row { display: grid; grid-template-columns: 84px minmax(0, 1fr); gap: 10px; align-items: center; padding: 7px 10px; background: var(--raised); font-size: 12px; }
.now-row .k { color: var(--muted); font-size: 11px; }
.now-row .v { display: flex; align-items: center; gap: 8px; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.now-row :deep(b) { font-weight: 700; }
.now-row :deep(.swatches) { display: inline-flex; gap: 3px; }
.now-row :deep(.swatches i) { width: 16px; height: 16px; border-radius: 3px; border: 1px solid rgba(255, 255, 255, 0.18); }
.now-row :deep(.tagl) { font: 500 10px var(--mono); color: var(--cyan); border: 1px solid rgba(22, 244, 212, 0.5); padding: 0 5px; border-radius: 2px; }
.easy-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 8px; }
.easy-grid button { padding: 10px 8px; }
.fields { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 8px; }
.field { display: flex; flex-direction: column; gap: 4px; font-size: 11px; color: var(--muted); }
.field-wide { grid-column: span 2; }
label.check { display: flex; align-items: flex-start; gap: 8px; margin-bottom: 8px; cursor: pointer; }
label.check input { margin: 3px 0 0; flex: none; }
label.check small { display: block; font-size: 11px; color: var(--muted); }
.rng-row { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; margin: 10px 0 2px; font-size: 12px; }
.rng-lab { color: var(--muted); font-size: 11px; }
.rng-info { font-size: 11px; }
.outbtns { display: grid; gap: 8px; margin-top: 12px; }
.outbtns button { padding: 10px 12px; }
.exp-text { font-size: 11px; overflow: hidden; text-overflow: ellipsis; }
</style>
