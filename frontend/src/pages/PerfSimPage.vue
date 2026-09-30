<script setup lang="ts">
/* 仿真性能测试页：/perfsim?fixture=jinge2&probe=1
   用真实歌曲夹具离线重放引擎负载，产出可判分的性能报告。
   驱动：tools/perf/run.ps1；夹具抓取：tools/perf/capture-fixture.ps1 */
import { onMounted, onUnmounted, ref } from 'vue';
import { startPerfSim } from '../jizura/perfsim';
import { probeEnabled } from '../jizura/probe';
import ProbeHud from '../components/common/ProbeHud.vue';

const snap = ref<any>(null);
const report = ref<any>(null);
let timer = 0;

onMounted(() => {
  startPerfSim();
  timer = window.setInterval(() => {
    const p = (window as any).__perfsim;
    snap.value = p ? { status: p.status, phase: p.phase, progress: p.progress, error: p.error, quick: p.quick } : null;
    if (p && p.report) report.value = p.report;
  }, 250);
});
onUnmounted(() => window.clearInterval(timer));

function download() { (window as any).__perfsim?.download(); }

const PHASE_LABEL: Record<string, string> = {
  boot: '准备', fonts: '字体', replan: '重排采样', sweep: '全曲扫描',
  realtime: '实时播放仿真', thumbs: '手法预览卡', done: '完成', error: '出错',
};
</script>

<template>
  <div class="min-h-screen bg-ink text-fg font-mono text-[13px] p-6 max-w-[900px] mx-auto">
    <h1 class="text-amber text-base mb-1">PERF SIM · 仿真性能测试</h1>
    <p class="text-mut mb-4">真实歌曲夹具 × 离线重放渲染负载。驱动脚本：tools\perf\run.ps1</p>

    <div v-if="snap && snap.status === 'error'" class="border border-red-500/60 rounded-md p-3 mb-4 text-red-400 whitespace-pre-wrap">{{ snap.error }}</div>

    <div v-if="snap && snap.status !== 'done' && snap.status !== 'error'" class="border border-line rounded-md p-3 mb-4">
      <div class="mb-2">{{ PHASE_LABEL[snap.status] || snap.status }} · {{ snap.phase }}</div>
      <div class="h-2 bg-raised rounded overflow-hidden mb-2">
        <div class="h-full bg-amber transition-all" :style="{ width: Math.round((snap.progress || 0) * 100) + '%' }"></div>
      </div>
      <div class="text-mut">{{ snap.quick ? JSON.stringify(snap.quick) : '…' }}</div>
    </div>

    <div v-if="report" class="border rounded-md p-3 mb-4" :class="report.verdict.pass ? 'border-emerald-500/60' : 'border-red-500/60'">
      <div class="text-base mb-2" :class="report.verdict.pass ? 'text-emerald-400' : 'text-red-400'">
        {{ report.verdict.pass ? 'PASS' : 'FAIL' }} · {{ report.song.title || report.song.audio }} （{{ report.song.duration }}s / {{ report.plan.cuts }} cuts）
      </div>
      <ul v-if="report.verdict.failures.length" class="text-red-400 mb-2 list-disc pl-5">
        <li v-for="f in report.verdict.failures" :key="f">{{ f }}</li>
      </ul>
      <div class="grid grid-cols-2 gap-x-8 gap-y-1">
        <div>replan p50 / max: <b>{{ report.replan.p50 }} / {{ report.replan.max }}</b> ms</div>
        <div>扫描 {{ report.sweep.frames }} 帧 @{{ report.sweep.hz }}Hz {{ report.sweep.canvasW }}px</div>
        <div>draw avg / p95 / max: <b>{{ report.sweep.draw.avg }} / {{ report.sweep.draw.p95 }} / {{ report.sweep.draw.max }}</b> ms</div>
        <div v-if="report.realtime">实时: 掉帧 <b>{{ report.realtime.droppedPct }}%</b> gapMax {{ report.realtime.gapMax }}ms longtask {{ report.realtime.longTasks.count }} ({{ report.realtime.longTasks.worstMs }}ms)</div>
        <div v-if="report.thumbs">预览卡 {{ report.thumbs.cards }} 张: paint avg {{ report.thumbs.paint.avg }}ms max {{ report.thumbs.paint.max }}ms</div>
        <div v-if="report.memory.afterRtMB">heap: {{ report.memory.beforeMB }} → {{ report.memory.afterSweepMB }} → {{ report.memory.afterRtMB }} MB</div>
      </div>
      <div class="mt-3 text-xs text-mut">最贵切分: <span v-for="(c, i) in report.sweep.worstCuts.slice(0, 5)" :key="i" class="mr-3">#{{ c.cut }} t={{ c.t }}s {{ c.layout }}/{{ c.enter }}→{{ c.exit }} max {{ c.max }}ms</span></div>
      <button class="mt-3 px-3 py-1 border border-line rounded hover:bg-raised" @click="download">下载报告 JSON</button>
    </div>

    <p v-if="!probeEnabled" class="text-mut text-xs">提示：URL 加 <code>?probe=1</code> 可同时打开实时探针 HUD（帧间隔/LongTask/埋点）。</p>
    <ProbeHud v-if="probeEnabled" />
  </div>
</template>
