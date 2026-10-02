// 引擎运行时状态。刻意保持非响应式：S.t 每帧变化，走 bus 通知，不进 Vue 响应式系统。
import {J} from '../engine';

export type LoopMode = 'all' | 'line' | 'cut' | false;

// 模块实例防撕裂：vite dev 下同一文件可能被多份求值（HMR ?t 变体跨分支残留），
// 编辑页曾因此出现「store 读到一份从未 boot 的死 S」，播放按钮/循环等 UI 永久冻结。
// 运行时状态一律锚在 globalThis 上，多少个模块副本都共享同一份 S/AP。
const g = globalThis as any;

const initialS = {
  project: null as any,
  plan: null as any,
  audio: null as any,
  renderer: new J.Renderer(),
  playing: false,
  t: 0,
  t0: 0,
  loop: 'all' as LoopMode,
  loopHold: null as any,
  need: true,
  exporting: null as AbortController | null,
  tap: null as {
    i: number;
    from: number;
    done: { i: number; had: number | null; cut?: boolean; li?: number; k?: number }[];
    rate: number;
    counting?: boolean;
    cl?: number | null;
    ck?: number;
    cd?: number
  } | null,
  slow: false,
  curLine: -2,
  scrubbing: false,
};
export type EngineState = typeof initialS;
export const S: EngineState = g.__jl_S || (g.__jl_S = initialS);

export const LOOP_CYCLE: LoopMode[] = ['all', 'line', 'cut', false];

// WebAudio 播放器（沙箱页面里 blob media 可能被禁，AudioContext 稳妥），同样锚 globalThis：
// 两个副本会各自 new 一个 AudioContext（浏览器有数量上限）。
// 播放图：src → 音频特效图（project.audioFx，J.buildAudioFx 构建，与导出共用）
// → 主音量 gain → destination。特效图按需重建，主音量独立于特效。
const initialAP = {
  ctx: null as AudioContext | null,
  src: null as AudioBufferSourceNode | null,
  startAt: 0,
  rate: 1,
  gain: null as GainNode | null,
  vol: 0.8,
  muted: false,
  fx: null as { input: AudioNode; output: AudioNode; channels: number } | null,
  fxKey: '',
  play(buffer: AudioBuffer, offset: number, rate = 1) {
    if (!this.ctx) this.ctx = new (window.AudioContext || (window as any).webkitAudioContext)();
    if (this.ctx.state === 'suspended') this.ctx.resume();
    this.stop();
    if (!this.gain) {
      this.gain = this.ctx.createGain();
      this.gain.connect(this.ctx.destination);
      // 初值直接写死：gain 默认是 1，若靠 setTargetAtTime 滑到目标，
      // 静音下首次播放会以全音量漏出几十毫秒（听感就是「卡出一声」）
      this.gain.gain.value = this.muted ? 0 : this.vol * this.vol;
    }
    // 特效图重建：曲线包络的自动化按播放起点铺排，其余预设仅在配置变化时重建
    this.rebuildFx(buffer, offset);
    const s = this.ctx.createBufferSource();
    s.buffer = buffer;
    s.connect(this.fx ? this.fx.input : this.gain);
    const off = Math.max(0, Math.min(offset, buffer.duration - 0.01));
    this.rate = rate > 0 ? rate : 1;
    s.playbackRate.value = this.rate;
    s.start(0, off);
    this.src = s;
    this.startAt = this.ctx.currentTime - off;
  },
  // 按当前 project.audioFx 重建特效图；曲线包络每次起点都重铺自动化
  rebuildFx(buffer: AudioBuffer, offset: number) {
    const ctx = this.ctx!;
    const cfg = (S.project && S.project.audioFx) || null;
    const want = cfg && cfg.preset && cfg.preset !== 'none';
    const key = want ? JSON.stringify(cfg) : '';
    if (!want) {
      if (this.fx) {
        try { this.fx.output.disconnect(); } catch { /* 已断开 */ }
        this.fx = null;
      }
      this.fxKey = '';
      return;
    }
    if (this.fx && this.fxKey === key) return;
    if (this.fx) {
      try { this.fx.output.disconnect(); } catch { /* 已断开 */ }
    }
    const f = J.buildAudioFx(ctx, cfg, {
      songDur: buffer.duration, fromT: offset, toT: buffer.duration, anchorT: ctx.currentTime,
    });
    if (f) f.output.connect(this.gain!);
    this.fx = f;
    this.fxKey = key;
  },
  stop() {
    if (this.src) {
      try {
        this.src.stop();
      } catch {
      }
      try {
        this.src.disconnect();
      } catch {
      }
      this.src = null;
    }
  },
  time() {
    return this.ctx && this.src ? (this.ctx.currentTime - this.startAt) * this.rate : 0;
  },
  // 仅预览音量，导出保持原始电平
  setVol(v: number | null, muted: boolean | null) {
    if (v != null) this.vol = Math.max(0, Math.min(1, v));
    if (muted != null) this.muted = !!muted;
    this.applyVol();
  },
    applyVol() {
        if (!this.gain || !this.ctx) return;
        const v = this.muted ? 0 : this.vol * this.vol, g = this.gain.gain;
        try {
            // 归零必须立即到位（消音不允许过渡）；升音量才走 15ms 缓入防爆音
            if (v === 0) g.setValueAtTime(0, this.ctx.currentTime);
            else g.setTargetAtTime(v, this.ctx.currentTime, 0.015);
        } catch {
            g.value = v;
        }
    },
};
export type AudioPlayer = typeof initialAP;
export const AP: AudioPlayer = g.__jl_AP || (g.__jl_AP = initialAP);

// 音频特效配置变化时调用：播放中立即重建特效图并重连当前源，
// 未播放则只作废缓存（下次 play 生效）。导出侧无需通知（导出现读现建）。
export function applyAudioFx() {
  if (!AP.ctx || !S.audio) return;
  if (S.playing) {
    AP.rebuildFx(S.audio.buffer, S.t);
    if (AP.src) {
      try {
        AP.src.disconnect();
      } catch { /* 已断开 */ }
      AP.src.connect(AP.fx ? AP.fx.input : AP.gain!);
    }
  } else {
    AP.fxKey = '';
  }
}

// 预览音量记忆
export function initVolume() {
  let v = 0.8, m = false;
  try {
    const o = JSON.parse(localStorage.getItem('jizura.previewVolume') || 'null');
    if (o) {
      v = +o.v;
      m = !!o.m;
    }
  } catch {
  }
  if (!(v >= 0 && v <= 1)) v = 0.8;
  AP.setVol(v, m);
}

export function saveVolume() {
  try {
    localStorage.setItem('jizura.previewVolume', JSON.stringify({v: AP.vol, m: AP.muted}));
  } catch {
  }
}

// 循环范围：切分循环 = 循环当前这一份切分；行循环 = 循环当前这一行（首切分到末切分）。
// 两者语义不同，导出范围（选段）不干预循环范围。
export function cutAround(t: number) {
  const c = J.cutAt(S.plan, t);
  if (c) return c;
  const cs = S.plan.cuts;
  let ans = null;
  for (let i = 0; i < cs.length; i++) {
    if (cs[i].start <= t) ans = cs[i]; else break;
  }
  return ans;
}

export function loopRange(t?: number) {
  const T = t != null ? t : S.t;
  const endAll = S.plan.duration;
  if (S.loop === 'cut') {
    const cut = cutAround(T);
    return cut ? {start: cut.start, end: cut.end} : {start: 0, end: endAll};
  }
  if (S.loop === 'line') {
    const cut = cutAround(T);
    if (!cut || cut.line < 0) return cut ? {start: cut.start, end: cut.end} : {start: 0, end: endAll};
    const same = S.plan.cuts.filter((c: any) => c.line === cut.line && c.layout !== 'interlude' && c.layout !== 'title');
    if (!same.length) return {start: cut.start, end: cut.end};
    return {start: same[0].start, end: same[same.length - 1].end};
  }
  return {start: 0, end: endAll};
}

export function refreshLoopHold(t?: number) {
  S.loopHold = (S.loop === 'line' || S.loop === 'cut') ? loopRange(t != null ? t : S.t) : null;
}

export function activeLoopRange() {
  if ((S.loop === 'line' || S.loop === 'cut') && S.loopHold) return S.loopHold;
  return loopRange(S.t);
}
