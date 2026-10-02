<script lang="ts" setup>
/* 原生 <dialog>：初始化确认 + 输出物权利说明 + 音频特效设置（简中重写） */
import {nextTick, onMounted, onUnmounted, ref} from 'vue';
import {bus} from '../../jizura/bus';
import {resetAll} from '../../jizura/look';
import {J} from '../../engine';
import {S} from '../../jizura/state';
import {setAudioFx} from '../../jizura/export';

const resetOpen = ref(false);
const termsOpen = ref(false);
const audioFxOpen = ref(false);
const busy = ref(false);
const offs: (() => void)[] = [];

function closeAll() {
  resetOpen.value = false;
  termsOpen.value = false;
  audioFxOpen.value = false;
}

async function doReset() {
  busy.value = true;
  resetOpen.value = false;
  await resetAll();
  busy.value = false;
}

function onEsc(e: KeyboardEvent) {
  if (e.key === 'Escape' && (resetOpen.value || termsOpen.value || audioFxOpen.value)) closeAll();
}

onMounted(() => {
  offs.push(bus.on('open-reset', () => {
    termsOpen.value = false;
    resetOpen.value = true;
  }));
  offs.push(bus.on('open-terms', () => {
    resetOpen.value = false;
    termsOpen.value = true;
  }));
  offs.push(bus.on('open-audiofx', () => {
    resetOpen.value = false;
    termsOpen.value = false;
    fx.value = J.sanitizeAudioFx(S.project.audioFx);
    audioFxOpen.value = true;
    nextTick(drawEq);
  }));
  window.addEventListener('keydown', onEsc);
});
onUnmounted(() => {
  window.removeEventListener('keydown', onEsc);
  offs.forEach(f => f());
  clearTimeout(eqTimer);
});

/* ---- 音频特效弹窗状态 ---- */
const fx = ref(J.sanitizeAudioFx(null));
const EQ_LABEL = ['60', '150', '400', '1k', '2.4k', '6k', '15k'];
const eqCanvas = ref<HTMLCanvasElement | null>(null);
let eqDrag = -1;
let eqTimer = 0;

function applyFx(patch: any) {
  setAudioFx(patch);
  fx.value = J.sanitizeAudioFx(S.project.audioFx);
  if (fx.value.preset === 'eq') nextTick(drawEq);   // 切到 EQ 时画布才刚 v-if 出现，必须等挂载后重绘
}

/* EQ 画布：7 个频段把手，拖动改增益（-12..+12dB）。拖动中节流提交，
   边拖边听；松手提交最终值 */
const eqX = (i: number, W: number) => 30 + i * (W - 42) / (J.AUDIO_EQ_BANDS.length - 1);
const eqY = (db: number, H: number) => H / 2 - (Math.min(12, Math.max(-12, db)) / 12) * (H / 2 - 8);

function drawEq() {
  const c = eqCanvas.value;
  if (!c) return;
  const x = c.getContext('2d')!;
  const W = c.width, H = c.height;
  x.fillStyle = '#131316';
  x.fillRect(0, 0, W, H);
  x.strokeStyle = 'rgba(255,255,255,0.18)';
  x.lineWidth = 1;
  x.beginPath();
  x.moveTo(0, eqY(0, H));
  x.lineTo(W, eqY(0, H));
  x.stroke();
  x.strokeStyle = '#f5a50c';
  x.lineWidth = 2;
  x.beginPath();
  fx.value.eq.forEach((db: number, i: number) => {
    const px = eqX(i, W);
    if (i) x.lineTo(px, eqY(db, H)); else x.moveTo(px, eqY(db, H));
  });
  x.stroke();
  x.fillStyle = '#f5a50c';
  fx.value.eq.forEach((db: number, i: number) => {
    x.beginPath();
    x.arc(eqX(i, W), eqY(db, H), 4, 0, Math.PI * 2);
    x.fill();
  });
  // dB 刻度
  x.fillStyle = 'rgba(255,255,255,0.4)';
  x.font = '9px monospace';
  x.fillText('+12', 2, 10);
  x.fillText('0', 2, eqY(0, H) - 3);
  x.fillText('-12', 2, H - 2);
}

function eqPick(e: PointerEvent): number {
  const c = eqCanvas.value!;
  const r = c.getBoundingClientRect();
  const fxPos = Math.min(1, Math.max(0, (e.clientX - r.left) / r.width));
  const n = J.AUDIO_EQ_BANDS.length;
  let best = 0, bd = 1e9;
  for (let i = 0; i < n; i++) {
    const d = Math.abs(eqX(i, c.width) / c.width - fxPos);   // 与绘制同一映射
    if (d < bd) { bd = d; best = i; }
  }
  return best;
}

function eqApply(e: PointerEvent) {
  const r = eqCanvas.value!.getBoundingClientRect();
  const db = Math.max(-12, Math.min(12, ((r.height / 2 - (e.clientY - r.top)) / (r.height / 2 - 8)) * 12));
  const eq = fx.value.eq.slice();
  eq[eqDrag] = +db.toFixed(1);
  fx.value = {...fx.value, eq};
  drawEq();
  clearTimeout(eqTimer);
  eqTimer = window.setTimeout(() => setAudioFx({eq}), 60);
}

function eqDown(e: PointerEvent) {
  try {
    eqCanvas.value!.setPointerCapture(e.pointerId);
  } catch { /* 合成事件 */ }
  eqDrag = eqPick(e);
  eqApply(e);
}

function eqMove(e: PointerEvent) {
  if (eqDrag >= 0) eqApply(e);
}

function eqUp() {
  if (eqDrag < 0) return;
  eqDrag = -1;
  clearTimeout(eqTimer);
  setAudioFx({eq: fx.value.eq});
}
</script>

<template>
  <!-- 模态用普通 fixed 层（原生 dialog 的 top layer 提升在本页有 ~470ms 的层树重建开销） -->
  <Teleport to="body">
    <Transition name="fade">
      <div v-if="resetOpen" class="jl-modal-open fixed inset-0 z-[3000] grid place-items-center p-6"
           style="background: rgba(6, 6, 8, 0.6)" @click.self="resetOpen = false">
        <div
            aria-labelledby="resetTitle"
            aria-modal="true"
            class="relative w-[min(540px,calc(100vw-32px))] max-h-[calc(100vh-48px)] overflow-auto bg-panel border border-line2 rounded-[14px] shadow-modal p-6 pb-4.5"
            role="dialog">
          <button aria-label="关闭" class="absolute right-3.5 top-3 text-mut hover:!text-white text-[15px] leading-none"
                  @click="resetOpen = false">✕</button>
          <h2 class="text-[15px] tracking-wider font-semibold mb-3.5">初始化</h2>
          <p class="text-[13px] leading-relaxed mb-2.5">将以下内容全部清空，回到最初状态。<strong
              class="text-amber font-bold">此操作无法撤销。</strong></p>
          <ul class="grid gap-0.5 text-[13px] pl-[18px] mb-3 list-disc">
            <li>歌词・曲名・歌手</li>
            <li>载入的歌曲（浏览器里保留的副本也一并删除）</li>
            <li>时间・逐行指定・导出范围</li>
            <li>风格・演出・配色・字体等设置</li>
            <li>方案历史（◀ ▶）与歌词・时间的撤销历史</li>
          </ul>
          <p class="text-mut text-xs mb-3">预览音量会保留。已保存的项目文件（.json）不受影响。</p>
          <div class="flex justify-end items-center gap-1.5 border-t border-line pt-3">
            <button class="btn btn-ghost" @click="resetOpen = false">取消</button>
            <button :disabled="busy" class="btn btn-danger" @click="doReset">初始化</button>
          </div>
        </div>
      </div>
    </Transition>

    <Transition name="fade">
      <div v-if="termsOpen" class="jl-modal-open fixed inset-0 z-[3000] grid place-items-center p-6"
           style="background: rgba(6, 6, 8, 0.6)" @click.self="termsOpen = false">
        <div
            aria-labelledby="termsTitle"
            aria-modal="true"
            class="relative w-[min(540px,calc(100vw-32px))] max-h-[calc(100vh-48px)] overflow-auto bg-panel border border-line2 rounded-[14px] shadow-modal p-6 pb-4"
            role="dialog">
          <button aria-label="关闭" class="absolute right-3.5 top-3 text-mut hover:!text-white text-[15px] leading-none z-10"
                  @click="termsOpen = false">✕</button>
          <h2 class="text-[15px] tracking-wider font-semibold mb-3.5">使用说明</h2>
          <div class="border-l-[3px] border-amber bg-raised px-3.5 pt-3 pb-1 rounded-r-md mb-3.5">
            <p class="!text-[15px]">用本工具制作的<strong
                class="text-amber font-bold">视频与图片（输出物）的权利归制作者</strong>所有。</p>
            <p class="text-mut text-xs">※ 所使用的歌词与歌曲的权利归各自权利人所有。</p>
          </div>
          <p class="text-[13px] leading-relaxed mb-2.5">工具本体基于 852wa/JIZURA 定制：</p>
          <p class="mb-2.5"><a class="text-cyan break-all" href="https://github.com/852wa/JIZURA" rel="noopener"
                               target="_blank">https://github.com/852wa/JIZURA</a></p>
          <p class="text-mut text-xs mb-3.5">
            输入的歌词与歌曲只在本地处理，不会上传到任何服务器。在线搜索功能经由本机服务代理访问音乐平台。</p>
          <details class="text-xs text-mut my-3.5">
            <summary class="cursor-pointer">使用的开源组件</summary>
            <p class="mt-1.5">
              界面：Vue 3、Pinia、Vue Router、Tailwind CSS（MIT License）<br>
              MP4 导出：mp4-muxer（MIT License）<br>
              内嵌浏览器：CefSharp + Chromium Embedded Framework（BSD 3-Clause License）<br>
              运行时：.NET 及官方扩展库（MIT License）<br>
              本机服务与音频处理：EmbedIO / Unosquare Swan、NAudio、Newtonsoft.Json、QRCoder（MIT License）、log4net（Apache License
              2.0）<br>
              图形输出：SharpDX（MIT License）、Spout2 / SpoutDX（BSD 2-Clause License）<br>
              字体：Google Fonts 发布的各字体（SIL Open Font License 1.1）
            </p>
          </details>
          <div class="flex justify-between items-center gap-3 border-t border-line pt-3">
            <span class="font-mono text-xs text-mut">字面 Live 测试版</span>
            <span class="font-mono text-xs text-mut">免费提供，如需付费请拉黑退款</span>
            <button class="btn btn-primary" @click="termsOpen = false">关闭</button>
          </div>
        </div>
      </div>
    </Transition>
  <Transition name="fade">
    <div v-if="audioFxOpen" class="jl-modal-open fixed inset-0 z-[3000] grid place-items-center p-6"
         style="background: rgba(6, 6, 8, 0.6)" @click.self="audioFxOpen = false">
      <div
          aria-labelledby="audiofxTitle"
          aria-modal="true"
          class="relative w-[min(560px,calc(100vw-32px))] max-h-[calc(100vh-48px)] overflow-auto bg-panel border border-line2 rounded-[14px] shadow-modal p-6 pb-4"
          role="dialog">
        <button aria-label="关闭" class="absolute right-3.5 top-3 text-mut hover:!text-white text-[15px] leading-none z-10"
                @click="audioFxOpen = false">✕</button>
        <h2 id="audiofxTitle" class="text-[15px] tracking-wider font-semibold mb-2.5">音频特效</h2>
        <div class="flex items-center gap-2 flex-wrap text-xs mb-1.5">
          <select :value="fx.preset" aria-label="特效类型" class="!h-[30px]"
                  title="预览与导出 MP4 同时生效"
                  @change="applyFx({preset: ($event.target as HTMLSelectElement).value})">
            <option value="none">关闭（原始声音）</option>
            <option value="pan">环绕音（左右摆动）</option>
            <option value="spatial">3D 丽音（双耳空间展宽）</option>
            <option value="orbit">3D 旋转（8D 环绕）</option>
            <option value="eq">自定义 EQ（音色均衡）</option>
          </select>
          <button v-if="fx.preset === 'eq'" class="btn btn-ghost btn-sm" @click="applyFx({eq: [0,0,0,0,0,0,0]})">EQ 归零
          </button>
        </div>
        <div v-if="fx.preset !== 'none' && fx.preset !== 'eq'" class="flex items-center gap-5 text-xs my-2.5">
          <label class="flex items-center gap-1.5 text-mut">速度
            <input type="range" min="0.02" max="1" step="0.005" :value="fx.rate" class="w-32"
                   @change="applyFx({rate: +($event.target as HTMLInputElement).value})"></label>
          <label class="flex items-center gap-1.5 text-mut">强度
            <input type="range" min="0" max="1" step="0.01" :value="fx.depth" class="w-32"
                   @change="applyFx({depth: +($event.target as HTMLInputElement).value})"></label>
        </div>
        <canvas v-if="fx.preset === 'eq'" ref="eqCanvas" width="512" height="120"
                class="block w-full my-2 cursor-crosshair rounded" @pointerdown="eqDown"
                @pointermove="eqMove" @pointerup="eqUp" @pointercancel="eqUp"></canvas>
        <p class="note !text-[10px]">环绕音=左右摆动；3D 系特效建议戴耳机听；EQ 拖动 7 个频段把手调音色（±12dB，60Hz〜15kHz）。特效只作用于歌曲音频，预览音量与消音不受影响。</p>
      </div>
    </div>
  </Transition>
  </Teleport>
</template>
