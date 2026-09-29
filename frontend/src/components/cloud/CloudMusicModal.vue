<script setup lang="ts">
/* 云端音乐统一弹窗：搜索 / 我的歌单 / 账号 三合一。
   Esc / 遮罩关闭；关闭时停掉扫码轮询。 */
import { onMounted, onUnmounted, watch } from 'vue';
import { useCloud } from '../../stores/cloud';
import { bus } from '../../jizura/bus';
import SearchPane from './SearchPane.vue';
import PlaylistPane from './PlaylistPane.vue';
import AccountPane from './AccountPane.vue';

const cloud = useCloud();
const offs: (() => void)[] = [];

const PANES = [
  { id: 'search', icon: '🔍', label: '搜索' },
  { id: 'playlists', icon: '☰', label: '我的歌单' },
  { id: 'account', icon: '👤', label: '账号' },
] as const;

function onKey(e: KeyboardEvent) {
  if (e.key === 'Escape' && cloud.open && !cloud.qr.active) cloud.open = false;
}

onMounted(() => {
  window.addEventListener('keydown', onKey, true);
  offs.push(bus.on('cloud-vip-needed', (src: string) => {
    // VIP 提示：打开账号页引导登录
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
      <div v-if="cloud.open" class="cm-mask" @click.self="cloud.open = false">
        <Transition name="pop" appear>
          <div class="cm-card" role="dialog" aria-modal="true" aria-label="云端音乐">
            <header class="cm-head">
              <strong>云端音乐</strong>
              <span class="src-switch" role="group" aria-label="搜索来源">
                <button :class="{ on: cloud.source === 'kugou' }" @click="cloud.setSource('kugou')">酷狗</button>
                <button :class="{ on: cloud.source === 'netease' }" @click="cloud.setSource('netease')">网易云</button>
              </span>
              <button class="icon ghost x" aria-label="关闭" @click="cloud.open = false">✕</button>
            </header>
            <div class="cm-body">
              <nav class="cm-nav">
                <button v-for="p in PANES" :key="p.id" :class="{ on: cloud.pane === p.id }" @click="cloud.pane = p.id">
                  <span class="ico">{{ p.icon }}</span>{{ p.label }}
                </button>
              </nav>
              <div class="cm-main">
                <SearchPane v-if="cloud.pane === 'search'" />
                <PlaylistPane v-else-if="cloud.pane === 'playlists'" />
                <AccountPane v-else />
              </div>
            </div>
          </div>
        </Transition>
      </div>
    </Transition>
  </Teleport>
</template>

<style scoped>
.cm-mask {
  position: fixed; inset: 0; z-index: 2500;
  background: var(--backdrop);
  backdrop-filter: blur(6px);
  -webkit-backdrop-filter: blur(6px);
  display: grid; place-items: center;
  padding: 24px;
}
.cm-card {
  width: min(860px, 96vw);
  height: min(600px, 88vh);
  display: flex; flex-direction: column;
  background: var(--panel-glass);
  backdrop-filter: blur(18px);
  -webkit-backdrop-filter: blur(18px);
  border: 1px solid var(--line2);
  border-radius: var(--r-xl);
  box-shadow: var(--shadow-3);
  overflow: hidden;
}
.cm-head {
  display: flex; align-items: center; gap: 14px;
  padding: 14px 18px;
  border-bottom: 1px solid var(--line);
}
.cm-head strong { font-size: 14px; letter-spacing: 0.06em; }
.src-switch { display: inline-flex; gap: 2px; background: var(--raised); border: 1px solid var(--line2); border-radius: 8px; padding: 2px; margin-left: auto; }
.src-switch button { border: 0; background: transparent; border-radius: 6px; padding: 4px 12px; color: var(--muted); font-size: 12px; min-height: 0; }
.src-switch button.on { background: var(--raised2); color: var(--text); }
.src-switch button:hover { background: var(--hover); }
.src-switch button.on:hover { background: var(--raised2); }
.x { margin-left: 8px; }
.cm-body { display: flex; flex: 1; min-height: 0; }
.cm-nav {
  display: flex; flex-direction: column; gap: 4px;
  width: 148px; flex: none;
  padding: 12px 10px;
  border-right: 1px solid var(--line);
  background: rgba(0, 0, 0, 0.14);
}
.cm-nav button {
  display: flex; align-items: center; gap: 8px;
  border: 0; background: transparent;
  border-radius: var(--r-sm);
  padding: 9px 12px;
  color: var(--muted);
  text-align: left;
  font-size: 13px;
  min-height: 0;
  transition: background var(--dur-1) var(--ease), color var(--dur-1) var(--ease);
}
.cm-nav button:hover { background: var(--hover); color: var(--text); }
.cm-nav button.on { background: var(--amber-soft); color: var(--amber); }
.cm-nav .ico { width: 18px; text-align: center; }
.cm-main { flex: 1; min-width: 0; display: flex; flex-direction: column; padding: 14px 16px; }

.fade-enter-active, .fade-leave-active { transition: opacity var(--dur-2) var(--ease); }
.fade-enter-from, .fade-leave-to { opacity: 0; }
.pop-enter-active { transition: opacity var(--dur-2) var(--ease), transform var(--dur-2) var(--ease-out); }
.pop-leave-active { transition: opacity var(--dur-1) var(--ease), transform var(--dur-1) var(--ease); }
.pop-enter-from { opacity: 0; transform: scale(0.97) translateY(10px); }
.pop-leave-to { opacity: 0; transform: scale(0.98); }

@media (max-width: 700px) {
  .cm-body { flex-direction: column; }
  .cm-nav { flex-direction: row; width: auto; border-right: 0; border-bottom: 1px solid var(--line); padding: 8px 10px; }
  .cm-nav button { flex: 1; justify-content: center; padding: 8px 6px; }
}
</style>
