<script setup lang="ts">
/* 纯 canvas 渲染页（100% 画布）：
   - Spout 渲染进程（SpoutPipeline.cs offscreen browser）加载本页出画面
   - 双窗口监看场景：编辑页经 BroadcastChannel 直通工程/时间/字幕（内存消息，不落盘）
   - C# 注入脚本（mirror）依赖的兼容面全部保留：
       window.J.ui          → stage store（reactive，注入脚本直接读写 t/need/plan）
       window.J.uiApi       → pause/replan
       canvas#view          → isolate/fitPlanSize 抓取目标
       __jlApplySync        → C# 时间锚点（保持暂停态 + 外推） */
import { onMounted, onUnmounted, ref, watch } from 'vue';
import { J } from '../engine';
import { useStage } from '../stores/stage';
import { bindStageBroadcast } from '../jizura/stage-bridge';
import { installSubtitleOverlay, setSubOverride } from '../cloud/subtitle';

const stage = useStage();
const canvas = ref<HTMLCanvasElement | null>(null);
const connected = ref(false);
let renderer: any = null;
let stopBridge: (() => void) | null = null;
let raf = 0;
let unwatch: (() => void) | null = null;

onMounted(() => {
  installSubtitleOverlay();
  renderer = new J.Renderer();
  const cv = canvas.value!;
  const stage = useStage();

  /* BroadcastChannel 直通：工程/字幕/时间全部来自编辑页 */
  stopBridge = bindStageBroadcast(stage.sink());
  connected.value = stage.connected;
  watch(() => stage.connected, v => { connected.value = v; });
  watch(() => [stage.subCfg, stage.subMap] as const, ([cfg, map]) => {
    if (cfg) setSubOverride(cfg, map);
  }, { immediate: true });

  /* Spout 注入脚本的兼容面：J.ui = store 本身（reactive，脚本直接读写） */
  J.ui = stage;
  J.uiApi = {
    toast: () => {},
    replan: () => stage.replan(),
    syncUI: () => {},
    pause: () => { stage.playing = false; },
    play: () => {},
    seek: (t: number) => { stage.t = t; stage.need = true; },
    flushSave: () => {},
    loadAudioFile: async () => false,
    restartPreview: () => {},
    exportRange: () => null,
    exportRangeLines: () => null,
  };

  /* 工程分辨率画布：plan 就绪时把物理像素设为 plan 尺寸（mirror 注入脚本也会保持） */
  unwatch = watch(() => stage.plan, () => fitPlanSize(cv), { deep: false });
  fitPlanSize(cv);

  /* 渲染泵：need 时画（Spout 注入泵/时间推送都会置 need） */
  const tick = () => {
    raf = requestAnimationFrame(tick);
    if (!stage.need || !stage.plan) return;
    stage.need = false;
    draw(cv);
  };
  raf = requestAnimationFrame(tick);
});

onUnmounted(() => {
  cancelAnimationFrame(raf);
  stopBridge?.();
  unwatch?.();
});

function fitPlanSize(cv: HTMLCanvasElement) {
  const plan = stage.plan;
  if (!plan || !plan.W) return;
  if (cv.width !== plan.W || cv.height !== plan.H) {
    cv.width = plan.W;
    cv.height = plan.H;
  }
}

function draw(cv: HTMLCanvasElement) {
  const plan = stage.plan;
  if (!plan) return;
  const ctx = cv.getContext('2d')!;
  try {
    renderer.frame(ctx, plan, stage.t, { scale: cv.width / plan.W, transparent: !!window.__jl_spout_transparent });
  } catch { /* 单帧失败不中断泵 */ }
}
</script>

<template>
  <div class="fixed inset-0 bg-black overflow-hidden">
    <!-- id="view" 是 Spout 注入脚本的 DOM 契约 -->
    <canvas id="view" ref="canvas" width="1280" height="720" class="block w-full h-full"></canvas>
    <div v-if="!connected" class="absolute bottom-3 left-1/2 -translate-x-1/2 text-mut text-xs bg-ink/70 border border-line2 rounded-full px-3 py-1 backdrop-blur">
      等待编辑页连接（BroadcastChannel）…
    </div>
  </div>
</template>
