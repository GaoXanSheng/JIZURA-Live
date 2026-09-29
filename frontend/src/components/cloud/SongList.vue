<script setup lang="ts">
/* 歌曲结果列表（搜索结果与歌单歌曲共用） */
import { useCloud } from '../../stores/cloud';
import { loadSong, songKey } from '../../cloud/songLoader';
import { fmtDur } from '../../cloud/api';
import type { Song } from '../../cloud/types';

const cloud = useCloud();

function state(it: Song) {
  const k = songKey(it);
  return cloud.loadingKey === k ? cloud.loadingState : '';
}

async function pick(it: Song) {
  const st = state(it);
  if (st === 'loading' || st === 'downloading') return;
  await loadSong(cloud.source, it, cloud.quality);
}
</script>

<template>
  <TransitionGroup name="sg" tag="ul" class="song-list">
    <li v-for="it in cloud.results" :key="songKey(it)">
      <button type="button" class="song" @click="pick(it)">
        <span class="name" :title="it.name">{{ it.name }}</span>
        <span class="meta" :title="it.singer">{{ it.singer }}{{ it.album ? ' · ' + it.album : '' }} · {{ fmtDur(it.duration) }}</span>
        <span v-if="it.pay && it.pay !== 0" class="vip">VIP</span>
        <span v-if="state(it) === 'loading'" class="state">载入中…</span>
        <span v-else-if="state(it) === 'downloading'" class="state">下载中…</span>
      </button>
    </li>
  </TransitionGroup>
</template>

<style scoped>
.song-list { list-style: none; margin: 0; padding: 0; overflow-y: auto; border: 1px solid var(--line); border-radius: var(--r-sm); flex: 1; min-height: 0; }
.song-list li { border-top: 1px solid var(--line); }
.song-list li:first-child { border-top: 0; }
.song { display: flex; width: 100%; gap: 8px; align-items: baseline; padding: 7px 10px; background: none; border: 0; border-radius: 0; text-align: left; white-space: normal; min-height: 0; }
.song:hover { background: var(--hover); }
.name { flex: none; max-width: 52%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.meta { color: var(--muted); font-size: 11px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; flex: 1; min-width: 0; }
.vip { flex: none; font-size: 10px; line-height: 1.5; border: 1px solid #8a6d2f; color: #c8a24a; border-radius: 4px; padding: 0 4px; }
.state { flex: none; font-size: 11px; color: var(--cyan); }
.sg-enter-active { transition: opacity 160ms var(--ease-out), transform 160ms var(--ease-out); }
.sg-enter-from { opacity: 0; transform: translateY(4px); }
</style>
