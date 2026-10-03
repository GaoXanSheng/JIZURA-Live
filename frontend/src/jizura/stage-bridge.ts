// BroadcastChannel 直通桥：编辑页 ↔ 渲染页（/render）同源直连。
// 工程变化/字幕/歌词映射是低频消息，播放时间是逐帧高频消息，
// 全部走内存消息通道，渲染进程不读写 localStorage，不增加落盘能耗。
// Spout 渲染进程另有 C# __jlApplySync 锚点通道（SpoutPipeline 注入），
// 两者时间源相同（都来自编辑页时钟），可并存。
import {S} from './state';
import {bus} from './bus';
import {currentLyric} from '../cloud/lyricStore';
import {subConfig} from '../cloud/subtitle';

const CHANNEL = 'jizura-live';

type Msg =
    | { type: 'hello' }
    | {
    type: 'state';
    project: any;
    beats: number[];
    duration: number;
    sub: any;
    map: [string, { roma: string; trans: string }][] | null;
    energy: Float32Array | null;
    energyRate: number;
    pitch: Float32Array | null;
    pitchRate: number
}
    | {
    type: 'project';
    project: any;
    beats: number[];
    duration: number;
    map: [string, { roma: string; trans: string }][] | null;
    energy: Float32Array | null;
    energyRate: number;
    pitch: Float32Array | null;
    pitchRate: number
}
    | { type: 'time'; t: number; playing: boolean }
    | { type: 'sub'; sub: any };

let ch: BroadcastChannel | null = null;
let lastMapJson = '';
let lastSubJson = '';

function beatsOf(): number[] {
    // 无音频时返回空：渲染页会按 timing.bpm 自建节拍网格
    return S.audio ? (S.audio.beats || []) : [];
}

function payloadMap() {
    const map = currentLyric()?.map;
    if (!map || !map.size) return null;
    return [...map];
}

// 音频分析数组随消息走结构化克隆（Float32Array 原生支持）。
// 渲染页的 plan 一直缺 energy/pitch，K线这类音频驱动风格在渲染端会全是无数据画面
function analysisOf() {
    return {
        energy: S.audio ? (S.audio.energy || null) : null,
        energyRate: S.audio ? (S.audio.energyRate || 0) : 0,
        pitch: S.audio ? (S.audio.pitch || null) : null,
        pitchRate: S.audio ? (S.audio.pitchRate || 0) : 0,
    };
}

function projectMsg(type: 'state' | 'project'): Msg {
    return {
        type,
        project: S.project,
        beats: beatsOf(),
        duration: S.audio?.duration || 0,
        map: payloadMap(),
        ...analysisOf(),
        ...(type === 'state' ? {sub: subConfig()} : {}),
    } as Msg;
}

// 镜像宿主进程里 BC 不可达，有 CefSharp 上行时把消息镜像给宿主（宿主转镜像页 __jlApplyMsg）。
// 标记键不能 __ 开头（CefSharp 转换会丢）；energy/pitch 转 Array——JSON 出来的 Float32Array
// 是对象，镜像端 .length 全断
function hostPost(msg: Msg): void {
    try {
        const w = window as any;
        if (w.CefSharp && w.CefSharp.PostMessage)
            w.CefSharp.PostMessage({jlHostRelay: 1, ...msg});
    } catch {
    }
}

function hostSafe(msg: Msg): Msg {
    if (msg.type !== 'state' && msg.type !== 'project')
        return msg;
    const m = msg as any;
    return {
        ...m,
        energy: m.energy ? Array.from(m.energy) : null,
        pitch: m.pitch ? Array.from(m.pitch) : null,
    } as Msg;
}

// 编辑端：广播本页状态
export function bindEditorBroadcast() {
    if (ch) return;
    try {
        ch = new BroadcastChannel(CHANNEL);
    } catch {
        return;
    }

    ch.onmessage = (e: MessageEvent<Msg>) => {
        if (e.data.type === 'hello') {
            try {
                const m = projectMsg('state');
                ch!.postMessage(m);
                hostPost(hostSafe(m));
            } catch {
            }
        }
    };

    // 工程变化：replan 后推全量（低频）
    bus.on('replan', () => {
        try {
            const m = projectMsg('project');
            ch!.postMessage(m);
            hostPost(hostSafe(m));
        } catch {
        }
    });
    // 播放时间推镜像：30Hz 节流对齐 C# 锚点（镜像泵外推，逐帧推白烧镜像主线程）。
    // 暂停且 t/playing 没变不推；playing 翻转和暂停期 seek 不节流
    let lastTimePushAt = 0;
    let pushedT = -1;
    let pushedPlaying: boolean | null = null;
    bus.on('frame', () => {
        const t = S.t;
        const playing = S.playing;
        const playingChanged = playing !== pushedPlaying;
        if (!playing && !playingChanged && t === pushedT) return;
        if (!playingChanged && playing && performance.now() - lastTimePushAt < 33) return;
        lastTimePushAt = performance.now();
        pushedT = t;
        pushedPlaying = playing;
        try {
            ch!.postMessage({type: 'time', t, playing} as Msg);
        } catch {
        }
    });
    // 字幕配置：变化时推
    bus.on('subcfg', () => {
        const json = JSON.stringify(subConfig());
        if (json === lastSubJson) return;
        lastSubJson = json;
        try {
            const m: Msg = {type: 'sub', sub: subConfig()};
            ch!.postMessage(m);
            hostPost(m);
        } catch {
        }
    });

    // 宿主中继补发戳：镜像页(重)加载后由宿主经 C# 调用，重推当前全量 state。
    // 与 __jlForceSync（时间补发）同构，见 MainWindow 的 FrameLoadEnd 注入
    (window as any).__jlHostResend = () => {
        try {
            hostPost(hostSafe(projectMsg('state')));
        } catch {
        }
    };
}

// 渲染端：接收并落到 pinia store（stores/stage.ts 组装）
export interface AnalysisPayload {
    energy: Float32Array | null;
    energyRate: number;
    pitch: Float32Array | null;
    pitchRate: number
}

export interface StageSink {
    setState(project: any, beats: number[], duration: number, sub: any, map: [string, {
        roma: string;
        trans: string
    }][] | null, analysis?: AnalysisPayload): void;

    setProject(project: any, beats: number[], duration: number, map: [string, {
        roma: string;
        trans: string
    }][] | null, analysis?: AnalysisPayload): void;

    setTime(t: number, playing: boolean): void;

    setSub(sub: any): void;

    isConnected(): boolean;
}

export function bindStageBroadcast(sink: StageSink): {
    stop: () => void;
    resendHello: () => void;
    dispatch: (m: Msg) => void;
} {
    let c: BroadcastChannel | null = null;
    try {
        c = new BroadcastChannel(CHANNEL);
    } catch {
        return {
            stop: () => {
            }, resendHello: () => {
            }, dispatch: () => {
            }
        };
    }
    // 分发器单独提出：宿主模式下 C# 经 window.__jlApplyMsg 注入同一协议消息
    const dispatch = (m: Msg) => {
        if (m.type === 'state') sink.setState(m.project, m.beats, m.duration, m.sub, m.map, {
            energy: m.energy,
            energyRate: m.energyRate,
            pitch: m.pitch,
            pitchRate: m.pitchRate
        });
        else if (m.type === 'project') sink.setProject(m.project, m.beats, m.duration, m.map, {
            energy: m.energy,
            energyRate: m.energyRate,
            pitch: m.pitch,
            pitchRate: m.pitchRate
        });
        else if (m.type === 'time') sink.setTime(m.t, m.playing);
        else if (m.type === 'sub') sink.setSub(m.sub);
    };
    c.onmessage = (e: MessageEvent<Msg>) => dispatch(e.data);
    const hello = () => {
        try {
            c!.postMessage({type: 'hello'} as Msg);
        } catch {
        }
    };
    hello();
    // hello 对握手竞态敏感：渲染页可能先于编辑页 channel 就绪发出，
    // 周期性重发直到连上（connected 由调用方判断）
    const retry = window.setInterval(() => {
        if (!sink.isConnected()) hello();
    }, 1500);
    return {
        stop: () => {
            window.clearInterval(retry);
            try {
                c!.close();
            } catch {
            }
        },
        resendHello: hello,
        dispatch,
    };
}
