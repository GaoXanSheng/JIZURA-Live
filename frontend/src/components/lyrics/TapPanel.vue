<script setup lang="ts">
/* TAP 打点同步面板（原 tapPanel） */
import { onMounted, onUnmounted, ref } from 'vue';
import { S } from '../../jizura/state';
import { bus } from '../../jizura/bus';
import { pause } from '../../jizura/engine';
import { tapNow, tapBack, stopTap } from '../../jizura/look';

const active = ref(false);
const lineText = ref('—');
const canBack = ref(false);
const compact = ref(false);
const offs: (() => void)[] = [];

function refresh() {
  active.value = !!S.tap;
  if (!S.tap) return;
  const ln = S.plan?.lines?.[S.tap.i];
  lineText.value = ln ? `${S.tap.i + 1}. ${ln.interlude ? '〔间奏〕' : ln.text}` : '—';
  canBack.value = S.tap.done.length > 0;
  compact.value = S.tap.done.length > 0;
}

onMounted(() => {
  offs.push(bus.on('tapstate', refresh));
  offs.push(bus.on('replan', refresh));
  refresh();
});
onUnmounted(() => offs.forEach(f => f()));

function onStop() { pause(); stopTap(); }
</script>

<template>
  <div v-if="active" class="grid gap-2 border border-amber rounded-[10px] p-2.5 mx-3 mt-3 shadow-pop"
       :style="{ background: 'linear-gradient(rgba(245,165,12,0.08), rgba(245,165,12,0.08)), var(--color-panel)' }">
    <div v-if="!compact" class="hint">跟着歌曲，在每行开始的瞬间按 <span class="kbd">Space</span> 或按钮。按错了用 <span class="kbd">Backspace</span> 回退一步。要从某一行重来，就在行列表里点 ◎。</div>
    <div class="text-[15px]">下一行: <b>{{ lineText }}</b></div>
    <button class="btn !h-auto !py-4 !font-mono !text-lg !font-bold !tracking-[0.2em] !bg-amber !text-[#120c00] !border-amber hover:!bg-[#ffb52a]" @click="tapNow">TAP</button>
    <button class="btn btn-ghost" :disabled="!canBack" @click="tapBack">回退一步</button>
    <button class="btn btn-ghost" @click="onStop">结束</button>
  </div>
</template>
