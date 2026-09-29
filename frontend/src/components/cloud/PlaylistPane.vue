<script setup lang="ts">
import { onMounted, watch } from 'vue';
import { useCloud } from '../../stores/cloud';
import { parsePlaylistId, api } from '../../cloud/api';
import { toast } from '../../jizura/look';
import SongList from './SongList.vue';

const cloud = useCloud();

onMounted(() => {
  cloud.loadPlaylists();
});

// 切换来源（酷狗/网易云）后自动重新加载我的歌单
watch(() => cloud.source, () => {
  cloud.loadPlaylists();
});

async function loadPlaylistSongs(id: string, label: string | null) {
  toast('歌单读取中…');
  try {
    const j = await api.playlist(cloud.source, id);
    if (!j.ok) throw new Error(j.error || '读取失败');
    cloud.results = j.list || [];
    cloud.pane = 'search';
    cloud.query = label || '';
    toast((label || '歌单') + '：' + (j.list || []).length + ' 首，点歌名即可载入');
  } catch (e: any) {
    toast('歌单载入失败：' + (e.message || e));
  }
}

function loadPasted() {
  const id = parsePlaylistId(cloud.playlistInput);
  if (!id) {
    cloud.playlistTip = cloud.source === 'netease'
      ? '没认出歌单 ID：粘贴 music.163.com/playlist?id=… 或纯数字 ID'
      : '没认出歌单 ID：粘贴酷狗歌单链接（含 id=）或 global_collection_id';
    return;
  }
  loadPlaylistSongs(id, null);
}
</script>

<template>
  <div class="flex flex-col gap-2.5 flex-1 min-h-0">
    <div class="row">
      <input v-model="cloud.playlistInput" type="text" class="flex-1" placeholder="粘贴歌单 ID / 链接，回车载入" aria-label="歌单 ID 或链接" @keydown.enter.prevent="loadPasted" @keydown.stop>
      <button class="btn" @click="loadPasted">载入</button>
    </div>
    <div v-if="cloud.playlistsLoading" class="py-1">
      <div v-for="i in 5" :key="i" class="skel h-[34px] mb-1.5"></div>
    </div>
    <div v-else-if="cloud.playlistsError" class="py-5 px-3 text-xs text-center text-danger">{{ cloud.playlistsError }}</div>
    <template v-else>
      <ul v-if="cloud.playlists.length" class="list-none m-0 p-0 overflow-y-auto border border-line rounded-md flex-1 min-h-0">
        <li v-for="p in cloud.playlists" :key="p.id" class="border-t border-line first:border-t-0">
          <button type="button" class="btn !border-0 !rounded-none !bg-none w-full !h-auto justify-between items-baseline px-3 py-2 text-left hover:!bg-white/5" @click="loadPlaylistSongs(p.id, p.name)">
            <span class="truncate">{{ p.name }}</span>
            <span class="text-mut text-[11px] flex-none">{{ p.count }} 首</span>
          </button>
        </li>
      </ul>
      <div v-else class="py-5 px-3 text-xs text-center text-mut">{{ cloud.playlistTip }}</div>
    </template>
    <div v-if="cloud.playlistTip && !cloud.playlistsLoading && !cloud.playlistsError" class="text-[11px] text-mut">{{ cloud.playlistTip }}</div>
  </div>
</template>
