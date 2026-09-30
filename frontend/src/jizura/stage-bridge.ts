/* BroadcastChannel 直通桥：编辑页 ↔ 渲染页（/render）同源直连。
   工程变化/字幕/歌词映射为低频消息；播放时间为每帧高频消息——
   全部走内存消息通道，渲染进程不读写 localStorage，不增加落盘能耗。
   Spout 渲染进程另有一条 C# __jlApplySync 锚点通道（SpoutPipeline 注入），
   两者时间源相同（都来自编辑页时钟），可并存。 */
import {S} from './state';
import {bus} from './bus';
import {currentLyric} from '../cloud/lyricStore';
import {subConfig} from '../cloud/subtitle';

const CHANNEL = 'jizura-live';

type Msg =
  | { type: 'hello' }
  | { type: 'state'; project: any; beats: number[]; duration: number; sub: any; map: [string, { roma: string; trans: string }][] | null }
  | { type: 'project'; project: any; beats: number[]; duration: number; map: [string, { roma: string; trans: string }][] | null }
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

function projectMsg(type: 'state' | 'project'): Msg {
  return {
    type,
    project: S.project,
    beats: beatsOf(),
    duration: S.audio?.duration || 0,
    map: payloadMap(),
    ...(type === 'state' ? { sub: subConfig() } : {}),
  } as Msg;
}

/* ---- 编辑端：广播本页状态 ---- */
export function bindEditorBroadcast() {
  if (ch) return;
  try { ch = new BroadcastChannel(CHANNEL); } catch { return; }

  ch.onmessage = (e: MessageEvent<Msg>) => {
    if (e.data.type === 'hello') {
      try { ch!.postMessage(projectMsg('state')); } catch {}
    }
  };

  // 工程变化：replan 后推全量（低频）
  bus.on('replan', () => { try { ch!.postMessage(projectMsg('project')); } catch {} });
  // 播放时间：每帧（高频、纯内存）——镜像完整时钟同步需要逐帧时间
  bus.on('frame', () => { try { ch!.postMessage({ type: 'time', t: S.t, playing: S.playing } as Msg); } catch {} });
  // 字幕配置：变化时推
  bus.on('subcfg', () => {
    const json = JSON.stringify(subConfig());
    if (json === lastSubJson) return;
    lastSubJson = json;
    try { ch!.postMessage({ type: 'sub', sub: subConfig() } as Msg); } catch {}
  });
}

/* ---- 渲染端：接收并落到 pinia store（由 stores/stage.ts 组装） ---- */
export interface StageSink {
  setState(project: any, beats: number[], duration: number, sub: any, map: [string, { roma: string; trans: string }][] | null): void;
  setProject(project: any, beats: number[], duration: number, map: [string, { roma: string; trans: string }][] | null): void;
  setTime(t: number, playing: boolean): void;
  setSub(sub: any): void;
  isConnected(): boolean;
}

export function bindStageBroadcast(sink: StageSink): { stop: () => void; resendHello: () => void } {
  let c: BroadcastChannel | null = null;
  try { c = new BroadcastChannel(CHANNEL); } catch { return { stop: () => {}, resendHello: () => {} }; }
  c.onmessage = (e: MessageEvent<Msg>) => {
    const m = e.data;
    if (m.type === 'state') sink.setState(m.project, m.beats, m.duration, m.sub, m.map);
    else if (m.type === 'project') sink.setProject(m.project, m.beats, m.duration, m.map);
    else if (m.type === 'time') sink.setTime(m.t, m.playing);
    else if (m.type === 'sub') sink.setSub(m.sub);
  };
  const hello = () => { try { c!.postMessage({ type: 'hello' } as Msg); } catch {} };
  hello();
  // hello 是握手竞态敏感的：渲染页可能先于编辑页 channel 就绪时发出，
  // 周期性重发直到连上（connected 由调用方判断）
  const retry = window.setInterval(() => { if (!sink.isConnected()) hello(); }, 1500);
  return {
    stop: () => { window.clearInterval(retry); try { c!.close(); } catch {} },
    resendHello: hello,
  };
}
