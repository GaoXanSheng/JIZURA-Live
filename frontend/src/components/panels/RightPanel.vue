<script setup lang="ts">
/* 右栏宿主：风格 / 特效 / 手法 / 导出 四个 tab */
import { ref } from 'vue';
import StyleTab from './StyleTab.vue';
import FxTab from './FxTab.vue';
import TechTab from './TechTab.vue';
import OutTab from './OutTab.vue';
import SubtitlePanel from './SubtitlePanel.vue';

const active = ref('style');
const tabs = [
  { id: 'style', label: '风格' },
  { id: 'fx', label: '特效' },
  { id: 'tech', label: '手法' },
  { id: 'out', label: '导出' },
];
</script>

<template>
  <nav class="sticky top-0 z-[2] grid grid-cols-4 border-b border-line bg-panel" role="tablist">
    <button v-for="t in tabs" :key="t.id" role="tab" class="border-0 rounded-none bg-transparent px-1 py-2.5 text-mut border-b-2 border-transparent min-h-0 hover:bg-white/5"
            :class="active === t.id ? '!text-fg !border-b-amber' : ''"
            :aria-selected="active === t.id" @click="active = t.id">{{ t.label }}</button>
  </nav>
  <StyleTab v-show="active === 'style'" />
  <FxTab v-show="active === 'fx'" />
  <TechTab v-show="active === 'tech'" />
  <OutTab v-show="active === 'out'" />
  <SubtitlePanel />
</template>
