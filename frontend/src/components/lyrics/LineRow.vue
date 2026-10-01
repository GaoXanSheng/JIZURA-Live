<script lang="ts">
/* 行组件：从 LineList 拆出。行独立成组件后 props 不变即跳过重渲——
   播放跨行只重渲新旧两行；此前每次跨行都会全量 patch 全部行的下拉框，
   把 ~2.8 万个 <option> 的 value 属性重写一遍（184 种布局 × 每行数个 select）。

   option 风暴根治：184 个布局 option 只保留一份「共享池」，哪个下拉打开就临时
   挂进哪个 select，关闭（change/blur）立即移出，平时每个 select 只带一个
   当前值的 option。此前常驻 option 数随规模线性涨（341 切分工程 ≈ 7.8 万个
   <option>），Chrome 给每个 option 配一套内部 shadow 节点（label span + slot
   + ShadowRoot，×3），堆快照实测 29.6 万 option-label span、7.5 万 ShadowRoot
   滞留不释放——heap 57MB / DOM 7.8 万节点的「option slot span 风暴」。 */
const techPoolHolder: { pool: HTMLOptionElement[] | null } = {pool: null};   // 模块级：全应用仅此一份
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
import {computed, nextTick, onMounted, onUpdated, ref} from 'vue';
import {J} from '../../engine';
import {S} from '../../jizura/state';
import {seek, replan} from '../../jizura/engine';
import {commit, pushEdit, remember, setCutTech, setExportRange, setOv, startTap, toast} from '../../jizura/look';
import {flushSave} from '../../jizura/project';
import {bus} from '../../jizura/bus';

const props = defineProps<{ row: Row; active: boolean }>();

const cutCountOpts = [1, 2, 3, 4, 5, 6];

/* ---- 手法下拉：共享选项池（techPoolHolder 在模块级，跨所有行实例复用） ---- */
function techPoolOpts(): HTMLOptionElement[] {
  if (!techPoolHolder.pool) {
    techPoolHolder.pool = [{v: '', t: '自动'}, ...J.LAYOUT_ORDER.map((k: string) => ({v: k, t: J.LAYOUTS[k].name}))]
        .map(o => {
          const el = document.createElement('option');
          el.value = o.v;
          el.textContent = o.t;
          return el;
        });
  }
  return techPoolHolder.pool;
}

function techLabel(v: string) {
  return v && J.LAYOUTS[v] ? J.LAYOUTS[v].name : '自动';
}

/* 关合状态：select 只带一个当前值的 option */
function techSync(sel: HTMLSelectElement, cur: string) {
  if (sel.options.length > 1) return;                        // 打开中：不碰
  if (sel.options.length === 1 && sel.options[0].value === cur) return;
  sel.textContent = '';
  const opt = document.createElement('option');
  opt.value = cur;
  opt.textContent = techLabel(cur);
  sel.append(opt);
  sel.value = cur;
}

/* 打开：挂入全量池 */
function techOpen(sel: HTMLSelectElement, cur: string) {
  if (sel.options.length > 1) return;
  const keep = sel.value || cur;
  sel.textContent = '';
  for (const o of techPoolOpts()) sel.append(o);
  sel.value = keep;
}

/* 关闭：移出全量池，回到单 option */
function techRelease(sel: HTMLSelectElement, cur: string) {
  if (sel.options.length <= 1) return;
  sel.textContent = '';
  const opt = document.createElement('option');
  opt.value = cur;
  opt.textContent = techLabel(cur);
  sel.append(opt);
  sel.value = cur;
}

const rootEl = ref<HTMLLIElement | null>(null);
onMounted(() => syncTechSelects());
onUpdated(() => syncTechSelects());

function syncTechSelects() {
  const root = rootEl.value;
  if (!root) return;
  for (const sel of root.querySelectorAll<HTMLSelectElement>('select[data-cur]')) techSync(sel, sel.getAttribute('data-cur') || '');
}

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
  const pre = (row.match(/^\s*(?:\[\d+:\d+(?:\.\d+)?\])*/) || [''])[0];
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
  const sel = ev.target as HTMLSelectElement;
  setOv(props.row.i, {layout: sel.value || undefined});
  techRelease(sel, sel.value);
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
  const sel = ev.target as HTMLSelectElement;
  const v = sel.value;
  setCutTech(props.row.i, cell.k, 'layout', v);
  techRelease(sel, v);
  replan();
  seek(cell.start + cell.half);
}

function onCutSelectDown(cell: CutCell, ev: Event) {
  techOpen(ev.target as HTMLSelectElement, cell.forced || cell.layout);
  seekCut(cell);
}

function onTechOpen(ev: Event) {
  const sel = ev.target as HTMLSelectElement;
  techOpen(sel, sel.getAttribute('data-cur') || '');
}

function onTechBlur(ev: Event) {
  const sel = ev.target as HTMLSelectElement;
  techRelease(sel, sel.value || sel.getAttribute('data-cur') || '');
}

function seekCut(cell: CutCell) {
  seek(cell.start + cell.half);
}

function onRng(ev: MouseEvent) {
  setExportRange(props.row.i, ev.shiftKey);
}
</script>

<template>
  <li ref="rootEl"
      :class="[active ? 'border-line2 bg-raised shadow-[inset_2px_0_0_var(--color-amber)]' : 'border-transparent hover:bg-raised', row.inRange ? '!border-amber' : '']"
      :data-i="row.i"
      class="row-cv grid grid-cols-[22px_66px_minmax(0,1fr)] gap-x-1.5 gap-y-1 items-center p-1.5 rounded-md border transition-colors">
    <span class="font-mono text-[11px] text-faint text-right">{{ row.no }}</span>
    <input :aria-label="`第 ${row.i + 1} 行的开始秒`" :class="row.manual ? '!border-cyan' : ''"
           :title="`开始（秒）${row.manual ? '・手动' : '・自动'}`"
           :value="row.start.toFixed(2)" class="!h-[26px] !py-0.5 !px-1 text-xs w-full mono" min="0"
           step="0.01" type="number"
           @change="onTime">
    <input v-if="editing && editing.i === row.i" ref="editInput" v-model="editing.value"
           class="!h-[26px] !py-0.5 !px-1.5 text-[13px] w-full"
           title="记法（/ 分隔・*强调*・行尾 !・| 注释・[间奏 8]）同样可用。Enter 确认、Esc 取消"
           type="text"
           @blur="finishEdit(true)" @keydown.enter.prevent="finishEdit(true)" @keydown.esc.prevent="finishEdit(false)">
    <span v-else :class="{ 'italic': row.interlude }" :title="row.full" class="truncate cursor-pointer"
          @click="seekLine">{{ row.label }}</span>
    <div class="col-span-2 col-start-2 flex justify-between items-start gap-1.5 flex-wrap">
      <span class="flex gap-1 flex-wrap min-w-0 flex-[1_1_140px]">
        <select v-for="cell in row.cuts" :key="cell.k"
                :aria-label="`第 ${row.i + 1} 行 切分 ${cell.k + 1} 的布局`"
                :class="{ '!border-amber': cell.forced }"
                :data-cur="cell.forced || cell.layout"
                :style="{ borderColor: `hsla(${cell.hue},70%,58%,0.7)` }" :title="cell.title"
                class="!h-[22px] !py-0 !px-1 text-[10px] max-w-[112px] w-auto border border-line2 rounded bg-raised text-fg"
                @blur="onTechBlur" @change="onCutLayout(cell, $event)"
                @focus="onTechOpen" @pointerdown="onCutSelectDown(cell, $event)"></select>
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
        <select v-if="!row.interlude" :aria-label="`第 ${row.i + 1} 行布局指定`" :data-cur="row.layout"
                class="!h-auto !py-0.5 !px-1 text-[11px] max-w-[92px] w-auto"
                @blur="onTechBlur" @change="onLayout" @focus="onTechOpen"></select>
        <button :aria-label="`从第 ${row.i + 1} 行开始打点`" class="btn btn-icon btn-ghost"
                title="从这一行开始重新打点同步" @click="startTap(row.i)">
          <svg fill="none" stroke="currentColor" stroke-width="1.4" viewBox="0 0 16 16"><circle cx="8" cy="8"
                                                                                                fill="currentColor"
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
          <svg fill="none" stroke="currentColor" stroke-width="1.4" viewBox="0 0 16 16"><rect height="7" rx="1.5"
                                                                                              width="10"
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
