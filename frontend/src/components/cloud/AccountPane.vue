<script setup lang="ts">
/* 账号页：双平台登录状态卡 + 扫码弹层（轮询状态机在 cloud store） */
import { onMounted, onUnmounted } from 'vue';
import { useCloud } from '../../stores/cloud';
import { toast } from '../../jizura/look';

const cloud = useCloud();
const offs: (() => void)[] = [];

onMounted(() => {
  cloud.refreshLogin();
  offs.push(() => cloud.stopQrPolling());   // 离开账号页/关弹窗都停轮询
});
onUnmounted(() => offs.forEach(f => f()));

async function toggleLogin(source: 'kugou' | 'netease') {
  if (cloud.login[source].logged) {
    const name = source === 'netease' ? '网易云' : '酷狗';
    if (!window.confirm(`确定退出${name}登录？\n退出后 VIP 歌曲将无法完整载入，重新扫码即可恢复。`)) return;
    await cloud.logout(source);
    toast(`已退出${name}登录`);
    return;
  }
  cloud.openQr(source);
}
</script>

<template>
  <div class="flex flex-col gap-3 flex-1 min-h-0 overflow-y-auto">
    <div v-for="src in (['kugou', 'netease'] as const)" :key="src" class="flex items-center gap-3 bg-raised border border-line rounded-[10px] p-3.5 transition-colors hover:border-line2">
      <span class="grid place-items-center w-10 h-10 rounded-[10px] text-white flex-none font-black text-lg font-brand"
            :style="{ background: src === 'kugou' ? 'linear-gradient(135deg,#2aa4f4,#0b6fd1)' : 'linear-gradient(135deg,#ec4141,#c01f1f)' }">{{ src === 'kugou' ? '酷' : '易' }}</span>
      <div class="flex-1 min-w-0 flex flex-col gap-0.5">
        <b>{{ src === 'kugou' ? '酷狗音乐' : '网易云音乐' }}</b>
        <small class="text-mut text-[11px] truncate">{{ cloud.login[src].logged ? `已登录${cloud.login[src].nickname ? '：' + cloud.login[src].nickname : ''}` : '未登录 — VIP 歌曲与完整音质需要扫码登录' }}</small>
      </div>
      <button :class="cloud.login[src].logged ? 'btn btn-ghost' : 'btn btn-primary'" @click="toggleLogin(src)">
        {{ cloud.login[src].logged ? '退出登录' : '扫码登录' }}
      </button>
    </div>
    <p class="hint border-l-2 border-line2 pl-2.5">登录凭据只保存在本机服务端（config/kg_login.json、config/ne_login.json），不会上传。</p>

    <Teleport to="body">
      <Transition name="pop">
        <div v-if="cloud.qr.active" class="fixed inset-0 z-[3000] bg-[rgba(6,6,8,0.6)] grid place-items-center" @click.self="cloud.closeQr()">
          <div class="bg-panel text-fg border border-line2 rounded-2xl p-6 flex flex-col gap-3 items-center shadow-modal">
            <strong>{{ cloud.qr.source === 'netease' ? '网易云扫码登录' : '酷狗扫码登录' }}</strong>
            <div class="relative w-[188px] h-[188px] rounded-[10px] overflow-hidden bg-white">
              <img v-if="cloud.qr.img" :src="cloud.qr.img" alt="登录二维码" width="188" height="188" class="block">
              <div v-else class="skel w-full h-full !rounded-none"></div>
              <Transition name="fade">
                <div v-if="cloud.qr.state === 'scanned'" class="absolute inset-0 grid place-items-center bg-[rgba(10,12,10,0.82)] text-cyan font-bold text-[22px] text-center leading-tight">已扫码</div>
              </Transition>
            </div>
            <div class="text-xs min-h-[1.2em]" :class="cloud.qr.state === 'expired' ? 'text-amber' : cloud.qr.state === 'error' ? 'text-danger' : 'text-mut'">{{ cloud.qr.tip }}</div>
            <div class="flex gap-2">
              <button v-if="cloud.qr.state === 'expired' || cloud.qr.state === 'error'" class="btn btn-ghost btn-sm" @click="cloud.openQr(cloud.qr.source)">刷新二维码</button>
              <button class="btn btn-ghost btn-sm" @click="cloud.closeQr()">关闭</button>
            </div>
          </div>
        </div>
      </Transition>
    </Teleport>
  </div>
</template>
