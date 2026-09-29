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
  <div class="flex flex-col gap-2.5 flex-1 min-h-0">
    <div class="row">
      <input v-model="cloud.query" type="search" placeholder="在线搜索：歌名 / 歌手" class="flex-[1_1_160px] min-w-[120px]" aria-label="在线搜索" @keydown.enter.prevent="cloud.doSearch()" @keydown.stop>
      <select v-model="cloud.quality" class="w-auto" title="音质（320k/无损需要扫码登录）" aria-label="音质">
        <option v-for="q in QUALITIES" :key="q.v" :value="q.v">{{ q.t }}</option>
      </select>
      <button class="btn btn-primary" :disabled="cloud.searching" @click="cloud.doSearch()">{{ cloud.searching ? '搜索中…' : '搜索' }}</button>
    </div>
    <div v-if="cloud.searching" class="py-1">
      <div v-for="i in 6" :key="i" class="skel h-[30px] mb-1.5"></div>
    </div>
    <div v-else-if="cloud.resultsError" class="py-6 px-3 text-xs text-center text-danger">{{ cloud.resultsError }}</div>
    <SongList v-else-if="cloud.results.length" />
    <div v-else class="py-6 px-3 text-xs text-center text-mut">输入关键词搜索歌曲，点歌名即可载入音频与歌词</div>
  </div>
</template>
