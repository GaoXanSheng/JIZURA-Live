/* 渲染进程（/render 纯 canvas 页）的 pinia store。
   工程与时间全部经 BroadcastChannel 从编辑页直通进来（内存消息，不走 localStorage）；
   本页只负责 replan 与画帧，同时也暴露 J.ui 兼容影子供 Spout 注入脚本读写
   （SpoutPipeline.cs 的 __jlApplySync / pump 直接操作该对象）。 */
import {defineStore} from 'pinia';
import {J} from '../engine';
import {mergeProject} from '../jizura/project';
import type {StageSink} from '../jizura/stage-bridge';
import type {SubConfig} from '../cloud/subtitle';

export const useStage = defineStore('stage', {
  state: () => ({
    /* —— 供 Spout 注入脚本读写的兼容影子（mirror 脚本读写 S.t / S.need / S.plan）—— */
    t: 0,
    playing: false,
    need: true,
    project: null as any,
    plan: null as any,

    /* —— 渲染页自身状态 —— */
    connected: false,
    lastPush: 0,
    subCfg: null as SubConfig | null,
    subMap: null as Map<string, { roma: string; trans: string }> | null,
    audioBeats: [] as number[],
    audioDuration: 0,
  }),
  actions: {
    replan() {
      if (!this.project) return;
      const like = this.audioBeats.length
        ? { beats: this.audioBeats }
        : (this.project.timing?.bpm > 0
          ? { beats: J.beatGrid(this.project.timing.bpm, this.project.timing.beatOffset || 0, this.audioDuration || 600) }
          : null);
      this.plan = J.plan(this.project, like);
      this.need = true;
    },
    sink(): StageSink {
      const applyMap = (map: [string, { roma: string; trans: string }][] | null) => {
        this.subMap = map && map.length ? new Map(map) : null;
      };
      return {
        setState: (project, beats, duration, sub, map) => {
          this.project = mergeProject(project);
          this.audioBeats = beats || [];
          this.audioDuration = duration || 0;
          this.subCfg = sub || null;
          applyMap(map);
          this.replan();
          this.connected = true;
        },
        setProject: (project, beats, duration, map) => {
          this.project = mergeProject(project);
          this.audioBeats = beats || [];
          this.audioDuration = duration || 0;
          applyMap(map);
          this.replan();
        },
        setTime: (t, playing) => {
          // 编辑器任何重绘都会带一条 time 推送（暂停态同值也推）：
          // 锚点没动、播放态没切换就不置 need，避免静止画面空转重绘
          const changed = playing !== this.playing || t !== this.t;
          this.t = t;
          this.playing = playing;
          this.lastPush = performance.now();
          if (changed || playing) this.need = true;
        },
        setSub: (sub) => {
          this.subCfg = sub || null;
          this.need = true;
        },
        isConnected: () => this.connected,
      };
    },
  },
});
