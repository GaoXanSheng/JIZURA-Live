import {J} from '../engine';
import {S} from '../jizura/state';
import {bus} from '../jizura/bus';
import {seek} from '../jizura/engine';
import {classifyLangRows, type LyricMap} from './krc';
import {currentLyric} from './lyricStore';

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

function subFont(px: number): string {
  if (cfg.font === 'serif') return `700 ${px}px "Noto Serif JP", SimSun, serif`;
  if (cfg.font === 'sans') return `700 ${px}px "Microsoft YaHei", "Noto Sans JP", sans-serif`;
  return `700 ${px}px "Noto Sans JP", "Microsoft YaHei", sans-serif`;
}

function wrapSubtitle(ctx: CanvasRenderingContext2D, text: string, maxW: number): string[] {
  // 先按全角空格分段，音译/翻译各占一段，超宽的段再逐字符折行
  const out: string[] = [];
  for (const seg of text.split('　')) {
    if (!seg) continue;
    if (ctx.measureText(seg).width <= maxW) {
      out.push(seg);
      continue;
    }
    let cur = '';
    for (const ch of seg) {
      if (cur && ctx.measureText(cur + ch).width > maxW) {
        out.push(cur);
        cur = ch;
      } else cur += ch;
    }
    if (cur) out.push(cur);
  }
  return out;
}

// 逐字符 measureText 每帧做一遍太浪费，缓存折行结果
// 键里带字体串和 maxW，字体或画布尺寸一变自然失效
const wrapCache = new Map<string, string[]>();

function wrapSubtitleCached(ctx: CanvasRenderingContext2D, text: string, font: string, maxW: number): string[] {
  const key = font + '|' + maxW + '|' + text;
  let rows = wrapCache.get(key);
  if (rows) return rows;
  rows = wrapSubtitle(ctx, text, maxW);
  if (wrapCache.size >= 64) {
    const oldest = wrapCache.keys().next().value as string;
    if (oldest !== undefined) wrapCache.delete(oldest);
  }
  wrapCache.set(key, rows);
  return rows;
}

// plan.lines.find 是每帧全表扫；字幕时间连续，命中后下一帧多半还在同一行
// 换行时才退回全表找
let subLinePlan: any = null;
let subLineIdx = 0;

function lineAt(plan: any, t: number): any {
  const lines = plan.lines;
  const ok = (l: any) => l && t >= l.start && t < (l.visEnd != null ? l.visEnd : l.end);
  if (subLinePlan !== plan) {
    subLinePlan = plan;
    subLineIdx = 0;
  }
  if (ok(lines[subLineIdx])) return lines[subLineIdx];
  if (ok(lines[subLineIdx + 1])) return lines[++subLineIdx];
  const idx = lines.findIndex(ok);
  if (idx < 0) return null;
  subLineIdx = idx;
  return lines[idx];
}

function effCfg(): SubConfig {
  return overrideCfg || cfg;
}

function drawSubtitle(ctx: CanvasRenderingContext2D, plan: any, t: number, opt: any) {
  const cfg = effCfg();
  if (cfg.content === 'off' || opt.noTrans || opt.layer) return;
  if (!plan || !plan.lines || !plan.W || !plan.H) return;
  const line = lineAt(plan, t);
  const text = subTextFor(line);
  if (!text) return;

  const W = plan.W, H = plan.H;
  const scale = opt.scale || (ctx.canvas.width ? ctx.canvas.width / W : 1);
  const fontPx = Math.max(8, H * cfg.size / 100);
  const strokePx = H / 1080 * cfg.stroke;
  const maxW = W * 0.86;
  const lh = fontPx * 1.32;

  ctx.save();
  ctx.setTransform(scale, 0, 0, scale, 0, 0);
  const font = subFont(fontPx);
  ctx.font = font;
  ctx.textAlign = 'center';
  ctx.textBaseline = 'alphabetic';
  const rows = wrapSubtitleCached(ctx, text, font, maxW);
  const blockH = rows.length * lh;
  // offX/offY 是画面宽/高的百分比偏移，0 就是默认的底部/顶部居中
  const clampPct = (v: number) => Math.max(-50, Math.min(50, parseFloat(String(v)) || 0));
  const centerX = W * (0.5 + clampPct(cfg.offX) / 100);
  let baseY = cfg.pos === 'top' ? H * 0.055 + fontPx : H - H * 0.055 - blockH + fontPx;
  baseY += H * clampPct(cfg.offY) / 100;

  if (cfg.bar) {
    const wmax = Math.max(...rows.map(r => ctx.measureText(r).width));
    const padX = fontPx * 0.5, padY = fontPx * 0.3;
    ctx.fillStyle = 'rgba(0,0,0,0.45)';
    ctx.fillRect(centerX - wmax / 2 - padX, baseY - fontPx * 0.82 - padY,
        wmax + padX * 2, blockH + padY * 2);
  }
  ctx.lineWidth = strokePx;
  ctx.strokeStyle = cfg.strokeColor;
  ctx.lineJoin = 'round';
  ctx.fillStyle = cfg.color;
  rows.forEach((r, i) => {
    const y = baseY + i * lh;
    if (strokePx > 0) ctx.strokeText(r, centerX, y);
    ctx.fillText(r, centerX, y);
  });
  ctx.restore();
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
