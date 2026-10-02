<script lang="ts" setup>
import {onMounted, onUnmounted, ref} from 'vue';
import {J} from '../../engine';
import {S} from '../../jizura/state';
import {bus} from '../../jizura/bus';
import {attachView, sizeViewport} from '../../jizura/engine';

const viewport = ref<HTMLElement | null>(null);
const canvas = ref<HTMLCanvasElement | null>(null);
const msg = ref<string | null>(null);
const keyBadge = ref<{ color: string; label: string } | null>(null);
const offs: (() => void)[] = [];
let ro: ResizeObserver | null = null;

function syncKeyBadge() {
  const k = S.project ? J.keyMode(S.project) : null;
  keyBadge.value = k ? {
    color: k === 'green' ? '#00b140' : '#000000',
    label: k === 'green' ? '绿幕背景' : '黑幕背景'
  } : null;
}

onMounted(() => {
  attachView(viewport.value!, canvas.value!);
  offs.push(bus.on('msg', (m: string | null) => {
    msg.value = m;
  }));
  offs.push(bus.on('replan', () => sizeViewport()));
  offs.push(bus.on('sync-ui', syncKeyBadge));
  ro = new ResizeObserver(() => {
    sizeViewport();
    bus.emit('viewresize');
  });
  ro.observe(viewport.value!);
  sizeViewport();
  syncKeyBadge();
});
onUnmounted(() => {
  offs.forEach(f => f());
  ro?.disconnect();
});
</script>

<template>
  <div ref="viewport"
       class="relative w-full flex-1 min-h-[180px] bg-black border border-line rounded-[10px] grid place-items-center overflow-hidden">
    <!-- id="view" 是 Spout 镜像脚本(SpoutPipeline.cs)的 DOM 契约，不能改名。
         will-change/contain：画布独占合成层，UI 浮层重绘不再 invalidate 画布层 -->
    <canvas id="view" ref="canvas" class="block w-full h-auto max-h-[68vh] object-contain max-[1180px]:max-h-[50vh]"
            style="will-change:transform;contain:content;"
            height="720"
            width="1280"></canvas>
    <div v-if="msg" class="absolute left-3 bottom-3 bg-ink/85 border border-line2 px-2.5 py-1.5 rounded-md text-xs">
      {{ msg }}
    </div>
    <div v-if="keyBadge"
         class="absolute right-2.5 top-2.5 bg-ink/80 border border-line2 text-white px-2 py-0.5 rounded-md text-[11px] tracking-wide pointer-events-none">
      <i :style="{ background: keyBadge.color }"
         class="inline-block w-2 h-2 rounded-sm mr-1.5 -mb-px border border-white/50"></i>{{ keyBadge.label }}
    </div>
  </div>
</template>
