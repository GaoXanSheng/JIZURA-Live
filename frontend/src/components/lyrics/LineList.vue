<script lang="ts" setup>
// 行本体在 LineRow.vue，跨行只重渲新旧两行，replan 时才全量重建行数据。
// 窗口化渲染：82+ 行全量挂载光歌词就 3.5k+ 节点，这里只挂可视区 ± 缓冲的行，
// 上下用定高 spacer 占位。行高渲染后量 offsetHeight 回填，没渲染的按 72px 估
// （行自带 content-visibility，首次可见后真实高度会补上，滚动锚定误差自愈）。
// 'scrolltoline' 播放行跟随走纯坐标计算，没挂载的行也能滚，滚到即挂。
// 编辑态和打开的下拉随滚出视口销毁、滚回重建，数据都在 rows 里，无损失。
import {computed, nextTick, onMounted, onUnmounted, ref, watch} from 'vue';
import {J} from '../../engine';
import {S} from '../../jizura/state';
import {bus} from '../../jizura/bus';
import {exportRangeCuts} from '../../jizura/look';
import {useEditor} from '../../stores/editor';
import type {Row} from './LineRow.vue';
import LineRow from './LineRow.vue';

const ed = useEditor();
const offs: (() => void)[] = [];

const rows = ref<Row[]>([]);
const infoText = computed(() => `${ed.linesCount} 行 / ${ed.cutsCount} 切分`);
const layoutHue = (k: string) => (J.LAYOUT_ORDER.indexOf(k) * 37 + 30) % 360;

const listEl = ref<HTMLOListElement | null>(null);
let scroller: HTMLElement | null = null;
const EST = 72, BUF = 420;             // 行高估算 / 上下缓冲，px
let rowH: number[] = [];
let pos: number[] = [0];               // 行首坐标前缀和
const win = ref({from: 0, to: 0, top: 0, bottom: 0});
let rafId = 0;

function rebuild() {
  if (!S.plan) {
    rows.value = [];
    return;
  }
  const ov = S.project.overrides, R = exportRangeCuts();
  // 每行的切分序号区间。导出范围按切分算：行的切分和范围相交就算命中
  const span: Record<number, [number, number]> = {};
  for (const c of S.plan.cuts as any[]) {
    const s = span[c.line] || (span[c.line] = [c.index, c.index]);
    s[0] = Math.min(s[0], c.index);
    s[1] = Math.max(s[1], c.index);
  }
  rows.value = S.plan.lines.map((ln: any, i: number): Row => {
    const o = ov[i] || {};
    const manual = S.project.timing.lineTimes && S.project.timing.lineTimes[i] != null;
    const label = ln.interlude ? `〔间奏${ln.secs ? ' ' + ln.secs + '秒' : ''}〕` : ln.text;
    const cuts = S.plan.cuts
        .filter((c: any) => c.line === i && J.LAYOUTS[c.layout] && !J.LAYOUTS[c.layout].special)
        .map((c: any, k: number) => ({
          k, layout: c.layout,
          forced: (o.cutLayouts && (o.cutLayouts[k] ?? o.cutLayouts[String(k)])) || null,
          ct: (o.cutTime && o.cutTime[k] != null && isFinite(+o.cutTime[k])) ? +o.cutTime[k] : null,
          hue: layoutHue(c.layout),
          title: `${c.text}｜${J.ENTER[c.enter].name} → ${J.EXIT[c.exit].name}`,
          start: c.start,
          half: Math.min(c.dur * 0.5, c.inDur + 0.05),
        }));
    const sp = span[i];
    return {
      i, no: String(i + 1).padStart(2, '0'),
      start: ln.start, label, full: label, interlude: !!ln.interlude,
      manual, layout: o.layout || '', ncut: o.cuts ? String(o.cuts) : '',
      locked: !!o.lock, inRange: !!(R && sp && sp[0] <= R.to && sp[1] >= R.from),
      cuts,
    };
  });
}

function rebuildPos() {
  pos = new Array(rows.value.length + 1);
  pos[0] = 0;
  for (let i = 0; i < rows.value.length; i++) pos[i + 1] = pos[i] + (rowH[i] || EST);
}

function measure(): boolean {
  const ol = listEl.value;
  if (!ol) return false;
  let changed = false;
  for (const li of ol.querySelectorAll<HTMLLIElement>('li[data-i]')) {
    const i = +(li.dataset.i || 0);
    const h = li.offsetHeight;
    if (h > 0 && Math.abs((rowH[i] || 0) - h) > 1) {
      rowH[i] = h;
      changed = true;
    }
  }
  if (changed) rebuildPos();
  return changed;
}

function updateWindow() {
  const ol = listEl.value;
  if (!ol || !scroller || !rows.value.length) {
    win.value = {from: 0, to: rows.value.length, top: 0, bottom: 0};
    return;
  }
  const olTop = ol.getBoundingClientRect().top - scroller.getBoundingClientRect().top + scroller.scrollTop;
  const vTop = scroller.scrollTop - olTop - BUF;
  const vBot = vTop + scroller.clientHeight + BUF * 2;
  let from = 0;
  while (from < rows.value.length && pos[from + 1] <= vTop) from++;
  let to = from;
  while (to < rows.value.length && pos[to] < vBot) to++;
  const to2 = Math.min(rows.value.length, to + 1);
  win.value = {from, to: to2, top: pos[from], bottom: Math.max(0, pos[rows.value.length] - pos[to2])};
}

function onScrollFrame() {
  rafId = 0;
  measure();
  updateWindow();
}

function requestUpdate() {
  if (!rafId) rafId = requestAnimationFrame(onScrollFrame);
}

function scrollToLine(i: number) {
  if (!scroller || !listEl.value || i < 0 || i >= rows.value.length) return;
  const olTop = listEl.value.getBoundingClientRect().top - scroller.getBoundingClientRect().top + scroller.scrollTop;
  scroller.scrollTop = olTop + Math.max(0, pos[i] - scroller.clientHeight * 0.35);
  requestUpdate();
}

function getScrollParent(el: HTMLElement): HTMLElement | null {
  let p = el.parentElement;
  while (p) {
    const o = getComputedStyle(p).overflowY;
    if (o === 'auto' || o === 'scroll') return p;
    p = p.parentElement;
  }
  return null;
}

onMounted(async () => {
  offs.push(bus.on('replan', rebuild));
  offs.push(bus.on('replan-sync', rebuild));
  offs.push(bus.on('sync-ui', rebuild));
  offs.push(bus.on('scrolltoline', scrollToLine));
  offs.push(bus.on('viewresize', requestUpdate));
  rebuild();
  await nextTick();
  if (listEl.value) scroller = getScrollParent(listEl.value);
  if (scroller) scroller.addEventListener('scroll', requestUpdate, {passive: true});
  window.addEventListener('resize', requestUpdate);
  rebuildPos();
  updateWindow();
  await nextTick();
  measure();
  updateWindow();
});
onUnmounted(() => {
  if (rafId) cancelAnimationFrame(rafId);
  if (scroller) scroller.removeEventListener('scroll', requestUpdate);
  window.removeEventListener('resize', requestUpdate);
  offs.forEach(f => f());
});

watch(rows, async () => {
  rebuildPos();
  await nextTick();
  measure();
  updateWindow();
});
</script>

<template>
  <div class="sec">
    <div class="sec-h"><span class="sec-title">行与切分</span><span class="font-mono text-mut text-[11px]">{{
        infoText
      }}</span></div>
    <ol ref="listEl" class="list-none m-0 p-0 grid gap-1.5">
      <li v-if="win.top > 0" :style="{ height: win.top + 'px' }" aria-hidden="true"></li>
      <LineRow v-for="r in rows.slice(win.from, win.to)" :key="r.i" :active="ed.curLine === r.i" :row="r"/>
      <li v-if="win.bottom > 0" :style="{ height: win.bottom + 'px' }" aria-hidden="true"></li>
    </ol>
  </div>
</template>
