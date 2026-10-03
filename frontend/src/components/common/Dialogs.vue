<script lang="ts" setup>
import {computed, nextTick, onMounted, onUnmounted, ref} from 'vue';
import {bus} from '../../jizura/bus';
import {resetAll} from '../../jizura/look';
import {J} from '../../engine';
import {S} from '../../jizura/state';
import {setAudioFx} from '../../jizura/export';
import {answerConfirm} from '../../jizura/confirm';

const resetOpen = ref(false);
const termsOpen = ref(false);
const audioFxOpen = ref(false);
const confirmOpen = ref(false);
const confirmTitle = ref('确认');
const confirmText = ref('');
const busy = ref(false);
const offs: (() => void)[] = [];

function closeAll() {
  resetOpen.value = false;
  termsOpen.value = false;
  audioFxOpen.value = false;
  if (confirmOpen.value) answerConfirm(false);   // Esc/关按钮=取消，别把等待方挂死
  confirmOpen.value = false;
}

async function doReset() {
  busy.value = true;
  resetOpen.value = false;
  await resetAll();
  busy.value = false;
}

function onEsc(e: KeyboardEvent) {
  if (e.key === 'Escape' && (resetOpen.value || termsOpen.value || audioFxOpen.value || confirmOpen.value)) closeAll();
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
  offs.push(bus.on('confirm-open', (p: any) => {
    confirmTitle.value = (p && p.title) || '确认';
    confirmText.value = (p && p.text) || '';
    confirmOpen.value = true;
  }));
  window.addEventListener('keydown', onEsc);
});
onUnmounted(() => {
  window.removeEventListener('keydown', onEsc);
  offs.forEach(f => f());
  clearTimeout(eqTimer);
});

// ---------- 音频特效 v2：预设包 + 效果链 rack + 变速 ----------
const fx = ref(J.sanitizeAudioFx(null));
const UNIT: any = J.AFX_UNITS, PRESETS: any[] = J.AFX_PRESETS;
const EQ_LABEL = ['60', '150', '400', '1k', '2.4k', '6k', '15k'];
const eqCanvas = ref<HTMLCanvasElement | null>(null);
let eqDrag = -1;
let eqTimer = 0;
const openUnit = ref(-1);
const addId = ref('');

const eqIdx = computed(() => fx.value.units.findIndex((u: any) => u.id === 'eq'));
const addable = computed(() => Object.keys(UNIT).filter(id => !fx.value.units.some((u: any) => u.id === id)));

const round = (v: number) => Math.abs(v) >= 100 ? Math.round(v) : +Number(v).toFixed(2);
const fmtDur = (s: number) => s >= 60 ? `${Math.floor(s / 60)}分${(s % 60).toFixed(0)}秒` : `${s.toFixed(1)}秒`;

function applyFx(patch: any) {
  setAudioFx(patch);
  fx.value = J.sanitizeAudioFx(S.project.audioFx);
  if (eqIdx.value >= 0) nextTick(drawEq);   // EQ 画布随链变化，得等挂载完再画
}

function usePreset(p: any) {
  applyFx(p.cfg);
  openUnit.value = -1;
}

function addUnit() {
  if (!addId.value) return;
  applyFx({units: [...fx.value.units, {id: addId.value, on: true, params: {...UNIT[addId.value].defaults}}]});
  openUnit.value = fx.value.units.length - 1;
  addId.value = '';
}

function rmUnit(i: number) {
  applyFx({units: fx.value.units.filter((_: any, k: number) => k !== i)});
}

function moveUnit(i: number, d: number) {
  const j = i + d;
  if (j < 0 || j >= fx.value.units.length) return;
  const units = fx.value.units.slice();
  [units[i], units[j]] = [units[j], units[i]];
  applyFx({units});
}

function toggleUnit(i: number) {
  applyFx({units: fx.value.units.map((u: any, k: number) => k === i ? {...u, on: !u.on} : u)});
}

function setParam(i: number, key: string, v: any) {
  applyFx({units: fx.value.units.map((u: any, k: number) => k === i ? {...u, params: {...u.params, [key]: v}} : u)});
}

function paramSummary(u: any): string {
  return UNIT[u.id].params.map((s: any) => {
    if (s.type === 'eq') return eqIdx.value >= 0 && fx.value.units[eqIdx.value].params.gains.some((g: number) => g) ? '自定义曲线' : '平直';
    return `${s.label} ${round(u.params[s.key])}${s.unit || ''}`;
  }).join('・');
}

// EQ 画布：绑定链上的 eq 单元（没有就不画）
function eqGains(): number[] {
  const i = eqIdx.value;
  return i >= 0 ? fx.value.units[i].params.gains : [0, 0, 0, 0, 0, 0, 0];
}

const eqX = (i: number, W: number) => 30 + i * (W - 42) / (J.AUDIO_EQ_BANDS.length - 1);
const eqY = (db: number, H: number) => H / 2 - (Math.min(12, Math.max(-12, db)) / 12) * (H / 2 - 8);

function drawEq() {
  const c = eqCanvas.value;
  if (!c) return;
  const x = c.getContext('2d')!;
  const W = c.width, H = c.height, gains = eqGains();
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
  gains.forEach((db: number, i: number) => {
    const px = eqX(i, W);
    if (i) x.lineTo(px, eqY(db, H)); else x.moveTo(px, eqY(db, H));
  });
  x.stroke();
  x.fillStyle = '#f5a50c';
  gains.forEach((db: number, i: number) => {
    x.beginPath();
    x.arc(eqX(i, W), eqY(db, H), 4, 0, Math.PI * 2);
    x.fill();
  });
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
    const d = Math.abs(eqX(i, c.width) / c.width - fxPos);   // 和绘制用同一套映射
    if (d < bd) { bd = d; best = i; }
  }
  return best;
}

function eqApply(e: PointerEvent) {
  const i = eqIdx.value;
  if (i < 0) return;
  const r = eqCanvas.value!.getBoundingClientRect();
  const db = Math.max(-12, Math.min(12, ((r.height / 2 - (e.clientY - r.top)) / (r.height / 2 - 8)) * 12));
  const gains = eqGains().slice();
  gains[eqDrag] = +db.toFixed(1);
  drawEq();
  clearTimeout(eqTimer);
  eqTimer = window.setTimeout(() => setParam(i, 'gains', gains), 60);
}

function eqDown(e: PointerEvent) {
  try {
    eqCanvas.value!.setPointerCapture(e.pointerId);
  } catch { /* 合成事件会抛错，忽略 */ }
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
  if (eqIdx.value >= 0) setParam(eqIdx.value, 'gains', eqGains());
}
</script>

<template>
  <!-- 不用原生 dialog：top layer 提升在本页有 ~470ms 的层树重建开销，改用普通 fixed 层 -->
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
        <!-- 预设包：一键写整条链 -->
        <div class="flex flex-wrap gap-1 mb-2.5">
          <button v-for="p in PRESETS" :key="p.id" class="btn btn-sm btn-ghost" :title="'套用预设：' + p.label"
                  @click="usePreset(p)">{{ p.label }}
          </button>
        </div>
        <!-- 变速：改出声快慢，歌词/节拍按歌曲原时刻自动同步 -->
        <div v-if="S.audio" class="row gap-2 text-xs mb-2">
          <span class="text-mut shrink-0">变速</span>
          <input type="range" min="0.5" max="2" step="0.05" :value="fx.speed" class="flex-1"
                 aria-label="变速倍率"
                 @change="applyFx({speed: +($event.target as HTMLInputElement).value})">
          <span class="font-mono w-12 text-right">×{{ fx.speed.toFixed(2) }}</span>
          <span class="font-mono text-mut w-20 text-right">{{ fmtDur(S.audio.duration / fx.speed) }}</span>
        </div>
        <!-- 效果链 rack -->
        <div class="grid gap-1 mb-2">
          <div v-for="(u, i) in fx.units" :key="u.id + i" class="rounded border border-line">
            <div class="row gap-2 px-2 py-1 text-[13px]">
              <input type="checkbox" :checked="u.on" :title="UNIT[u.id].note" @change="toggleUnit(i)">
              <button class="font-semibold" :aria-expanded="openUnit === i"
                      @click="openUnit = openUnit === i ? -1 : i">{{ UNIT[u.id].label }} {{ openUnit === i ? '▾' : '▸' }}
              </button>
              <span class="text-mut text-[11px] truncate flex-1">{{ paramSummary(u) }}</span>
              <button class="btn btn-ghost btn-sm !px-1.5" :disabled="i === 0" aria-label="上移" @click="moveUnit(i, -1)">↑
              </button>
              <button class="btn btn-ghost btn-sm !px-1.5" :disabled="i === fx.units.length - 1" aria-label="下移"
                      @click="moveUnit(i, 1)">↓
              </button>
              <button class="btn btn-ghost btn-sm !px-1.5" aria-label="移除" @click="rmUnit(i)">✕</button>
            </div>
            <div v-if="openUnit === i" class="px-3 pb-2 grid gap-1.5">
              <p class="note !text-[10px] mb-0">{{ UNIT[u.id].note }}</p>
              <template v-for="s in UNIT[u.id].params" :key="s.key">
                <label v-if="!s.type" class="row gap-2 text-xs">
                  <span class="text-mut w-14 shrink-0">{{ s.label }}</span>
                  <input type="range" class="flex-1" :min="s.min" :max="s.max" :step="s.step"
                         :value="u.params[s.key]"
                         @change="setParam(i, s.key, +($event.target as HTMLInputElement).value)">
                  <span class="font-mono w-16 text-right">{{ round(u.params[s.key]) }}{{ s.unit || '' }}</span>
                </label>
                <div v-else-if="s.type === 'eq'">
                  <canvas ref="eqCanvas" width="512" height="120"
                          class="block w-full my-1 cursor-crosshair rounded" @pointerdown="eqDown"
                          @pointermove="eqMove" @pointerup="eqUp" @pointercancel="eqUp"></canvas>
                  <div class="row justify-between text-[9px] text-mut font-mono -mt-1">
                    <span v-for="l in EQ_LABEL" :key="l">{{ l }}</span>
                  </div>
                </div>
              </template>
            </div>
          </div>
          <p v-if="!fx.units.length" class="note mb-0">链上没有效果——从上面挑个预设，或在下方添加效果单元自由组合。</p>
        </div>
        <!-- 添加效果 -->
        <div class="row gap-1.5 text-xs">
          <select v-model="addId" class="flex-1" aria-label="添加效果">
            <option value="" disabled>添加效果…</option>
            <option v-for="id in addable" :key="id" :value="id">{{ UNIT[id].label }}</option>
          </select>
          <button class="btn btn-sm" :disabled="!addId" @click="addUnit()">添加</button>
        </div>
        <p class="note !text-[10px]">效果按顺序串联，↑↓ 调整先后（顺序影响听感）；预览与导出 MP4/WAV 同时生效。变速改变歌曲时长，歌词与节拍自动跟随；混响等需要噪声的效果用固定种子，导出与预览逐样本一致。</p>
      </div>
    </div>
  </Transition>

  <!-- 通用确认框：askConfirm(text, title) → Promise<boolean>，是/否或 Esc 取消 -->
  <Transition name="fade">
    <div v-if="confirmOpen" class="jl-modal-open fixed inset-0 z-[3100] grid place-items-center p-6"
         style="background: rgba(6, 6, 8, 0.6)" @click.self="closeAll()">
      <div
          aria-labelledby="confirmTitle"
          aria-modal="true"
          class="relative w-[min(420px,calc(100vw-32px))] bg-panel border border-line2 rounded-[14px] shadow-modal p-5 pb-4"
          role="dialog">
        <h2 id="confirmTitle" class="text-[14px] tracking-wider font-semibold mb-2.5">{{ confirmTitle }}</h2>
        <p class="text-[13px] leading-relaxed mb-3 whitespace-pre-line">{{ confirmText }}</p>
        <div class="flex justify-end items-center gap-1.5 border-t border-line pt-3">
          <button class="btn btn-ghost" @click="closeAll()">取消</button>
          <button class="btn btn-primary" @click="confirmOpen = false; answerConfirm(true)">确定</button>
        </div>
      </div>
    </div>
  </Transition>
  </Teleport>
</template>
