<script setup lang="ts">
/* 设置 tab：渲染窗口分辨率 / 渲染帧率 / Spout 输出与 ID / 渲染透明背景 / 调试端口 / 重启渲染引擎。
   全部经 /api/app/settings 读写 C# 侧设置（改动即时应用），CDP 重启应用生效。 */
import { onMounted, onUnmounted, reactive, ref } from 'vue';
import { toast } from '../../jizura/look';

const form = reactive({
  width: 1920,
  height: 1080,
  fps: 60,
  spoutEnabled: true,
  spoutName: 'JIZURA-live',
  spoutTransparent: false,
  cdpEnabled: false,
  cdpPort: 9222,
});
const loading = ref(true);
const saving = ref(false);
const offs: (() => void)[] = [];

const RESOLUTIONS = [
  { w: 1280, h: 720, t: '1280 × 720 (16:9)' },
  { w: 1920, h: 1080, t: '1920 × 1080 (16:9)' },
  { w: 2560, h: 1440, t: '2560 × 1440 (16:9)' },
  { w: 1080, h: 1920, t: '1080 × 1920 (9:16 竖屏)' },
  { w: 2160, h: 3840, t: '2160 × 3840 (9:16 竖屏)' },
];
const FPSES = [30, 60, 120];

async function load() {
  loading.value = true;
  try {
    const r = await fetch('/api/app/settings');
    const j = await r.json();
    if (j.ok) Object.assign(form, j.settings);
  } catch { /* 服务不在时保持默认 */ }
  loading.value = false;
}

async function save() {
  if (saving.value || loading.value) return;
  saving.value = true;
  try {
    const r = await fetch('/api/app/settings', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        width: form.width, height: form.height, fps: form.fps,
        spoutEnabled: form.spoutEnabled, spoutName: form.spoutName,
        spoutTransparent: form.spoutTransparent,
        cdpEnabled: form.cdpEnabled, cdpPort: form.cdpPort,
      }),
    });
    const j = await r.json();
    if (j.ok) {
      Object.assign(form, j.settings);
      toast(j.changed && j.changed.length ? j.changed.join('；') : '设置无变化');
    }
  } catch {
    toast('本机服务不可用，设置未保存');
  }
  saving.value = false;
}

function onRes(e: Event) {
  const sel = e.target as HTMLSelectElement;
  const r = RESOLUTIONS[+sel.value];
  if (!r) return;
  form.width = r.w; form.height = r.h;
  save();
}
function onFps(e: Event) { form.fps = +(e.target as HTMLSelectElement).value; save(); }

const resIndex = () => RESOLUTIONS.findIndex(r => r.w === form.width && r.h === form.height);

async function restartRender() {
  try {
    await fetch('/api/app/render/restart', { method: 'POST' });
    toast('渲染引擎已重启');
  } catch {
    toast('本机服务不可用');
  }
}

onMounted(load);
onUnmounted(() => offs.forEach(f => f()));
</script>

<template>
  <div class="px-4 py-3.5">
    <div v-if="loading" class="py-6 grid place-items-center"><i class="spinner lg"></i></div>
    <template v-else>
      <div class="flex flex-col gap-2.5">
        <label class="flex items-center gap-3 text-xs">
          <span class="w-[110px] flex-none text-mut">渲染窗口分辨率</span>
          <select :value="resIndex()" class="flex-1" aria-label="渲染窗口分辨率" @change="onRes">
            <option v-for="(r, i) in RESOLUTIONS" :key="r.t" :value="i">{{ r.t }}</option>
          </select>
        </label>
        <label class="flex items-center gap-3 text-xs">
          <span class="w-[110px] flex-none text-mut">渲染帧率</span>
          <select :value="String(form.fps)" class="w-32" aria-label="渲染帧率" @change="onFps">
            <option v-for="f in FPSES" :key="f" :value="String(f)">{{ f }}</option>
          </select>
        </label>
      </div>

      <div class="flex flex-col gap-1.5 mt-3.5">
        <label class="checkline"><input v-model="form.spoutEnabled" type="checkbox" @change="save"> Spout 输出</label>
        <label class="flex items-center gap-3 text-xs pl-6">
          <span class="flex-none text-mut">Spout ID：</span>
          <input v-model="form.spoutName" type="text" class="flex-1" @change="save">
        </label>
        <label class="checkline"><input v-model="form.spoutTransparent" type="checkbox" @change="save"> 渲染透明背景（输出透明图层，重启镜像后生效）</label>
      </div>

      <div class="flex flex-col gap-1.5 mt-3.5">
        <label class="checkline"><input v-model="form.cdpEnabled" type="checkbox" @change="save"> 调试端口（CDP，重启应用后生效）</label>
        <label class="flex items-center gap-3 text-xs pl-6">
          <span class="flex-none text-mut">端口：</span>
          <input v-model="form.cdpPort" type="number" class="w-28" min="1" max="65535" @change="save">
        </label>
      </div>

      <button class="btn w-full mt-4" @click="restartRender">重启渲染引擎</button>
      <p class="note">这里控制的是 Spout 输出画面（供 OBS 等下游接收）：渲染窗口分辨率与帧率、Spout ID、透明背景。导出视频的分辨率/fps 在「导出」选项卡里设置。</p>
    </template>
  </div>
</template>
