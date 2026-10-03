// /render 渲染页的 store：工程与时间经 BroadcastChannel 从编辑页直通进来（内存消息，不走 localStorage）
// 本页只做 replan 和画帧；J.ui 兼容影子供 Spout 注入脚本读写（SpoutPipeline.cs 的 __jlApplySync / pump 直接操作它）
import {defineStore} from 'pinia';
import {markRaw} from 'vue';
import {J} from '../engine';
import {mergeProject} from '../jizura/project';
import type {AnalysisPayload, StageSink} from '../jizura/stage-bridge';
import type {SubConfig} from '../cloud/subtitle';

// plan/project/字幕映射/音频分析都是整存整取的大对象图：只换引用、从不原地改。
// 它们绝不能进 Pinia 的深度响应式——devtools 模式下 pinia 给 store 挂了一个
// flush:'sync' 的深度订阅，任何 state 写入都会同步 traverse 整棵 state，
// plan 图实测 ~3.2 万属性、一次写入 5ms+，镜像泵每帧写两次 t/need 直接被打满。
// markRaw 后 traverse 首行按 __v_skip 整棵剪枝，写入回到微秒级（生产构建无此税，
// 但 dev 是日常调优环境，必须治）
export const useStage = defineStore('stage', {
    state: () => ({
        // 供 Spout 注入脚本读写的兼容影子（mirror 脚本读写 S.t / S.need / S.plan）
        t: 0,
        playing: false,
        need: true,
        project: null as any,
        plan: null as any,

        connected: false,
        lastPush: 0,
        subCfg: null as SubConfig | null,
        subMap: null as Map<string, { roma: string; trans: string }> | null,
        audioBeats: [] as number[],
        audioDuration: 0,
        audioAnalysis: null as AnalysisPayload | null,
        // 镜像泵独占播放时钟时由 RenderPage 注入：推送改走再锚定（只进不退），
        // 不然双通道推送和泵外推互相覆盖，画面会锯齿闪烁
        extClock: null as ((t: number, playing: boolean) => void) | null,
    }),
    actions: {
        replan() {
            if (!this.project) return;
            const a = this.audioAnalysis;
            const like = {
                ...(this.audioBeats.length ? {beats: this.audioBeats} : (this.project.timing?.bpm > 0
                    ? {beats: J.beatGrid(this.project.timing.bpm, this.project.timing.beatOffset || 0, this.audioDuration || 600)}
                    : {})),
                // 音频分析数组，K线这类音频驱动风格靠它出画面（没有就是 NO AUDIO DATA）
                ...(a ? {
                    energy: a.energy || null,
                    energyRate: a.energyRate || 0,
                    pitch: a.pitch || null,
                    pitchRate: a.pitchRate || 0
                } : {}),
            };
            this.plan = markRaw(J.plan(this.project, like));
            this.need = true;
        },
        sink(): StageSink {
            const applyMap = (map: [string, { roma: string; trans: string }][] | null) => {
                this.subMap = map && map.length ? markRaw(new Map(map)) : null;
            };
            return {
                setState: (project, beats, duration, sub, map, analysis) => {
                    this.project = markRaw(mergeProject(project));
                    this.audioBeats = markRaw(beats || []);
                    this.audioDuration = duration || 0;
                    this.audioAnalysis = analysis ? markRaw(analysis) : null;
                    this.subCfg = sub ? markRaw(sub) : null;
                    applyMap(map);
                    this.replan();
                    this.connected = true;
                },
                setProject: (project, beats, duration, map, analysis) => {
                    this.project = markRaw(mergeProject(project));
                    this.audioBeats = markRaw(beats || []);
                    this.audioDuration = duration || 0;
                    if (analysis) this.audioAnalysis = markRaw(analysis);
                    applyMap(map);
                    this.replan();
                },
                setTime: (t, playing) => {
                    // 泵主导时钟时推送只做再锚定，不直接写 t
                    if (this.extClock) {
                        this.extClock(t, playing);
                        return;
                    }
                    // 编辑器每次重绘都会带一条 time 推送（暂停时同值也推）
                    // 锚点和播放态都没变就不置 need，免得静止画面空转重绘
                    const changed = playing !== this.playing || t !== this.t;
                    this.t = t;
                    this.playing = playing;
                    this.lastPush = performance.now();
                    if (changed || playing) this.need = true;
                },
                setSub: (sub) => {
                    this.subCfg = sub ? markRaw(sub) : null;
                    this.need = true;
                },
                isConnected: () => this.connected,
            };
        },
    },
});
