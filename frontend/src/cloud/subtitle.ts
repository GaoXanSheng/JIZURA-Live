/* 字幕（渲染时叠加在画面上，预览与 MP4 导出都生效）
   数据：KRC/网易云载入时建的 音译/翻译 映射（jizura.kgsubmap，跨窗口镜像共享）；
         没有映射时退回行内注音（| 后的内容）按脚本分类。
   入口：包装 J.Renderer.prototype.frame —— 正式帧画完再叠加；
         转场/ morph 的离屏采样（noTrans）与分层导出（layer）不画。
   设置：localStorage jizura.kgsub；镜像窗口经 storage 事件实时跟随。 */
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

/* 渲染进程（/render 页）的字幕配置/映射经 BroadcastChannel 注入，
   优先于本页 localStorage（渲染进程不读写 localStorage）。 */
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
  } catch { /* 忽略 */
  }
}

/* 镜像窗口跟随主窗口的设置变化 */
window.addEventListener('storage', e => {
  if (e.key === 'jizura.kgsub') {
    cfg = loadCfg();
    bus.emit('subcfg');
  }
});

/* 暂停时引擎不主动重绘：按当前时间原地 seek 强制出一帧 */
export function redrawSub() {
  try {
    seek(S.t);
  } catch { /* 尚无方案时无需处理 */
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
    // 字幕通道：映射经广播注入；缺失时与主画面一致回退行内注音
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
  // 先按全角空格分段（音译/翻译各占一段），超宽的段再按字符折行
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

/* 折行结果缓存：字幕文本/字体/宽度不变时每帧逐字符 measureText 是纯浪费。
   键含字体串（含像素）与 maxW，字体或画布尺寸一变自然失效 */
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

/* 行查找缓存：plan.lines.find 是每帧全表线性扫；字幕时间连续，命中后
   下一帧大概率还在同一行（或紧邻下一行），只有换行才退回全表扫 */
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
  // 自定义位置：offX/offY 为画面宽/高的百分比偏移（默认 0 = 原生底部/顶部居中）
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

/* 独立字幕通道绘制入口：/render?layer=lyric 只画字幕（不渲染歌词动画） */
export function drawSubtitleFrame(ctx: CanvasRenderingContext2D, plan: any, t: number, opt: any = {}) {
  drawSubtitle(ctx, plan, t, opt);
}

/* 正式帧画完再叠加（幂等：重复挂载不再包一层） */
export function installSubtitleOverlay() {
  const proto = J.Renderer.prototype as any;
  if ((proto as any).__jl_sub_patched) {
    return;
  }
  const origFrame = proto.frame;
  proto.frame = function (this: any, ctx: CanvasRenderingContext2D, plan: any, t: number, opt: any = {}) {
    origFrame.call(this, ctx, plan, t, opt);
    // 字幕分离：主通道页被 C# 注入 __jl_no_sub=true 时不叠加（字幕只走独立通道）
    if (window.__jl_no_sub) return;
    if (effCfg().content !== 'off') {
      try {
        drawSubtitle(ctx, plan, t, opt);
      } catch { /* 字幕出错不影响渲染 */
      }
    }
  };
  proto.__jl_sub_patched = true;
}
