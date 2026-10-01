<script setup lang="ts">
import {onMounted, onUnmounted, ref, watch} from 'vue';
import {J} from '../engine';
import {useStage} from '../stores/stage';
import {mergeProject} from '../jizura/project';
import {bindStageBroadcast} from '../jizura/stage-bridge';
import {drawSubtitleFrame, installSubtitleOverlay, setSubOverride} from '../cloud/subtitle';

const stage = useStage();
const canvas = ref<HTMLCanvasElement | null>(null);
const connected = ref(false);
/* ?layer=lyric：独立 Spout 字幕通道——透明底、只画歌词文字（无背景/装饰/HUD） */
const spoutParams = new URLSearchParams(window.location.search);
const isLyricLayer = spoutParams.get('layer') === 'lyric';
const isMirror = spoutParams.get('mirror') === '1';
let renderer: any = null;
let stopBridge: (() => void) | null = null;
let raf = 0;
let unwatch: (() => void) | null = null;
let mirrorCleanup: (() => void) | null = null;

/* 镜像模式时间同步——完整跟随编辑器时钟（时钟同步已恢复）。
   时间来源（双路并存）：BC time 每帧 + C# __jlApplySync ~30Hz 锚点。
   两条通道 + 泵外推三方写 stage.t 会互相打架（锯齿闪烁：画面在两个时间
   段之间跳）。播放中统一由泵外推时钟独占 stage.t；推送只作为锚点输入，
   且只进不退——迟到的锚点（落后外推值 <0.35s）直接忽略，大回跳才算 seek。 */
const anchor = {playing: false, lastT: 0, stamp: 0, has: false};

function onMirrorTime(t: number, playing: boolean) {
  if (!Number.isFinite(t)) return;
  const now = performance.now();
  if (playing && anchor.playing && anchor.has) {
    const ext = anchor.lastT + (now - anchor.stamp) / 1000;
    if (t < ext && ext - t < 0.35) return;   // 迟到的锚点：不回拨外推时钟
  }
  const changed = playing !== anchor.playing || t !== anchor.lastT;
  anchor.playing = playing;
  anchor.lastT = t;
  anchor.stamp = now;
  anchor.has = true;
  if (changed || playing) {
    stage.t = t;
    stage.need = true;
  }
}

onMounted(() => {
  installSubtitleOverlay();
  renderer = new J.Renderer();
  const cv = canvas.value!;

  if (isMirror) startMirror(cv);

  /* BroadcastChannel 直通：工程/字幕/时间全部来自编辑页（每帧时钟同步） */
  const bridge = bindStageBroadcast(stage.sink());
  stopBridge = bridge.stop;
  connected.value = stage.connected;
  watch(() => stage.connected, v => { connected.value = v; });
  watch(() => [stage.subCfg, stage.subMap] as const, ([cfg, map]) => {
    if (cfg) setSubOverride(cfg, map);
  }, { immediate: true });

  /* C# 宿主契约面：J.ui = store 本身（宿主侧脚本/排障直接读写） */
  J.ui = stage;
  J.uiApi = {
    toast: () => {},
    replan: () => stage.replan(),
    syncUI: () => {},
    pause: () => { stage.playing = false; },
    play: () => {},
    seek: (t: number) => {
      stage.t = t;
      stage.need = true;
    },
    flushSave: () => {},
    loadAudioFile: async () => false,
    restartPreview: () => {},
    exportRange: () => null,
    exportRangeCuts: () => null,
  };

  /* 工程分辨率画布：plan 就绪时把物理像素设为 plan 尺寸（镜像泵也会保持） */
  unwatch = watch(() => stage.plan, () => {
    fitPlanSize(cv);
  }, {deep: false});
  fitPlanSize(cv);

  /* 渲染泵：need 时画（镜像泵/时间推送都会置 need） */
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
  mirrorCleanup?.();
});

function fitPlanSize(cv: HTMLCanvasElement) {
  const plan = stage.plan;
  if (!plan || !plan.W) return;
  if (cv.width !== plan.W || cv.height !== plan.H) {
    cv.width = plan.W;
    cv.height = plan.H;
  }
}

/* 无预热：与上游 JIZURA 对齐——morph/首帧播放时惰性构建（morphCache LRU），
   镜像页不再起任何后台预热任务（实测全量 warmMorphs 会把共享 GPU 进程打满，
   编辑器主界面成片掉帧） */

function draw(cv: HTMLCanvasElement) {
  const plan = stage.plan;
  if (!plan) return;
  const ctx = cv.getContext('2d')!;
  try {
    if (isLyricLayer) {
      // 字幕通道：透明画布上只叠加字幕组件（翻译/音译字幕）
      ctx.clearRect(0, 0, cv.width, cv.height);
      drawSubtitleFrame(ctx, plan, stage.t, { scale: cv.width / plan.W });
    } else {
      renderer.frame(ctx, plan, stage.t, { scale: cv.width / plan.W, transparent: !!window.__jl_spout_transparent });
    }
  } catch { /* 单帧失败不中断泵 */ }
}

/* ================================================================
   Spout 镜像模式（?mirror=1）——原 C# 注入脚本（SpoutPipeline.MirrorScript）
   的页面原生版。行为契约逐条保留：
     - isolate：页面只剩 #view 画布，铺满视口（CSS 像素=纹理像素 1:1 出图）
     - fitPlanSize：画布钉在工程分辨率，变更时 PostMessage 上报 plan-size
     - __jlApplySync / BC time：完整时钟同步——每帧跟随编辑器时间，
       锚点之间泵按墙钟外推（消除 ~30Hz 推送与出画节奏的拍频）
     - 泵：播放满帧率出画；暂停只在画面真的会变时重绘（无条件置 need 会让
       GPU 进程永不空闲，是整条出帧管线卡顿的主因之一）
     - storage：主窗口保存工程后 storage 事件跨浏览器触发
       （依赖镜像与主窗口共享默认 RequestContext）
   ================================================================ */
function startMirror(cv: HTMLCanvasElement) {
  (window as any).__jl_spout_mirror = true;
  (window as any).__jl_spout_transparent = spoutParams.get('transparent') === '1';
  (window as any).__jl_no_sub = spoutParams.get('nosub') === '1';
  // 播放时钟由本页泵独占：BC/锚点推送都改走 onMirrorTime 再锚定（只进不退）
  stage.extClock = onMirrorTime;

  const fps = Math.max(1, Number(spoutParams.get('fps')) || 60);
  const PUMP_MS = Math.max(4, Math.floor(1000 / fps));

  // ------------------------------------------------------------
  // 高帧率泵：requestAnimationFrame 受 VSync 约束（≤60Hz），
  // 即使 CEF 侧 WindowlessFrameRate=240 也只能出 60 帧。
  // 改用 setTimeout 递归调度：最小粒度 ~4ms（~250Hz），
  // 足够覆盖 240fps 目标。仅镜像（离屏）浏览器走此路径，
  // 主窗口编辑器仍用 rAF 以保证流畅 + 节能。
  // ------------------------------------------------------------
  const useHighFreqPump = fps > 60;
  let lastW = 0, lastH = 0;
  let postedPending = false;

  const postHost = (o: any) => {
    try {
      const cs = (window as any).CefSharp;
      if (cs && cs.PostMessage) {
        cs.PostMessage(o);
        return true;
      }
    } catch { /* binding 未就绪，下个泵 tick 重试 */
    }
    return false;
  };

  function isolate() {
    try {
      if (cv.parentElement !== document.body) document.body.appendChild(cv);
      // width/height 必须铺满视口：CEF 页面缩放（按来源持久化）会让 CSS 视口 ≠
      // 纹理物理尺寸，canvas 若按位图尺寸显示就会留出透明带（OBS 合成黑边）。
      // 铺满视口后 CSS 尺寸×dpr 恒等于纹理物理尺寸，任何缩放下精确 1:1 出图
      cv.style.cssText =
          'position:fixed;left:0;top:0;margin:0;width:100vw;height:100vh;' +
          'z-index:2147483647;display:block;visibility:visible;';
      const kids = document.body.children;
      for (let i = 0; i < kids.length; i++) {
        if (kids[i] !== cv) (kids[i] as HTMLElement).style.display = 'none';
      }
      const bg = (window as any).__jl_spout_transparent ? 'transparent' : '#000';
      document.documentElement.style.background = bg;
      document.body.style.background = bg;
    } catch (e) {
      console.error('[mirror] isolate failed:', e);
    }
  }

  function fit() {
    const plan = stage.plan;
    if (!plan) return false;
    const w = Number(plan.W) || 0, h = Number(plan.H) || 0;
    if (w <= 0 || h <= 0) return false;
    let changed = false;
    if (cv.width !== w || cv.height !== h) {
      cv.width = w;
      cv.height = h;
      stage.need = true;
      changed = true;
    }
    if (w !== lastW || h !== lastH) {
      lastW = w;
      lastH = h;
      postedPending = !postHost({type: 'plan-size', w: lastW, h: lastH});
      changed = true;
    } else if (postedPending) {
      postedPending = !postHost({type: 'plan-size', w: lastW, h: lastH});
    }
    return changed;
  }

  (window as any).__jlApplySync = (data: any) => {
    try {
      if (!data) return;
      const t = Number(data.t);
      if (!Number.isFinite(t)) return;
      // C# ~30Hz 锚点落地：泵在两次锚点之间按墙钟外推（拍频消除）
      onMirrorTime(t, !!data.playing);
    } catch (e) {
      console.error('[mirror] ApplySync failed:', e);
    }
  };

  let pumpLast = 0;
  let drawnT: number | undefined = undefined;
  const schedulePump = useHighFreqPump
      ? () => setTimeout(pump, PUMP_MS)
      : () => requestAnimationFrame(pump);

  const pump = () => {
    schedulePump();
    try {
      const now = performance.now();
      if (now - pumpLast < PUMP_MS) return;
      pumpLast = now;
      const sizeChanged = fit();
      // 播放中在两次锚点之间按墙钟外推，满帧率出画；暂停时只在画面真的会变时
      //（锚点 t 变化 / 分辨率变化 / 首帧）才置 need
      if (anchor.playing) {
        let te = anchor.lastT + (now - anchor.stamp) / 1000;
        const dur = stage.plan?.duration ?? 0;
        if (dur > 0) te = Math.min(te, dur - 1e-3);
        stage.t = te;
        stage.need = true;
        drawnT = te;
        return;
      }
      if (sizeChanged || drawnT === undefined || stage.t !== drawnT) {
        stage.need = true;
        drawnT = stage.t;
      }
    } catch { /* 泵不容错中断 */
    }
  };
  schedulePump();

  // isolate 低频重入即可（自愈 DOM 变动）；分辨率保持已移入泵里
  const isolateTimer = window.setInterval(isolate, 500);

  // 工程跟随：主窗口保存工程后 storage 事件跨浏览器触发
  //（依赖镜像与主窗口共享默认 RequestContext）
  const onStorage = (ev: StorageEvent) => {
    if (ev.key !== 'jizura.project.v1' || !ev.newValue) return;
    try {
      stage.project = mergeProject(JSON.parse(ev.newValue));
      stage.replan();
    } catch (e) {
      console.error('[mirror] storage sync failed:', e);
    }
  };
  window.addEventListener('storage', onStorage);

  stage.need = true;   // 初始渲染
  isolate();

  mirrorCleanup = () => {
    window.removeEventListener('storage', onStorage);
    window.clearInterval(isolateTimer);
    stage.extClock = null;
    delete (window as any).__jlApplySync;
  };
}
</script>

<template>
  <div class="fixed inset-0 overflow-hidden" :class="isLyricLayer ? 'bg-transparent' : 'bg-black'">
    <!-- id="view" 是镜像 isolate / 宿主抓图的 DOM 契约 -->
    <canvas id="view" ref="canvas" width="1280" height="720" class="block w-full h-full"></canvas>
    <div v-if="!connected" class="absolute bottom-3 left-1/2 -translate-x-1/2 text-mut text-xs bg-ink/70 border border-line2 rounded-full px-3 py-1">
      等待编辑页连接（BroadcastChannel）{{ isLyricLayer ? ' · 歌词通道' : '' }}
    </div>
  </div>
</template>
