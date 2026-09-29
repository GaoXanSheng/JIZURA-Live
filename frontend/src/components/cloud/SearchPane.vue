<script setup lang="ts">
import { useCloud } from '../../stores/cloud';
import SongList from './SongList.vue';

const cloud = useCloud();
const QUALITIES = [
  { v: '128', t: '128k' },
  { v: '320', t: '320k' },
  { v: 'flac', t: '无损' },
];
</script>

<template>
  <div class="pane">
    <div class="search-row">
      <input v-model="cloud.query" type="search" class="q" :placeholder="cloud.source === 'netease' ? '网易云在线搜索：歌名 / 歌手' : '酷狗在线搜索：歌名 / 歌手'" aria-label="在线搜索" @keydown.enter.prevent="cloud.doSearch()" @keydown.stop>
      <select v-model="cloud.quality" title="音质（320k/无损需要扫码登录）" aria-label="音质">
        <option v-for="q in QUALITIES" :key="q.v" :value="q.v">{{ q.t }}</option>
      </select>
      <button class="primary" :disabled="cloud.searching" @click="cloud.doSearch()">{{ cloud.searching ? '搜索中…' : '搜索' }}</button>
    </div>
    <div v-if="cloud.searching" class="skel-box">
      <div v-for="i in 6" :key="i" class="skel" style="height: 30px; margin-bottom: 6px"></div>
    </div>
    <div v-else-if="cloud.resultsError" class="empty error">{{ cloud.resultsError }}</div>
    <SongList v-else-if="cloud.results.length" />
    <div v-else class="empty">{{ cloud.results.length ? '' : '输入关键词搜索歌曲，点歌名即可载入音频与歌词' }}</div>
  </div>
</template>

<style scoped>
.pane { display: flex; flex-direction: column; gap: 10px; flex: 1; min-height: 0; }
.search-row { display: flex; gap: 8px; flex-wrap: wrap; }
.search-row .q { flex: 1 1 160px; min-width: 120px; }
.empty { padding: 24px 12px; color: var(--muted); font-size: 12px; text-align: center; }
.empty.error { color: var(--danger); }
.skel-box { padding: 4px 0; }
</style>
