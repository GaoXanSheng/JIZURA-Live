<script lang="ts" setup>
import {onMounted, onUnmounted, ref, watch} from 'vue';
import {J} from '../engine';
import {useStage} from '../stores/stage';
import {mergeProject} from '../jizura/project';
import {bindStageBroadcast} from '../jizura/stage-bridge';
import {drawSubtitleFrame, installSubtitleOverlay, setSubOverride} from '../cloud/subtitle';

const stage = useStage();
const canvas = ref<HTMLCanvasElement | null>(null);
const connected = ref(false);
// ?layer=lyric 是独立的 Spout 字幕通道，透明底只画歌词文字
const spoutParams = new URLSearchParams(window.location.search);
const isLyricLayer = spoutParams.get('layer') === 'lyric';
const isMirror = spoutParams.get('mirror') === '1';
// desynchronized 是给 Chromium 的低延迟提示，画布更新绕过合成器直出，代价是可能撕裂
// ?dsync=0 关掉做 A/B；画布被遮挡时 Chromium 会静默忽略该提示（镜像 isolate 后画布独占页面才稳定生效）
const CTX_2D: CanvasRenderingContext2DSettings = {desynchronized: spoutParams.get('dsync') !== '0'};
let renderer: any = null;
let stopBridge: (() => void) | null = null;
let raf = 0;
let unwatch: (() => void) | null = null;
let mirrorCleanup: (() => void) | null = null;

// BC 通道最近一次推工程/全量状态的时刻。
// storage 事件（autosave 700ms 防抖落盘的回声）和 BC 推的是同一份工程文档，
// 两条都走就会连做两次全量 J.plan，直播镜像页 CPU 翻倍。
// BC 刚推过（2.5s 内）storage 就跳过；BC 失联时这个时间戳停在远古，storage 照常兜底
let lastBCProjectAt = -1e9;

// 镜像模式时间同步。
// 时间来源两路：BC time 每帧 + C# __jlApplySync ~30Hz 锚点，加上泵外推三方写 stage.t
// 会互相打架（画面在两个时间段之间跳）。播放中由泵外推独占 stage.t，推送只当锚点，
// 且只进不退：迟到锚点（落后外推值 <0.35s）忽略，大回跳才算 seek
const anchor = {playing: false, lastT: 0, stamp: 0, has: false};

function onMirrorTime(t: number, playing: boolean) {
  if (!Number.isFinite(t)) return;
  const now = performance.now();
  if (playing && anchor.playing && anchor.has) {
    const ext = anchor.lastT + (now - anchor.stamp) / 1000;
    if (t < ext && ext - t < 0.35) return; // 迟到的锚点，不回拨外推时钟
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

  // 工程/字幕/时间全来自编辑页的 BroadcastChannel 直通
  // 包一层 sink 记工程推送时刻，给 storage 回声去重用
  const sink = stage.sink();
  const bridge = bindStageBroadcast({
    setState: (p, b, d, sub, map, an) => {
      lastBCProjectAt = performance.now();
      sink.setState(p, b, d, sub, map, an);
    },
    setProject: (p, b, d, map, an) => {
      lastBCProjectAt = performance.now();
      sink.setProject(p, b, d, map, an);
    },
    setTime: sink.setTime,
    setSub: sink.setSub,
    isConnected: sink.isConnected,
  });
  stopBridge = bridge.stop;
  connected.value = stage.connected;
  watch(() => stage.connected, v => {
    connected.value = v;
  });
  watch(() => [stage.subCfg, stage.subMap] as const, ([cfg, map]) => {
    if (cfg) setSubOverride(cfg, map);
  }, {immediate: true});

  // C# 宿主契约面：J.ui 就是 store 本身，宿主侧脚本直接读写
  J.ui = stage;
  J.uiApi = {
    toast: () => {
    },
    replan: () => stage.replan(),
    syncUI: () => {
    },
    pause: () => {
      stage.playing = false;
    },
    play: () => {
    },
    seek: (t: number) => {
      stage.t = t;
      stage.need = true;
    },
    flushSave: () => {
    },
    loadAudioFile: async () => false,
    restartPreview: () => {
    },
    exportRange: () => null,
    exportRangeCuts: () => null,
  };

  // plan 就绪时把画布物理像素设为工程分辨率，镜像泵也会保持
  unwatch = watch(() => stage.plan, () => {
    fitPlanSize(cv);
  }, {deep: false});
  fitPlanSize(cv);

  // 渲染泵：need 时才画
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

// 不做后台预热，与上游 JIZURA 一致：morph/首帧播放时惰性构建（morphCache LRU）
// 实测全量 warmMorphs 会把共享 GPU 进程打满，编辑器主界面成片掉帧

function draw(cv: HTMLCanvasElement) {
  const plan = stage.plan;
  if (!plan) return;
  const ctx = cv.getContext('2d', CTX_2D)!;
  try {
    if (isLyricLayer) {
      // 透明画布上只叠字幕
      ctx.clearRect(0, 0, cv.width, cv.height);
      drawSubtitleFrame(ctx, plan, stage.t, {scale: cv.width / plan.W});
    } else {
      renderer.frame(ctx, plan, stage.t, {scale: cv.width / plan.W, transparent: !!window.__jl_spout_transparent});
    }
  } catch { // 单帧失败不中断泵
  }
}

// Spout 镜像模式（?mirror=1），行为对齐原 C# 注入脚本 SpoutPipeline.MirrorScript：
// isolate 后页面只剩画布，CSS 像素与纹理 1:1；画布钉在工程分辨率并上报 plan-size
// 时钟完整同步（锚点间泵按墙钟外推）；暂停只在画面真会变时重绘，
// 无条件置 need 会让 GPU 进程永不空闲，是出帧管线卡顿的主因之一
function startMirror(cv: HTMLCanvasElement) {
  (window as any).__jl_spout_mirror = true;
  (window as any).__jl_spout_transparent = spoutParams.get('transparent') === '1';
  (window as any).__jl_no_sub = spoutParams.get('nosub') === '1';
  // 播放时钟由本页泵独占：BC/锚点推送都改走 onMirrorTime 再锚定（只进不退）
  stage.extClock = onMirrorTime;

  const fps = Math.max(1, Number(spoutParams.get('fps')) || 60);
  const PUMP_MS = Math.max(4, Math.floor(1000 / fps));

  // rAF 受 VSync 约束顶到 60Hz，CEF 侧 WindowlessFrameRate=240 也白搭
  // 所以超 60fps 时用 setTimeout 递归（最小粒度 ~4ms）凑够 240fps
  // 只有镜像离屏浏览器走这条路，主窗口编辑器仍用 rAF
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
    } catch { // binding 未就绪，下个泵 tick 重试
    }
    return false;
  };

  function isolate() {
    try {
      if (cv.parentElement !== document.body) document.body.appendChild(cv);
      // 画布必须铺满视口：CEF 页面缩放（按来源持久化）会让 CSS 视口 ≠ 纹理物理尺寸，
      // 按位图尺寸显示就会留出透明带（OBS 合成黑边）。
      // 铺满后 CSS 尺寸×dpr 恒等于纹理物理尺寸，任何缩放下都 1:1 出图
      cv.style.cssText =
          'position:fixed;left:0;top:0;margin:0;width:100vw;height:100vh;' +
          'z-index:2147483647;display:block;visibility:visible;' +
          'will-change:transform;contain:strict;';
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
      // C# ~30Hz 锚点，泵在两次锚点之间按墙钟外推
      onMirrorTime(t, !!data.playing);
    } catch (e) {
      console.error('[mirror] ApplySync failed:', e);
    }
  };

  let pumpLast = 0;
  let drawnT: number | undefined = undefined;
  let pumpStopped = false;
  const schedulePump = useHighFreqPump
      ? () => setTimeout(pump, PUMP_MS)
      : () => requestAnimationFrame(pump);

  const pump = () => {
    if (pumpStopped) return; // 清理后终止递归链，别叠出双泵
    schedulePump();
    try {
      const now = performance.now();
      if (now - pumpLast < PUMP_MS) return;
      pumpLast = now;
      const sizeChanged = fit();
      // 播放中在锚点间按墙钟外推满帧率出画；暂停只在画面真会变时
      //（锚点 t 变 / 分辨率变 / 首帧）才置 need
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
    } catch { // 泵不容错中断
    }
  };
  schedulePump();

  // isolate 低频重入就行，自愈 DOM 变动；分辨率保持已挪进泵里
  const isolateTimer = window.setInterval(isolate, 500);

  // 工程跟随：主窗口保存后 storage 事件跨浏览器触发
  //（依赖镜像与主窗口共享默认 RequestContext）
  const onStorage = (ev: StorageEvent) => {
    if (ev.key !== 'jizura.project.v1' || !ev.newValue) return;
    // BC 刚推过同一份工程的话，storage 只是防抖落盘的回声，跳过
    if (performance.now() - lastBCProjectAt < 2500) return;
    try {
      stage.project = mergeProject(JSON.parse(ev.newValue));
      stage.replan();
    } catch (e) {
      console.error('[mirror] storage sync failed:', e);
    }
  };
  window.addEventListener('storage', onStorage);

  stage.need = true;
  isolate();

  mirrorCleanup = () => {
    pumpStopped = true;
    window.removeEventListener('storage', onStorage);
    window.clearInterval(isolateTimer);
    stage.extClock = null;
    delete (window as any).__jlApplySync;
  };
}
</script>

<template>
  <div :class="isLyricLayer ? 'bg-transparent' : 'bg-black'" class="fixed inset-0 overflow-hidden">
    <!-- id="view" 是镜像 isolate 和宿主抓图的 DOM 契约 -->
    <canvas id="view" ref="canvas" class="block w-full h-full" style="will-change:transform;contain:content;"
            height="720" width="1280"></canvas>
    <div v-if="!connected"
         class="absolute bottom-3 left-1/2 -translate-x-1/2 text-mut text-xs bg-ink/70 border border-line2 rounded-full px-3 py-1">
      等待编辑页连接（BroadcastChannel）{{ isLyricLayer ? ' · 歌词通道' : '' }}
    </div>
  </div>
</template>
