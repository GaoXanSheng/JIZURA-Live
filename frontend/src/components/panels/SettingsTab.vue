<script lang="ts" setup>
// 画布比例和「导出」页同源，都是 project.aspect，改一处两处同步、即时生效。
// 分辨率/帧率/Spout/透明背景/调试端口只存草稿，点「重启渲染引擎」才统一生效，
// 免得随手一改把正在跑的输出打断。
// 预览渲染尺寸是例外：即时生效、不进草稿组，只管主编辑器预览的清晰度和性能，
// 预览画布分辨率与字幕设计尺寸是解绑的。
import {computed, onMounted, onUnmounted, reactive, ref} from 'vue';
import {J} from '../../engine';
import {S} from '../../jizura/state';
import {bus} from '../../jizura/bus';
import {setAspect, setKeyBg} from '../../jizura/export';
import {toast} from '../../jizura/look';
import {
  getAutoDegrade,
  getPreviewRes,
  type PreviewResTier,
  setAutoDegrade,
  setPreviewRes
} from '../../jizura/previewres';
import SubtitlePanel from './SubtitlePanel.vue';
import {replan, sizeViewport} from '../../jizura/engine';

const ASPECTS = ['16:9', '9:16', '4:3', '3:4', '1:1', '4:5', '21:9'];
const RES_MIN = 64, RES_MAX = 4096;

const form = reactive({
  width: 1920,
  height: 1080,
  fps: 60,
  lyricsFps: 30,
  spoutEnabled: true,
  spoutName: 'JIZURA-live',
  spoutTransparent: false,
  spoutLyricsEnabled: false,
  splitMirrorHost: true,
  mirrorExternalBeginFrame: false,
  mirrorDsync: false,
  mirrorCdpEnabled: true,
  mirrorCdpPort: 9223,
  cdpEnabled: false,
  cdpPort: 9222,
});
const aspect = ref(S.project?.aspect || '16:9');
const keyBg = ref('off');
const centerFree = ref(S.project?.centerFree === true);
const centerGap = ref(S.project?.centerGap ?? 30);
const centerDir = ref(S.project?.centerDir === 'lr' ? 'lr' : 'tb');
const centerZone = ref(S.project?.centerZone || 'center');
const loading = ref(true);
const applying = ref(false);
const dirty = ref(false);
const offs: (() => void)[] = [];

const resValid = computed(() => {
  const w = Math.round(form.width), h = Math.round(form.height);
  return w >= RES_MIN && w <= RES_MAX && h >= RES_MIN && h <= RES_MAX && Number.isFinite(w) && Number.isFinite(h);
});
const fpsValid = computed(() => {
  const f = Math.round(form.fps);
  return f >= 1 && f <= 240 && Number.isFinite(f);
});
const lyricsFpsValid = computed(() => {
  const f = Math.round(form.lyricsFps);
  return f >= 1 && f <= 240 && Number.isFinite(f);
});
const mirrorCdpValid = computed(() => {
  const p = Math.round(form.mirrorCdpPort);
  return p >= 0 && p <= 65535 && Number.isFinite(p);
});
const canApply = computed(() => resValid.value && fpsValid.value && lyricsFpsValid.value && mirrorCdpValid.value);

function markDirty() {
  dirty.value = true;
}

// 预览渲染尺寸：即时生效的应用偏好，不进草稿组
const previewRes = ref<PreviewResTier>(getPreviewRes());

// 播放掉帧自动降质：同为即时生效的应用偏好
const autoDegrade = ref(getAutoDegrade());

function onPreviewRes(e: Event) {
  const raw = (e.target as HTMLSelectElement).value;
  const v: PreviewResTier = raw === 'auto' ? 'auto' : Number(raw) as PreviewResTier;
  previewRes.value = v;
  setPreviewRes(v);
  sizeViewport();   // 重建画布物理像素，置 need 立即重绘
}

function onAspect(e: Event) {
  const v = (e.target as HTMLSelectElement).value;
  aspect.value = v;
  setAspect(v);   // 和导出页同源，写 project.aspect 并广播
}

async function load() {
  loading.value = true;
  try {
    const r = await fetch('/api/app/settings');
    const j = await r.json();
    if (j.ok) Object.assign(form, j.settings);
  } catch { // 服务不在就用默认值
  }
  loading.value = false;
}

// 先保存草稿，再重启渲染引擎，保存的值一次生效
async function applyAndRestart() {
  if (!canApply.value || applying.value) return;
  applying.value = true;
  try {
    const r = await fetch('/api/app/settings', {
      method: 'POST',
      headers: {'Content-Type': 'application/json'},
      body: JSON.stringify({
        width: Math.round(form.width), height: Math.round(form.height), fps: Math.round(form.fps),
        lyricsFps: Math.round(form.lyricsFps),
        spoutEnabled: form.spoutEnabled, spoutName: form.spoutName,
        spoutTransparent: form.spoutTransparent,
        spoutLyricsEnabled: form.spoutLyricsEnabled,
        splitMirrorHost: form.splitMirrorHost,
        mirrorExternalBeginFrame: form.mirrorExternalBeginFrame,
        mirrorDsync: form.mirrorDsync,
        mirrorCdpEnabled: form.mirrorCdpEnabled,
        mirrorCdpPort: Math.round(form.mirrorCdpPort),
        cdpEnabled: form.cdpEnabled, cdpPort: form.cdpPort,
      }),
    });
    const j = await r.json();
    if (!j.ok) throw new Error('保存失败');
    Object.assign(form, j.settings);
    dirty.value = false;

    await fetch('/api/app/render/restart', {method: 'POST'});

    const cdpChanged = j.changed && j.changed.some((c: string) => c.includes('调试端口'));
    toast(j.changed && j.changed.length ? `已生效：${j.changed.join('；')}` : '渲染引擎已重启');
    if (cdpChanged) toast('调试端口需重启应用后生效');
  } catch {
    toast('本机服务不可用，设置未保存');
  }
  applying.value = false;
}

function syncCenter() {
  centerFree.value = S.project?.centerFree === true;
  centerGap.value = S.project?.centerGap ?? 30;
  centerDir.value = S.project?.centerDir === 'lr' ? 'lr' : 'tb';
  centerZone.value = S.project?.centerZone || 'center';
}

function saveCenter() {
  S.project.centerFree = centerFree.value;
  S.project.centerGap = Math.min(70, Math.max(10, Math.round(centerGap.value) || 30));
  S.project.centerDir = centerDir.value;
  S.project.centerZone = centerZone.value;
  bus.emit('sync-ui');
  replan();
}

onMounted(() => {
  load();
  offs.push(bus.on('out', () => {
    aspect.value = S.project?.aspect || '16:9';
    keyBg.value = (J.keyMode(S.project) || 'off');
  }));
  offs.push(bus.on('replan', syncCenter));
  syncCenter();
});
onUnmounted(() => offs.forEach(f => f()));
</script>

<template>
  <div class="px-4 py-3.5">
    <div v-if="loading" class="py-6 grid place-items-center"><i class="spinner lg"></i></div>
    <template v-else>
      <SubtitlePanel/>

      <div class="sec !border-b-0 !px-0">
        <div class="sec-h"><span class="sec-title">自定义留出画面范围</span><span class="text-[11px] text-muted">给角色留出空白，文字与演出放两侧</span>
        </div>
        <label class="checkline"><input v-model="centerFree" type="checkbox" @change="saveCenter">
          留出画面中央（导出页可开关，宽度用这里自定义的值）</label>
        <div v-if="centerFree" class="flex flex-col gap-2">
          <label class="flex items-center gap-3 text-xs">
            <span class="w-[110px] flex-none text-mut">留白位置</span>
            <select v-model="centerZone" aria-label="留白区域位置" class="flex-1" @change="saveCenter">
              <option value="center">画面中央（文字分列两侧/上下）</option>
              <option value="left">左侧留白（文字排右侧）</option>
              <option value="right">右侧留白（文字排左侧）</option>
              <option value="top">顶部留白（文字排下方）</option>
              <option value="bottom">底部留白（文字排上方）</option>
            </select>
          </label>
          <label class="flex items-center gap-3 text-xs">
            <span class="w-[110px] flex-none text-mut">留白宽度 %</span>
            <input v-model.number="centerGap" aria-label="留白宽度百分比" class="w-24" max="70" min="10" step="5"
                   type="number" @change="saveCenter">
            <span class="text-mut">%（10~70，按画面宽/高计）</span>
          </label>
          <label v-if="centerZone === 'center'" class="flex items-center gap-3 text-xs">
            <span class="w-[110px] flex-none text-mut">竖屏分列</span>
            <select v-model="centerDir" aria-label="竖屏画面的分列方式" class="w-40" @change="saveCenter">
              <option value="tb">上下留白</option>
              <option value="lr">左右留白</option>
            </select>
          </label>
        </div>
      </div>

      <div class="flex flex-col gap-2.5">
        <label class="flex items-center gap-3 text-xs">
          <span class="w-[110px] flex-none text-mut">画布比例</span>
          <select :value="aspect" aria-label="画布比例" class="flex-1" @change="onAspect">
            <option v-for="a in ASPECTS" :key="a" :value="a">{{ a }}</option>
          </select>
        </label>
        <p class="hint !text-[11px] !mt-0">与「导出」页的画面比例同源，改这里导出页会同步。</p>

        <label class="flex items-center gap-3 text-xs">
          <span class="w-[110px] flex-none text-mut">背景</span>
          <select :value="keyBg" aria-label="背景" class="flex-1"
                  @change="setKeyBg((($event.target) as HTMLSelectElement).value)">
            <option value="off">常规（风格背景）</option>
            <option value="green">绿幕（用于合成）</option>
            <option value="black">黑幕（用于合成）</option>
          </select>
        </label>
        <p class="hint !text-[11px] !mt-0">
          与「导出」页的背景同源：绿幕/黑幕时只在单色背景上绘制白色文字与演出，供合成抠像。</p>

        <label class="flex items-center gap-3 text-xs">
          <span class="w-[110px] flex-none text-mut">渲染窗口分辨率</span>
          <span class="flex items-center gap-1.5 flex-1">
            <input v-model.number="form.width" :max="RES_MAX" :min="RES_MIN" aria-label="渲染窗口宽" class="w-24"
                   step="2"
                   type="number" @input="markDirty">
            <span class="text-mut">×</span>
            <input v-model.number="form.height" :max="RES_MAX" :min="RES_MIN" aria-label="渲染窗口高" class="w-24"
                   step="2"
                   type="number" @input="markDirty">
            <span v-if="!resValid" class="text-danger text-[11px]">{{ RES_MIN }}~{{ RES_MAX }}</span>
          </span>
        </label>
        <label class="flex items-center gap-3 text-xs">
          <span class="w-[110px] flex-none text-mut">渲染帧率</span>
          <span class="flex items-center gap-1.5">
            <input v-model.number="form.fps" aria-label="渲染帧率" class="w-24" max="240" min="1" step="1" type="number"
                   @input="markDirty">
            <span class="text-mut">fps</span>
            <span v-if="!fpsValid" class="text-danger text-[11px]">1~240</span>
          </span>
        </label>
      </div>

      <div class="flex flex-col gap-1.5 mt-3.5">
        <label class="checkline"><input v-model="form.spoutEnabled" type="checkbox" @change="markDirty"> Spout
          输出</label>
        <label class="flex items-center gap-3 text-xs pl-6">
          <span class="flex-none text-mut">Spout ID：</span>
          <input v-model="form.spoutName" class="flex-1" type="text" @input="markDirty">
        </label>
        <label class="checkline"><input v-model="form.spoutTransparent" type="checkbox" @change="markDirty">
          渲染透明背景（输出透明图层）</label>
        <label class="checkline"><input v-model="form.spoutLyricsEnabled" type="checkbox" @change="markDirty"> 独立
          Spout 字幕通道 <b>{{ form.spoutName || 'JIZURA-live' }}-LYRICS</b></label>
        <label v-if="form.spoutLyricsEnabled" class="flex items-center gap-3 text-xs pl-6">
          <span class="flex-none text-mut">字幕渲染帧率：</span>
          <span class="flex items-center gap-1.5">
            <input v-model.number="form.lyricsFps" aria-label="字幕渲染帧率" class="w-24" max="240" min="1" step="1"
                   type="number" @input="markDirty">
            <span class="text-mut">fps</span>
            <span v-if="!lyricsFpsValid" class="text-danger text-[11px]">1~240</span>
          </span>
        </label>
        <p v-if="form.spoutLyricsEnabled" class="hint !text-[11px] !mt-0 !pl-6">
          字幕通道的输出帧率，生效值不超过渲染帧率；改后随「重启渲染引擎」生效。</p>
        <label class="checkline"><input v-model="form.splitMirrorHost" type="checkbox" @change="markDirty">
          镜像独立进程渲染（GPU 隔离）</label>
        <p class="hint !text-[11px] !mt-0 !pl-6">渲染独立宿主进程，与编辑器互不抢占 GPU：镜像光栅打满时编辑器预览不再被拖去等待。 切换会重建镜像输出（短暂黑 1~2 秒）</p>
      </div>

      <div class="flex flex-col gap-1.5 mt-3.5">
        <label class="checkline">
          <input v-model="form.cdpEnabled" type="checkbox" @change="markDirty"> 调试端口（CDP）
          <input v-model="form.cdpPort" class="w-28" max="65535" min="1" type="number" @change="markDirty">
        </label>

      </div>

      <button :disabled="applying || !canApply"
              :title="dirty ? '保存并重启渲染引擎使设置生效' : '以当前设置重启渲染引擎'"
              class="btn btn-primary w-full mt-4"
              @click="applyAndRestart">
        {{ applying ? '正在重启…' : (dirty ? '保存并重启渲染引擎' : '重启渲染引擎') }}
      </button>
      <p class="note">
        分辨率・帧率・字幕帧率・Spout・透明背景・镜像独立进程的改动需点上方按钮保存后生效（镜像独立进程即时切换，无需重启应用）；调试端口需重启应用。画布比例即时生效。</p>

      <details class="border border-line rounded-md bg-raised mt-4">
        <summary
            class="flex items-center gap-1.5 px-2.5 py-2 cursor-pointer text-xs list-none [&::-webkit-details-marker]:hidden before:content-['▸'] before:text-mut before:mr-2 before:transition-transform open:before:rotate-90">
          <span class="flex-1 font-semibold">高级设置</span>
        </summary>
        <div class="flex flex-col gap-2 px-2.5 pb-2.5 pt-0.5">
          <label class="flex items-center gap-3 text-xs">
            <span class="w-[110px] flex-none text-mut">预览渲染尺寸</span>
            <select :value="String(previewRes)" aria-label="预览渲染尺寸" class="flex-1" @change="onPreviewRes">
              <option value="auto">自动（跟随窗口，上限＝字幕尺寸）</option>
              <option value="720">720p（更流畅）</option>
              <option value="1080">1080p</option>
              <option value="1440">1440p</option>
              <option value="2160">2160p（更精细）</option>
            </select>
          </label>
          <p class="hint !text-[11px] !mt-0">主编辑器预览的渲染上限（按短边计，比例始终跟随画幅）。只影响预览的清晰度与性能：不影响导出分辨率（「导出」页单独设置），也不影响
            Spout 渲染窗口。</p>
          <label class="checkline pt-1.5">
            <input v-model="autoDegrade" type="checkbox" @change="setAutoDegrade(autoDegrade)"> 播放掉帧时自动降质（跳过色差重影层与辉光）</label>
          <p class="hint !text-[11px] !mt-0">
            播放中单帧渲染超时会自动进入降质：在关闭滤镜/辉光之外再跳过色差重影层（约省 2/3 画面绘制），恢复流畅后自动还原。只影响播放中的预览——暂停与导出始终是完整画质；关闭后卡顿也保持完整效果。</p>
          <label class="checkline pt-1.5">
            <input v-model="form.mirrorExternalBeginFrame" type="checkbox" @change="markDirty"> 镜像 External
            BeginFrame（宿主帧泵驱动）</label>
          <p class="hint !text-[11px] !mt-0">
            帧节奏由宿主按输出帧率主动驱动，绕开 Chromium 合成器自调度：消除 BeginFrame 断供与帧间隔抖动，暂停时不再空转出帧。随「重启渲染引擎」生效。</p>
          <label class="checkline">
            <input v-model="form.mirrorDsync" type="checkbox" @change="markDirty"> desynchronized
            画布（低延迟交换链）</label>
          <p class="hint !text-[11px] !mt-0">
            镜像用低延迟交换链直出，省约一帧合成延迟。建议与 External BeginFrame 一起开启——合成器调度下单独开启会被消费端反压，出现数百毫秒停顿。随「重启渲染引擎」生效。</p>
          <label class="checkline pt-1.5">
            <input v-model="form.mirrorCdpEnabled" type="checkbox" @change="markDirty"> 镜像宿主调试端口（CDP）
            <input v-model.number="form.mirrorCdpPort" class="w-24" max="65535" min="0" step="1" type="number"
                   @change="markDirty">
            <span v-if="!mirrorCdpValid" class="text-danger text-[11px]">0~65535</span>
          </label>
          <p class="hint !text-[11px] !mt-0">
            「镜像独立进程渲染」开启时，渲染页面跑在独立宿主进程里，主进程的调试端口够不着它——这里就是那个进程的 CDP
            端口（默认 9223，别与主进程调试端口相同，后启动的一方连不上）。DevTools 连 http://127.0.0.1:端口。改后需重启应用，或关开一次「镜像独立进程渲染」重建宿主进程。</p>
        </div>
      </details>
    </template>
  </div>
</template>
