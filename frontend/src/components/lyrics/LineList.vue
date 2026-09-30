<script setup lang="ts">
/* 行与切分列表（原 12_ui.js renderLines 的 Vue 化）
   planVersion 变化时重建行数据；逐行工具直接改 project.overrides / timing.lineTimes */
import {computed, onMounted, onUnmounted, ref} from 'vue';
import {J} from '../../engine';
import {S} from '../../jizura/state';
import {bus} from '../../jizura/bus';
import {replan, seek} from '../../jizura/engine';
import {
  commit,
  exportRangeLines,
  pushEdit,
  remember,
  setCutTech,
  setExportRange,
  setOv,
  startTap,
  toast,
} from '../../jizura/look';
import {flushSave} from '../../jizura/project';
import {probeRecord} from '../../jizura/probe';
import {useEditor} from '../../stores/editor';

const ed = useEditor();
const offs: (() => void)[] = [];

interface CutCell { k: number; layout: string; forced: string | null; hue: number; title: string; start: number; half: number }
interface Row {
  i: number; no: string; start: number; label: string; full: string; interlude: boolean;
  manual: boolean; layout: string; ncut: string; locked: boolean; inRange: boolean;
  cuts: CutCell[];
}

const rows = ref<Row[]>([]);
const infoText = computed(() => `${ed.linesCount} 行 / ${ed.cutsCount} 切分`);
const layoutOpts = computed(() => [{ v: '', t: '自动' }, ...J.LAYOUT_ORDER.map((k: string) => ({ v: k, t: J.LAYOUTS[k].name }))]);
const cutCountOpts = [1, 2, 3, 4, 5, 6];
const editing = ref<{ i: number; pre: string; body: string; value: string } | null>(null);
const editInput = ref<HTMLInputElement | null>(null);

const layoutHue = (k: string) => (J.LAYOUT_ORDER.indexOf(k) * 37 + 30) % 360;

function rebuild() {
  const __t0 = performance.now();
  if (!S.plan) { rows.value = []; return; }
  const ov = S.project.overrides, R = exportRangeLines();
  rows.value = S.plan.lines.map((ln: any, i: number): Row => {
    const o = ov[i] || {};
    const manual = S.project.timing.lineTimes && S.project.timing.lineTimes[i] != null;
    const label = ln.interlude ? `〔间奏${ln.secs ? ' ' + ln.secs + '秒' : ''}〕` : ln.text;
    const cuts: CutCell[] = S.plan.cuts
      .filter((c: any) => c.line === i && J.LAYOUTS[c.layout] && !J.LAYOUTS[c.layout].special)
      .map((c: any, k: number) => ({
        k, layout: c.layout,
        forced: (o.cutLayouts && (o.cutLayouts[k] ?? o.cutLayouts[String(k)])) || null,
        hue: layoutHue(c.layout),
        title: `${c.text}｜${J.ENTER[c.enter].name} → ${J.EXIT[c.exit].name}`,
        start: c.start,
        half: Math.min(c.dur * 0.5, c.inDur + 0.05),
      }));
    return {
      i, no: String(i + 1).padStart(2, '0'),
      start: ln.start, label, full: label, interlude: !!ln.interlude,
      manual, layout: o.layout || '', ncut: o.cuts ? String(o.cuts) : '',
      locked: !!o.lock, inRange: !!(R && i >= R.from && i <= R.to),
      cuts,
    };
  });
  probeRecord('lines:rebuild', performance.now() - __t0);
}

onMounted(() => {
  offs.push(bus.on('replan', rebuild));
  offs.push(bus.on('replan-sync', rebuild));
  offs.push(bus.on('sync-ui', rebuild));
  rebuild();
});
onUnmounted(() => offs.forEach(f => f()));

function onTime(i: number, ev: Event) {
  const v = parseFloat((ev.target as HTMLInputElement).value);
  pushEdit();
  if (!S.project.timing.lineTimes) S.project.timing.lineTimes = {};
  if (isFinite(v)) S.project.timing.lineTimes[i] = Math.max(0, v); else delete S.project.timing.lineTimes[i];
  replan();
}
function seekLine(ln: { start: number }) { seek(ln.start + 0.001); }
function startEdit(i: number) {
  const ln = S.plan.lines[i];
  if (ln.src == null) return;
  const rowList = S.project.lyrics.replace(/\r/g, '').split('\n');
  const row = rowList[ln.src] || '';
  const pre = (row.match(/^\s*(?:\[\d+:\d+(?:[.:]\d+)?\])*/) || [''])[0];
  const body = row.slice(pre.length).trim();
  editing.value = { i, pre, body, value: body };
  requestAnimationFrame(() => editInput.value?.focus());
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
  replan(); flushSave();
  toast(`第 ${ln.index + 1} 行歌词已修改`);
}
function onLayout(i: number, ev: Event) { setOv(i, { layout: (ev.target as HTMLSelectElement).value || undefined }); replan(); }
function onNcut(i: number, lnStart: number, ev: Event) {
  remember();
  setOv(i, { cuts: +(ev.target as HTMLSelectElement).value || undefined, single: undefined });
  replan(); commit(); seek(lnStart + 0.001);
}
function onDice(i: number) {
  const cur = S.project.overrides[i] || {};
  setOv(i, { seed: ((cur.seed | 0) + 1), lock: false, lockedSeed: undefined, lockedCuts: undefined });
  replan(); seek(S.plan.lines[i].start + 0.001);
}
function onLock(i: number) {
  const cur = S.project.overrides[i] || {};
  if (cur.lock) setOv(i, { lock: false, lockedSeed: undefined, lockedCuts: undefined });
  else setOv(i, { lock: true, lockedSeed: S.plan.lines[i].seed, lockedCuts: J.lineSnapshot(S.plan, i) || undefined });
  replan();
}
function onCutLayout(i: number, cell: CutCell, ev: Event) {
  setCutTech(i, cell.k, 'layout', (ev.target as HTMLSelectElement).value);
  replan(); seek(cell.start + cell.half);
}
function seekCut(cell: CutCell) { seek(cell.start + cell.half); }
function onRng(i: number, ev: MouseEvent) { setExportRange(i, ev.shiftKey); }
</script>

<template>
  <div class="sec">
    <div class="sec-h"><span class="sec-title">行与切分</span><span class="font-mono text-mut text-[11px]">{{ infoText }}</span></div>
    <ol class="list-none m-0 p-0 grid gap-1.5">
      <li v-for="r in rows" :key="r.i"
          class="row-cv grid grid-cols-[22px_66px_minmax(0,1fr)] gap-x-1.5 gap-y-1 items-center p-1.5 rounded-md border transition-colors"
          :class="[ed.curLine === r.i ? 'border-line2 bg-raised shadow-[inset_2px_0_0_var(--color-amber)]' : 'border-transparent hover:bg-raised', r.inRange ? '!border-amber' : '']">
        <span class="font-mono text-[11px] text-faint text-right">{{ r.no }}</span>
        <input class="!h-[26px] !py-0.5 !px-1 text-xs w-full mono" :class="r.manual ? '!border-cyan' : ''" type="number" step="0.01" min="0" :value="r.start.toFixed(2)"
               :title="`开始（秒）${r.manual ? '・手动' : '・自动'}`" :aria-label="`第 ${r.i + 1} 行的开始秒`"
               @change="onTime(r.i, $event)">
        <input v-if="editing && editing.i === r.i" ref="editInput" v-model="editing.value" class="!h-[26px] !py-0.5 !px-1.5 text-[13px] w-full" type="text"
               title="记法（/ 分隔・*强调*・行尾 !・| 注释・[间奏 8]）同样可用。Enter 确认、Esc 取消"
               @keydown.enter.prevent="finishEdit(true)" @keydown.esc.prevent="finishEdit(false)" @blur="finishEdit(true)">
        <span v-else class="truncate cursor-pointer" :class="{ 'italic': r.interlude }" :title="r.full" @click="seekLine(r)">{{ r.label }}</span>
        <div class="col-span-2 col-start-2 flex justify-between items-start gap-1.5 flex-wrap">
          <span class="flex gap-1 flex-wrap min-w-0 flex-[1_1_140px]">
            <select v-for="cell in r.cuts" :key="cell.k" class="!h-[22px] !py-0 !px-1 text-[10px] max-w-[112px] w-auto border border-line2 rounded bg-raised text-fg"
                    :class="{ '!border-amber': cell.forced }"
                    :style="{ borderColor: `hsla(${cell.hue},70%,58%,0.7)` }"
                    :value="cell.forced || cell.layout" :title="cell.title" :aria-label="`第 ${r.i + 1} 行 切分 ${cell.k + 1} 的布局`"
                    @pointerdown="seekCut(cell)" @change="onCutLayout(r.i, cell, $event)">
              <option v-for="o in layoutOpts" :key="o.v" :value="o.v">{{ o.t }}</option>
            </select>
          </span>
          <span class="flex gap-1 flex-wrap justify-end ml-auto">
            <button class="btn btn-icon btn-ghost" title="修改这一行的歌词" :aria-label="`修改第 ${r.i + 1} 行歌词`" @click="startEdit(r.i)">
              <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4"><path d="M3 13l1-3.5L11 2.5l2.5 2.5L6.5 12z"/><path d="M9.5 4l2.5 2.5"/></svg>
            </button>
            <select v-if="!r.interlude" class="!h-auto !py-0.5 !px-1 text-[11px] max-w-[92px] w-auto" :value="r.ncut" :aria-label="`第 ${r.i + 1} 行的切分数`" @change="onNcut(r.i, r.start, $event)">
              <option value="">切分 自动</option>
              <option v-for="n in cutCountOpts" :key="n" :value="String(n)">切分 {{ n }}</option>
            </select>
            <select v-if="!r.interlude" v-model="rows[r.i].layout" class="!h-auto !py-0.5 !px-1 text-[11px] max-w-[92px] w-auto" :aria-label="`第 ${r.i + 1} 行布局指定`" @change="onLayout(r.i, $event)">
              <option v-for="o in layoutOpts" :key="o.v" :value="o.v">{{ o.t }}</option>
            </select>
            <button class="btn btn-icon btn-ghost" title="从这一行开始重新打点同步" :aria-label="`从第 ${r.i + 1} 行开始打点`" @click="startTap(r.i)">
              <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4"><circle cx="8" cy="8" r="2.2" fill="currentColor"/><circle cx="8" cy="8" r="5.5"/></svg>
            </button>
            <button class="btn btn-icon btn-ghost" :class="r.inRange ? '!text-amber' : ''" :aria-pressed="r.inRange" title="设为导出范围（Shift+点击扩大范围）" :aria-label="`把第 ${r.i + 1} 行设为导出范围`" @click="onRng(r.i, $event)">
              <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4"><path d="M3 3v10M13 3v10"/><path d="M5.5 8h5M8.5 5.5L11 8l-2.5 2.5"/></svg>
            </button>
            <button v-if="!r.interlude" class="btn btn-icon btn-ghost" title="重抽这一行" @click="onDice(r.i)">
              <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4"><rect x="2" y="2" width="12" height="12" rx="2"/><circle cx="5.5" cy="5.5" r="1" fill="currentColor"/><circle cx="10.5" cy="10.5" r="1" fill="currentColor"/><circle cx="10.5" cy="5.5" r="1" fill="currentColor"/><circle cx="5.5" cy="10.5" r="1" fill="currentColor"/></svg>
            </button>
            <button v-if="!r.interlude" class="btn btn-icon btn-ghost" :class="r.locked ? '!text-amber' : ''" :aria-pressed="r.locked" title="锁定这一行的构成" @click="onLock(r.i)">
              <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4"><rect x="3" y="7" width="10" height="7" rx="1.5"/><path d="M5 7V5a3 3 0 0 1 6 0v2"/></svg>
            </button>
          </span>
        </div>
      </li>
    </ol>
  </div>
</template>

<style scoped>
.ln.in-range { border-color: var(--color-amber); }

/* 不可见的行不参与排版/绘制：长歌词列表的布局成本随行数线性涨，
   content-visibility 让视口外的行跳过 layout+paint（可交互性保留，
   滚动到即渲染）；72px 为估算行高，首次排版后浏览器自动替换为实测值 */
.row-cv {
  content-visibility: auto;
  contain-intrinsic-size: auto 72px;
}
</style>
