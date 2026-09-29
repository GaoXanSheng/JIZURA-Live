<script setup lang="ts">
import { ref } from 'vue';
import { bus } from '../../jizura/bus';

interface Item { id: number; text: string; cols?: string[] }
let seq = 0;
const items = ref<Item[]>([]);

bus.on('toast', (text: string, cols?: string[]) => {
  const item: Item = { id: ++seq, text, cols };
  items.value.push(item);
  if (items.value.length > 3) items.value.shift();
  setTimeout(() => {
    items.value = items.value.filter(i => i.id !== item.id);
  }, 1900);
});
</script>

<template>
  <div class="toast-wrap" role="status" aria-live="polite">
    <TransitionGroup name="toastt">
      <div v-for="i in items" :key="i.id" class="toast-item">
        <span class="txt">{{ i.text }}</span>
        <span v-if="i.cols && i.cols.length" class="swatches"><i v-for="(c, k) in i.cols" :key="k" :style="{ background: c }"></i></span>
      </div>
    </TransitionGroup>
  </div>
</template>

<style scoped>
.toastt-enter-active { transition: opacity 180ms var(--ease-out), transform 180ms var(--ease-out); }
.toastt-leave-active { transition: opacity 220ms var(--ease), transform 220ms var(--ease); }
.toastt-enter-from { opacity: 0; transform: translateY(-8px); }
.toastt-leave-to { opacity: 0; transform: translateY(-6px); }
.toast-item .txt { overflow: hidden; text-overflow: ellipsis; }
</style>
