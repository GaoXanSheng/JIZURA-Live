<script lang="ts" setup>
/* 渲染调试面板（Ctrl+Alt+D 呼出）：折叠图标条（FPS/CDP 点）+ 展开弹窗
   （帧与主线程、CDP 精确模式、元素标框）。
   EditorPage 常挂载，内部按 devUi.open 显隐；采集器启停逻辑在 jizura/devui/index.ts。 */
import {computed, onMounted, onUnmounted, ref, watch} from 'vue';
import {devUi, toggleDevUi} from '../../jizura/devui';
import {type FrameSnapshot, framesSnapshot} from '../../jizura/devui/frames';
import type {CdpMetrics} from '../../jizura/devui/cdp';
import {cdpConnect, cdpDisconnect, cdpState, recordPipeline} from '../../jizura/devui/cdp';
import type {TraceReport} from '../../jizura/devui/traceparse';
import {boxerLocate, boxerTop, boxerTracked, type TopChanger} from '../../jizura/devui/boxer';

const data = ref<FrameSnapshot | null>(null);
const top = ref<TopChanger[]>([]);
const tracked = ref(0);
const cdpUi = ref<{ status: string; error: string; port: number; metrics: CdpMetrics | null }>({
  status: 'off', error: '', port: 9222, metrics: null,
});
const trace = ref<TraceReport | null>(null);
const tracing = ref(false);

/* ---- 自由拖拽：折叠条整体 / 展开面板标题栏可拖，位置记忆 ---- */
const POS_LS = 'jizura.devui.pos';
const rootEl = ref<HTMLElement | null>(null);
const pos = ref<{ x: number; y: number } | null>(readPos());
let dragOff = {x: 0, y: 0};
let didDrag = false;

function readPos(): { x: number; y: number } | null {
  try {
    const j = JSON.parse(localStorage.getItem(POS_LS) || 'null');
    if (j && Number.isFinite(j.x) && Number.isFinite(j.y)) return j;
  } catch { /* 损坏则回退默认位置 */
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
  pos.value = {x: r.left, y: r.top};       // 由默认 right 定位切换为 left/top
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

/* 折叠条：拖拽结束时不算点击（避免拖完顺手展开） */
function pillClick() {
  if (!didDrag) devUi.expanded = true;
}

const posStyle = computed(() =>
    pos.value ? {left: `${pos.value.x}px`, top: `${pos.value.y}px`, right: 'auto'} : undefined);

let timer = 0;

function tick() {
  data.value = framesSnapshot();
  top.value = boxerTop(8);
  tracked.value = boxerTracked();
  cdpUi.value = {status: cdpState.status, error: cdpState.error, port: cdpState.port, metrics: cdpState.metrics};
}

onMounted(() => {
  tick();
  if (pos.value) pos.value = clampPos(pos.value);   // 记忆位置可能因窗口变小越界
  timer = window.setInterval(tick, 500);
});
onUnmounted(() => window.clearInterval(timer));

/* ---- sparkline ---- */
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

/* ---- 展示格式化 ---- */
const n1 = (v: number) => (Number.isFinite(v) ? v.toFixed(1) : '—');
const fpsCls = (f?: number) => (!f ? 'text-mut' : f >= 55 ? 'text-cyan' : f >= 40 ? 'text-amber' : 'text-danger');
const typeText = (t: string) => (({geom: '几何', style: '样式', content: '内容'} as any)[t] || '—');

/* ---- CDP ---- */
function connectCdp() {
  cdpConnect();
}

function disconnectCdp() {
  devUi.paintFlash = false;
  cdpDisconnect();
}

async function doRecord() {
  if (tracing.value) return;
  tracing.value = true;
  try {
    trace.value = await recordPipeline(3000);
  } finally {
    tracing.value = false;
  }
}
</script>

<template>
  <Teleport to="body">
    <div v-if="devUi.open" ref="rootEl"
         :style="posStyle"
         class="fixed top-2 right-2 z-[6000] font-mono text-[10px] leading-[1.6] text-left select-none">
      <!-- 折叠图标条 -->
      <div v-if="!devUi.expanded"
           class="flex items-center gap-2.5 bg-ink/90 border border-line2 rounded-md px-2.5 py-1.5 shadow-pop cursor-move"
           title="渲染调试面板 — 点击展开 / 拖拽移动（Ctrl+Alt+D 开关）"
           @click="pillClick"
           @mousedown="onDragStart">
        <span :class="fpsCls(data?.fps)">FPS {{ data ? data.fps.toFixed(0) : '—' }}</span>
        <span :class="cdpUi.status === 'connected' ? 'bg-cyan' : cdpUi.status === 'connecting' ? 'bg-amber' : 'bg-faint'"
              :title="'CDP ' + cdpUi.status"
              class="w-1.5 h-1.5 rounded-full"></span>
        <span class="text-mut hover:text-danger cursor-pointer pl-1" title="关闭面板（Ctrl+Alt+D）"
              @click.stop="toggleDevUi(false)">×</span>
      </div>

      <!-- 展开面板 -->
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

        <!-- 帧与主线程 -->
        <div class="px-2.5 py-2 border-b border-line2">
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

        <!-- CDP 精确模式 -->
        <div class="px-2.5 py-2 border-b border-line2">
          <div class="flex items-center justify-between mb-1">
            <span class="text-mut">CDP 精确模式</span>
            <span v-if="cdpUi.status === 'connected'" class="text-cyan">已连接 :{{ cdpUi.port }}</span>
            <span v-else-if="cdpUi.status === 'connecting'" class="text-amber">连接中…</span>
            <span v-else class="text-faint">{{ cdpUi.status === 'off' ? '未连接' : '不可用' }}</span>
          </div>
          <div class="flex items-center gap-3 mb-1">
            <button class="btn btn-accent !py-0.5 !px-2 !text-[10px]"
                    @click="cdpUi.status === 'connected' ? disconnectCdp() : connectCdp()">
              {{ cdpUi.status === 'connected' ? '断开' : '连接' }}
            </button>
            <label class="checkline !gap-1"><input v-model="devUi.paintFlash" type="checkbox"> 原生 Paint 闪烁</label>
          </div>
          <div v-if="cdpUi.error" class="text-amber mb-1">{{ cdpUi.error }}</div>
          <template v-if="cdpUi.metrics">
            <div>布局 {{ n1(cdpUi.metrics.layoutPerSec) }}/s {{ n1(cdpUi.metrics.layoutMsPerSec) }}ms/s
              · 样式 {{ n1(cdpUi.metrics.recalcPerSec) }}/s {{ n1(cdpUi.metrics.recalcMsPerSec) }}ms/s
            </div>
            <div>脚本 {{ n1(cdpUi.metrics.scriptMsPerSec) }}ms/s · 任务 {{ n1(cdpUi.metrics.taskMsPerSec) }}ms/s</div>
          </template>
          <div class="flex items-center gap-2 mt-1">
            <button :disabled="tracing || cdpUi.status !== 'connected'"
                    class="btn btn-accent !py-0.5 !px-2 !text-[10px]" @click="doRecord">
              {{ tracing ? '录制中…' : '录制 3s 管线' }}
            </button>
            <span v-if="trace" class="text-faint">{{ trace.frames }} 帧 / {{ n1(trace.spanMs / 1000) }}s</span>
          </div>
          <div v-if="trace && trace.stages.length" class="mt-1">
            <div v-for="st in trace.stages.slice(0, 6)" :key="st.name">
              {{ st.label }} {{ st.count }} 次 · {{ n1(st.totalMs) }}ms · max {{ n1(st.maxMs) }}ms
            </div>
          </div>
        </div>

        <!-- 元素标框 -->
        <div class="px-2.5 py-2">
          <div class="flex items-center justify-between mb-1">
            <span class="text-mut">元素标框</span>
            <span class="text-faint">{{ tracked }} 元素在册</span>
          </div>
          <div class="flex gap-4 mb-1">
            <label class="checkline !gap-1"><input v-model="devUi.flash" type="checkbox"> 重绘闪烁</label>
            <label class="checkline !gap-1"><input v-model="devUi.hover" type="checkbox"> hover 详情</label>
          </div>
          <div v-if="top.length" class="text-faint">最近 1s 变更最多（点击定位）：</div>
          <div v-for="(t, i) in top" :key="i" :title="t.label + ' — 点击滚动定位'"
               class="cursor-pointer hover:text-amber truncate" @click="boxerLocate(t.el)">
            {{ t.label }}<span v-if="t.comp" class="text-faint"> ⟨{{ t.comp }}⟩</span>
            <span :class="t.win > 30 ? 'text-danger' : ''"> ×{{ t.win }}/s</span>
            <span class="text-faint"> {{ typeText(t.type) }}</span>
          </div>
          <div v-if="!top.length" class="text-faint">未检测到变更（开启重绘闪烁后操作页面观察）</div>
        </div>
      </div>
    </div>
  </Teleport>
</template>
