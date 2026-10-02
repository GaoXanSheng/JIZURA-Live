// typo 手法包第 2 部分：排版风的入场/持续/退场
(() => {
    'use strict';
    const E = J.E;
    const P = 'typo';
    const SET = 'typo';
    const reg = (g, key, def) => J.register(g, key, Object.assign({set: SET}, def), P);
    const HIDE = Object.freeze({hide: true});
    const clamp = J.clamp, lerp = J.lerp, DEG = J.DEG;

    const isSp = c => c === ' ' || c === '　';
    const layOf = it => (it._m || (it._m = J.measure(it))).lay;
    const motionK = env => J.clamp((env.fx && env.fx.motion != null ? env.fx.motion : 0.7) / 0.7, 0, 1.6);
    const cutN = env => Math.max(1, J.glyphCount(String(env.cut && env.cut.text || '')));
    const accentOf = env => {
        const sc = env.sc;
        return J.contrast(sc.accent, sc.bg) >= 1.8 ? sc.accent : sc.fg;
    };
    const subOf = env => {
        const sc = env.sc;
        return J.contrast(sc.sub, sc.bg) >= 1.5 ? sc.sub : sc.fg;
    };

    function addPost(it, fn) {
        const prev = it.post;
        it.post = prev ? (env, x, bb) => {
            prev(env, x, bb);
            fn(env, x, bb);
        } : fn;
    }

    // 每个 glyph 的阅读顺序 0..1（空格跳过）；单字 item 用它在 cut 内的 mi
    function orders(env, it) {
        const lay = layOf(it), out = new Array(lay.N).fill(0);
        const idx = [];
        for (const g of lay) if (!isSp(g.ch)) idx.push(g.i);
        const n = idx.length;
        if (n <= 1) {
            const N = cutN(env);
            const o = N > 1 ? clamp((+it.mi || 0) / (N - 1)) : 0;
            idx.forEach(i => {
                out[i] = o;
            });
            return out;
        }
        idx.forEach((i, k) => {
            out[i] = k / (n - 1);
        });
        return out;
    }

    // 关键字：最长汉字串的首字，否则第一个普通字形 → layout 下标
    function keyOf(lay) {
        let best = -1, bl = 0;
        for (let i = 0; i < lay.length; i++) {
            if (!J.isKanji(lay[i].ch)) continue;
            let j = i;
            while (j < lay.length && J.isKanji(lay[j].ch)) j++;
            if (j - i > bl) {
                bl = j - i;
                best = i;
            }
            i = j;
        }
        if (best >= 0) return lay[best].i;
        // 拉丁：最长单词的首字母
        let wb = -1, wl = 0;
        for (let i = 0; i < lay.length; i++) {
            if (!/[A-Za-z0-9]/.test(lay[i].ch)) continue;
            let j = i;
            while (j < lay.length && /[A-Za-z0-9'’]/.test(lay[j].ch)) j++;
            if (j - i > wl) {
                wl = j - i;
                wb = i;
            }
            i = j;
        }
        if (wb >= 0) return lay[wb].i;
        for (const g of lay) if (!isSp(g.ch) && !J.isPunct(g.ch) && !J.isSmallKana(g.ch)) return g.i;
        return lay.length ? lay[0].i : 0;
    }

    // 每行（竖排则每列）沿阅读轴的范围 + 横向位置，item 本地单位（已乘 sx/sy）
    function lineInfo(it) {
        const lay = layOf(it), sx = it.sx || 1, sy = it.sy || 1, map = new Map();
        for (const g of lay) {
            if (isSp(g.ch)) continue;
            const a0 = it.vertical ? (g.y - g.h / 2) * sy : (g.x - g.w / 2) * sx,
                a1 = it.vertical ? (g.y + g.h / 2) * sy : (g.x + g.w / 2) * sx;
            const L = map.get(g.li);
            if (!L) map.set(g.li, {li: g.li, a0, a1, c: it.vertical ? g.x * sx : g.y * sy});
            else {
                L.a0 = Math.min(L.a0, a0);
                L.a1 = Math.max(L.a1, a1);
            }
        }
        return [...map.values()].sort((a, b) => a.li - b.li);
    }

    // 在 item 本地空间（平移+旋转）里跑 fn，只在 main pass
    const inItem = (env, it, fn) => {
        if (env.pass !== 'main') return;
        const ctx = env.ctx;
        ctx.save();
        ctx.translate(it.x, it.y);
        if (it.rot) ctx.rotate(it.rot * DEG);
        try {
            fn();
        } finally {
            ctx.restore();
        }
    };
    // item 在设计空间的盒子（未旋转）
    const boxOf = it => {
        it._m = J.measure(it);
        return J.itemBox(it);
    };


    // キー字先行：关键字先落位（大→正常），其余从它背后滑出
    reg('enter', 'tyKeyFirst', {
        name: 'キー字先行', tags: ['pop', 'graphic', 'editorial', 'emotional'], ae: 'pop', w: 1,
        apply(env, it, p) {
            const lay = layOf(it), sx = it.sx || 1, sy = it.sy || 1;
            const plain = lay.filter(g => !isSp(g.ch));
            if (plain.length <= 1) {
                const q = clamp(p / 0.7);
                it.charFns.push(() => (q <= 0 ? HIDE : {s: lerp(1.9, 1, E.outExpo(q)), a: clamp(q * 4)}));
                return;
            }
            const ki = keyOf(lay), gk = lay.find(g => g.i === ki) || plain[0];
            let md = 1;
            for (const g of plain) md = Math.max(md, Math.abs(g.i - ki));
            const kq = clamp(p / 0.42);
            it.charFns.push((i, g) => {
                if (i === ki) return kq <= 0 ? HIDE : {s: lerp(2.1, 1, E.outExpo(kq)), a: clamp(kq * 4)};
                const d = Math.abs(i - ki) / md, q = clamp((p - 0.3 - d * 0.35) / 0.35);
                if (q <= 0) return HIDE;
                const e = E.outExpo(q);
                return {
                    dx: (gk.x - g.x) * sx * (1 - e),
                    dy: (gk.y - g.y) * sy * (1 - e),
                    s: lerp(0.4, 1, e),
                    a: clamp(q * 2.5)
                };
            });
        },
    });

    // 行送りワイプ：逐行擦入，accent 细线在行前跑
    reg('enter', 'tyLineWipe', {
        name: '行送りワイプ', tags: ['editorial', 'graphic', 'calm'], ae: 'wipe', w: 1,
        apply(env, it, p) {
            const lines = lineInfo(it), nL = Math.max(1, lines.length), st = Math.min(0.28, 0.6 / nL);
            const qOf = li => {
                const k = lines.findIndex(L => L.li === li);
                return E.inOutCubic(clamp((p - Math.max(0, k) * st) / (1 - (nL - 1) * st)));
            };
            const qs = new Map(lines.map(L => [L.li, qOf(L.li)]));
            const sx = it.sx || 1, sy = it.sy || 1, V = !!it.vertical;
            it.charFns.push((i, g) => {
                const q = qs.get(g.li) ?? 1;
                if (q >= 1) return null;
                const L = lines.find(l => l.li === g.li);
                if (!L) return null;
                const edge = lerp(L.a0, L.a1, q);
                const start = V ? (g.y - g.h / 2) * sy : (g.x - g.w / 2) * sx, len = V ? g.h * sy : g.w * sx;
                const f = (edge - start) / Math.max(1, len);
                if (f <= 0) return HIDE;
                if (f >= 1) return null;
                const lim = -0.6 + 1.2 * f;
                return V ? {clipY: [-0.7, lim]} : {clipX: [-0.7, lim]};
            });
            const ac = accentOf(env), lw = Math.max(2, it.size * 0.05);
            addPost(it, (e, i) => inItem(e, i, () => {
                for (const L of lines) {
                    const q = qs.get(L.li);
                    if (q >= 1) continue;
                    const a = lerp(L.a0, L.a1, E.inCubic(clamp(q * 1.4 - 0.4))), b = lerp(L.a0, L.a1, q);
                    if (b - a < 1) continue;
                    const off = L.c + i.size * (V ? sx : sy) * 0.64;
                    e.line(V ? [[off, a], [off, b]] : [[a, off], [b, off]], ac, lw, i.alpha ?? 1, false);
                }
            }));
        },
    });

    // 一字ずつ拡大：逐字在中央放大闪现，再落到各自位置
    reg('enter', 'tyZoomOne', {
        name: '一字ずつ拡大', tags: ['pop', 'graphic', 'emotional'], ae: 'zoom', w: 0.8, minDur: 1.0,
        inDur: (dur, n) => J.clamp(0.14 * n + 0.2, 0.4, Math.min(1.5, dur * 0.55)),
        apply(env, it, p) {
            const lay = layOf(it), sx = it.sx || 1, sy = it.sy || 1;
            const plain = lay.filter(g => !isSp(g.ch)), nn = plain.length;
            if (!nn) return;
            let x0 = 1e9, x1 = -1e9, y0 = 1e9, y1 = -1e9;
            for (const g of plain) {
                x0 = Math.min(x0, g.x - g.w / 2);
                x1 = Math.max(x1, g.x + g.w / 2);
                y0 = Math.min(y0, g.y - g.h / 2);
                y1 = Math.max(y1, g.y + g.h / 2);
            }
            const mx = (x0 + x1) / 2, my = (y0 + y1) / 2;
            const big = clamp(Math.min(env.W, env.H) * 0.5 / Math.max(1, it.size * Math.max(sx, sy)), 1.15, 2.6);
            const L = nn > 1 ? Math.min(0.55, 2.2 / (nn + 1)) : 1;
            const rank = new Map(plain.map((g, k) => [g.i, k]));
            const single = nn === 1 && cutN(env) > 1;
            it.charFns.push((i, g) => {
                const k = rank.get(i);
                if (k == null) return null;
                const s0 = nn > 1 ? k / (nn - 1) * (1 - L) : 0;
                const q = clamp((p - s0) / L);
                if (q <= 0) return HIDE;
                if (single) return {s: lerp(big, 1, E.outExpo(q)), a: clamp(q * 5)};
                const cx = (mx - g.x) * sx, cy = (my - g.y) * sy;
                if (q < 0.38) {
                    const u = E.outCubic(q / 0.38);
                    return {dx: cx, dy: cy, s: lerp(big * 1.25, big, u), a: clamp(q / 0.1)};
                }
                const e = E.inOutCubic((q - 0.38) / 0.62);
                return {dx: cx * (1 - e), dy: cy * (1 - e), s: lerp(big, 1, e)};
            });
        },
    });

    // 下線から立つ：先画下划线，字从线上长出来，最后线收回
    reg('enter', 'tyUnderLift', {
        name: '下線から立つ', tags: ['editorial', 'graphic', 'pop'], ae: 'stretch', w: 1,
        apply(env, it, p) {
            const lines = lineInfo(it), ord = orders(env, it), V = !!it.vertical, sx = it.sx || 1, sy = it.sy || 1;
            const multi = layOf(it).filter(g => !isSp(g.ch)).length > 1;
            it.charFns.push((i, g) => {
                const o = multi ? ord[i] : 0, q = clamp((p - 0.22 - o * 0.38) / 0.4);
                if (q <= 0) return HIDE;
                if (q >= 1) return null;
                const e = Math.max(0.02, E.outBack(q, 1.5));
                return V ? {sx: e, dx: -(1 - e) * g.w * sx * 0.45} : {sy: e, dy: (1 - e) * g.h * sy * 0.45};
            });
            const qb = E.outExpo(clamp(p / 0.32)), qr = E.inCubic(clamp((p - 0.72) / 0.28)), ac = accentOf(env);
            addPost(it, (e, i) => inItem(e, i, () => {
                const lw = Math.max(2.5, i.size * 0.055);
                for (const L of lines) {
                    const a = lerp(L.a0, L.a1, qr), b = lerp(L.a0, L.a1, qb);
                    if (b - a < 1) continue;
                    const off = V ? L.c - i.size * sx * 0.56 : L.c + i.size * sy * 0.56;
                    e.line(V ? [[off, a], [off, b]] : [[a, off], [b, off]], ac, lw, i.alpha ?? 1, false);
                }
            }));
        },
    });

    // 点から字：每字先以中黑点（・）弹出，再变成字形
    reg('enter', 'tyDotGrow', {
        name: '点から字', tags: ['pop', 'calm', 'graphic'], ae: 'pop', w: 0.9,
        apply(env, it, p) {
            const ord = orders(env, it), ac = accentOf(env);
            it.charFns.push((i) => {
                const t0 = ord[i] * 0.5, q1 = clamp((p - t0) / 0.18), q2 = clamp((p - t0 - 0.2) / 0.3);
                if (q1 <= 0) return HIDE;
                if (q2 <= 0) return {ch: '・', s: 1.25 * E.outBack(q1, 2.6), color: ac};
                if (q2 >= 1) return null;
                return {s: lerp(0.3, 1, E.outBack(q2, 1.8)), a: clamp(q2 * 3)};
            });
        },
    });

    // 括弧が開く：「 」从中间一起出现再向两侧滑开，露出中间的句子
    const drawBrackets = (e, V, cx, cy, half, w, h, size, col, a) => {
        if (a <= 0.01) return;
        const g = size * 0.18, L = Math.max(size * 0.28, (V ? w : h) * 0.3), lw = Math.max(2, size * 0.055);
        if (!V) {
            const xl = cx - half - g, xr = cx + half + g, yt = cy - h / 2 - g * 0.5, yb = cy + h / 2 + g * 0.5;
            e.line([[xl, yt + L], [xl, yt], [xl + L, yt]], col, lw, a, false);
            e.line([[xr, yb - L], [xr, yb], [xr - L, yb]], col, lw, a, false);
        } else {
            const yt = cy - half - g, yb = cy + half + g, xr = cx + w / 2 + g * 0.5, xl = cx - w / 2 - g * 0.5;
            e.line([[xr - L, yt], [xr, yt], [xr, yt + L]], col, lw, a, false);
            e.line([[xl + L, yb], [xl, yb], [xl, yb - L]], col, lw, a, false);
        }
    };
    reg('enter', 'tyBracketOpen', {
        name: '括弧が開く', tags: ['editorial', 'graphic', 'pop'], ae: 'wipe', w: 0.9,
        apply(env, it, p) {
            const b = boxOf(it), V = !!it.vertical, pad = it.size * 0.12;
            const e = E.inOutCubic(clamp((p - 0.08) / 0.62));
            const half = ((V ? b.h : b.w) / 2 + pad) * e;
            if (V) it.clipY = [b.cy - half, b.cy + half]; else it.clip = [b.cx - half, b.cx + half];
            const solo = layOf(it).filter(g => !isSp(g.ch)).length <= 1 && cutN(env) > 1;
            const a = solo ? 0 : clamp(p / 0.08) * (1 - clamp((p - 0.78) / 0.22)), col = accentOf(env);
            addPost(it, (en) => {
                if (en.pass === 'main') drawBrackets(en, V, b.cx, b.cy, half, b.w, b.h, it.size, col, a);
            });
        },
    });

    // 打ち直し：带光标打字；打错一个字，删除重打
    reg('enter', 'tyRetype', {
        name: '打ち直し', tags: ['editorial', 'glitch', 'emotional'], ae: 'type', w: 0.8, minDur: 1.0, cursor: true,
        inDur: (dur, n) => J.clamp(0.1 * n + 0.4, 0.45, Math.min(1.5, dur * 0.55)),
        apply(env, it, p) {
            const lay = layOf(it), N = lay.N;
            const plain = lay.filter(g => !isSp(g.ch));
            const seed = it.seed | 0;
            if (plain.length <= 1) {
                it.charFns.push(() => (p < 0.35 ? HIDE : null));
                return;
            }
            let wi = -1;
            if (plain.length >= 3) wi = plain[1 + (J.h(seed, 31) % (plain.length - 1))].i;
            const S = N + (wi >= 0 ? 2 : 0);
            const k = Math.floor(clamp(p) * (S + 0.999));
            let shown, wrong = false;
            if (wi < 0 || k <= wi) shown = Math.min(N, k);
            else if (k === wi + 1) {
                shown = wi + 1;
                wrong = true;
            } else if (k === wi + 2) shown = wi;
            else shown = Math.min(N, k - 2);
            const pool = J.pool('kana') || 'あいうえお';
            const wch = [...pool][J.h(seed, 32) % [...pool].length];
            const ac = accentOf(env);
            it.charFns.push((i) => {
                if (i >= shown) return HIDE;
                if (wrong && i === wi) return {ch: wch === lay[wi].ch ? '＊' : wch, color: ac};
                return null;
            });
            it.cursorAt = p < 1 && !it.vertical && !it.rot ? shown : -1;
        },
    });

    // ルビから：每字先以小字出现在注音位，再落下来长到位
    reg('enter', 'tyRubyDrop', {
        name: 'ルビから', tags: ['calm', 'editorial', 'emotional'], ae: 'drop', w: 1,
        apply(env, it, p) {
            const ord = orders(env, it), V = !!it.vertical, sx = it.sx || 1, sy = it.sy || 1, sub = subOf(env);
            it.charFns.push((i, g) => {
                const q = clamp((p - ord[i] * 0.45) / 0.55);
                if (q <= 0) return HIDE;
                if (q >= 1) return null;
                const off = V ? g.w * sx * 0.8 : -g.h * sy * 0.8;
                if (q < 0.4) {
                    const a = clamp(q / 0.4 * 1.6);
                    return V ? {dx: off, s: 0.32, a, color: sub} : {dy: off, s: 0.32, a, color: sub};
                }
                const e = E.outCubic((q - 0.4) / 0.6), o = {s: lerp(0.32, 1, e)};
                if (V) o.dx = off * (1 - e); else o.dy = off * (1 - e);
                if (e < 0.55) o.color = sub;
                return o;
            });
        },
    });


    // 線で消す：每行画一条删除线，字向线塌缩，然后线收回
    reg('exit', 'tyStrike', {
        name: '線で消す', tags: ['editorial', 'graphic', 'emotional'], ae: 'wipe', w: 1,
        apply(env, it, p) {
            const lines = lineInfo(it), ord = orders(env, it), V = !!it.vertical;
            it.charFns.push((i) => {
                const q = clamp((p - 0.3 - ord[i] * 0.3) / 0.4);
                if (q <= 0) return null;
                if (q >= 1) return HIDE;
                const e = E.inCubic(q);
                return V ? {sx: 1 - e * 0.95, a: 1 - e * e} : {sy: 1 - e * 0.95, a: 1 - e * e};
            });
            const ql = E.outExpo(clamp(p / 0.38)), qr = E.inCubic(clamp((p - 0.74) / 0.26)), ac = accentOf(env),
                sx = it.sx || 1, sy = it.sy || 1;
            addPost(it, (e, i) => inItem(e, i, () => {
                const lw = Math.max(2.5, i.size * 0.07);
                for (const L of lines) {
                    const pad = i.size * 0.1, a0 = L.a0 - pad, a1 = L.a1 + pad;
                    const a = lerp(a0, a1, qr), b = lerp(a0, a1, ql);
                    if (b - a < 1) continue;
                    const off = L.c + i.size * (V ? sx : sy) * 0.04;
                    e.line(V ? [[off, a], [off, b]] : [[a, off], [b, off]], ac, lw, 1, false);
                }
            }));
        },
    });

    // 点に戻る：字缩成中黑点（・），再眨掉
    reg('exit', 'tyToDot', {
        name: '点に戻る', tags: ['calm', 'pop', 'graphic'], ae: 'shrink', w: 0.9,
        apply(env, it, p) {
            const ord = orders(env, it), ac = accentOf(env);
            it.charFns.push((i) => {
                const q = clamp((p - ord[i] * 0.4) / 0.6);
                if (q <= 0) return null;
                if (q >= 1) return HIDE;
                if (q < 0.5) return {s: lerp(1, 0.3, E.inCubic(q / 0.5))};
                const u = (q - 0.5) / 0.5;
                return {ch: '・', color: ac, s: 1.5 * (1 - E.inCubic(u)), a: 1 - u * u};
            });
        },
    });

    // 改行送り：整行分三步向上换行，从窗口里离开
    reg('exit', 'tyLineFeed', {
        name: '改行送り', tags: ['editorial', 'calm', 'graphic'], ae: 'wipe', w: 1,
        apply(env, it, p) {
            const b = boxOf(it), V = !!it.vertical, pad = it.size * 0.12;
            const k = clamp(p) * 3, st = Math.min(3, Math.floor(k)), f = k - st;
            const step = Math.min(1, (st + E.outCubic(clamp(f * 2.6))) / 3);
            const dist = (V ? b.w : b.h) + it.size * 0.3;
            if (V) {
                it.clip = [b.x0 - pad, b.x1 + pad];
                it.x += dist * step;
            } else {
                it.clipY = [b.y0 - pad, b.y1 + pad];
                it.y -= dist * step;
            }
            if (p >= 0.999) it.alpha = 0;
        },
    });

    // 括弧閉じ：「 」出现在两端向中间合拢，把句子带走
    reg('exit', 'tyBracketClose', {
        name: '括弧閉じ', tags: ['editorial', 'graphic', 'pop'], ae: 'wipe', w: 0.9,
        apply(env, it, p) {
            const b = boxOf(it), V = !!it.vertical, pad = it.size * 0.12;
            const e = E.inOutCubic(clamp((p - 0.12) / 0.7));
            const half = ((V ? b.h : b.w) / 2 + pad) * (1 - e);
            if (V) it.clipY = [b.cy - half, b.cy + half]; else it.clip = [b.cx - half, b.cx + half];
            if (e >= 1) it.alpha = 0;
            const solo = layOf(it).filter(g => !isSp(g.ch)).length <= 1 && cutN(env) > 1;
            const a = solo ? 0 : clamp(p / 0.12) * (1 - clamp((p - 0.84) / 0.16)), col = accentOf(env);
            addPost(it, (en) => {
                if (en.pass === 'main') drawBrackets(en, V, b.cx, b.cy, half, b.w, b.h, it.size, col, a);
            });
        },
    });

    // 番号に変わる：每字变成自己的小序号，序号再淡出
    reg('exit', 'tyToIndex', {
        name: '番号に変わる', tags: ['editorial', 'glitch', 'graphic'], ae: 'scatter', w: 0.8,
        apply(env, it, p) {
            const lay = layOf(it), ord = orders(env, it), sub = subOf(env), V = !!it.vertical;
            const num = new Map();
            let k = 0;
            const plain = lay.filter(g => !isSp(g.ch));
            const base = plain.length <= 1 ? Math.round(+it.mi || 0) : 0;
            for (const g of plain) num.set(g.i, String(base + (++k)).padStart(2, '0'));
            it.charFns.push((i, g) => {
                const q = clamp((p - ord[i] * 0.4) / 0.6);
                if (q <= 0) return null;
                if (q >= 1) return HIDE;
                if (q < 0.3) return {s: lerp(1, 0.55, E.inCubic(q / 0.3))};
                const u = (q - 0.3) / 0.7;
                const o = {ch: num.get(i) || '00', s: 0.42, color: sub, a: 1 - E.inCubic(u)};
                if (V) o.dx = -g.w * 0.2 * u; else o.dy = -g.h * 0.2 * u;
                return o;
            });
        },
    });

    // 一字残し：全部折进关键字，关键字胀大后淡出
    reg('exit', 'tyKeyLast', {
        name: '一字残し', tags: ['emotional', 'pop', 'graphic'], ae: 'shrink', w: 0.9,
        apply(env, it, p) {
            const lay = layOf(it), sx = it.sx || 1, sy = it.sy || 1;
            const plain = lay.filter(g => !isSp(g.ch));
            const ki = plain.length > 1 ? keyOf(lay) : (plain[0] ? plain[0].i : 0);
            const gk = lay.find(g => g.i === ki) || plain[0];
            let md = 1;
            for (const g of plain) md = Math.max(md, Math.abs(g.i - ki));
            if (plain.length <= 1 && cutN(env) > 1) {
                // 单字布局：只留 cut 的首个汉字，其余直接缩没
                const keep = J.isKanji((plain[0] || {}).ch || '') && (J.h(env.cut.seed | 0, Math.round(+it.mi || 0), 5) % 2 === 0);
                it.charFns.push(() => {
                    if (!keep) {
                        const q = clamp(p / 0.5);
                        return q >= 1 ? HIDE : {s: 1 - 0.6 * E.inCubic(q), a: 1 - q};
                    }
                    const q = clamp((p - 0.4) / 0.6);
                    return q >= 1 ? HIDE : {s: 1 + E.inCubic(q) * 1.4, a: 1 - E.inCubic(q)};
                });
                return;
            }
            it.charFns.push((i, g) => {
                if (i === ki) {
                    const q = clamp((p - 0.45) / 0.55);
                    return q >= 1 ? HIDE : {s: 1 + E.inCubic(q) * 1.8, a: 1 - E.inCubic(q)};
                }
                const d = Math.abs(i - ki) / md, q = clamp((p - (1 - d) * 0.2) / 0.45);
                if (q <= 0) return null;
                if (q >= 1) return HIDE;
                const e = E.inCubic(q);
                return {dx: (gk.x - g.x) * sx * e, dy: (gk.y - g.y) * sy * e, s: 1 - 0.6 * e, a: 1 - e};
            });
        },
    });

    // 下線へ沈む：画下划线，每字沉进线里（按行遮罩），线再收回
    reg('exit', 'tyUnderSink', {
        name: '下線へ沈む', tags: ['editorial', 'calm', 'graphic'], ae: 'fall', w: 1,
        apply(env, it, p) {
            const lines = lineInfo(it), ord = orders(env, it), V = !!it.vertical, sx = it.sx || 1, sy = it.sy || 1;
            it.charFns.push((i, g) => {
                const q = clamp((p - 0.2 - ord[i] * 0.4) / 0.35);
                if (q <= 0) return null;
                if (q >= 1) return HIDE;
                const e = E.inCubic(q), d = e * 1.25;
                return V ? {dx: -g.w * sx * d, clipX: [-0.55 + d, 0.7]} : {dy: g.h * sy * d, clipY: [-0.7, 0.55 - d]};
            });
            const qb = E.outExpo(clamp(p / 0.28)), qr = E.inCubic(clamp((p - 0.78) / 0.22)), ac = accentOf(env);
            addPost(it, (e, i) => inItem(e, i, () => {
                const lw = Math.max(2.5, i.size * 0.05);
                for (const L of lines) {
                    const a = lerp(L.a0, L.a1, qr), b = lerp(L.a0, L.a1, qb);
                    if (b - a < 1) continue;
                    const off = V ? L.c - i.size * sx * 0.56 : L.c + i.size * sy * 0.56;
                    e.line(V ? [[off, a], [off, b]] : [[a, off], [b, off]], ac, lw, 1, false);
                }
            }));
        },
    });

    // 縦組に折れる：横排折成竖列（竖排折成横排），再淡出
    reg('exit', 'tyFoldVert', {
        name: '縦組に折れる', tags: ['graphic', 'editorial', 'pop'], ae: 'fall', w: 0.9,
        apply(env, it, p) {
            const lay = layOf(it), V = !!it.vertical, sx = it.sx || 1, sy = it.sy || 1;
            const ln = new Map();
            for (const g of lay) {
                if (isSp(g.ch)) continue;
                const L = ln.get(g.li) || {gs: [], c0: 1e9, c1: -1e9};
                L.gs.push(g);
                L.c0 = Math.min(L.c0, V ? g.y : g.x);
                L.c1 = Math.max(L.c1, V ? g.y : g.x);
                ln.set(g.li, L);
            }
            const e = E.inOutCubic(clamp(p / 0.5)), f = clamp((p - 0.36) / 0.5);
            it.charFns.push((i, g) => {
                if (f >= 1) return HIDE;
                const L = ln.get(g.li);
                if (!L) return null;
                const k = Math.max(0, L.gs.findIndex(q => q.i === g.i)), n = L.gs.length, mid = (L.c0 + L.c1) / 2;
                const pitch = Math.min(1.02, (V ? env.W : env.H) * 0.82 / Math.max(1, n * (V ? g.w * sx : g.h * sy)));
                const off = (k - (n - 1) / 2) * pitch;
                let dx, dy;
                if (!V) {
                    dx = (mid - g.x) * sx;
                    dy = off * g.h * sy;
                } else {
                    dy = (mid - g.y) * sy;
                    dx = off * g.w * sx;
                }
                const drift = E.inQuad(f) * it.size * 0.25;
                return {dx: dx * e - (V ? drift : 0), dy: dy * e + (V ? 0 : drift), a: 1 - E.inQuad(f)};
            });
        },
    });


    // 一字の鼓動：只有关键字脉动（有节拍就踩拍）
    reg('hold', 'tyKeyPulse', {
        name: '一字の鼓動', tags: ['emotional', 'pop', 'calm'], ae: 'breathe', w: 1,
        apply(env, it, amt) {
            const lay = layOf(it), plain = lay.filter(g => !isSp(g.ch));
            if (!plain.length) return;
            const b = env.beat;
            const pulse = b ? Math.exp(-b.since / Math.max(0.12, b.len * 0.35)) : Math.pow(0.5 + 0.5 * Math.sin(env.ltb * J.TAU * 0.95), 4);
            const k = 0.1 * amt * motionK(env) * pulse;
            if (k < 0.002) return;
            if (plain.length === 1) {
                if (!J.isKanji(plain[0].ch)) return;
                it.charFns.push(() => ({s: 1 + k * 0.7}));
                return;
            }
            const ki = keyOf(lay);
            it.charFns.push((i) => (i === ki ? {s: 1 + k} : null));
        },
    });

    // 読み送り：阅读光标沿线步进，当前字以 accent 色微微抬起
    reg('hold', 'tyReadCursor', {
        name: '読み送り', tags: ['calm', 'editorial', 'pop'], ae: 'wave', w: 1,
        apply(env, it, amt) {
            if (amt < 0.3) return;
            const lay = layOf(it), plain = lay.filter(g => !isSp(g.ch)), n = plain.length;
            if (!n) return;
            const ac = accentOf(env), V = !!it.vertical, lift = it.size * 0.07 * Math.min(1, motionK(env));
            const rate = env.beat ? 1 / Math.max(0.2, env.beat.len) : 4;
            const tot = n <= 1 ? cutN(env) : n;
            const pos = Math.floor(env.ltb * rate) % (tot + 2);
            const frac = (env.ltb * rate) % 1,
                up = E.outCubic(clamp(frac * 4)) * (1 - E.inCubic(clamp((frac - 0.6) / 0.4)));
            if (n <= 1) {
                if (Math.round(+it.mi || 0) % (tot + 2) !== pos) return;
                it.charFns.push(() => (V ? {dx: lift * up, color: ac} : {dy: -lift * up, color: ac}));
                return;
            }
            const gi = pos < n ? plain[pos].i : -1;
            it.charFns.push((i) => (i === gi ? (V ? {dx: lift * up, color: ac} : {dy: -lift * up, color: ac}) : null));
        },
    });

    // 白抜き明滅：偶尔一个字短暂切成只描边
    reg('hold', 'tyOutlineBlink', {
        name: '白抜き明滅', tags: ['glitch', 'graphic', 'pop'], ae: 'glitchtick', w: 0.9,
        apply(env, it, amt) {
            const lay = layOf(it), plain = lay.filter(g => !isSp(g.ch));
            if (!plain.length) return;
            const seed = it.seed | 0, st = env.step >> 1;
            if (J.r(seed, st, 41) > 0.28 * amt * (0.5 + 0.5 * motionK(env)) / (plain.length === 1 ? Math.max(1, cutN(env) * 0.5) : 1)) return;
            const pick = plain[J.h(seed, st, 42) % plain.length].i;
            const two = plain.length > 3 && J.r(seed, st, 43) < 0.3 ? plain[J.h(seed, st, 44) % plain.length].i : -1;
            it.charFns.push((i) => (i === pick || i === two ? {outline: true} : null));
        },
    });

    // 字間ステップ：字距在几个档位间跳变（踩拍），像排版员在试方案
    reg('hold', 'tyTrackStep', {
        name: '字間ステップ', tags: ['graphic', 'editorial', 'pop'], ae: 'breathe', w: 0.8,
        apply(env, it, amt) {
            if (layOf(it).filter(g => !isSp(g.ch)).length < 2) return;
            const len = env.beat ? Math.max(0.25, env.beat.len) : 0.55;
            const t = env.ltb / len, k = Math.floor(t), f = t - k;
            const lv = [0, 1, 2, 1], a = lv[k % 4], b = lv[(k + 1) % 4];
            const v = lerp(a, b, E.outExpo(clamp((f - 0.85) / 0.15)));
            it.track = (it.track || 0) + v * 0.05 * amt * Math.min(1.2, motionK(env));
            it._m = null;
            it._lay = null;
        },
    });

})();
