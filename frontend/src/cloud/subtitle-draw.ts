// 字幕绘制核心（纯函数，零依赖）：预览叠加层（cloud/subtitle.ts）与导出烤入
// （主线程 overlay / export-worker）共用这一套排版，保证导出与预览所见一致。
// 不 import 任何有副作用的模块——export worker 的 shims 环境里也要能直接加载。

export interface SubDrawCfg {
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

// 导出烤入字幕的载荷：主线程按本页语义（字幕面板配置 + 翻译映射/行内注音）预解析出
// plan.lines[i] 对应的字幕文本，worker 只拿现成文本按 cfg 排版——worker 侧没有
// localStorage 和歌词库，解析不了这些
export interface SubPayload {
    cfg: SubDrawCfg;
    texts: string[];
}

function subFont(px: number, kind: SubDrawCfg['font']): string {
    if (kind === 'serif') return `700 ${px}px "Noto Serif JP", SimSun, serif`;
    if (kind === 'sans') return `700 ${px}px "Microsoft YaHei", "Noto Sans JP", sans-serif`;
    return `700 ${px}px "Noto Sans JP", "Microsoft YaHei", sans-serif`;
}

function wrapSubtitle(ctx: any, text: string, maxW: number): string[] {
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

function wrapSubtitleCached(ctx: any, text: string, font: string, maxW: number): string[] {
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

function lineAt(plan: any, t: number): { line: any; idx: number } | null {
    const lines = plan.lines;
    const ok = (l: any) => l && t >= l.start && t < (l.visEnd != null ? l.visEnd : l.end);
    if (subLinePlan !== plan) {
        subLinePlan = plan;
        subLineIdx = 0;
    }
    if (ok(lines[subLineIdx])) return {line: lines[subLineIdx], idx: subLineIdx};
    if (ok(lines[subLineIdx + 1])) return {line: lines[++subLineIdx], idx: subLineIdx};
    const idx = lines.findIndex(ok);
    if (idx < 0) return null;
    subLineIdx = idx;
    return {line: lines[idx], idx};
}

// textOf(line, idx) 返回该行字幕文本（'' 即不画）。idx 给 worker 的 texts 数组用
export function drawSubtitleAt(ctx: any, plan: any, t: number, opt: any,
                               cfg: SubDrawCfg, textOf: (line: any, idx: number) => string) {
    if (cfg.content === 'off' || opt.noTrans || opt.layer) return;
    if (!plan || !plan.lines || !plan.W || !plan.H) return;
    const at = lineAt(plan, t);
    if (!at) return;
    const text = textOf(at.line, at.idx);
    if (!text) return;

    const W = plan.W, H = plan.H;
    const scale = opt.scale || (ctx.canvas.width ? ctx.canvas.width / W : 1);
    const fontPx = Math.max(8, H * cfg.size / 100);
    const strokePx = H / 1080 * cfg.stroke;
    const maxW = W * 0.86;
    const lh = fontPx * 1.32;

    ctx.save();
    ctx.setTransform(scale, 0, 0, scale, 0, 0);
    const font = subFont(fontPx, cfg.font);
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
    rows.forEach((r: string, i: number) => {
        const y = baseY + i * lh;
        if (strokePx > 0) ctx.strokeText(r, centerX, y);
        ctx.fillText(r, centerX, y);
    });
    ctx.restore();
}

// 由载荷生成叠加回调：导出（主线程与 worker 同构）在每帧标准渲染后调用它烤入字幕
export function makeSubtitleOverlay(payload: SubPayload) {
    return (ctx: any, plan: any, t: number, opt: any = {}) =>
        drawSubtitleAt(ctx, plan, t, opt, payload.cfg, (_l: any, i: number) => (payload.texts || [])[i] || '');
}
