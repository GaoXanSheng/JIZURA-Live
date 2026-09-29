<script setup lang="ts">
/* TAP 打点同步面板（原 tapPanel） */
import { computed, onMounted, onUnmounted, ref } from 'vue';
import { S } from '../../jizura/state';
import { bus } from '../../jizura/bus';
import { pause } from '../../jizura/engine';
import { tapNow, tapBack, stopTap } from '../../jizura/look';

const active = ref(false);
const lineText = ref('—');
const canBack = ref(false);
const compact = ref(false);
const offs: (() => void)[] = [];

const kbd = typeof navigator !== 'undefined' && /Mac/.test(navigator.platform || '') ? 'Space' : 'Space';

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
  <div v-if="active" class="tapbox" :class="{ compact }">
    <div class="hint">跟着歌曲，在每行开始的瞬间按 <span class="kbd">Space</span> 或按钮。按错了用 <span class="kbd">Backspace</span> 回退一步。要从某一行重来，就在行列表里点 ◎。</div>
    <div class="next">下一行: <b>{{ lineText }}</b></div>
    <button class="tap" @click="tapNow">TAP</button>
    <button class="ghost" :disabled="!canBack" @click="tapBack">回退一步</button>
    <button class="ghost" @click="onStop">结束</button>
  </div>
</template>

<style scoped>
.tapbox {
  border: 1px solid var(--amber);
  border-radius: var(--r);
  padding: 10px;
  display: grid;
  gap: 8px;
  background: linear-gradient(rgba(245, 165, 12, 0.08), rgba(245, 165, 12, 0.08)), var(--panel);
  box-shadow: var(--shadow-2);
}
.tapbox .next { font-size: 15px; }
button.tap { font: 700 18px/1 var(--mono); letter-spacing: 0.2em; padding: 16px; background: var(--amber); color: #120c00; border-color: var(--amber); border-radius: var(--r); min-height: 0; }
button.tap:hover { background: #ffb52a; }
.tapbox.compact .hint { display: none; }
</style>
