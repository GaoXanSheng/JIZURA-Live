<script lang="ts" setup>
// 搜索结果和歌单里的歌曲共用这份列表
import {useCloud} from '../../stores/cloud';
import {loadSong, songKey} from '../../cloud/songLoader';
import {fmtDur} from '../../cloud/api';
import type {Song} from '../../cloud/types';

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
  <TransitionGroup class="list-none m-0 p-0 overflow-y-auto border border-line rounded-md flex-1 min-h-0" name="sg"
                   tag="ul">
    <li v-for="it in cloud.results" :key="songKey(it)" class="border-t border-line first:border-t-0">
      <button
          class="btn !border-0 !rounded-none !bg-none w-full !h-auto gap-2 items-baseline px-2.5 py-1.5 text-left whitespace-normal hover:!bg-white/5"
          type="button"
          @click="pick(it)">
        <span :title="it.name" class="flex-none max-w-[52%] truncate">{{ it.name }}</span>
        <span class="text-mut text-[11px] truncate flex-1 min-w-0">{{ it.singer }}{{ it.album ? ' · ' + it.album : '' }} · {{
            fmtDur(it.duration)
          }}</span>
        <span v-if="it.pay && it.pay !== 0"
              class="flex-none text-[10px] leading-[1.5] border border-[#8a6d2f] text-[#c8a24a] rounded px-1">VIP</span>
        <span v-if="state(it) === 'loading' || state(it) === 'downloading'"
              class="flex-none inline-flex items-center gap-1.5 text-[11px] text-cyan">
          <i class="spinner"></i>{{ state(it) === 'loading' ? '载入中…' : '下载中…' }}
        </span>
      </button>
    </li>
  </TransitionGroup>
</template>
