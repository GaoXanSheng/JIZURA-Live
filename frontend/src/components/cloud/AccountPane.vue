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
  <div class="pane">
    <div v-for="src in (['kugou', 'netease'] as const)" :key="src" class="acct-card">
      <span class="logo" :class="src">{{ src === 'kugou' ? '酷' : '易' }}</span>
      <div class="acct-info">
        <b>{{ src === 'kugou' ? '酷狗音乐' : '网易云音乐' }}</b>
        <small v-if="cloud.login[src].logged">已登录{{ cloud.login[src].nickname ? '：' + cloud.login[src].nickname : '' }}</small>
        <small v-else>未登录 — VIP 歌曲与完整音质需要扫码登录</small>
      </div>
      <button :class="cloud.login[src].logged ? 'ghost' : 'primary'" @click="toggleLogin(src)">
        {{ cloud.login[src].logged ? '退出登录' : '扫码登录' }}
      </button>
    </div>
    <p class="hint note">登录凭据只保存在本机服务端（config/kg_login.json、config/ne_login.json），不会上传。</p>

    <!-- 扫码弹层 -->
    <Teleport to="body">
      <Transition name="pop">
        <div v-if="cloud.qr.active" class="qr-mask" @click.self="cloud.closeQr()">
          <div class="qr-card">
            <strong>{{ cloud.qr.source === 'netease' ? '网易云扫码登录' : '酷狗扫码登录' }}</strong>
            <div class="qr-imgbox" :class="{ loading: cloud.qr.state === 'loading' }">
              <img v-if="cloud.qr.img" :src="cloud.qr.img" alt="登录二维码" width="188" height="188">
              <div v-else class="skel qr-skel"></div>
              <Transition name="fade">
                <div v-if="cloud.qr.state === 'scanned'" class="qr-done">✓<br>已扫码</div>
              </Transition>
            </div>
            <div class="qr-tip" :class="{ expired: cloud.qr.state === 'expired', err: cloud.qr.state === 'error' }">{{ cloud.qr.tip }}</div>
            <div class="qr-btns">
              <button v-if="cloud.qr.state === 'expired' || cloud.qr.state === 'error'" class="ghost small" @click="cloud.openQr(cloud.qr.source)">刷新二维码</button>
              <button class="ghost small" @click="cloud.closeQr()">关闭</button>
            </div>
          </div>
        </div>
      </Transition>
    </Teleport>
  </div>
</template>

<style scoped>
.pane { display: flex; flex-direction: column; gap: 12px; flex: 1; min-height: 0; overflow-y: auto; }
.acct-card {
  display: flex; align-items: center; gap: 12px;
  background: var(--raised);
  border: 1px solid var(--line);
  border-radius: var(--r);
  padding: 14px 16px;
  transition: border-color var(--dur-1) var(--ease);
}
.acct-card:hover { border-color: var(--line2); }
.logo {
  display: grid; place-items: center;
  width: 40px; height: 40px; border-radius: 10px;
  font: 900 18px var(--brand);
  color: #fff; flex: none;
}
.logo.kugou { background: linear-gradient(135deg, #2aa4f4, #0b6fd1); }
.logo.netease { background: linear-gradient(135deg, #ec4141, #c01f1f); }
.acct-info { flex: 1; min-width: 0; display: flex; flex-direction: column; gap: 2px; }
.acct-info small { color: var(--muted); font-size: 11px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.note { border-left: 2px solid var(--line2); padding-left: 10px; }

.qr-mask {
  position: fixed; inset: 0; z-index: 3000;
  background: var(--backdrop);
  backdrop-filter: blur(4px);
  -webkit-backdrop-filter: blur(4px);
  display: grid; place-items: center;
}
.qr-card {
  background: var(--panel-glass);
  backdrop-filter: blur(16px);
  -webkit-backdrop-filter: blur(16px);
  color: var(--text);
  border: 1px solid var(--line2);
  border-radius: var(--r-lg);
  padding: 22px 24px;
  display: flex; flex-direction: column; gap: 12px; align-items: center;
  box-shadow: var(--shadow-3);
}
.qr-imgbox { position: relative; width: 188px; height: 188px; border-radius: var(--r); overflow: hidden; background: #fff; }
.qr-imgbox img { display: block; width: 188px; height: 188px; }
.qr-skel { width: 188px; height: 188px; border-radius: 0; }
.qr-done {
  position: absolute; inset: 0;
  display: grid; place-items: center;
  background: rgba(10, 12, 10, 0.82);
  color: var(--cyan);
  font: 700 30px/1.3 var(--ui);
  text-align: center;
  font-size: 22px;
}
.qr-tip { color: var(--muted); font-size: 12px; min-height: 1.2em; }
.qr-tip.expired { color: var(--amber); }
.qr-tip.err { color: var(--danger); }
.qr-btns { display: flex; gap: 8px; }
.fade-enter-active, .fade-leave-active { transition: opacity 180ms; }
.fade-enter-from, .fade-leave-to { opacity: 0; }
.pop-enter-active { transition: opacity var(--dur-2) var(--ease), transform var(--dur-2) var(--ease-out); }
.pop-leave-active { transition: opacity var(--dur-1) var(--ease), transform var(--dur-1) var(--ease); }
.pop-enter-from { opacity: 0; transform: scale(0.96); }
.pop-leave-to { opacity: 0; transform: scale(0.97); }
</style>
