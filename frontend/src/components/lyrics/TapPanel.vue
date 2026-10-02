<script lang="ts" setup>
// 打点中可减速播放，存的仍是歌曲本来时刻；Tab 把刚打点那行的
// 下一个切分对齐到现在（多切分行用）
import {onMounted, onUnmounted, ref} from 'vue';
import {S} from '../../jizura/state';
import {bus} from '../../jizura/bus';
import {pause} from '../../jizura/engine';
import {saveTapOpts, stopTap, tapBack, tapNow} from '../../jizura/look';

const active = ref(false);
const lineText = ref('—');
const canBack = ref(false);
const compact = ref(false);
const counting = ref(false);
const rate = ref(1);
const count = ref(false);
const offs: (() => void)[] = [];

function refresh() {
  active.value = !!S.tap;
  if (!S.tap) return;
  const ln = S.plan?.lines?.[S.tap.i];
  lineText.value = ln ? `${S.tap.i + 1}. ${ln.interlude ? '〔间奏〕' : ln.text}` : '—';
  canBack.value = S.tap.done.length > 0;
  compact.value = S.tap.done.length > 2;
  counting.value = !!S.tap.counting;
  rate.value = S.tap.rate > 0 ? S.tap.rate : 1;
}

function onRate(e: Event) {
  const v = +(e.target as HTMLSelectElement).value || 1;
  rate.value = v;
  saveTapOpts(v, count.value);
}

function onCount(e: Event) {
  count.value = (e.target as HTMLInputElement).checked;
  saveTapOpts(rate.value, count.value);
}

onMounted(() => {
  try {
    const o = JSON.parse(localStorage.getItem('jizura.tapOpts') || 'null');
    if (o) {
      if ([1, 0.75, 0.5].includes(+o.rate)) rate.value = +o.rate;
      count.value = !!o.count;
    }
  } catch { // 没存过就用默认
  }
  offs.push(bus.on('tapstate', refresh));
  offs.push(bus.on('replan', refresh));
  refresh();
});
onUnmounted(() => offs.forEach(f => f()));

function onStop() {
  pause();
  stopTap();
}
</script>

<template>
  <div v-if="active"
       :style="{ background: 'linear-gradient(rgba(245,165,12,0.08), rgba(245,165,12,0.08)), var(--color-panel)' }"
       class="grid gap-2 border border-amber rounded-[10px] p-2.5 mx-3 mt-3 shadow-pop">
    <div v-if="!compact" class="hint">跟着歌曲，在每行开始的瞬间按 <span class="kbd">Space</span> 或按钮；多切分的行再按
      <span class="kbd">Tab</span> 对齐下一个切分。按错了用 <span class="kbd">Backspace</span> 回退。要从某一行重来，就在行列表里点
      ◎。
    </div>
    <div class="text-[15px]">下一行: <b>{{ lineText }}</b></div>
    <button
        class="btn !h-auto !py-4 !font-mono !text-lg !font-bold !tracking-[0.2em] !bg-amber !text-[#120c00] !border-amber hover:!bg-[#ffb52a]"
        :disabled="counting"
        @click="tapNow">{{ counting ? '准备开始…' : 'TAP' }}
    </button>
    <div class="flex items-center gap-3 text-xs">
      <label class="flex items-center gap-1.5">
        <span class="text-mut">打点速度</span>
        <select :value="String(rate)" aria-label="打点同步的播放速度" class="!h-[26px] !py-0.5" @change="onRate">
          <option value="1">1×</option>
          <option value="0.75">0.75×</option>
          <option value="0.5">0.5×</option>
        </select>
      </label>
      <label class="flex items-center gap-1.5">
        <input v-model="count" aria-label="开始前倒数 3 秒" type="checkbox" @change="onCount"> 倒数 3・2・1
      </label>
    </div>
    <button :disabled="!canBack" class="btn btn-ghost" @click="tapBack">回退一步</button>
    <button class="btn btn-ghost" @click="onStop">结束</button>
  </div>
</template>
