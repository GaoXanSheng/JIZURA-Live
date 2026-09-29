<script setup lang="ts">
import { onMounted } from 'vue';
import { useCloud } from '../../stores/cloud';
import { parsePlaylistId, api } from '../../cloud/api';
import { toast } from '../../jizura/look';
import SongList from './SongList.vue';

const cloud = useCloud();

onMounted(() => {
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
  <div class="pane">
    <div class="pl-row">
      <input v-model="cloud.playlistInput" type="text" placeholder="粘贴歌单 ID / 链接，回车载入" aria-label="歌单 ID 或链接" @keydown.enter.prevent="loadPasted" @keydown.stop>
      <button @click="loadPasted">载入</button>
    </div>
    <div v-if="cloud.playlistsLoading" class="skel-box">
      <div v-for="i in 5" :key="i" class="skel" style="height: 34px; margin-bottom: 6px"></div>
    </div>
    <div v-else-if="cloud.playlistsError" class="empty error">{{ cloud.playlistsError }}</div>
    <template v-else>
      <ul v-if="cloud.playlists.length" class="pl-list">
        <li v-for="p in cloud.playlists" :key="p.id">
          <button type="button" @click="loadPlaylistSongs(p.id, p.name)">
            <span class="pl-name">{{ p.name }}</span>
            <span class="pl-count">{{ p.count }} 首</span>
          </button>
        </li>
      </ul>
      <div v-else class="empty">{{ cloud.playlistTip }}</div>
    </template>
    <div v-if="cloud.playlistTip && !cloud.playlistsLoading && !cloud.playlistsError" class="tip muted">{{ cloud.playlistTip }}</div>
  </div>
</template>

<style scoped>
.pane { display: flex; flex-direction: column; gap: 10px; flex: 1; min-height: 0; }
.pl-row { display: flex; gap: 8px; }
.pl-row input { flex: 1; }
.pl-list { list-style: none; margin: 0; padding: 0; overflow-y: auto; border: 1px solid var(--line); border-radius: var(--r-sm); flex: 1; min-height: 0; }
.pl-list li { border-top: 1px solid var(--line); }
.pl-list li:first-child { border-top: 0; }
.pl-list button { display: flex; width: 100%; gap: 8px; justify-content: space-between; align-items: baseline; padding: 8px 12px; background: none; border: 0; text-align: left; min-height: 0; }
.pl-list button:hover { background: var(--hover); }
.pl-name { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.pl-count { color: var(--muted); font-size: 11px; flex: none; }
.empty { padding: 20px 12px; color: var(--muted); font-size: 12px; text-align: center; }
.empty.error { color: var(--danger); }
.tip { font-size: 11px; }
</style>
