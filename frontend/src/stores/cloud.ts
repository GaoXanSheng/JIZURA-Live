/* 云端音乐 store：来源切换、搜索、歌单、双平台登录态 + 扫码轮询状态机。
   轮询定时器集中在 store 管理，弹窗关闭/组件卸载必须 stopQrPolling()。 */
import { defineStore } from 'pinia';
import { api } from '../cloud/api';
import { toast } from '../jizura/look';
import type { Song, Source, PlaylistInfo } from '../cloud/types';

export type QrState = 'idle' | 'loading' | 'waiting' | 'scanned' | 'expired' | 'ok' | 'error';

let pollTimer = 0;   // 轮询定时器不放响应式 state 里

export const useCloud = defineStore('cloud', {
  state: () => ({
    open: false,
    pane: 'search' as 'search' | 'playlists' | 'account',
    source: 'kugou' as Source,
    quality: '320' as string,

    query: '',
    searching: false,
    results: [] as Song[],
    resultsError: '',

    playlists: [] as PlaylistInfo[],
    playlistsLoading: false,
    playlistsError: '',
    playlistInput: '',
    playlistTip: '',

    /* 每个平台独立登录态 */
    login: {
      kugou: { logged: false, nickname: '' },
      netease: { logged: false, nickname: '' },
    } as Record<Source, { logged: boolean; nickname: string }>,

    /* 扫码弹层（当前只同时展示一个平台的二维码） */
    qr: {
      active: false,
      source: 'kugou' as Source,
      state: 'idle' as QrState,
      img: '',
      tip: '',
      key: '',
    },
    loadingKey: '',
    loadingState: '' as '' | 'loading' | 'downloading' | 'done' | 'error',
  }),
  actions: {
    async refreshLogin(source?: Source) {
      for (const s of source ? [source] : (['kugou', 'netease'] as Source[])) {
        try {
          const j = await api.login(s);
          this.login[s] = { logged: !!j.logged, nickname: j.nickname || '' };
        } catch { /* 服务不在就保持默认 */ }
      }
    },
    setSource(s: Source) {
      this.source = s;
      this.results = [];
      this.resultsError = '';
      this.playlistInput = '';
      this.playlistTip = '';
    },
    async doSearch() {
      const kw = this.query.trim();
      if (!kw) return;
      this.searching = true;
      this.resultsError = '';
      try {
        const j = await api.search(this.source, kw);
        if (!j.ok) throw new Error(j.error || '搜索失败');
        this.results = j.list || [];
      } catch (e: any) {
        this.results = [];
        this.resultsError = '搜索失败：' + (e.message || e);
      } finally {
        this.searching = false;
      }
    },
    async loadPlaylists() {
      this.playlistsLoading = true;
      this.playlistsError = '';
      this.playlistTip = '正在加载我的歌单…';
      try {
        const j = await api.playlists(this.source);
        if (!j.ok) throw new Error(j.error || '读取失败');
        this.playlists = j.list || [];
        this.playlistTip = this.playlists.length
          ? '点歌单载入全部歌曲；也可粘贴歌单链接 / ID 载入'
          : '这个账号还没有歌单；可粘贴歌单链接 / ID 载入';
      } catch (e: any) {
        this.playlists = [];
        const msg = e.message || String(e);
        this.playlistsError = (msg === '请先扫码登录酷狗' || msg === '请先扫码登录网易云')
          ? msg + '（在「账号」页扫码即可）'
          : '加载失败：' + msg;
      } finally {
        this.playlistsLoading = false;
      }
    },

    /* ---- 扫码 ---- */
    async openQr(source: Source) {
      this.stopQrPolling();
      this.qr = { active: true, source, state: 'loading', img: '', tip: '正在获取二维码…', key: '' };
      try {
        const j = await api.qr(source);
        if (!j.ok) throw new Error(j.error || '二维码生成失败');
        this.qr.key = j.key || '';
        this.qr.img = j.img || '';
        this.qr.state = 'waiting';
        this.qr.tip = source === 'netease' ? '打开网易云音乐 App 扫一扫' : '打开酷狗手机版扫一扫';
        this.startQrPolling();
      } catch (e: any) {
        this.qr.state = 'error';
        this.qr.tip = '失败：' + (e.message || e);
      }
    },
    startQrPolling() {
      this.stopQrPolling();
      const ne = this.qr.source === 'netease';
      // 酷狗状态：1 等待 / 2 已扫码 / 0 过期 / 4 成功；网易云：801 / 802 / 800 / 803
      const S_WAIT = ne ? '801' : '1', S_SCAN = ne ? '802' : '2', S_EXPIRE = ne ? '800' : '0', S_OK = ne ? '803' : '4';
      const key = this.qr.key;
      pollTimer = window.setInterval(async () => {
        if (!this.qr.active) return this.stopQrPolling();
        try {
          const j = await api.qrCheck(this.qr.source, key);
          const st = String(j.status);
          if (st === S_WAIT) this.qr.tip = '等待扫码…';
          else if (st === S_SCAN) { this.qr.state = 'scanned'; this.qr.tip = '已扫码，请在手机上确认'; }
          else if (st === S_EXPIRE) { this.qr.state = 'expired'; this.qr.tip = '二维码已过期'; this.stopQrPolling(); }
          else if (st === S_OK) {
            this.qr.state = 'ok';
            this.stopQrPolling();
            this.qr.active = false;
            const src = this.qr.source;
            await this.refreshLogin(src);
            toast(src === 'netease' ? '网易云已登录，VIP 歌曲可以载入了' : '酷狗已登录，VIP 歌曲可以载入完整音质了');
          }
        } catch { /* 网络抖动，下一轮再试 */ }
      }, 2000);
    },
    stopQrPolling() {
      if (pollTimer) { clearInterval(pollTimer); pollTimer = 0; }
    },
    closeQr() {
      this.stopQrPolling();
      this.qr.active = false;
      this.qr.state = 'idle';
    },
    async logout(source: Source) {
      await api.logout(source);
      await this.refreshLogin(source);
    },
    setLoading(key: string, state: typeof this.loadingState) {
      this.loadingKey = key;
      this.loadingState = state;
    },
  },
});
