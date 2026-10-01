/* 引擎运行时状态（12_ui.js 的 S + AP，原样移植）。
   刻意保持非响应式：S.t 每帧变化，走 bus 通知，不进 Vue 响应式系统。 */
import {J} from '../engine';

export type LoopMode = 'all' | 'line' | 'cut' | false;

export const S = {
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
  tap: null as { i: number; from: number; done: { i: number; had: number | null }[] } | null,
  slow: false,
  curLine: -2,
  scrubbing: false,
};

export const LOOP_CYCLE: LoopMode[] = ['all', 'line', 'cut', false];

/* WebAudio 播放器（沙箱页面里 blob media 可能被禁，AudioContext 稳妥） */
export const AP = {
  ctx: null as AudioContext | null,
  src: null as AudioBufferSourceNode | null,
  startAt: 0,
  gain: null as GainNode | null,
  vol: 0.8,
  muted: false,
  play(buffer: AudioBuffer, offset: number) {
    if (!this.ctx) this.ctx = new (window.AudioContext || (window as any).webkitAudioContext)();
    if (this.ctx.state === 'suspended') this.ctx.resume();
    this.stop();
      if (!this.gain) {
          this.gain = this.ctx.createGain();
          this.gain.connect(this.ctx.destination);
          // 初值直接写死：gain 节点默认是 1，若靠 setTargetAtTime 滑到目标，
          // 静音状态下首次播放会以全音量漏出几十毫秒（听感就是「卡出一声」）
          this.gain.gain.value = this.muted ? 0 : this.vol * this.vol;
      }
    const s = this.ctx.createBufferSource(); s.buffer = buffer; s.connect(this.gain);
    const off = Math.max(0, Math.min(offset, buffer.duration - 0.01));
    s.start(0, off); this.src = s; this.startAt = this.ctx.currentTime - off;
  },
  stop() { if (this.src) { try { this.src.stop(); } catch {} try { this.src.disconnect(); } catch {} this.src = null; } },
  time() { return this.ctx ? this.ctx.currentTime - this.startAt : 0; },
  /* 仅预览音量（导出保持原始电平） */
  setVol(v: number | null, muted: boolean | null) { if (v != null) this.vol = Math.max(0, Math.min(1, v)); if (muted != null) this.muted = !!muted; this.applyVol(); },
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

/* 预览音量记忆 */
export function initVolume() {
  let v = 0.8, m = false;
  try { const o = JSON.parse(localStorage.getItem('jizura.previewVolume') || 'null'); if (o) { v = +o.v; m = !!o.m; } } catch {}
  if (!(v >= 0 && v <= 1)) v = 0.8;
  AP.setVol(v, m);
}
export function saveVolume() {
  try { localStorage.setItem('jizura.previewVolume', JSON.stringify({ v: AP.vol, m: AP.muted })); } catch {}
}

/* ---- 循环范围 ----
   切分循环 = 循环当前这一份切分；行循环 = 循环当前这一行（首切分到末切分）。
   两者语义不同，导出范围（选段）不干预循环范围 */
export function cutAround(t: number) {
  const c = J.cutAt(S.plan, t);
  if (c) return c;
  const cs = S.plan.cuts;
  let ans = null;
  for (let i = 0; i < cs.length; i++) { if (cs[i].start <= t) ans = cs[i]; else break; }
  return ans;
}

export function loopRange(t?: number) {
  const T = t != null ? t : S.t;
  const endAll = S.plan.duration;
  if (S.loop === 'cut') {
    const cut = cutAround(T);
    return cut ? { start: cut.start, end: cut.end } : { start: 0, end: endAll };
  }
  if (S.loop === 'line') {
    const cut = cutAround(T);
    if (!cut || cut.line < 0) return cut ? { start: cut.start, end: cut.end } : { start: 0, end: endAll };
    const same = S.plan.cuts.filter((c: any) => c.line === cut.line && c.layout !== 'interlude' && c.layout !== 'title');
    if (!same.length) return { start: cut.start, end: cut.end };
    return { start: same[0].start, end: same[same.length - 1].end };
  }
  return { start: 0, end: endAll };
}

export function refreshLoopHold(t?: number) {
  S.loopHold = (S.loop === 'line' || S.loop === 'cut') ? loopRange(t != null ? t : S.t) : null;
}

export function activeLoopRange() {
  if ((S.loop === 'line' || S.loop === 'cut') && S.loopHold) return S.loopHold;
  return loopRange(S.t);
}
