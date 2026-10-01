<script lang="ts">
/* 行组件：从 LineList 拆出。行独立成组件后 props 不变即跳过重渲——
   播放跨行只重渲新旧两行；此前每次跨行都会全量 patch 全部行的下拉框，
   把 ~2.8 万个 <option> 的 value 属性重写一遍（184 种布局 × 每行数个 select）。 */
export interface CutCell {
  k: number;
  layout: string;
  forced: string | null;
  hue: number;
  title: string;
  start: number;
  half: number
}

export interface Row {
  i: number;
  no: string;
  start: number;
  label: string;
  full: string;
  interlude: boolean;
  manual: boolean;
  layout: string;
  ncut: string;
  locked: boolean;
  inRange: boolean;
  cuts: CutCell[];
}
</script>

<script lang="ts" setup>
import {computed, nextTick, ref} from 'vue';
import {J} from '../../engine';
import {S} from '../../jizura/state';
import {seek, replan} from '../../jizura/engine';
import {commit, pushEdit, remember, setCutTech, setExportRange, setOv, startTap, toast} from '../../jizura/look';
import {flushSave} from '../../jizura/project';
import {bus} from '../../jizura/bus';

const props = defineProps<{ row: Row; active: boolean }>();

const layoutOpts = computed(() => [{v: '', t: '自动'}, ...J.LAYOUT_ORDER.map((k: string) => ({
  v: k,
  t: J.LAYOUTS[k].name
}))]);
const cutCountOpts = [1, 2, 3, 4, 5, 6];

const editing = ref<{ i: number; pre: string; body: string; value: string } | null>(null);
const editInput = ref<HTMLInputElement | null>(null);

function onTime(ev: Event) {
  const v = parseFloat((ev.target as HTMLInputElement).value);
  pushEdit();
  if (!S.project.timing.lineTimes) S.project.timing.lineTimes = {};
  if (isFinite(v)) S.project.timing.lineTimes[props.row.i] = Math.max(0, v); else delete S.project.timing.lineTimes[props.row.i];
  replan();
}

function seekLine() {
  seek(props.row.start + 0.001);
}

function startEdit() {
  const ln = S.plan.lines[props.row.i];
  if (ln.src == null) return;
  const rowList = S.project.lyrics.replace(/\r/g, '').split('\n');
  const row = rowList[ln.src] || '';
  const pre = (row.match(/^\s*(?:\[\d+:\d+(?:[.:]\d+)?\])*/) || [''])[0];
  const body = row.slice(pre.length).trim();
  editing.value = {i: props.row.i, pre, body, value: body};
  nextTick(() => editInput.value?.focus());
}

function finishEdit(ok: boolean) {
  const e = editing.value;
  if (!e) return;
  editing.value = null;
  if (!ok) return;
  const v = e.value.trim();
  if (!v || v === e.body) return;
  const ln = S.plan.lines[e.i];
  pushEdit();
  const rowList = S.project.lyrics.replace(/\r/g, '').split('\n');
  rowList[ln.src] = e.pre + v;
  S.project.lyrics = rowList.join('\n');
  bus.emit('lyrics-sync');
  replan();
  flushSave();
  toast(`第 ${ln.index + 1} 行歌词已修改`);
}

function onLayout(ev: Event) {
  setOv(props.row.i, {layout: (ev.target as HTMLSelectElement).value || undefined});
  replan();
}

function onNcut(ev: Event) {
  remember();
  setOv(props.row.i, {cuts: +(ev.target as HTMLSelectElement).value || undefined, single: undefined});
  replan();
  commit();
  seek(props.row.start + 0.001);
}

function onDice() {
  const cur = S.project.overrides[props.row.i] || {};
  setOv(props.row.i, {seed: ((cur.seed | 0) + 1), lock: false, lockedSeed: undefined, lockedCuts: undefined});
  replan();
  seek(S.plan.lines[props.row.i].start + 0.001);
}

function onLock() {
  const cur = S.project.overrides[props.row.i] || {};
  if (cur.lock) setOv(props.row.i, {lock: false, lockedSeed: undefined, lockedCuts: undefined});
  else setOv(props.row.i, {
    lock: true,
    lockedSeed: S.plan.lines[props.row.i].seed,
    lockedCuts: J.lineSnapshot(S.plan, props.row.i) || undefined
  });
  replan();
}

function onCutLayout(cell: CutCell, ev: Event) {
  setCutTech(props.row.i, cell.k, 'layout', (ev.target as HTMLSelectElement).value);
  replan();
  seek(cell.start + cell.half);
}

function seekCut(cell: CutCell) {
  seek(cell.start + cell.half);
}

function onRng(ev: MouseEvent) {
  setExportRange(props.row.i, ev.shiftKey);
}
</script>

<template>
  <li :class="[active ? 'border-line2 bg-raised shadow-[inset_2px_0_0_var(--color-amber)]' : 'border-transparent hover:bg-raised', row.inRange ? '!border-amber' : '']"
      class="row-cv grid grid-cols-[22px_66px_minmax(0,1fr)] gap-x-1.5 gap-y-1 items-center p-1.5 rounded-md border transition-colors">
    <span class="font-mono text-[11px] text-faint text-right">{{ row.no }}</span>
    <input :aria-label="`第 ${row.i + 1} 行的开始秒`" :class="row.manual ? '!border-cyan' : ''" :title="`开始（秒）${row.manual ? '・手动' : '・自动'}`"
           :value="row.start.toFixed(2)" class="!h-[26px] !py-0.5 !px-1 text-xs w-full mono" min="0"
           step="0.01" type="number"
           @change="onTime">
    <input v-if="editing && editing.i === row.i" ref="editInput" v-model="editing.value"
           class="!h-[26px] !py-0.5 !px-1.5 text-[13px] w-full" title="记法（/ 分隔・*强调*・行尾 !・| 注释・[间奏 8]）同样可用。Enter 确认、Esc 取消"
           type="text"
           @blur="finishEdit(true)" @keydown.enter.prevent="finishEdit(true)" @keydown.esc.prevent="finishEdit(false)">
    <span v-else :class="{ 'italic': row.interlude }" :title="row.full" class="truncate cursor-pointer"
          @click="seekLine">{{ row.label }}</span>
    <div class="col-span-2 col-start-2 flex justify-between items-start gap-1.5 flex-wrap">
      <span class="flex gap-1 flex-wrap min-w-0 flex-[1_1_140px]">
        <select v-for="cell in row.cuts" :key="cell.k"
                :aria-label="`第 ${row.i + 1} 行 切分 ${cell.k + 1} 的布局`"
                :class="{ '!border-amber': cell.forced }"
                :style="{ borderColor: `hsla(${cell.hue},70%,58%,0.7)` }"
                :title="cell.title" :value="cell.forced || cell.layout"
                class="!h-[22px] !py-0 !px-1 text-[10px] max-w-[112px] w-auto border border-line2 rounded bg-raised text-fg"
                @change="onCutLayout(cell, $event)" @pointerdown="seekCut(cell)">
          <option v-for="o in layoutOpts" :key="o.v" :value="o.v">{{ o.t }}</option>
        </select>
      </span>
      <span class="flex gap-1 flex-wrap justify-end ml-auto">
        <button :aria-label="`修改第 ${row.i + 1} 行歌词`" class="btn btn-icon btn-ghost" title="修改这一行的歌词"
                @click="startEdit">
          <svg fill="none" stroke="currentColor" stroke-width="1.4" viewBox="0 0 16 16"><path
              d="M3 13l1-3.5L11 2.5l2.5 2.5L6.5 12z"/><path d="M9.5 4l2.5 2.5"/></svg>
        </button>
        <select v-if="!row.interlude" :aria-label="`第 ${row.i + 1} 行的切分数`" :value="row.ncut"
                class="!h-auto !py-0.5 !px-1 text-[11px] max-w-[92px] w-auto" @change="onNcut">
          <option value="">切分 自动</option>
          <option v-for="n in cutCountOpts" :key="n" :value="String(n)">切分 {{ n }}</option>
        </select>
        <select v-if="!row.interlude" :aria-label="`第 ${row.i + 1} 行布局指定`" :value="row.layout"
                class="!h-auto !py-0.5 !px-1 text-[11px] max-w-[92px] w-auto" @change="onLayout">
          <option v-for="o in layoutOpts" :key="o.v" :value="o.v">{{ o.t }}</option>
        </select>
        <button :aria-label="`从第 ${row.i + 1} 行开始打点`" class="btn btn-icon btn-ghost"
                title="从这一行开始重新打点同步" @click="startTap(row.i)">
          <svg fill="none" stroke="currentColor" stroke-width="1.4" viewBox="0 0 16 16"><circle cx="8" cy="8" fill="currentColor"
                                                                                                r="2.2"/><circle
              cx="8" cy="8" r="5.5"/></svg>
        </button>
        <button :aria-label="`把第 ${row.i + 1} 行的切分设为导出范围`" :aria-pressed="row.inRange"
                :class="row.inRange ? '!text-amber' : ''"
                class="btn btn-icon btn-ghost" title="这一行的切分设为导出范围（Shift+点击扩大范围）"
                @click="onRng">
          <svg fill="none" stroke="currentColor" stroke-width="1.4" viewBox="0 0 16 16"><path d="M3 3v10M13 3v10"/><path
              d="M5.5 8h5M8.5 5.5L11 8l-2.5 2.5"/></svg>
        </button>
        <button v-if="!row.interlude" :aria-pressed="row.locked" :class="row.locked ? '!text-amber' : ''"
                class="btn btn-icon btn-ghost" title="锁定这一行的构成" @click="onLock">
          <svg fill="none" stroke="currentColor" stroke-width="1.4" viewBox="0 0 16 16"><rect height="7" rx="1.5" width="10"
                                                                                              x="3" y="7"/><path
              d="M5 7V5a3 3 0 0 1 6 0v2"/></svg>
        </button>
      </span>
    </div>
  </li>
</template>

<style scoped>
/* 不可见的行不参与排版/绘制：长歌词列表的布局成本随行数线性涨，
   content-visibility 让视口外的行跳过 layout+paint（可交互性保留，
   滚动到即渲染）；72px 为估算行高，首次排版后浏览器自动替换为实测值 */
.row-cv {
  content-visibility: auto;
  contain-intrinsic-size: auto 72px;
}
</style>
