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
  <div class="fixed top-3.5 left-1/2 -translate-x-1/2 z-[4000] flex flex-col items-center gap-2 pointer-events-none" role="status" aria-live="polite">
    <TransitionGroup name="pop">
      <div v-for="i in items" :key="i.id" class="flex items-center gap-2.5 max-w-[min(560px,calc(100vw-32px))] bg-ink/70 backdrop-blur-xl border border-line2 border-l-2 border-l-amber px-3.5 py-2 rounded-[10px] text-xs shadow-pop whitespace-nowrap overflow-hidden text-ellipsis">
        <span class="truncate">{{ i.text }}</span>
        <span v-if="i.cols && i.cols.length" class="inline-flex gap-0.5">
          <i v-for="(c, k) in i.cols" :key="k" class="w-4 h-4 rounded-sm border border-white/20" :style="{ background: c }"></i>
        </span>
      </div>
    </TransitionGroup>
  </div>
</template>
