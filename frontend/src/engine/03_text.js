(() => {
    'use strict';

    J.PID = Object.freeze({dx: 0, dy: 0, rot: 0, s: 1, st: 1, sdir: 0, a: 1});
    J.PT = (dx = 0, dy = 0, rot = 0, s = 1, st = 1, sdir = 0, a = 1) => ({dx, dy, rot, s, st, sdir, a});

    // 文字整列（typeset），planner/renderer 按方案设置：假名排紧、助词缩小、
    // 首字放大、拉丁稍大且和日语之间留小空隙。只改每个字的字号/步进，layout 都仍适用
    J.TYPESET = false;
    J.setTypeset = on => {
        J.TYPESET = !!on;
    };
    const HIRA = /[ぁ-ゟ]/, KATA = /[゠-ヿㇰ-ㇿ]/, KANJI = /[㐀-鿿豈-﫿々〆]/, LATIN = /[A-Za-z0-9]/;
    const PARTICLES = 'はがをにでとのへも';
    const cls = ch => (!ch ? '' : LATIN.test(ch) ? 'L' : KANJI.test(ch) ? 'K' : KATA.test(ch) ? 'T' : HIRA.test(ch) ? 'H' : /\s/.test(ch) ? 'S' : 'P');

    function isParticle(arr, i) {
        const ch = arr[i], prev = arr[i - 1], next = arr[i + 1];
        if (!prev || PARTICLES.indexOf(ch) < 0 || prev === 'っ' || prev === 'ッ' || prev === 'ー') return false;
        if (ch === 'を') return true;
        const cp = cls(prev), cn = cls(next);
        const nextOK = !next || cn === 'K' || cn === 'T' || cn === 'L' || cn === 'S' || cn === 'P';
        return nextOK && cp !== 'S' && cp !== 'P' && !(cp === 'H' && cn === 'H');
    }

    // 每个字：f = 字号系数，gap = 它前面多出的空隙（em）
    J.typesetLine = (arr) => {
        const out = arr.map(() => ({f: 1, gap: 0, adv: 1}));
        if (!J.TYPESET) return out;
        const content = arr.filter(c => !/\s/.test(c)).length;
        let first = true;
        arr.forEach((ch, i) => {
            const c = cls(ch), o = out[i];
            if (c === 'S') return;
            if (c === 'H' || c === 'T') o.adv = J.isSmallKana(ch) ? 0.86 : ch === 'ー' ? 0.94 : 0.9;       // 假名排紧
            if (c === 'L') o.f = 1.08;
            if ((c === 'H' || c === 'T') && content >= 3 && isParticle(arr, i)) o.f = 0.78;
            if (first && content >= 3 && (c === 'K' || c === 'H' || c === 'T')) o.f = 1.18;               // 頭の字を大きく
            first = false;
            const pc = i > 0 ? cls(arr[i - 1]) : '';
            if (i > 0 && pc !== 'S' && ((c === 'L') !== (pc === 'L')) && pc && pc !== 'P' && c !== 'P') o.gap = 0.2;   // 英字と日本語の間
        });
        return out;
    };

    // 排版：每个字形的中心，相对 item 原点，未乘 sx/sy 的 item 空间
    J.layoutText = (it) => {
        const text = String(it.text ?? '');
        const size = it.size, track = it.track || 0, sx = it.sx || 1, sy = it.sy || 1;
        const lines = text.split('\n');
        const out = [];
        const vertical = !!it.vertical;
        const lead = (it.lead || 1.3) * size;
        let gi = 0;
        if (!vertical) {
            const sets = lines.map(line => {
                const arr = [...line];
                return {arr, ts: J.typesetLine(arr)};
            });
            const widths = sets.map(({arr, ts}) => {
                let w = 0;
                arr.forEach((ch, i) => {
                    w += (J.metrics.adv(it.font, ch) * ts[i].adv * ts[i].f + ts[i].gap) * size + (i < arr.length - 1 ? track * size : 0);
                });
                return w;
            });
            const maxW = Math.max(1, ...widths);
            sets.forEach(({arr, ts}, li) => {
                let x = it.align === 'left' ? 0 : it.align === 'right' ? -widths[li] : -widths[li] / 2;
                const y = (li - (lines.length - 1) / 2) * lead;
                arr.forEach((ch, ci) => {
                    const t = ts[ci], a = J.metrics.adv(it.font, ch) * size * t.adv * t.f;
                    x += t.gap * size;
                    // 大小不同的字形保持行基线
                    out.push({
                        ch,
                        i: gi++,
                        li,
                        ci,
                        n: arr.length,
                        x: x + a / 2,
                        y: y + (1 - t.f) * size * 0.36,
                        w: a,
                        h: size * t.f,
                        r90: false,
                        vx: 0,
                        vy: 0,
                        fs: t.f
                    });
                    x += a + track * size;
                });
            });
            out.W = maxW;
            out.H = lines.length * lead - (lead - size);
        } else {
            const sets = lines.map(line => {
                const arr = [...line];
                return {arr, ts: J.typesetLine(arr)};
            });
            const heights = sets.map(({
                                          arr,
                                          ts
                                      }) => arr.reduce((h, ch, i) => h + (vAdv(it.font, ch, size) * ts[i].adv * ts[i].f + ts[i].gap * size) + track * size, 0) - track * size);
            const maxH = Math.max(1, ...heights);
            sets.forEach(({arr, ts}, li) => {
                let y = it.align === 'left' ? 0 : -heights[li] / 2;         // 竖排时 'left' 即顶对齐
                const x = -(li - (lines.length - 1) / 2) * lead;
                arr.forEach((ch, ci) => {
                    const t = ts[ci], a = vAdv(it.font, ch, size) * t.adv * t.f;
                    y += t.gap * size;
                    const r90 = J.VERT_ROTATE.includes(ch) || /[A-Za-z0-9]/.test(ch);
                    let vx = 0, vy = 0;
                    if (J.isSmallKana(ch)) {
                        vx = 0.11 * size;
                        vy = -0.11 * size;
                    }
                    if ('、。，．'.includes(ch)) {
                        vx = 0.3 * size;
                        vy = -0.3 * size;
                    }
                    out.push({
                        ch,
                        i: gi++,
                        li,
                        ci,
                        n: arr.length,
                        x,
                        y: y + a / 2,
                        w: size * t.f,
                        h: a,
                        r90,
                        vx,
                        vy,
                        fs: t.f
                    });
                    y += a + track * size;
                });
            });
            out.W = lines.length * lead - (lead - size);
            out.H = maxH;
        }
        out.N = gi;
        return out;
    };

    function vAdv(font, ch, size) {
        return /[A-Za-z0-9]/.test(ch) ? J.metrics.adv(font, ch) * size : size;
    }

    // 模糊/发光的 item 只画一次到离屏层，整层做 blur/glow：每个字都挂 filter 或
    // shadowBlur（再乘 3 个色差 pass）在 canvas 上非常慢
    let layerCv = null;

    function drawItemLayered(env, it) {
        const ctx = env.ctx;
        const lay = it._lay || (it._lay = J.layoutText(it));
        const size = it.size, sx = it.sx || 1, sy = it.sy || 1;
        // 全部字形的 item 本地包围盒，含每字偏移
        let x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
        for (const g of lay) {
            const c = it.charFn ? it.charFn(g.i, g, lay.N) : null;
            if (c && c.hide) continue;
            const s = c && c.s != null ? Math.abs(c.s) : 1, gw = g.w * sx * s * (c && c.sx ? Math.abs(c.sx) : 1),
                gh = g.h * sy * s * (c && c.sy ? Math.abs(c.sy) : 1);
            const r = Math.max(gw, gh) * (c && c.rot ? 0.75 : 0.55);
            const gx = g.x * sx + g.vx * sx + (c ? c.dx || 0 : 0), gy = g.y * sy + g.vy * sy + (c ? c.dy || 0 : 0);
            x0 = Math.min(x0, gx - r);
            x1 = Math.max(x1, gx + r);
            y0 = Math.min(y0, gy - r);
            y1 = Math.max(y1, gy + r);
        }
        if (x0 > x1) return null;
        const sh = it.shadow, ex = it.extrude;
        const pad = (it.blur || 0) * 2.6 + size * 0.12 + (it.stroke || 0) + (sh ? (sh.blur || 0) * 1.3 + Math.abs(sh.dx || 0) + Math.abs(sh.dy || 0) : 0) + (ex ? Math.abs(ex.dx || 0) + Math.abs(ex.dy || 0) : 0);
        x0 -= pad;
        y0 -= pad;
        x1 += pad;
        y1 += pad;
        const T = ctx.getTransform(), k = Math.max(0.05, Math.hypot(T.a, T.b));
        const ow = Math.ceil((x1 - x0) * k), oh = Math.ceil((y1 - y0) * k);
        if (ow < 2 || oh < 2 || ow * oh > ctx.canvas.width * ctx.canvas.height * 1.6) return undefined;   // 层太大画不下，走直画路径
        if (!layerCv) layerCv = document.createElement('canvas');
        if (layerCv.width < ow || layerCv.height < oh) {
            layerCv.width = Math.max(ow, layerCv.width);
            layerCv.height = Math.max(oh, layerCv.height);
        }
        const L = layerCv.getContext('2d');
        L.setTransform(1, 0, 0, 1, 0, 0);
        L.globalAlpha = 1;
        L.globalCompositeOperation = 'source-over';
        L.filter = 'none';
        L.clearRect(0, 0, ow, oh);
        L.setTransform(k, 0, 0, k, -x0 * k, -y0 * k);
        const inner = Object.assign({}, it, {x: 0, y: 0, rot: 0, skew: 0, blur: 0, shadow: null, blend: null});
        const bb = J.drawItem(Object.assign({}, env, {ctx: L, inLayer: true, scale: k}), inner);
        ctx.save();
        ctx.translate(it.x, it.y);
        if (it.rot) ctx.rotate(it.rot * J.DEG);
        if (it.skew) ctx.transform(1, 0, Math.tan(it.skew * J.DEG), 1, 0, 0);
        if (it.blend) ctx.globalCompositeOperation = it.blend;
        if (it.blur > 0.4) ctx.filter = `blur(${(it.blur * env.scale).toFixed(1)}px)`;
        if (sh && env.pass === 'main') {
            ctx.shadowColor = sh.color || 'rgba(0,0,0,0.6)';
            ctx.shadowBlur = (sh.blur || 0) * env.scale;
            ctx.shadowOffsetX = (sh.dx || 0) * env.scale;
            ctx.shadowOffsetY = (sh.dy || 0) * env.scale;
        }
        ctx.drawImage(layerCv, 0, 0, ow, oh, x0, y0, ow / k, oh / k);
        ctx.restore();
        if (!bb) return null;
        return Object.assign({}, bb, {
            x0: bb.x0 + it.x,
            x1: bb.x1 + it.x,
            y0: bb.y0 + it.y,
            y1: bb.y1 + it.y,
            cx: it.x,
            cy: it.y
        });
    }

    // 装饰性数字/时刻的识别：内容只是编号（No.01、#03、128）或时刻（00:12.34、REC 1:05）。
    // 同样组合出现在切分歌词里时算歌词，保留
    const CJK = /[\u3040-\u30ff\u3400-\u9fff\uf900-\ufaff\uac00-\ud7af]/;
    J.decoTextKind = (t) => {
        t = String(t || '').trim();
        if (!t || !/\d/.test(t) || CJK.test(t)) return null;
        if (/\d{1,2}[:：]\d{2}/.test(t) && /^[A-Za-z]{0,4}[\s.#]*[\d:：.;'"\s\/\-–—+]+[A-Za-z]{0,3}$/.test(t)) return 'time';
        if ((t.match(/[A-Za-z]/g) || []).length <= 5 && /^[A-Za-z#№.\s\d\/\-–—+×x%:,'°]+$/.test(t)) return 'no';
        return null;
    };
    J.hideDecoText = (env, text) => {
        const fx = env.fx || {};
        if (!fx.hideNo && !fx.hideTime) return false;
        const k = J.decoTextKind(text);
        if (!k || !(k === 'no' ? fx.hideNo : fx.hideTime)) return false;
        const lyr = env.cut ? String(env.cut.lineText || env.cut.text || '') : '';
        return !lyr.includes(String(text).trim());
    };
    J.drawItem = (env, it) => {
        const ctx = env.ctx;
        const ghostPass = env.pass !== 'main';
        if (ghostPass && it.ghost === false) return null;
        if (!it.text || it.size <= 0.5) return null;
        if (J.hideDecoText(env, it.text)) return null;
        if (!env.inLayer && !env.glyphLog && !env.hideText && env.allowFilter && !it.pieceFn && ((it.blur || 0) > 0.4 || (it.shadow && !ghostPass && (it.shadow.blur || 0) * (env.scale || 1) > 6))) {
            const r = drawItemLayered(env, it);
            if (r !== undefined) return r;
        }
        const lay = it._lay || J.layoutText(it);
        const size = it.size, sx = it.sx || 1, sy = it.sy || 1;
        const baseAlpha = (it.alpha ?? 1) * (ghostPass ? (it.ghostAlpha ?? 1) : 1);
        if (baseAlpha <= 0.002) return null;
        const col = ghostPass ? env.passColor : (it.color || '#fff');
        const sCol = ghostPass ? env.passColor : (it.strokeColor || it.color || '#fff');
        const fill = it.fill !== false;
        ctx.save();
        ctx.translate(it.x, it.y);
        if (it.rot) ctx.rotate(it.rot * J.DEG);
        if (it.skew) ctx.transform(1, 0, Math.tan(it.skew * J.DEG), 1, 0, 0);
        if (it.blend) ctx.globalCompositeOperation = it.blend;
        if (it.blur > 0.4 && env.allowFilter) ctx.filter = `blur(${(it.blur * env.scale).toFixed(1)}px)`;
        // 太さ（統一感）：歌词在可变字体（Noto Sans / Serif JP）上从细长长到粗
        const wg = env.cut && env.cut.weightGrow && !it.noWeight ? J.weightNow(env) : 0;
        ctx.font = wg ? J.varFontCSS(it.font, size, wg) : J.fontCSS(it.font, size);
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        let grad = null;
        if (!ghostPass && it.gradient && fill) {
            // 渐变坐标在每字形自己的局部空间（fill 在每字形 translate 之后才发生）
            grad = ctx.createLinearGradient(0, -size * 0.5, 0, size * 0.5);
            if (it.gradient.length === 2) {
                grad.addColorStop(0, it.gradient[0]);
                grad.addColorStop(1, it.gradient[1]);
            } else it.gradient.forEach(([o, c]) => grad.addColorStop(o, c));      // [[offset, colour], ...] 硬分割用
        }
        if (!ghostPass && it.pattern && fill && !grad) grad = J.textPattern(ctx, it.pattern, it.patternColor || it.color || '#fff', it.patternBg, size, env.scale || 1);
        const fillA = it.fillAlpha ?? 1;
        const shadow = !ghostPass && it.shadow;
        if (shadow) {
            const k = env.scale || 1;
            ctx.shadowColor = shadow.color || 'rgba(0,0,0,0.6)';
            ctx.shadowBlur = env.allowFilter === false ? 0 : (shadow.blur || 0) * k;
            ctx.shadowOffsetX = (shadow.dx || 0) * k;
            ctx.shadowOffsetY = (shadow.dy || 0) * k;
        }
        const ext = !ghostPass && it.extrude && it.extrude.n > 0 ? it.extrude : null;
        const dash = it.dash != null && it.dash < 1 ? it.dash : null;
        const boxes = [];
        const pxScale = size * Math.max(sx, sy) * (env.scale || 1);
        for (const g of lay) {
            if (g.ch === ' ' || g.ch === '　') continue;
            const c = it.charFn ? it.charFn(g.i, g, lay.N) : null;
            if (c && c.hide) continue;
            const a = baseAlpha * (c && c.a != null ? c.a : 1);
            if (a <= 0.002) continue;
            const ch = (c && c.ch) || g.ch;
            const cs = (c && c.s != null ? c.s : 1) * (g.fs || 1);
            const gx = g.x * sx + g.vx * sx + (c ? c.dx || 0 : 0);
            const gy = g.y * sy + g.vy * sy + (c ? c.dy || 0 : 0);
            const crot = (c ? c.rot || 0 : 0) + (g.r90 ? 90 : 0);
            const csx = sx * cs * (c && c.sx ? c.sx : 1), csy = sy * cs * (c && c.sy ? c.sy : 1);
            const gcol = (!ghostPass && c && c.color) || col;
            // bbox 与 layered 路径同口径：计入 per-glyph 缩放 c.sx/c.sy，否则压扁/拉伸入场时 decor 锚定偏小
            boxes.push({
                x: gx,
                y: gy,
                w: g.w * sx * cs / (g.fs || 1) * (c && c.sx ? Math.abs(c.sx) : 1),
                h: g.h * sy * cs / (g.fs || 1) * (c && c.sy ? Math.abs(c.sy) : 1)
            });
            // モーフ：记录每个字形的落点（设备空间）/ morph 绘制期间本处先不出字形
            if (env.glyphLog && !ghostPass) {
                ctx.save();
                ctx.translate(gx, gy);
                if (crot) ctx.rotate(crot * J.DEG);
                if (csx !== 1 || csy !== 1) ctx.scale(csx, csy);
                const T = ctx.getTransform();
                ctx.restore();
                env.glyphLog.push({
                    ch,
                    m: [T.a, T.b, T.c, T.d, T.e, T.f],
                    font: ctx.font,
                    px: size,
                    color: typeof gcol === 'string' ? gcol : (it.color || '#fff'),
                    a: a * (fill ? fillA : 1),
                    stroke: it.stroke > 0 ? it.stroke : 0,
                    strokeColor: typeof sCol === 'string' ? sCol : null,
                    fill: fill && !(c && c.outline)
                });
            }
            if (env.hideText) continue;
            if (it.pieceFn && fill && !(c && c.ch) && !it.gradient && dash == null && !(c && (c.clipY || c.clipX || c.outline))) {
                if (drawPieces(env, it, g, ch, gx, gy, crot, csx, csy, gcol, a, pxScale * cs)) continue;
            }
            ctx.save();
            ctx.translate(gx, gy);
            if (crot) ctx.rotate(crot * J.DEG);
            if (c && c.skew) ctx.transform(1, 0, Math.tan(c.skew * J.DEG), 1, 0, 0);
            if (csx !== 1 || csy !== 1) ctx.scale(csx, csy);
            if (c && (c.clipY || c.clipX)) {           // 每字形遮罩，字形盒子的比例值（中心为 0）
                const cy = c.clipY || [-0.7, 0.7], cx = c.clipX || [-0.7, 0.7];
                ctx.beginPath();
                ctx.rect(cx[0] * g.w, cy[0] * g.h, (cx[1] - cx[0]) * g.w, (cy[1] - cy[0]) * g.h);
                ctx.clip();
            }
            if (c && c.blur > 0.4 && env.allowFilter) ctx.filter = `blur(${(c.blur * env.scale).toFixed(1)}px)`;
            ctx.globalAlpha = a;
            const outlineOnly = c && c.outline;
            if (ext && !outlineOnly) {
                ctx.fillStyle = ext.color || '#000';
                const ea = ext.a ?? 1;
                for (let k = ext.n; k >= 1; k--) {
                    ctx.globalAlpha = a * ea * (ext.fade ? 1 - (k - 1) / ext.n * 0.85 : 1);
                    ctx.fillText(ch, ext.dx * k / ext.n / csx, ext.dy * k / ext.n / csy);
                }
                ctx.globalAlpha = a;
            }
            if (fill && !outlineOnly && fillA > 0.002 && dash == null) {
                ctx.globalAlpha = a * fillA;
                ctx.fillStyle = grad || gcol;
                ctx.fillText(ch, 0, 0);
                ctx.globalAlpha = a;
            }
            if (it.stroke > 0 || outlineOnly || dash != null) {
                if (shadow && fill) {
                    ctx.shadowColor = 'rgba(0,0,0,0)';
                }
                ctx.lineJoin = 'round';
                ctx.miterLimit = 2;
                // c.sx/c.sy 可为 0 而 alpha>0：除零会得到 Infinity 描边（Canvas 会静默丢弃）
                ctx.lineWidth = (it.stroke > 0 ? it.stroke : Math.max(1, size * 0.02)) / Math.max(0.05, Math.sqrt(Math.abs(csx * csy)));
                ctx.strokeStyle = (!ghostPass && c && c.color) || sCol;
                if (dash != null) {
                    const L = size * 3.2;
                    ctx.setLineDash([Math.max(0.01, L * dash), L]);
                    ctx.lineDashOffset = 0;
                } else if (it.strokeDash) ctx.setLineDash(it.strokeDash);
                ctx.strokeText(ch, 0, 0);
                ctx.setLineDash([]);
                if (fill && !outlineOnly && (it.strokeUnder || (dash != null && fillA > 0.002))) {
                    ctx.globalAlpha = a * (dash != null ? fillA : 1);
                    ctx.fillStyle = grad || gcol;
                    ctx.fillText(ch, 0, 0);
                }
            }
            ctx.restore();
        }
        ctx.restore();
        // 设计空间 bbox（旋转只算平移）
        if (!boxes.length) return null;
        let x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
        for (const b of boxes) {
            x0 = Math.min(x0, b.x - b.w / 2);
            x1 = Math.max(x1, b.x + b.w / 2);
            y0 = Math.min(y0, b.y - b.h / 2);
            y1 = Math.max(y1, b.y + b.h / 2);
        }
        return {x0: it.x + x0, y0: it.y + y0, x1: it.x + x1, y1: it.y + y1, boxes, cx: it.x, cy: it.y};
    };

    // 密排小字一次画一行，省掉逐字 draw；只该在 main pass 用，sp 为每字额外间距 px。
    // 整行一根 fillText：不逐字走 drawItem，也就不吃 weightGrow/morph 抓取（满版平铺的
    // 背景行正该如此）。letterSpacing 不受支持的环境退回逐字 env.draw
    J.fastRow = (env, text, font, size, x, y, sp, color, alpha, align = 'left') => {
        if (env.pass !== 'main' || alpha <= 0.01 || !text || J.hideDecoText(env, text)) return;
        const ctx = env.ctx;
        if (!('letterSpacing' in ctx)) {
            env.draw({text, font, size, x, y, align, track: sp / size, color, alpha, ghost: false});
            return;
        }
        ctx.save();
        ctx.font = J.fontCSS(font, size);
        ctx.letterSpacing = sp.toFixed(2) + 'px';
        ctx.textAlign = align;
        ctx.textBaseline = 'middle';
        ctx.fillStyle = color;
        ctx.globalAlpha = alpha;
        ctx.fillText(text, x, y);
        ctx.restore();
    };

    // 太さ：当前 cut 的生长度 0..1（永不到 0，非 0 同时表示"开"）；eased，前半段走完
    J.weightNow = (env) => {
        const span = Math.max(0.5, Math.min(1.4, env.cut.dur * 0.5));
        const k = J.clamp(((env.ltb ?? env.lt) || 0) / span);
        return 1e-3 + (1 - Math.pow(1 - k, 3)) * (1 - 1e-3);
    };
    J.varFontCSS = (key, px, e) => {
        const f = J.FONTS[key] || {}, serif = f.kind === 'mincho';
        const w = Math.round(serif ? 200 + e * 700 : 100 + e * 800);
        return `${w} ${px.toFixed(2)}px ${serif ? '"Noto Serif JP"' : '"Noto Sans JP"'},${f.fb || 'sans-serif'}`;
    };

    // 返回 true 表示 pieces 画了（不在静止位）
    function drawPieces(env, it, g, ch, gx, gy, crot, sx, sy, col, alpha, px) {
        const glyph = J.glyphs.get(it.font, ch, px);
        const list = it.shatter ? fragList(glyph, it.seed || 1) : glyph.pieces;
        if (!list.length) return false;
        const size = it.size;
        const res = glyph.res;
        const pts = new Array(list.length);
        let moving = false;
        const cr = Math.cos(crot * J.DEG), sr = Math.sin(crot * J.DEG);
        for (let j = 0; j < list.length; j++) {
            const p = list[j];
            const ex = p.cx * size * sx, ey = p.cy * size * sy;          // piece 中心偏移（em→px，乘缩放）
            const ox = gx + ex * cr - ey * sr, oy = gy + ex * sr + ey * cr;
            const t = it.pieceFn(g.i, j, p, ox, oy, g);
            pts[j] = t;
            if (t !== J.PID && t !== null) moving = true;
            if (t === null) moving = true;
            pts[j] = {t, ox, oy};
        }
        if (!moving) return false;
        const ctx = env.ctx;
        for (let j = 0; j < list.length; j++) {
            const {t, ox, oy} = pts[j];
            if (!t || t.a <= 0.003) continue;
            const p = list[j];
            const spr = J.glyphs.sprite(p.src || p, col);
            ctx.save();
            ctx.translate(ox + t.dx, oy + t.dy);
            if (t.st !== 1) {
                const d = t.sdir * J.DEG;
                ctx.rotate(d);
                ctx.scale(t.st, 1 / Math.sqrt(t.st));
                ctx.rotate(-d);
            }
            ctx.rotate((crot + t.rot) * J.DEG);
            const k = size / res * t.s;
            ctx.scale(sx * k, sy * k);
            ctx.globalAlpha = alpha * t.a;
            if (p.poly) {
                // fragment：先按多边形裁剪（em 坐标，绕 fragment 中心），再画母 sprite
                const src = p.src;
                ctx.beginPath();
                p.poly.forEach((q, qi) => {
                    const X = (q[0] - p.fx) * res, Y = (q[1] - p.fy) * res;
                    qi ? ctx.lineTo(X, Y) : ctx.moveTo(X, Y);
                });
                ctx.closePath();
                ctx.clip();
                ctx.drawImage(spr, -p.fx * res - src.w * res / 2, -p.fy * res - src.h * res / 2);
            } else {
                ctx.drawImage(spr, -spr.width / 2, -spr.height / 2);
            }
            ctx.restore();
        }
        return true;
    }

    function fragList(glyph, seed) {
        // 按 seed 键控：glyph 是缓存共享的，不同碎裂 cut 的碎片图案必须各自独立
        const by = glyph.frags || (glyph.frags = new Map());
        let out = by.get(seed);
        if (out) return out;
        out = [];
        for (const p of glyph.pieces) {
            const fr = J.fragments(p, seed);
            if (fr.length <= 1) {
                out.push(p);
                continue;
            }
            for (const f of fr) out.push({
                src: p,
                poly: f.poly,
                fx: f.cx,
                fy: f.cy,
                cx: p.cx + f.cx,
                cy: p.cy + f.cy,
                w: p.w,
                h: p.h,
                area: p.area / fr.length,
                id: p.id * 8 + out.length
            });
        }
        by.set(seed, out);
        return out;
    }

    // 文字填充图案：'dots' | 'stripes' | 'hatch' | 'grid' | 'lines'，格子大小跟随字号
    const patCache = new Map();
    J.textPattern = (ctx, kind, color, bg, size, scale) => {
        const cell = Math.max(3, Math.round(size * (kind === 'dots' ? 0.075 : 0.06))),
            px = Math.max(2, Math.round(cell * scale));
        const key = kind + color + (bg || '') + px;
        let cv = patCache.get(key);
        if (!cv) {
            cv = document.createElement('canvas');
            cv.width = cv.height = px;
            const x = cv.getContext('2d');
            if (bg) {
                x.fillStyle = bg;
                x.fillRect(0, 0, px, px);
            }
            x.fillStyle = color;
            x.strokeStyle = color;
            if (kind === 'dots') {
                x.beginPath();
                x.arc(px / 2, px / 2, px * 0.34, 0, J.TAU);
                x.fill();
            } else if (kind === 'stripes') {
                x.lineWidth = px * 0.38;
                x.beginPath();
                x.moveTo(-px, px * 2);
                x.lineTo(px * 2, -px);
                x.moveTo(-px, px);
                x.lineTo(px, -px);
                x.moveTo(0, px * 2);
                x.lineTo(px * 2, 0);
                x.stroke();
            } else if (kind === 'hatch') {
                x.lineWidth = Math.max(1, px * 0.16);
                x.beginPath();
                x.moveTo(0, 0);
                x.lineTo(px, px);
                x.moveTo(px, 0);
                x.lineTo(0, px);
                x.stroke();
            } else if (kind === 'grid') {
                x.fillRect(0, 0, px, Math.max(1, px * 0.18));
                x.fillRect(0, 0, Math.max(1, px * 0.18), px);
            } else {
                x.fillRect(0, 0, px, Math.max(1, px * 0.45));
            }                          // 'lines'
            if (patCache.size > 80) patCache.clear();
            patCache.set(key, cv);
        }
        const pat = ctx.createPattern(cv, 'repeat');
        try {
            if (pat.setTransform && scale !== 1) pat.setTransform(new DOMMatrix().scale(1 / scale));
        } catch (e) {
        }
        return pat;
    };

    // 量 item 排版后的尺寸（设计像素，已乘 sx/sy）
    // 测量 memo：版式每帧每 pass 重复 measure/fitSize（fitSize 的探测也走这里），结果
    // 纯数值 + lay 只读，可跨 item/跨帧共享。哨兵挂在 metrics 表上：字体装载后
    // metrics.clear() 哨兵消失，整表作废（同 11p_layoutsA 的 SENT 模式）
    const MMEMO = new Map(), MSENT = '\u0001measure';
    const mKey = it => String(it.text ?? '') + '\u0002' + it.font + '\u0002' + it.size + '\u0002' +
        (it.track || 0) + '\u0002' + (it.lead || 1.3) + '\u0002' + (it.sx || 1) + '\u0002' + (it.sy || 1) +
        '\u0002' + (it.align || '') + '\u0002' + (it.vertical ? 1 : 0) + '\u0002' + (J.TYPESET ? 1 : 0);

    J.measure = (it) => {
        const mm = J.metrics && J.metrics.m;
        if (!mm || !mm.has(MSENT)) {
            MMEMO.clear();
            if (mm) mm.set(MSENT, 1);
        }
        const k = mKey(it);
        let m = MMEMO.get(k);
        if (!m) {
            const l = J.layoutText(it);
            m = {w: l.W * (it.sx || 1), h: l.H * (it.sy || 1), lay: l};
            if (MMEMO.size > 600) MMEMO.delete(MMEMO.keys().next().value);
            MMEMO.set(k, m);
        }
        return m;
    };

    // 求正好装进盒子的字号
    J.fitSize = (text, font, maxW, maxH, opt = {}) => {
        const probe = Object.assign({text, font, size: 100, track: 0}, opt);
        const m = J.measure(probe);
        const k = Math.min(maxW / Math.max(1, m.w), maxH / Math.max(1, m.h));
        return 100 * k;
    };
})();
