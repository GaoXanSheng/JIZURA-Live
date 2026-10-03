import {J} from '../engine';
import {S} from '../jizura/state';
import {bus} from '../jizura/bus';
import {seek} from '../jizura/engine';
import {classifyLangRows, type LyricMap} from './krc';
import {currentLyric} from './lyricStore';
import {drawSubtitleAt, type SubDrawCfg, type SubPayload} from './subtitle-draw';

export interface SubConfig {
  content: 'off' | 'trans' | 'roma' | 'both';
  pos: 'bottom' | 'top';
  size: number;
  stroke: number;
  color: string;
  strokeColor: string;
  font: '' | 'sans' | 'serif';
  bar: boolean;
  offX: number;
  offY: number;
}

export const SUB_DEFAULT: SubConfig = {
  content: 'off', pos: 'bottom', size: 4.5, stroke: 4,
  color: '#ffffff', strokeColor: '#000000', font: '', bar: false,
  offX: 0, offY: 0,
};

let cfg: SubConfig = loadCfg();

// /render 渲染进程不读写 localStorage，配置和映射经 BroadcastChannel 注入
// 有注入值时优先于本页 localStorage
let overrideCfg: SubConfig | null = null;
let overrideMap: LyricMap | null = null;

export function setSubOverride(o: SubConfig | null, map: LyricMap | null) {
  overrideCfg = o;
  overrideMap = map;
}

function loadCfg(): SubConfig {
  try {
    return Object.assign({}, SUB_DEFAULT, JSON.parse(localStorage.getItem('jizura.kgsub') || '{}'));
  } catch {
    return Object.assign({}, SUB_DEFAULT);
  }
}

export function subConfig(): SubConfig {
  return {...cfg};
}

export function saveSubConfig(p: Partial<SubConfig>) {
  cfg = {...cfg, ...p};
  try {
    localStorage.setItem('jizura.kgsub', JSON.stringify(cfg));
  } catch {
  }
}

// 镜像窗口跟随主窗口的设置变化
window.addEventListener('storage', e => {
  if (e.key === 'jizura.kgsub') {
    cfg = loadCfg();
    bus.emit('subcfg');
  }
});

// 暂停时引擎不会自己重绘，按当前时间原地 seek 强制出一帧
export function redrawSub() {
  try {
    seek(S.t);
  } catch { // 方案还没加载，无事可做
  }
}

function fromNoteOf(line: any): { roma: string; trans: string } {
  const note = String(line.note || '');
  if (!note) return {roma: '', trans: ''};
  const roma: string[] = [], trans: string[] = [];
  for (const p of note.split('　').map((s: string) => s.trim()).filter(Boolean)) {
    (classifyLangRows([p]) === 'roma' ? roma : trans).push(p);
  }
  return {roma: roma.join(' '), trans: trans.join(' ')};
}

function subTextFor(line: any): string {
  if (!line || line.interlude) return '';
  const fromNote = () => fromNoteOf(line);
  if (overrideCfg) {
    // 映射是广播注入的，没有就回退行内注音，跟主画面行为一致
    const m = overrideMap && overrideMap.get(line.text);
    const parts = m || fromNote();
    if (overrideCfg.content === 'roma') return parts.roma || '';
    if (overrideCfg.content === 'trans') return parts.trans || '';
    return [parts.roma, parts.trans].filter(Boolean).join('　');
  }
  const map = currentLyric() && currentLyric()!.map.get(line.text);
  const parts = map || fromNote();
  if (cfg.content === 'roma') return parts.roma || '';
  if (cfg.content === 'trans') return parts.trans || '';
  return [parts.roma, parts.trans].filter(Boolean).join('　');
}

// 导出烤入字幕的载荷：content=off 时返回 null。文本按本页语义（映射→注音→内容模式）
// 逐行预解析，导出线程（含 worker）只吃现成文本，保证与预览完全一致
export function subPayloadForPlan(plan: any): SubPayload | null {
  const c = effCfg();
  if (c.content === 'off' || !plan || !Array.isArray(plan.lines)) return null;
  return {cfg: {...c} as SubDrawCfg, texts: plan.lines.map((l: any) => subTextFor(l))};
}

function effCfg(): SubConfig {
  return overrideCfg || cfg;
}

// 排版核心在 subtitle-draw.ts（与导出烤入共用）；这里只提供本页的文本解析
function drawSubtitle(ctx: CanvasRenderingContext2D, plan: any, t: number, opt: any) {
  const c = effCfg();
  if (c.content === 'off' || opt.noTrans || opt.layer) return;
  drawSubtitleAt(ctx, plan, t, opt, c, line => subTextFor(line));
}

// /render?layer=lyric 的独立字幕通道只画字幕，不渲染歌词动画
export function drawSubtitleFrame(ctx: CanvasRenderingContext2D, plan: any, t: number, opt: any = {}) {
  drawSubtitle(ctx, plan, t, opt);
}

// subwebm 字幕层导出和 Spout 独立字幕通道都走这个入口
// subtitleCfg 给导出前校验用：content 是 off 的话导出只会得到空视频
J.subtitleFrame = drawSubtitleFrame;
J.subtitleCfg = () => ({...effCfg()});

// 正式帧画完再叠加；重复挂载不再包一层
export function installSubtitleOverlay() {
  const proto = J.Renderer.prototype as any;
  if ((proto as any).__jl_sub_patched) {
    return;
  }
  const origFrame = proto.frame;
  proto.frame = function (this: any, ctx: CanvasRenderingContext2D, plan: any, t: number, opt: any = {}) {
    origFrame.call(this, ctx, plan, t, opt);
    // 字幕分离：主通道页被 C# 注入 __jl_no_sub 时不叠加，字幕只走独立通道
    if (window.__jl_no_sub) return;
    if (effCfg().content !== 'off') {
      try {
        drawSubtitle(ctx, plan, t, opt);
      } catch { // 字幕出错不能拖垮渲染
      }
    }
  };
  proto.__jl_sub_patched = true;
}
