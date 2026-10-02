<script lang="ts" setup>
import {onMounted, onUnmounted, watch} from 'vue';
import {useCloud} from '../../stores/cloud';
import {bus} from '../../jizura/bus';
import SearchPane from './SearchPane.vue';
import PlaylistPane from './PlaylistPane.vue';
import AccountPane from './AccountPane.vue';

const cloud = useCloud();
const offs: (() => void)[] = [];

const PANES = [
  {id: 'search', icon: '', label: '搜索'},
  {id: 'playlists', icon: '', label: '我的歌单'},
  {id: 'account', icon: '', label: '账号'},
] as const;

function onKey(e: KeyboardEvent) {
  if (e.key === 'Escape' && cloud.open && !cloud.qr.active) cloud.open = false;
}

onMounted(() => {
  window.addEventListener('keydown', onKey, true);
  offs.push(bus.on('cloud-vip-needed', (src: string) => {
    // 收到 VIP 提示就切到账号页，按来源刷新登录态
    cloud.pane = 'account';
    cloud.refreshLogin(src as any);
  }));
});
onUnmounted(() => {
  window.removeEventListener('keydown', onKey, true);
  offs.forEach(f => f());
  cloud.stopQrPolling();
});

watch(() => cloud.open, (v) => {
  if (v) cloud.refreshLogin();
  else cloud.stopQrPolling();
});
</script>

<template>
  <Teleport to="body">
    <Transition name="fade">
      <div v-if="cloud.open" class="fixed inset-0 z-[2500] bg-[rgba(6,6,8,0.6)] grid place-items-center p-6"
           @click.self="cloud.open = false">
        <Transition appear name="pop">
          <div
              aria-label="云端音乐"
              aria-modal="true"
              class="w-[min(860px,96vw)] h-[min(600px,88vh)] flex flex-col bg-panel border border-line2 rounded-2xl shadow-modal overflow-hidden"
              role="dialog">
            <header class="flex items-center gap-3.5 px-4.5 py-3.5 border-b border-line" style="padding-inline: 18px">
              <strong class="text-sm tracking-wider">云端音乐</strong>
              <span aria-label="搜索来源"
                    class="inline-flex gap-0.5 bg-raised border border-line2 rounded-lg p-0.5 ml-auto"
                    role="group">
                <button :class="cloud.source === 'kugou' ? '!bg-raised2 !text-fg' : ''"
                        class="border-0 bg-transparent rounded-md px-3 py-1 text-xs text-mut min-h-0 hover:bg-white/5"
                        @click="cloud.setSource('kugou')">酷狗</button>
                <button :class="cloud.source === 'netease' ? '!bg-raised2 !text-fg' : ''"
                        class="border-0 bg-transparent rounded-md px-3 py-1 text-xs text-mut min-h-0 hover:bg-white/5"
                        @click="cloud.setSource('netease')">网易云</button>
              </span>
              <button aria-label="关闭" class="btn btn-icon btn-ghost ml-2" @click="cloud.open = false">✕</button>
            </header>
            <div class="flex flex-1 min-h-0 max-[700px]:flex-col">
              <nav
                  class="flex flex-col gap-1 w-36 flex-none p-2.5 border-r border-line bg-black/15 max-[700px]:flex-row max-[700px]:w-auto max-[700px]:border-r-0 max-[700px]:border-b max-[700px]:px-2.5">
                <button v-for="p in PANES" :key="p.id"
                        :class="cloud.pane === p.id ? '!bg-amber/10 !text-amber' : ''"
                        class="flex items-center border-0 bg-transparent rounded-md px-3 py-2.5 text-left text-[13px] text-mut min-h-0 transition-colors hover:bg-white/5 hover:text-fg"
                        @click="cloud.pane = p.id">
                  {{ p.label }}
                </button>
              </nav>
              <div class="flex-1 min-w-0 flex flex-col px-4 py-3.5">
                <SearchPane v-if="cloud.pane === 'search'"/>
                <PlaylistPane v-else-if="cloud.pane === 'playlists'"/>
                <AccountPane v-else/>
              </div>
            </div>
          </div>
        </Transition>
      </div>
    </Transition>
  </Teleport>
</template>
