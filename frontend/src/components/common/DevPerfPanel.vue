<script lang="ts" setup>
// EditorPage 常挂载，按 devUi.open 显隐；采集器启停在 jizura/devui/index.ts
import {computed, onMounted, onUnmounted, ref, watch} from 'vue';
import {devUi, toggleDevUi} from '../../jizura/devui';
import {type FrameSnapshot, framesSnapshot} from '../../jizura/devui/frames';
import {type EngineFrameRow, enginePerf} from '../../jizura/devui/engineperf';

const data = ref<FrameSnapshot | null>(null);
const epRows = ref<EngineFrameRow[]>([]);

const POS_LS = 'jizura.devui.pos';
const rootEl = ref<HTMLElement | null>(null);
const pos = ref<{ x: number; y: number } | null>(readPos());
let dragOff = {x: 0, y: 0};
let didDrag = false;

function readPos(): { x: number; y: number } | null {
  try {
    const j = JSON.parse(localStorage.getItem(POS_LS) || 'null');
    if (j && Number.isFinite(j.x) && Number.isFinite(j.y)) return j;
  } catch { // 损坏就回退默认位置
  }
  return null;
}

function clampPos(p: { x: number; y: number }) {
  const w = rootEl.value?.offsetWidth ?? 0, h = rootEl.value?.offsetHeight ?? 0;
  return {
    x: Math.min(Math.max(4, p.x), Math.max(4, window.innerWidth - w - 4)),
    y: Math.min(Math.max(4, p.y), Math.max(4, window.innerHeight - h - 4)),
  };
}

function onDragStart(e: MouseEvent) {
  if (e.button !== 0 || (e.target as HTMLElement).closest('input,button,label')) return;
  const el = rootEl.value;
  if (!el) return;
  const r = el.getBoundingClientRect();
  pos.value = {x: r.left, y: r.top};       // 默认是 right 定位，拖起来切成 left/top
  dragOff = {x: e.clientX - r.left, y: e.clientY - r.top};
  didDrag = false;
  window.addEventListener('mousemove', onDragMove);
  window.addEventListener('mouseup', onDragEnd);
}

function onDragMove(e: MouseEvent) {
  if (!pos.value) return;
  const next = clampPos({x: e.clientX - dragOff.x, y: e.clientY - dragOff.y});
  if (Math.abs(next.x - pos.value.x) > 1 || Math.abs(next.y - pos.value.y) > 1) didDrag = true;
  pos.value = next;
}

function onDragEnd() {
  window.removeEventListener('mousemove', onDragMove);
  window.removeEventListener('mouseup', onDragEnd);
  if (pos.value) {
    try {
      localStorage.setItem(POS_LS, JSON.stringify(pos.value));
    } catch {
    }
  }
}

// 折叠条拖完不算点击，免得顺手展开
function pillClick() {
  if (!didDrag) devUi.expanded = true;
}

const posStyle = computed(() =>
    pos.value ? {left: `${pos.value.x}px`, top: `${pos.value.y}px`, right: 'auto'} : undefined);

let timer = 0;

function tick() {
  data.value = framesSnapshot();
  epRows.value = enginePerf.snapshot();
}

onMounted(() => {
  tick();
  if (pos.value) pos.value = clampPos(pos.value);   // 记忆的位置可能因窗口变小越界
  timer = window.setInterval(tick, 500);
});
onUnmounted(() => window.clearInterval(timer));

const spark = ref<HTMLCanvasElement | null>(null);

function drawSpark() {
  const cv = spark.value;
  const ctx = cv?.getContext('2d');
  if (!cv || !ctx) return;
  const W = cv.width, H = cv.height;
  ctx.clearRect(0, 0, W, H);
  const hist = data.value?.fpsHistory || [];
  if (!hist.length) return;
  const max = Math.max(70, ...hist);
  const y60 = H - (60 / max) * (H - 4) - 2;      // 60fps 参考线
  ctx.strokeStyle = 'rgba(142,138,148,.4)';
  ctx.setLineDash([3, 3]);
  ctx.beginPath();
  ctx.moveTo(0, y60);
  ctx.lineTo(W, y60);
  ctx.stroke();
  ctx.setLineDash([]);
  ctx.strokeStyle = '#f5a50c';
  ctx.beginPath();
  hist.forEach((fps, i) => {
    const x = (i / Math.max(1, hist.length - 1)) * (W - 2) + 1;
    const y = H - Math.min(1, fps / max) * (H - 4) - 2;
    if (i) ctx.lineTo(x, y); else ctx.moveTo(x, y);
  });
  ctx.stroke();
}

watch(data, () => requestAnimationFrame(drawSpark));

const n1 = (v: number) => (Number.isFinite(v) ? v.toFixed(1) : '—');
const fpsCls = (f?: number) => (!f ? 'text-mut' : f >= 55 ? 'text-cyan' : f >= 40 ? 'text-amber' : 'text-danger');

// 引擎帧归因：只统计真渲染帧（缓存命中帧单独给命中率）
const epStats = computed(() => {
  const rows = epRows.value;
  if (!rows.length) return null;
  const real = rows.filter(r => !r.hit);
  const pct = (arr: number[], p: number) => {
    if (!arr.length) return NaN;
    const s = arr.slice().sort((a, b) => a - b);
    return s[Math.min(s.length - 1, Math.ceil(p * s.length) - 1)];
  };
  const avg = (arr: number[]) => arr.length ? arr.reduce((a, b) => a + b, 0) / arr.length : NaN;
  const totals = real.map(r => r.total);
  const byLayout = new Map<string, number[]>();
  real.forEach(r => {
    const a = byLayout.get(r.layout) || (byLayout.set(r.layout, []), byLayout.get(r.layout)!);
    a.push(r.total);
  });
  const top = [...byLayout.entries()]
      .map(([layout, ts]) => ({layout, n: ts.length, avg: avg(ts), max: Math.max(...ts)}))
      .sort((a, b) => b.avg - a.avg).slice(0, 3);
  const last = rows[rows.length - 1];
  return {
    n: rows.length,
    hitRate: rows.length ? (rows.length - real.length) / rows.length : 0,
    avg: avg(totals), p95: pct(totals, 0.95), worst: totals.length ? Math.max(...totals) : NaN,
    bg: avg(real.map(r => r.bg)), pass: avg(real.map(r => r.pass)), morph: avg(real.map(r => r.morph)),
    trans: avg(real.map(r => r.trans)), post: avg(real.map(r => r.post)),
    top, last,
  };
});
</script>

<template>
  <Teleport to="body">
    <div v-if="devUi.open" ref="rootEl"
         :style="posStyle"
         class="fixed top-2 right-2 z-[6000] font-mono text-[10px] leading-[1.6] text-left select-none">
      <div v-if="!devUi.expanded"
           class="flex items-center gap-2.5 bg-ink/90 border border-line2 rounded-md px-2.5 py-1.5 shadow-pop cursor-move"
           title="渲染调试面板 — 点击展开 / 拖拽移动（Ctrl+Alt+D 开关）"
           @click="pillClick"
           @mousedown="onDragStart">
        <span :class="fpsCls(data?.fps)">FPS {{ data ? data.fps.toFixed(0) : '—' }}</span>
        <span class="text-mut hover:text-danger cursor-pointer pl-1" title="关闭面板（Ctrl+Alt+D）"
              @click.stop="toggleDevUi(false)">×</span>
      </div>

      <div v-else
           class="w-[300px] max-h-[calc(100vh-1rem)] overflow-y-auto bg-ink/95 border border-line2 rounded-md shadow-modal">
        <div
            class="flex items-center justify-between px-2.5 py-1.5 border-b border-line2 sticky top-0 bg-ink/95 z-10 cursor-move"
            title="拖拽移动面板"
            @mousedown="onDragStart">
          <span class="text-amber tracking-[0.15em]">RENDER DEBUG</span>
          <span class="flex gap-2 text-mut">
            <span class="cursor-pointer hover:text-fg" @click="devUi.expanded = false">收起</span>
            <span class="cursor-pointer hover:text-danger" @click="toggleDevUi(false)">关闭</span>
          </span>
        </div>

        <div class="px-2.5 py-2">
          <div class="text-mut mb-1">帧与主线程</div>
          <canvas ref="spark" class="w-full mb-1" height="30" width="270"></canvas>
          <template v-if="data">
            <div>fps <b :class="fpsCls(data.fps)">{{ data.fps.toFixed(1) }}</b>
              · gap {{ n1(data.gapAvg) }} / p95 {{ n1(data.gapP95) }} / max {{ n1(data.gapMax) }}ms
            </div>
            <div>掉帧 {{ data.dropped }} · longtask {{ data.longTasks }}<span
                v-if="data.longTasks"> (worst {{ n1(data.longTaskWorstMs) }}ms)</span></div>
            <div>heap {{ data.heapMB ?? '—' }}MB · DOM {{ data.nodes }} 节点</div>
            <div v-if="data.vueUpdates">Vue 组件 {{ data.vueUpdates }}/s · avg {{ n1(data.vueUpdateAvgMs) }}ms · worst
              {{ n1(data.vueUpdateWorstMs) }}ms
            </div>
            <div v-else class="text-faint">Vue 组件耗时需 dev 构建才有数据</div>
          </template>
        </div>

        <div v-if="epStats" class="px-2.5 py-2 border-t border-line2">
          <div class="text-mut mb-1">引擎帧归因</div>
          <div>整帧缓存命中 <b class="text-cyan">{{ (epStats.hitRate * 100).toFixed(0) }}%</b>
            · 真渲染 {{ epStats.n - Math.round(epStats.n * epStats.hitRate) }}/{{ epStats.n }} 帧</div>
          <div>frame avg <b>{{ n1(epStats.avg) }}</b> / p95 {{ n1(epStats.p95) }} / max {{ n1(epStats.worst) }}ms</div>
          <div class="text-mut">分段均值：bg {{ n1(epStats.bg) }} · pass {{ n1(epStats.pass) }} · morph
            {{ n1(epStats.morph) }} · trans {{ n1(epStats.trans) }} · 后期 {{ n1(epStats.post) }} ms</div>
          <div v-if="epStats.last">cut #{{ epStats.last.cut + 1 }}：{{ epStats.last.layout }}
            · cam {{ epStats.last.cam || '—' }} · bg {{ epStats.last.bgId || '—' }} · decor {{ epStats.last.decor }}</div>
          <div v-if="epStats.top.length">
            <div class="text-mut">重版式 TOP（avg·max ms）</div>
            <div v-for="t in epStats.top" :key="t.layout">
              {{ t.layout }} <b :class="t.avg > 12 ? 'text-danger' : t.avg > 6 ? 'text-amber' : 'text-cyan'">{{ n1(t.avg) }}</b>
              · {{ n1(t.max) }}（{{ t.n }}帧）
            </div>
          </div>
        </div>
      </div>
    </div>
  </Teleport>
</template>
