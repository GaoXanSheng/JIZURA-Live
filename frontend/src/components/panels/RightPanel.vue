<script lang="ts" setup>
/* 右栏宿主：风格 / 特效 / 手法 / 导出 四个 tab */
import {ref} from 'vue';
import StyleTab from './StyleTab.vue';
import FxTab from './FxTab.vue';
import TechTab from './TechTab.vue';
import OutTab from './OutTab.vue';
import SettingsTab from './SettingsTab.vue';

const active = ref('style');
const tabs = [
  {id: 'style', label: '风格'},
  {id: 'fx', label: '特效'},
  {id: 'tech', label: '手法'},
  {id: 'settings', label: '设置'},
  {id: 'out', label: '导出'},
];
</script>

<template>
  <nav class="sticky top-0 z-[2] grid grid-cols-5 border-b border-line bg-panel" role="tablist">
    <button v-for="t in tabs" :key="t.id" :aria-selected="active === t.id"
            :class="active === t.id ? '!text-fg !border-b-amber' : ''"
            class="border-0 rounded-none bg-transparent px-1 py-2.5 text-mut border-b-2 border-transparent min-h-0 hover:bg-white/5"
            role="tab" @click="active = t.id">{{ t.label }}
    </button>
  </nav>
  <StyleTab v-show="active === 'style'"/>
  <FxTab v-show="active === 'fx'"/>
  <!-- 手法 tab 用 v-if 整体卸载：860 张预览 canvas 只在 tab 打开时存在，
       切走即释放 renderer + GPU 两侧的常驻内存；筛选/折叠状态在 TechTab 模块级保留 -->
  <TechTab v-if="active === 'tech'"/>
  <SettingsTab v-if="active === 'settings'"/>
  <OutTab v-show="active === 'out'"/>
</template>
