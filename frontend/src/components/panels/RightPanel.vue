<script setup lang="ts">
/* 右栏宿主：详细模式 = 四个 tab；简单/手机模式 = かんたん面板 */
import { ref } from 'vue';
import { useEditor } from '../../stores/editor';
import StyleTab from './StyleTab.vue';
import FxTab from './FxTab.vue';
import TechTab from './TechTab.vue';
import OutTab from './OutTab.vue';
import SubtitlePanel from './SubtitlePanel.vue';

const ed = useEditor();
const active = ref('style');
const tabs = [
  { id: 'style', label: '风格' },
  { id: 'fx', label: '演出' },
  { id: 'tech', label: '手法' },
  { id: 'out', label: '导出' },
];
</script>

<template>
  <nav class="tabs" role="tablist">
    <button v-for="t in tabs" :key="t.id" role="tab" :aria-selected="active === t.id" @click="active = t.id">{{ t.label }}</button>
  </nav>
  <StyleTab v-show="active === 'style'" />
  <FxTab v-show="active === 'fx'" />
  <TechTab v-show="active === 'tech'" />
  <OutTab v-show="active === 'out'" />
  <SubtitlePanel />
</template>

<style scoped>
.tabs {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  border-bottom: 1px solid var(--line);
  position: sticky;
  top: 0;
  background: var(--panel);
  z-index: 2;
}
.tabs button { border: 0; border-radius: 0; background: transparent; padding: 11px 4px; color: var(--muted); border-bottom: 2px solid transparent; min-height: 0; }
.tabs button:hover { background: var(--hover); }
.tabs button[aria-selected='true'] { color: var(--text); border-bottom-color: var(--amber); }
</style>
