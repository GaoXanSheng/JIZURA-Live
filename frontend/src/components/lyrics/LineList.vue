<script setup lang="ts">
/* 行与切分列表（原 12_ui.js renderLines 的 Vue 化）
   行本体在 LineRow.vue：跨行只重渲新旧两行，replan 时才全量重建行数据 */
import {computed, onMounted, onUnmounted, ref} from 'vue';
import {J} from '../../engine';
import {S} from '../../jizura/state';
import {bus} from '../../jizura/bus';
import {exportRangeLines} from '../../jizura/look';
import {useEditor} from '../../stores/editor';
import type {Row} from './LineRow.vue';
import LineRow from './LineRow.vue';

const ed = useEditor();
const offs: (() => void)[] = [];

const rows = ref<Row[]>([]);
const infoText = computed(() => `${ed.linesCount} 行 / ${ed.cutsCount} 切分`);
const layoutHue = (k: string) => (J.LAYOUT_ORDER.indexOf(k) * 37 + 30) % 360;

function rebuild() {
  if (!S.plan) { rows.value = []; return; }
  const ov = S.project.overrides, R = exportRangeLines();
  rows.value = S.plan.lines.map((ln: any, i: number): Row => {
    const o = ov[i] || {};
    const manual = S.project.timing.lineTimes && S.project.timing.lineTimes[i] != null;
    const label = ln.interlude ? `〔间奏${ln.secs ? ' ' + ln.secs + '秒' : ''}〕` : ln.text;
    const cuts = S.plan.cuts
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
}

onMounted(() => {
  offs.push(bus.on('replan', rebuild));
  offs.push(bus.on('replan-sync', rebuild));
  offs.push(bus.on('sync-ui', rebuild));
  rebuild();
});
onUnmounted(() => offs.forEach(f => f()));
</script>

<template>
  <div class="sec">
    <div class="sec-h"><span class="sec-title">行与切分</span><span class="font-mono text-mut text-[11px]">{{ infoText }}</span></div>
    <ol class="list-none m-0 p-0 grid gap-1.5">
      <LineRow v-for="r in rows" :key="r.i" :active="ed.curLine === r.i" :row="r"/>
    </ol>
  </div>
</template>
