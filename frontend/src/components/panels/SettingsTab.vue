<script setup lang="ts">
/* 设置 tab（导出前）：
   - 画布比例：与「导出」页画面比例同源（同一个 project.aspect，改一处两处同步，即时生效）
   - 渲染窗口分辨率/帧率（自定义输入，帧率 1~240）/Spout 输出与 ID/渲染透明背景/调试端口：
     改动只存草稿，点「重启渲染引擎」才保存并统一生效（避免随手改动实时打断输出） */
import { computed, onMounted, onUnmounted, reactive, ref } from 'vue';
import { J } from '../../engine';
import { S } from '../../jizura/state';
import { bus } from '../../jizura/bus';
import { setAspect, setKeyBg } from '../../jizura/export';
import { toast } from '../../jizura/look';
import SubtitlePanel from './SubtitlePanel.vue';
import { replan } from '../../jizura/engine';

const ASPECTS = ['16:9', '9:16', '4:3', '3:4', '1:1', '4:5', '21:9'];
const RES_MIN = 64, RES_MAX = 4096;

const form = reactive({
  width: 1920,
  height: 1080,
  fps: 60,
  spoutEnabled: true,
  spoutName: 'JIZURA-live',
  spoutTransparent: false,
  spoutLyricsEnabled: false,
  cdpEnabled: false,
  cdpPort: 9222,
});
const aspect = ref(S.project?.aspect || '16:9');
const keyBg = ref('off');
const centerFree = ref(S.project?.centerFree === true);
const centerGap = ref(S.project?.centerGap ?? 30);
const centerDir = ref(S.project?.centerDir === 'lr' ? 'lr' : 'tb');
const centerZone = ref(S.project?.centerZone || 'center');
const probeOn = ref(localStorage.getItem('jizura.probe') === '1');
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
const canApply = computed(() => resValid.value && fpsValid.value);

function markDirty() { dirty.value = true; }

function onAspect(e: Event) {
  const v = (e.target as HTMLSelectElement).value;
  aspect.value = v;
  setAspect(v);   // 与导出页画面比例同源：写 project.aspect 并广播
}

async function load() {
  loading.value = true;
  try {
    const r = await fetch('/api/app/settings');
    const j = await r.json();
    if (j.ok) Object.assign(form, j.settings);
  } catch { /* 服务不在时保持默认 */ }
  loading.value = false;
}

/* 保存草稿 → 重启渲染引擎（以保存的值重建镜像，一次生效） */
function toggleProbe() {
  try {
    if (probeOn.value) localStorage.setItem('jizura.probe', '1');
    else localStorage.removeItem('jizura.probe');
  } catch {}
  window.location.reload();
}

async function applyAndRestart() {
  if (!canApply.value || applying.value) return;
  applying.value = true;
  try {
    const r = await fetch('/api/app/settings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        width: Math.round(form.width), height: Math.round(form.height), fps: Math.round(form.fps),
        spoutEnabled: form.spoutEnabled, spoutName: form.spoutName,
        spoutTransparent: form.spoutTransparent,
        spoutLyricsEnabled: form.spoutLyricsEnabled,
        cdpEnabled: form.cdpEnabled, cdpPort: form.cdpPort,
      }),
    });
    const j = await r.json();
    if (!j.ok) throw new Error('保存失败');
    Object.assign(form, j.settings);
    dirty.value = false;

    await fetch('/api/app/render/restart', { method: 'POST' });

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
      <SubtitlePanel />

      <div class="sec !border-b-0 !px-0">
        <div class="sec-h"><span class="sec-title">留出画面中央</span><span class="text-[11px] text-muted">给角色留出空白，文字与演出放两侧</span></div>
        <label class="checkline"><input v-model="centerFree" type="checkbox" @change="saveCenter"> 留出画面中央（导出页可开关，宽度用这里自定义的值）</label>
        <div v-if="centerFree" class="flex flex-col gap-2">
          <label class="flex items-center gap-3 text-xs">
            <span class="w-[110px] flex-none text-mut">留白位置</span>
            <select v-model="centerZone" class="flex-1" aria-label="留白区域位置" @change="saveCenter">
              <option value="center">画面中央（文字分列两侧/上下）</option>
              <option value="left">左侧留白（文字排右侧）</option>
              <option value="right">右侧留白（文字排左侧）</option>
              <option value="top">顶部留白（文字排下方）</option>
              <option value="bottom">底部留白（文字排上方）</option>
            </select>
          </label>
          <label class="flex items-center gap-3 text-xs">
            <span class="w-[110px] flex-none text-mut">留白宽度 %</span>
            <input v-model.number="centerGap" type="number" min="10" max="70" step="5" class="w-24" aria-label="留白宽度百分比" @change="saveCenter">
            <span class="text-mut">%（10~70，按画面宽/高计）</span>
          </label>
          <label v-if="centerZone === 'center'" class="flex items-center gap-3 text-xs">
            <span class="w-[110px] flex-none text-mut">竖屏分列</span>
            <select v-model="centerDir" class="w-40" aria-label="竖屏画面的分列方式" @change="saveCenter">
              <option value="tb">上下留白</option>
              <option value="lr">左右留白</option>
            </select>
          </label>
        </div>
      </div>

      <div class="flex flex-col gap-2.5">
        <label class="flex items-center gap-3 text-xs">
          <span class="w-[110px] flex-none text-mut">画布比例</span>
          <select :value="aspect" class="flex-1" aria-label="画布比例" @change="onAspect">
            <option v-for="a in ASPECTS" :key="a" :value="a">{{ a }}</option>
          </select>
        </label>
        <p class="hint !text-[11px] !mt-0">与「导出」页的画面比例同源，改这里导出页会同步。</p>

        <label class="flex items-center gap-3 text-xs">
          <span class="w-[110px] flex-none text-mut">背景</span>
          <select :value="keyBg" class="flex-1" aria-label="背景" @change="setKeyBg((($event.target) as HTMLSelectElement).value)">
            <option value="off">常规（风格背景）</option>
            <option value="green">绿幕（用于合成）</option>
            <option value="black">黑幕（用于合成）</option>
          </select>
        </label>
        <p class="hint !text-[11px] !mt-0">与「导出」页的背景同源：绿幕/黑幕时只在单色背景上绘制白色文字与演出，供合成抠像。</p>

        <label class="flex items-center gap-3 text-xs">
          <span class="w-[110px] flex-none text-mut">渲染窗口分辨率</span>
          <span class="flex items-center gap-1.5 flex-1">
            <input v-model.number="form.width" type="number" :min="RES_MIN" :max="RES_MAX" step="2" class="w-24" aria-label="渲染窗口宽" @input="markDirty">
            <span class="text-mut">×</span>
            <input v-model.number="form.height" type="number" :min="RES_MIN" :max="RES_MAX" step="2" class="w-24" aria-label="渲染窗口高" @input="markDirty">
            <span v-if="!resValid" class="text-danger text-[11px]">{{ RES_MIN }}~{{ RES_MAX }}</span>
          </span>
        </label>
        <label class="flex items-center gap-3 text-xs">
          <span class="w-[110px] flex-none text-mut">渲染帧率</span>
          <span class="flex items-center gap-1.5">
            <input v-model.number="form.fps" type="number" min="1" max="240" step="1" class="w-24" aria-label="渲染帧率" @input="markDirty">
            <span class="text-mut">fps</span>
            <span v-if="!fpsValid" class="text-danger text-[11px]">1~240</span>
          </span>
        </label>
      </div>

      <div class="flex flex-col gap-1.5 mt-3.5">
        <label class="checkline"><input v-model="form.spoutEnabled" type="checkbox" @change="markDirty"> Spout 输出</label>
        <label class="flex items-center gap-3 text-xs pl-6">
          <span class="flex-none text-mut">Spout ID：</span>
          <input v-model="form.spoutName" type="text" class="flex-1" @input="markDirty">
        </label>
        <label class="checkline"><input v-model="form.spoutTransparent" type="checkbox" @change="markDirty"> 渲染透明背景（输出透明图层）</label>
        <label class="checkline"><input v-model="form.spoutLyricsEnabled" type="checkbox" @change="markDirty"> 独立 Spout 字幕通道（歌词单独输出到 <b>{{ form.spoutName || 'JIZURA-live' }}-LYRICS</b>，透明底）</label>
      </div>

      <div class="flex flex-col gap-1.5 mt-3.5">
        <label class="checkline"><input v-model="form.cdpEnabled" type="checkbox" @change="markDirty"> 调试端口（CDP）</label>
        <label class="flex items-center gap-3 text-xs pl-6">
          <span class="flex-none text-mut">端口：</span>
          <input v-model="form.cdpPort" type="number" class="w-28" min="1" max="65535" @change="markDirty">
        </label>
      </div>

      <div class="sec !border-b-0 !px-0 mt-3.5">
        <div class="sec-h"><span class="sec-title">开发者</span></div>
        <label class="checkline"><input v-model="probeOn" type="checkbox" @change="toggleProbe"> 性能探针 HUD（右下角实时显示各路径耗时，切换后刷新页面）</label>
      </div>

      <button class="btn btn-primary w-full mt-4" :disabled="applying || !canApply"
              :title="dirty ? '保存并重启渲染引擎使设置生效' : '以当前设置重启渲染引擎'"
              @click="applyAndRestart">
        {{ applying ? '正在重启…' : (dirty ? '保存并重启渲染引擎' : '重启渲染引擎') }}
      </button>
      <p class="note">分辨率・帧率・Spout・透明背景的改动需点上方按钮保存并重启渲染引擎后生效；调试端口需重启应用。画布比例即时生效。</p>
    </template>
  </div>
</template>
