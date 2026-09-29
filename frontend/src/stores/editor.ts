/* 编辑器响应式镜像：引擎运行时(S)是普通对象，这里存放 UI 需要的响应式投影。
   组件读这里，动作走 jizura/* 模块；高频帧数据由组件按需订阅 bus。 */
import { defineStore } from 'pinia';
import { S } from '../jizura/state';
import { J } from '../engine';

export const useEditor = defineStore('editor', {
  state: () => ({
    booted: false,
    playing: false,
    /* 播放时间（每帧更新，只有依赖它的组件重渲染） */
    t: 0,
    duration: 0,
    curLine: -2,
    planVersion: 0,          // 每次 replan 自增，行列表/时间轴据此重建
    audioName: '未载入歌曲（载入后自动检测节拍并对齐切分）',
    audioBpm: 0,
    hasAudio: false,
    linesCount: 0,
    cutsCount: 0,
    loopMode: 'all' as 'all' | 'line' | 'cut' | 'off',
    histPos: '',
    canUndo: false,
    canRedo: false,
    canHistBack: false,
    canHistFwd: false,
    vol: 80,
    muted: false,
  }),
  getters: {
    project: () => S.project,
    plan: () => S.plan,
    aspect: (s) => (S.project ? S.project.aspect : '16:9'),
  },
  actions: {
    syncPlayState() {
      this.playing = S.playing;
      this.loopMode = S.loop || 'off';
    },
    syncTime() {
      this.t = S.t;
      this.duration = S.plan ? S.plan.duration : 0;
    },
    syncPlan() {
      this.planVersion++;
      this.duration = S.plan ? S.plan.duration : 0;
      this.linesCount = S.plan ? S.plan.lines.length : 0;
      this.cutsCount = S.plan ? S.plan.cuts.length : 0;
      this.hasAudio = !!S.audio;
      this.audioBpm = S.audio ? S.audio.bpm : 0;
    },
    syncAll() {
      this.syncPlayState();
      this.syncPlan();
      this.syncTime();
    },
  },
});

export const jizura = () => (globalThis as any).J as typeof J;
