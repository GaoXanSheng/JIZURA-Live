<script setup lang="ts">
/* 演出 tab：强度滑杆 + 闪烁/二拍打/显示 + seed，含锁定 */
import { onMounted, onUnmounted, ref } from 'vue';
import { J } from '../../engine';
import { S } from '../../jizura/state';
import { bus } from '../../jizura/bus';
import { replan, replanSoon } from '../../jizura/engine';
import { remember, commit, toggleParamLock, lockOn } from '../../jizura/look';
import { flushSave } from '../../jizura/project';

const FX: [string, string][] = [['motion', '动效强度'], ['glitch', '故障'], ['chroma', '色偏'], ['decor', '装饰量'], ['density', '切分密度'], ['texture', '质感'], ['bgSwitch', '背景切换']];
const LOCK_ON = '锁定：一键随机/重抽不会改动';
const LOCK_OFF = '锁定中，点击解除';

const sliders = ref<{ k: string; label: string; v: number; locked: boolean }[]>([]);
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
  <div class="tabpane" data-pane="fx">
    <div class="unify-box">
      <span class="muted" style="font-size: 11px">锁定按钮：防止一键随机 / 重抽改动对应项</span>
    </div>
    <div v-for="s in sliders" :key="s.k" class="slider">
      <label :for="`fx_${s.k}`">{{ s.label }}</label>
      <input :id="`fx_${s.k}`" type="range" min="0" max="1" step="0.01" :value="s.v" @input="onSlider(s.k, $event)">
      <output>{{ Math.round(s.v * 100) }}</output>
      <button type="button" class="icon ghost lk" :aria-pressed="s.locked" :title="s.locked ? LOCK_OFF : LOCK_ON" @click="onLock(s.k)">
        <svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4"><rect x="3" y="7" width="10" height="7" rx="1.5"/><path d="M5 7V5a3 3 0 0 1 6 0v2"/></svg>
      </button>
    </div>
    <div class="row" style="margin-top: 6px">
      <label class="row" style="gap: 6px"><input v-model="flash" type="checkbox" @change="onFlash"> 闪烁</label>
      <label class="row" style="gap: 6px">抽帧
        <select v-model="koma" title="动效每秒画多少张（与导出 fps 无关）" @change="onKoma">
          <option value="0">完整（跟随导出 fps）</option>
          <option value="12">二拍抽帧（12张/秒）</option>
          <option value="8">三拍抽帧（8张/秒）</option>
        </select>
      </label>
    </div>
    <div class="fields" style="margin-top: 12px">
      <label class="field">HUD
        <select v-model="hud" @change="onHud">
          <option value="auto">跟随风格</option>
          <option value="on">始终显示</option>
          <option value="off">不显示</option>
        </select>
      </label>
      <label class="field">种子<input v-model="seed" type="number" step="1" @change="onSeed"></label>
      <label class="field">&nbsp;<button class="ghost" @click="newSeed">新种子</button></label>
    </div>
    <p class="note">相同种子生成相同构成。逐行的「重抽」「锁定」在左侧行列表。</p>
  </div>
</template>

<style scoped>
.unify-box { margin-bottom: 10px; padding-bottom: 6px; border-bottom: 1px solid var(--line); }
.slider { display: grid; grid-template-columns: 96px minmax(0, 1fr) 36px 30px; gap: 8px; align-items: center; margin-bottom: 8px; }
.slider input { width: 100%; }
.slider output { font: 500 11px var(--mono); color: var(--muted); text-align: right; }
.slider .lk[aria-pressed='true'] { color: var(--amber); }
.fields { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 8px; }
.field { display: flex; flex-direction: column; gap: 4px; font-size: 11px; color: var(--muted); }
.note { font-size: 12px; color: var(--muted); border-left: 2px solid var(--line2); padding-left: 10px; margin-top: 12px; }
</style>
