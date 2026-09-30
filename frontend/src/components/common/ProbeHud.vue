<script setup lang="ts">
/* 性能探针 HUD：?probe=1 或 Ctrl+Alt+P 激活后显示，每 500ms 刷新 */
import { onMounted, onUnmounted, ref } from 'vue';
import { dump } from '../../jizura/probe';

const data = ref<any>(null);
let timer = 0;

onMounted(() => {
  const tick = () => { data.value = dump(); };
  tick();
  timer = window.setInterval(tick, 500);
});
onUnmounted(() => window.clearInterval(timer));

function fmtMarks() {
  const d = data.value;
  if (!d) return [];
  return d.marks.slice(0, 14).map((m: any) => `${m.name}  n=${m.count}  avg=${m.avg}  max=${m.max}  last=${m.last}`);
}
</script>

<template>
  <div class="fixed bottom-2 left-2 z-[5000] pointer-events-none font-mono text-[10px] leading-[1.5] text-left">
    <div class="bg-ink/85 border border-line2 rounded-md px-2.5 py-2 max-w-[420px]">
      <div class="text-amber mb-1">PERF PROBE <span v-if="data" class="text-mut">{{ data.frame.count }}f</span></div>
      <template v-if="data">
        <div>fps~{{ data.frame.gapAvg ? (1000 / Math.max(1, data.frame.gapAvg)).toFixed(1) : '-' }}  gapAvg {{ data.frame.gapAvg?.toFixed(1) }}ms  gapMax {{ data.frame.gapMax?.toFixed(0) }}ms  dropped {{ data.frame.dropped }}</div>
        <div>longtask {{ data.longTasks.count }} (worst {{ data.longTasks.worstMs?.toFixed(0) }}ms)  heap {{ data.memoryMB ?? '-' }}MB</div>
        <div v-for="l in fmtMarks()" :key="l" class="whitespace-pre">{{ l }}</div>
      </template>
    </div>
  </div>
</template>
