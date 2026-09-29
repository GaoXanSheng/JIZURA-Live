<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue';
import { J } from '../../engine';
import { S } from '../../jizura/state';
import { bus } from '../../jizura/bus';
import { attachView, sizeViewport } from '../../jizura/engine';

const viewport = ref<HTMLElement | null>(null);
const canvas = ref<HTMLCanvasElement | null>(null);
const msg = ref<string | null>(null);
const keyBadge = ref<{ color: string; label: string } | null>(null);
const offs: (() => void)[] = [];
let ro: ResizeObserver | null = null;

function syncKeyBadge() {
  const k = S.project ? J.keyMode(S.project) : null;
  keyBadge.value = k ? { color: k === 'green' ? '#00b140' : '#000000', label: k === 'green' ? '绿幕背景' : '黑幕背景' } : null;
}

onMounted(() => {
  attachView(viewport.value!, canvas.value!);
  offs.push(bus.on('msg', (m: string | null) => { msg.value = m; }));
  offs.push(bus.on('replan', () => sizeViewport()));
  offs.push(bus.on('mode', () => setTimeout(sizeViewport, 0)));
  offs.push(bus.on('sync-ui', syncKeyBadge));
  ro = new ResizeObserver(() => { sizeViewport(); bus.emit('viewresize'); });
  ro.observe(viewport.value!);
  sizeViewport();
  syncKeyBadge();
});
onUnmounted(() => { offs.forEach(f => f()); ro?.disconnect(); });
</script>

<template>
  <div ref="viewport" class="viewport">
    <!-- id="view" 是 Spout 镜像脚本(SpoutPipeline.cs)的 DOM 契约，不能改名 -->
    <canvas id="view" ref="canvas" width="1280" height="720"></canvas>
    <div v-if="msg" class="overlay-msg">{{ msg }}</div>
    <div v-if="keyBadge" class="key-badge"><i :style="{ background: keyBadge.color }"></i>{{ keyBadge.label }}</div>
  </div>
</template>

<style scoped>
.viewport {
  position: relative;
  width: 100%;
  background: #000;
  border: 1px solid var(--line);
  border-radius: var(--r);
  display: grid;
  place-items: center;
  overflow: hidden;
  flex: 1 1 0;
  min-height: 180px;
}
.viewport canvas { display: block; width: 100%; height: auto; max-height: 68vh; object-fit: contain; }
@media (min-width: 1181px) and (min-height: 620px) {
  :global(html.fixed-ok) .viewport { flex: 1 1 0; min-height: 160px; }
  :global(html.fixed-ok) .viewport canvas { max-height: none; }
}
.overlay-msg {
  position: absolute; left: 12px; bottom: 12px;
  background: rgba(12, 12, 14, 0.85);
  border: 1px solid var(--line2);
  padding: 6px 10px;
  border-radius: var(--r-sm);
  font-size: 12px;
  backdrop-filter: blur(6px);
}
.key-badge {
  position: absolute; right: 10px; top: 10px;
  background: rgba(12, 12, 14, 0.78);
  border: 1px solid var(--line2);
  color: #fff;
  padding: 3px 8px;
  border-radius: var(--r-sm);
  font-size: 11px;
  pointer-events: none;
  letter-spacing: 0.04em;
}
.key-badge i { display: inline-block; width: 9px; height: 9px; border-radius: 2px; margin-right: 6px; vertical-align: -1px; border: 1px solid rgba(255, 255, 255, 0.5); }
</style>
