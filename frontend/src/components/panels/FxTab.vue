<script setup lang="ts">
/* 特效 tab：统一感/文字排列 + 强度滑杆（官方译名）+ 闪光/抽帧/HUD/种子，含锁定 */
import { onMounted, onUnmounted, ref } from 'vue';
import { J } from '../../engine';
import { S } from '../../jizura/state';
import { bus } from '../../jizura/bus';
import { replan, replanSoon } from '../../jizura/engine';
import { remember, commit, toggleParamLock, lockOn, toast } from '../../jizura/look';
import { flushSave } from '../../jizura/project';

const FX: [string, string][] = [
  ['motion', '动态强度'], ['glitch', '故障效果'], ['chroma', '色彩错位'], ['decor', '装饰量'],
  ['density', '镜头密度'], ['texture', '质感'], ['bgSwitch', '背景切换'],
];
const LOCK_ON = '锁定：一键随机/重抽不改动此项';
const LOCK_OFF = '锁定中，点击解除';

const sliders = ref<{ k: string; label: string; v: number; locked: boolean }[]>([]);
const unify = ref(false);
const typeset = ref(false);
const flash = ref(false);
const koma = ref('0');
const hud = ref('auto');
const seed = ref(0);
const offs: (() => void)[] = [];

function rebuild() {
  if (!S.project) return;
  sliders.value = FX.map(([k, label]) => ({
    k, label,
    v: S.project.fx[k] ?? 0.5,
    locked: lockOn(k),
  }));
  unify.value = S.project.unify === true;
  typeset.value = S.project.typeset === true;
  flash.value = !!S.project.fx.flash;
  koma.value = String(J.komaOf(S.project.fx));
  hud.value = S.project.fx.hud || 'auto';
  seed.value = S.project.seed;
}

function onSlider(k: string, ev: Event) {
  S.project.fx[k] = +(ev.target as HTMLInputElement).value;
  S.project.mood = null;
  const row = sliders.value.find(s => s.k === k);
  if (row) row.v = S.project.fx[k];
  replanSoon(120);
}

function onLock(k: string) { toggleParamLock(k); rebuild(); }

function onSet(cls: 'unify' | 'typeset') {
  remember();
  S.project[cls] = cls === 'unify' ? unify.value : typeset.value;
  replan(); commit(); flushSave();
  toast(cls === 'unify'
    ? (unify.value ? '统一感：开（分段内统一、并使用点睛/变形/加粗）' : '统一感：关')
    : (typeset.value ? '文字排列：开（字距/助词/英文/0.2秒提前/效果收敛）' : '文字排列：关'));
}

function onFlash() { S.project.fx.flash = flash.value; replan(); }
function onKoma() {
  const k = +koma.value;
  S.project.fx.koma = k;
  S.project.fx.onTwos = k > 0;
  S.project.mood = null;
  replan();
}
function onHud() { S.project.fx.hud = hud.value; replan(); }
function onSeed() { S.project.seed = parseInt(String(seed.value), 10) || 0; replan(); }
function newSeed() { S.project.seed = (Math.random() * 1e9) | 0; seed.value = S.project.seed; replan(); }

onMounted(() => {
  offs.push(bus.on('sync-ui', rebuild));
  offs.push(bus.on('replan', () => { seed.value = S.project.seed; }));
  rebuild();
});
onUnmounted(() => offs.forEach(f => f()));
</script>

<template>
  <div class="px-4 py-3.5">
    <div class="flex flex-col gap-1.5 mb-3 pb-1.5 border-b border-line">
      <label class="checkline"><input v-model="unify" type="checkbox" @change="onSet('unify')"><span>统一感</span></label>
      <label class="checkline"><input v-model="typeset" type="checkbox" @change="onSet('typeset')"><span>文字排列</span></label>
    </div>
    <div v-for="s in sliders" :key="s.k" class="grid grid-cols-[96px_minmax(0,1fr)_36px_30px] gap-2 items-center mb-2">
      <label :for="`fx_${s.k}`" class="text-xs">{{ s.label }}</label>
      <input :id="`fx_${s.k}`" type="range" min="0" max="1" step="0.01" :value="s.v" class="w-full" @input="onSlider(s.k, $event)">
      <output class="font-mono text-[11px] text-mut text-right">{{ Math.round(s.v * 100) }}</output>
      <button type="button" class="btn btn-icon btn-ghost" :class="s.locked ? '!text-amber' : ''" :aria-pressed="s.locked" :title="s.locked ? LOCK_OFF : LOCK_ON" @click="onLock(s.k)">
        <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4"><rect x="3" y="7" width="10" height="7" rx="1.5"/><path d="M5 7V5a3 3 0 0 1 6 0v2"/></svg>
      </button>
    </div>
    <div class="row mt-1.5">
      <label class="checkline"><input v-model="flash" type="checkbox" @change="onFlash"> 闪光</label>
      <label class="btn btn-ghost !h-8" style="border: 0; background: none; padding: 0 0 0 6px">
        <span class="text-xs text-mut shrink-0">抽帧</span>
        <select v-model="koma" class="!h-7 text-xs" title="动效每秒画多少张（与导出 fps 无关）" @change="onKoma">
          <option value="0">全帧（同输出 fps）</option>
          <option value="12">一拍二（12 张/秒）</option>
          <option value="8">一拍三（8 张/秒）</option>
        </select>
      </label>
      <button type="button" class="btn btn-icon btn-ghost" :aria-pressed="lockOn('koma')" :title="lockOn('koma') ? LOCK_OFF : LOCK_ON" @click="onLock('koma'); rebuild()">
        <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4"><rect x="3" y="7" width="10" height="7" rx="1.5"/><path d="M5 7V5a3 3 0 0 1 6 0v2"/></svg>
      </button>
    </div>
    <div class="grid grid-cols-3 gap-2 mt-3">
      <label class="field">HUD
        <select v-model="hud" @change="onHud">
          <option value="auto">随风格</option>
          <option value="on">始终显示</option>
          <option value="off">不显示</option>
        </select>
      </label>
      <label class="field">种子<input v-model="seed" type="number" step="1" @change="onSeed"></label>
      <label class="field">&nbsp;<button class="btn btn-ghost" @click="newSeed">新种子</button></label>
    </div>
    <p class="note">相同的种子会生成相同的编排。逐行“重新随机”和“锁定”请在左侧行列表中操作。</p>
  </div>
</template>
