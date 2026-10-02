// kinetic 第 2 包：逐词的 enter / exit / hold
(() => {
    'use strict';
    const E = J.E;
    const P = 'kinetic';
    const DEG = J.DEG, TAU = J.TAU, clamp = J.clamp, lerp = J.lerp;
    const HIDE = Object.freeze({hide: true});
    const reg = (g, key, def) => J.register(g, key, Object.assign(def, {set: 'kinetic'}), P);

    const strip = t => String(t || '').replace(/\s+/g, '');
    const hasLatin = t => /[A-Za-z]/.test(String(t || ''));
    const isBlank = ch => ch === ' ' || ch === '　';
    const bellK = k => Math.sin(Math.PI * clamp(k));
    const rotV = (x, y, a) => {
        const c = Math.cos(a * DEG), s = Math.sin(a * DEG);
        return [x * c - y * s, x * s + y * c];
    };
// 从 0 起振的阻尼摆动，出去再荡回来；t 秒
    const kick = (t, k = 9, f = 26) => (t <= 0 ? 0 : Math.exp(-k * t) * Math.sin(f * t));

    function bounceE(x) {
        const n1 = 7.5625, d1 = 2.75;
        if (x < 1 / d1) return n1 * x * x;
        if (x < 2 / d1) return n1 * (x -= 1.5 / d1) * x + 0.75;
        if (x < 2.5 / d1) return n1 * (x -= 2.25 / d1) * x + 0.9375;
        return n1 * (x -= 2.625 / d1) * x + 0.984375;
    }

// n 个元素里第 k 个的错峰进度，spread 是错峰占掉的时间比例
    const stg = (p, k, n, spread) => clamp((p - (n > 1 ? k / (n - 1) : 0) * spread) / (1 - spread));
    const glyphs = (it, fn) => {
        (it.charFns || (it.charFns = [])).push(fn);
    };
    const mot = env => (env.fx.motion ?? 0.7);
    const beatSince = (env, per) => (env.beat && env.beat.len > 0.15 ? env.beat.since : (((env.ltb ?? env.lt) % per) + per) % per);
    const beatIdx = (env, per) => (env.beat && env.beat.len > 0.15 ? env.beat.index : Math.floor((env.ltb ?? env.lt) / per));

    // item 的词几何：每个字属于哪个词、size 1 下的词框
    const geoCache = new Map();

    function wordsFor(env, text) {
        const lat = hasLatin(text);
        let ws = (env.cut && env.cut.words ? env.cut.words : []).map(strip).filter(Boolean);
        const flatS = strip(text), all = ws.join('');
        let off = ws.length ? all.indexOf(flatS) : -1;
        if (off < 0) {
            ws = (lat ? String(text).split(/\s+/) : (J.chunkText ? J.chunkText(String(text).replace(/\n/g, '')) : [text])).map(strip).filter(Boolean);
            off = 0;
        }
        return {ws, off};
    }

    function wgeo(env, it) {
        const text = String(it.text || '');
        const key = [text, it.font, it.track || 0, it.lead || 0, it.vertical ? 1 : 0, it.align || '', it.sx || 1, it.sy || 1, J.TYPESET ? 1 : 0, (env.cut && env.cut.words || []).join('\u0001')].join('\u0002');
        let G = geoCache.get(key);
        if (G) return G;
        const L1 = J.layoutText(Object.assign({}, it, {size: 1, _lay: null, _m: null}));
        const {ws, off} = wordsFor(env, text);
        const bnd = [];
        let acc = 0;
        ws.forEach(w => {
            acc += [...w].length;
            bnd.push(acc);
        });
        const map = new Array(L1.length).fill(-1), inW = new Array(L1.length).fill(0);
        let k = 0, jmin = 1e9, jmax = -1;
        for (const g of L1) {
            if (isBlank(g.ch)) continue;
            const pos = off + k;
            let j = bnd.findIndex(b => pos < b);
            if (j < 0) j = Math.max(0, bnd.length - 1);
            map[g.i] = j;
            jmin = Math.min(jmin, j);
            jmax = Math.max(jmax, j);
            k++;
        }
        if (jmax < 0) {
            jmin = 0;
            jmax = 0;
        }
        const nW = jmax - jmin + 1, sx = it.sx || 1, sy = it.sy || 1;
        const wb = [];
        for (let j = 0; j < nW; j++) wb.push({x0: 1e9, x1: -1e9, y0: 1e9, y1: -1e9, n: 0});
        for (const g of L1) {
            if (map[g.i] < 0) continue;
            const j = map[g.i] - jmin;
            map[g.i] = j;
            const b = wb[j], x = (g.x + g.vx) * sx, y = (g.y + g.vy) * sy;
            if (!b.n) b.li = g.li;
            inW[g.i] = b.n++;
            b.x0 = Math.min(b.x0, x - g.w * sx / 2);
            b.x1 = Math.max(b.x1, x + g.w * sx / 2);
            b.y0 = Math.min(b.y0, y - g.h * sy / 2);
            b.y1 = Math.max(b.y1, y + g.h * sy / 2);
        }
        wb.forEach(b => {
            if (!b.n) {
                b.x0 = b.x1 = b.y0 = b.y1 = 0;
            }
            b.cx = (b.x0 + b.x1) / 2;
            b.cy = (b.y0 + b.y1) / 2;
        });
        let X0 = 1e9, X1 = -1e9, Y0 = 1e9, Y1 = -1e9;
        wb.forEach(b => {
            if (!b.n) return;
            X0 = Math.min(X0, b.x0);
            X1 = Math.max(X1, b.x1);
            Y0 = Math.min(Y0, b.y0);
            Y1 = Math.max(Y1, b.y1);
        });
        if (X0 > X1) {
            X0 = X1 = Y0 = Y1 = 0;
        }
        G = {
            L1,
            map,
            inW,
            nW,
            wb,
            box: {x0: X0, x1: X1, y0: Y0, y1: Y1, cx: (X0 + X1) / 2, cy: (Y0 + Y1) / 2},
            vert: !!it.vertical
        };
        if (geoCache.size > 300) geoCache.clear();
        geoCache.set(key, G);
        return G;
    }

// 词按文本行分组、按阅读序：[[li, [j…]], …]
    const lineWords = G => {
        if (G._lw) return G._lw;
        const m = new Map();
        for (let j = 0; j < G.nW; j++) {
            const li = G.wb[j].li | 0;
            if (!m.has(li)) m.set(li, []);
            m.get(li).push(j);
        }
        return (G._lw = [...m.entries()]);
    };
// 画出的字相对 size-1 布局的尺寸比；gpos 给出 size-1 坐标点的当前位置
    const kOf = (G, g, it) => {
        const r = G.L1[g.i];
        return r && r.w > 1e-6 ? g.w / r.w : it.size;
    };
    const gpos = (g, it) => [(g.x + g.vx) * (it.sx || 1), (g.y + g.vy) * (it.sy || 1)];

    const WIN = dur => clamp(dur * 0.45, 0.32, 1.0);

    reg('enter', 'knWordSlam', {
        // 词从超大尺寸逐个砸下；已落地的词在每次撞击时震一下
        name: '語ごとスラム', tags: ['pop', 'graphic', 'glitch'], w: 1.1, ae: 'pop', minDur: 0.5, inDur: WIN,
        apply(env, it, p, ctx) {
            const G = wgeo(env, it), n = G.nW, sp = n > 1 ? 0.62 : 0, secs = ctx.inDur * (1 - sp);
            const land = j => stg(p, j, n, sp);
            glyphs(it, (i, g) => {
                const j = G.map[i];
                if (j < 0) return null;
                const q = land(j);
                if (q <= 0) return HIDE;
                const k = kOf(G, g, it), b = G.wb[j], [gx, gy] = gpos(g, it);
                const e = E.outExpo(clamp(q * 1.35)), s = 1 + 2.1 * (1 - e);
                let dy = 0;
                for (let j2 = n - 1; j2 > j; j2--) {
                    const q2 = land(j2);
                    if (q2 >= 0.74) {
                        dy = it.size * 0.09 * kick((q2 - 0.74) * secs, 11, 30);
                        break;
                    }
                }
                dy *= clamp((1 - p) * 10);
                return {
                    s,
                    dx: (gx - b.cx * k) * (s - 1),
                    dy: (gy - b.cy * k) * (s - 1) + dy,
                    a: clamp(q * 7),
                    rot: (1 - e) * (j % 2 ? 7 : -7)
                };
            });
        },
    });

    reg('enter', 'knTypeToSlam', {
        // 每个词先以小尺寸打出来，再弹到全尺寸
        name: '打鍵→拡大', tags: ['pop', 'editorial', 'graphic'], w: 0.9, ae: 'type', minDur: 0.55, inDur: WIN,
        apply(env, it, p) {
            const G = wgeo(env, it), n = G.nW, sp = n > 1 ? 0.55 : 0;
            glyphs(it, (i, g) => {
                const j = G.map[i];
                if (j < 0) return null;
                const q = stg(p, j, n, sp);
                if (q <= 0) return HIDE;
                const b = G.wb[j], cnt = b.n, k = kOf(G, g, it), [gx, gy] = gpos(g, it);
                const tq = 0.55;
                if (q < tq && G.inW[i] >= Math.floor(q / tq * cnt + 1e-6) + 1) return HIDE;
                const e = q < tq ? 0 : E.outBack(clamp((q - tq) / (1 - tq)), 2.2), s = lerp(0.42, 1, e);
                const ax = G.vert ? b.cx * k : b.x0 * k, ay = G.vert ? b.y0 * k : b.cy * k;
                return {s, dx: (gx - ax) * (s - 1), dy: (gy - ay) * (s - 1), a: q < tq ? 0.85 : 1};
            });
        },
    });

    reg('enter', 'knReplaceIn', {
        // 词逐个在中央大字闪现替换，最后全体飞回各自位置
        name: '入れ替わり登場',
        tags: ['pop', 'graphic', 'glitch'],
        w: 0.9,
        ae: 'scramble',
        minDur: 0.7,
        inDur: dur => clamp(dur * 0.5, 0.45, 1.2),
        apply(env, it, p) {
            const G = wgeo(env, it), n = G.nW, T = n > 1 ? 0.7 : 0.2, B = G.box,
                bw = Math.max(1e-3, (G.vert ? B.y1 - B.y0 : B.x1 - B.x0));
            const fly = E.outExpo(clamp((p - T) / (1 - T)));
            glyphs(it, (i, g) => {
                const j = G.map[i];
                if (j < 0) return null;
                const b = G.wb[j], k = kOf(G, g, it), [gx, gy] = gpos(g, it);
                const ww = Math.max(1e-3, G.vert ? b.y1 - b.y0 : b.x1 - b.x0),
                    big = Math.min(2.4, Math.max(1, bw / ww * 0.8));
                const w0 = j / n * T, w1 = (j + 1) / n * T;
                let s, cx, cy, a = 1;
                if (p < T) {
                    if (p < w0 || p >= w1) return HIDE;
                    const q = (p - w0) / (w1 - w0);
                    s = big * (1 + 0.35 * Math.exp(-q * 9));
                    cx = B.cx * k;
                    cy = B.cy * k;
                    a = clamp(q * 8);
                } else {
                    const last = j === n - 1, s0 = last ? big : 0.3;
                    s = lerp(s0, 1, fly);
                    cx = lerp(B.cx, b.cx, fly) * k;
                    cy = lerp(B.cy, b.cy, fly) * k;
                    a = last ? 1 : clamp(fly * 3);
                }
                // 字相对词中心的偏移，缩放后摆到 (cx, cy) 周围
                return {s, dx: cx + (gx - b.cx * k) * s - gx, dy: cy + (gy - b.cy * k) * s - gy, a};
            });
        },
    });

    reg('enter', 'knHingeDrop', {
        // 每个词立着，绕左下角铰链倒下，弹两下放平
        name: '蝶番おろし', tags: ['pop', 'graphic'], w: 0.9, ae: 'drop', minDur: 0.5, inDur: WIN,
        apply(env, it, p) {
            const G = wgeo(env, it), n = G.nW, sp = n > 1 ? 0.5 : 0,
                dir = (J.h(env.cut.seed | 0, it.mi | 0, 61) & 1) ? 1 : -1;
            glyphs(it, (i, g) => {
                const j = G.map[i];
                if (j < 0) return null;
                const q = stg(p, j, n, sp);
                if (q <= 0) return HIDE;
                const b = G.wb[j], k = kOf(G, g, it), [gx, gy] = gpos(g, it);
                const th = -88 * dir * (1 - bounceE(q));
                const px = (dir > 0 ? b.x0 : b.x1) * k, py = b.y1 * k;
                const [rx, ry] = rotV(gx - px, gy - py, th);
                return {dx: px + rx - gx, dy: py + ry - gy, rot: th, a: clamp(q * 6)};
            });
        },
    });

    reg('enter', 'knLoopIn', {
        // 所有字走同一条环轨进位，一节节像列车
        name: 'ループ入り',
        tags: ['pop', 'graphic'],
        w: 0.8,
        ae: 'spin',
        minDur: 0.5,
        inDur: dur => clamp(dur * 0.5, 0.4, 1.1),
        apply(env, it, p) {
            const vert = !!it.vertical, dir = (J.h(env.cut.seed | 0, it.mi | 0, 63) & 1) ? 1 : -1;
            const D = Math.min(env.W, env.H) * 0.55, R = D / TAU * 1.5;
            glyphs(it, (i, g, n) => {
                const q = stg(p, dir > 0 ? i : n - 1 - i, n, 0.5);
                if (q <= 0) return HIDE;
                const v = 1 - E.outCubic(q), ph = TAU * v;
                const a0 = v * D - R * Math.sin(ph), b0 = -R * (1 - Math.cos(ph));
                const ta = D - TAU * R * Math.cos(ph), tb = -TAU * R * Math.sin(ph);
                const ang = (Math.atan2(tb, ta * dir) / DEG) * Math.min(1, v * 4);
                return vert ? {dx: b0, dy: -a0 * dir, rot: ang, a: clamp(q * 6)} : {
                    dx: a0 * dir,
                    dy: b0,
                    rot: ang,
                    a: clamp(q * 6)
                };
            });
        },
    });

    reg('enter', 'knPushIn', {
        // 词逐个赶到行尾，把前面的词顶进位
        name: '押し込み', tags: ['pop', 'editorial', 'graphic'], w: 1, ae: 'type', minDur: 0.45, inDur: WIN,
        apply(env, it, p) {
            const G = wgeo(env, it), n = G.nW, vert = G.vert;
            const f = p * n, k0 = Math.min(n - 1, Math.floor(f)), e = E.outBack(clamp((f - k0) / 0.75), 1.5);
            // 每行单独推：已出现的词贴住该行的最终末端
            const S = new Map();
            for (const [li, js] of lineWords(G)) {
                const end = vert ? G.wb[js[js.length - 1]].y1 : G.wb[js[js.length - 1]].x1,
                    far = j => end - (vert ? G.wb[j].y1 : G.wb[j].x1);
                const vis = js.filter(j => j <= k0);
                if (!vis.length) continue;
                const last = vis[vis.length - 1], prev = vis.length > 1 ? vis[vis.length - 2] : -1;
                let v = far(last);
                if (last === k0) v += prev >= 0 ? (far(prev) - far(last)) * (1 - e) : (end - (vert ? G.wb[last].y0 : G.wb[last].x0)) * 0.6 * (1 - e);
                S.set(li, v);
            }
            glyphs(it, (i, g) => {
                const j = G.map[i];
                if (j < 0) return null;
                if (j > k0) return HIDE;
                const k = kOf(G, g, it), a = j === k0 ? clamp(e * 3) : 1, v = S.get(G.wb[j].li | 0) || 0;
                return vert ? {dy: v * k, a} : {dx: v * k, a};
            });
        },
    });

    reg('enter', 'knInertia', {
        // 整行刹车进场：车头先停，后面的挤上来再弹开
        name: '急ブレーキ',
        tags: ['pop', 'graphic'],
        w: 0.9,
        ae: 'stretch',
        minDur: 0.45,
        inDur: dur => clamp(dur * 0.4, 0.3, 0.8),
        apply(env, it, p) {
            const vert = !!it.vertical, dir = (J.h(env.cut.seed | 0, it.mi | 0, 67) & 1) ? 1 : -1; // 1 = 从左来
            const D = (vert ? env.H : env.W) * 0.6;
            glyphs(it, (i, g, n) => {
                const back = n > 1 ? (dir > 0 ? (n - 1 - i) / (n - 1) : i / (n - 1)) : 0; // 0 = 车头
                const q = clamp((p - back * 0.3) / 0.7);
                const e = E.outBack(q, 2.1), v = (E.outBack(Math.min(1, q + 0.02), 2.1) - e) / 0.02;
                const off = -dir * D * (1 - e), st = 1 + Math.min(0.5, Math.abs(v) * 0.08);
                const sq = q > 0.55 ? 1 - 0.18 * back * bellK((q - 0.55) / 0.45) : 1;
                return vert ? {dy: off, sy: st * sq, sx: 1 / st} : {dx: off, sx: st * sq, sy: 1 / st};
            });
        },
    });

    reg('enter', 'knWordSpin', {
        // 每个词整体刚性旋入，相邻词反向转
        name: '語ごと回転', tags: ['pop', 'graphic'], w: 0.9, ae: 'spin', inDur: WIN,
        apply(env, it, p) {
            const G = wgeo(env, it), n = G.nW, sp = n > 1 ? 0.5 : 0;
            glyphs(it, (i, g) => {
                const j = G.map[i];
                if (j < 0) return null;
                const q = stg(p, j, n, sp);
                if (q <= 0) return HIDE;
                const b = G.wb[j], k = kOf(G, g, it), [gx, gy] = gpos(g, it);
                const e = E.outBack(q, 1.6), th = (1 - E.outCubic(q)) * 200 * (j % 2 ? -1 : 1), s = lerp(0.15, 1, e);
                const [rx, ry] = rotV((gx - b.cx * k) * s, (gy - b.cy * k) * s, th);
                return {dx: b.cx * k + rx - gx, dy: b.cy * k + ry - gy, rot: th, s, a: clamp(q * 5)};
            });
        },
    });

    reg('enter', 'knDiveIn', {
        // 词从镜头背后逐个飞来，又大又虚，落点变实
        name: '手前から語', tags: ['pop', 'emotional', 'graphic'], w: 0.9, ae: 'zoom', inDur: WIN,
        apply(env, it, p) {
            const G = wgeo(env, it), n = G.nW, sp = n > 1 ? 0.55 : 0;
            glyphs(it, (i, g) => {
                const j = G.map[i];
                if (j < 0) return null;
                const q = stg(p, j, n, sp);
                if (q <= 0) return HIDE;
                const b = G.wb[j], k = kOf(G, g, it), [gx, gy] = gpos(g, it);
                const e = E.outCubic(q), s = lerp(5.5, 1, e);
                return {
                    s,
                    dx: (gx - b.cx * k) * (s - 1) + (b.cx - G.box.cx) * k * (s - 1) * 0.6,
                    dy: (gy - b.cy * k) * (s - 1),
                    a: clamp(q * 2.2) * lerp(0.35, 1, e)
                };
            });
        },
    });

    reg('enter', 'knStretchOut', {
        // 每个词从首字母拉出来，像卷尺，到位后绷紧
        name: '伸び出し', tags: ['pop', 'graphic'], w: 0.9, ae: 'stretch', inDur: WIN,
        apply(env, it, p) {
            const G = wgeo(env, it), n = G.nW, sp = n > 1 ? 0.55 : 0;
            glyphs(it, (i, g) => {
                const j = G.map[i];
                if (j < 0) return null;
                const q = stg(p, j, n, sp);
                if (q <= 0) return HIDE;
                const b = G.wb[j], k = kOf(G, g, it), [gx, gy] = gpos(g, it);
                const s = q >= 1 ? 1 : Math.max(0.02, E.outElastic(q));
                return G.vert ? {
                    dy: (gy - b.y0 * k) * (s - 1),
                    sy: s,
                    a: clamp(q * 8)
                } : {dx: (gx - b.x0 * k) * (s - 1), sx: s, a: clamp(q * 8)};
            });
        },
    });

    const WOUT = dur => clamp(dur * 0.32, 0.3, 0.75);

    reg('exit', 'knWordKick', {
        // 词逐个被踢出去，上下交替翻滚
        name: '語ごと蹴り出し', tags: ['pop', 'graphic'], w: 1, ae: 'scatter', outDur: WOUT,
        apply(env, it, p) {
            const G = wgeo(env, it), n = G.nW, sp = n > 1 ? 0.4 : 0, H = env.H;
            glyphs(it, (i, g) => {
                const j = G.map[i];
                if (j < 0) return null;
                const q = stg(p, j, n, sp);
                if (q <= 0) return null;
                if (q >= 1) return HIDE;
                const b = G.wb[j], k = kOf(G, g, it), [gx, gy] = gpos(g, it);
                const up = j % 2 ? 1 : -1, e = E.inCubic(q), th = up * 70 * e;
                const [rx, ry] = rotV(gx - b.cx * k, gy - b.cy * k, th);
                return {
                    dx: b.cx * k + rx - gx + up * it.size * 0.6 * e,
                    dy: b.cy * k + ry - gy + up * H * 0.9 * e - up * it.size * 0.25 * bellK(q * 2),
                    rot: th,
                    a: 1 - E.inQuad(clamp((q - 0.45) / 0.55))
                };
            });
        },
    });

    reg('exit', 'knPushOut', {
        // 整行按词宽逐格推移；经过行首的词淡出
        name: '押し出し退場', tags: ['editorial', 'graphic', 'pop'], w: 0.9, ae: 'wipe', outDur: WOUT,
        apply(env, it, p) {
            const G = wgeo(env, it), vert = G.vert;
            const st = j => (vert ? G.wb[j].y0 : G.wb[j].x0), en = j => (vert ? G.wb[j].y1 : G.wb[j].x1);
            // 每行单独推，步长按本行的词
            const S = new Map(), start = new Map();
            for (const [li, js] of lineWords(G)) {
                const m = js.length, s0 = st(js[0]), f = p * m, k0 = Math.min(m - 1, Math.floor(f)),
                    e = E.outBack(clamp((f - k0) / 0.8), 1.6);
                const to = q => (q >= m ? en(js[m - 1]) - s0 + 0.2 : st(js[q]) - s0);
                S.set(li, lerp(to(k0), to(k0 + 1), e));
                start.set(li, s0);
            }
            glyphs(it, (i, g) => {
                const j = G.map[i];
                if (j < 0) return null;
                const li = G.wb[j].li | 0, sh = S.get(li) || 0, k = kOf(G, g, it), [gx, gy] = gpos(g, it);
                const pos = (vert ? gy / k : gx / k) - sh, over = (start.get(li) || 0) - pos;
                const a = (1 - clamp(over / 0.45)) * (1 - J.smooth(0.72, 1, p));
                if (a <= 0.01) return HIDE;
                return vert ? {dy: -sh * k, a} : {dx: -sh * k, a};
            });
            if (p > 0.97) it.alpha = (it.alpha ?? 1) * (1 - clamp((p - 0.97) / 0.03));
        },
    });

    reg('exit', 'knDiveGlyph', {
        // 镜头扎进一个字：整行绕它放大，其余的字擦身飞过
        name: '一字へ突入',
        tags: ['pop', 'emotional', 'graphic'],
        w: 0.9,
        ae: 'shrink',
        outDur: dur => clamp(dur * 0.3, 0.3, 0.7),
        apply(env, it, p) {
            const lay = J.layoutText(Object.assign({}, it, {
                size: 1,
                _lay: null,
                _m: null
            })).filter(g => !isBlank(g.ch));
            if (!lay.length) return;
            const f = lay.find(g => J.isKanji(g.ch)) || lay[Math.floor(lay.length / 2)];
            const one = lay.length <= 1, fi = f.i, S = Math.exp(Math.pow(p, 1.7) * Math.log(one ? 3 : 34));
            if (one) {
                it.alpha = (it.alpha ?? 1) * (1 - J.smooth(0.3, 1, p));
            } else {
                // 俯冲时选中的字漂到画面中央
                const e = E.inOutCubic(p), fx0 = (f.x + f.vx) * (it.sx || 1) * it.size,
                    fy0 = (f.y + f.vy) * (it.sy || 1) * it.size;
                if (!it.rot) {
                    it.x += (env.W / 2 - (it.x + fx0)) * e;
                    it.y += (env.H / 2 - (it.y + fy0)) * e;
                }
            }
            let fx = null, fy = null;
            glyphs(it, (i, g, n) => {
                if (fx == null) {
                    const k = g.w / Math.max(1e-6, (lay.find(q => q.i === g.i) || f).w);
                    fx = (f.x + f.vx) * (it.sx || 1) * k;
                    fy = (f.y + f.vy) * (it.sy || 1) * k;
                }
                const [gx, gy] = gpos(g, it);
                const a = (i === fi ? 1 - J.smooth(0.6, 1, p) : 1 - J.smooth(0.25, 0.7, p));
                if (a <= 0.01) return HIDE;
                return {s: S, dx: (gx - fx) * (S - 1), dy: (gy - fy) * (S - 1), a};
            });
        },
    });

    reg('exit', 'knLaunch', {
        // 整行弹射离场：车头的字先走，后面像拉长的链子跟上
        name: '急発進', tags: ['pop', 'graphic'], w: 0.9, ae: 'stretch', outDur: dur => clamp(dur * 0.3, 0.3, 0.65),
        apply(env, it, p) {
            const vert = !!it.vertical, dir = (J.h(env.cut.seed | 0, it.mi | 0, 71) & 1) ? 1 : -1,
                D = (vert ? env.H : env.W) * 1.25;
            glyphs(it, (i, g, n) => {
                const back = n > 1 ? (dir > 0 ? (n - 1 - i) / (n - 1) : i / (n - 1)) : 0;
                const q = clamp((p - back * 0.35) / 0.65);
                const antic = -0.06 * bellK(clamp(p / 0.18)); // 出发前先小小回拉蓄力
                const e = E.inCubic(q) + antic, v = 3 * q * q;
                const st = 1 + Math.min(1.2, v * 0.35);
                if (q >= 1) return HIDE;
                return vert ? {dy: dir * D * e, sy: st, sx: 1 / Math.sqrt(st)} : {
                    dx: dir * D * e,
                    sx: st,
                    sy: 1 / Math.sqrt(st)
                };
            });
        },
    });

    reg('exit', 'knWordBlink', {
        // 一次一个词：accent 色冲一下就消失；有节拍就跟节拍
        name: '一語ずつ消灯',
        tags: ['pop', 'glitch', 'graphic'],
        w: 0.9,
        ae: 'cut',
        outDur: dur => clamp(dur * 0.35, 0.3, 0.8),
        apply(env, it, p) {
            const G = wgeo(env, it), n = G.nW, rev = (J.h(env.cut.seed | 0, 73) & 1) === 1, acc = env.sc.accent;
            glyphs(it, (i, g) => {
                const j0 = G.map[i];
                if (j0 < 0) return null;
                const j = rev ? n - 1 - j0 : j0;
                // 单字 item（一字一 item 的版式）按自己的时刻闪没
                const w0 = n > 1 ? j / n : J.r(env.cut.seed | 0, it.mi | 0, 74) * 0.6,
                    w1 = n > 1 ? (j + 1) / n : w0 + 0.4;
                if (p >= w1 || p >= 0.999) return HIDE;
                if (p < w0) return null;
                const q = (p - w0) / (w1 - w0), b = G.wb[j0], k = kOf(G, g, it), [gx, gy] = gpos(g, it),
                    s = 1 + 0.16 * bellK(q * 1.4);
                return {
                    s,
                    dx: (gx - b.cx * k) * (s - 1),
                    dy: (gy - b.cy * k) * (s - 1),
                    color: q > 0.25 ? acc : null,
                    a: q > 0.7 ? 0.35 : 1
                };
            });
        },
    });

    reg('exit', 'knCloseGap', {
        // 词逐个消失，剩下的滑拢重新居中，最后一个弹掉
        name: '詰めて消える',
        tags: ['editorial', 'graphic', 'pop'],
        w: 0.9,
        ae: 'shrink',
        outDur: dur => clamp(dur * 0.35, 0.35, 0.8),
        apply(env, it, p) {
            const G = wgeo(env, it), n = G.nW, vert = G.vert;
            const lo = j => (vert ? G.wb[j].y0 : G.wb[j].x0), hi = j => (vert ? G.wb[j].y1 : G.wb[j].x1);
            // 消失顺序：两端交替、向中间推进
            const ord = [];
            for (let a = 0, b = n - 1; a <= b; a++, b--) {
                ord.push(a);
                if (b !== a) ord.push(b);
            }
            const rank = new Array(n);
            ord.forEach((j, r) => {
                rank[j] = r;
            });
            const c = new Array(n);
            for (let j = 0; j < n; j++) c[j] = E.inOutCubic(clamp(p * n - rank[j]));
            // 沿阅读轴逐行重排
            const ctr = new Array(n).fill(0), byLine = new Map();
            for (let j = 0; j < n; j++) {
                const li = G.wb[j].li | 0;
                if (!byLine.has(li)) byLine.set(li, []);
                byLine.get(li).push(j);
            }
            for (const js of byLine.values()) {
                const m = js.length, wid = js.map(j => hi(j) - lo(j)),
                    gap = js.map((j, q) => (q < m - 1 ? lo(js[q + 1]) - hi(j) : 0));
                const cc = q => (q < m - 1 ? gap[q] * (1 - Math.max(c[js[q]], c[js[q + 1]])) : 0);
                let tot = 0;
                for (let q = 0; q < m; q++) tot += wid[q] * (1 - c[js[q]]) + cc(q);
                let x = (lo(js[0]) + hi(js[m - 1])) / 2 - tot / 2;
                for (let q = 0; q < m; q++) {
                    const w = wid[q] * (1 - c[js[q]]);
                    ctr[js[q]] = x + w / 2;
                    x += w + cc(q);
                }
            }
            glyphs(it, (i, g) => {
                const j = G.map[i];
                if (j < 0) return null;
                if (c[j] >= 0.999) return HIDE;
                const k = kOf(G, g, it), [gx, gy] = gpos(g, it), s = 1 - c[j], wc = (lo(j) + hi(j)) / 2;
                const along = vert ? gy : gx, cross = vert ? gx : gy, cc = vert ? G.wb[j].cx : G.wb[j].cy;
                const na = ctr[j] * k + (along - wc * k) * s, nc = cc * k + (cross - cc * k) * s;
                return vert ? {dx: nc - gx, dy: na - gy, s: Math.max(0.01, s)} : {
                    dx: na - gx,
                    dy: nc - gy,
                    s: Math.max(0.01, s)
                };
            });
        },
    });

    reg('exit', 'knJumpCutOut', {
        // 三次硬跳切（更近、更远、倾斜）后整行消失，没有中间帧
        name: 'ジャンプカット',
        tags: ['pop', 'glitch', 'graphic'],
        w: 0.8,
        ae: 'glitch',
        outDur: dur => clamp(dur * 0.3, 0.3, 0.6),
        apply(env, it, p) {
            const sd = J.h(env.cut.seed | 0, it.mi | 0, 79), side = sd & 1 ? 1 : -1;
            const st = Math.floor(clamp(p) * 4);
            if (st >= 3) {
                it.alpha = 0;
                return;
            }
            const S = [[1.22, 0.05 * side, -0.02, 0], [0.8, -0.07 * side, 0.03, 0], [1.5, 0.02 * side, 0.0, 3 * side]][st];
            it.size *= S[0];
            it.x += S[1] * env.W;
            it.y += S[2] * env.H;
            it.rot = (it.rot || 0) + S[3];
            it._lay = null;
            it._m = null;
        },
    });

    reg('exit', 'knStackAway', {
        // 词逐个跳成一座塔，然后整塔掉出画面
        name: '積んで落とす',
        tags: ['pop', 'graphic'],
        w: 0.8,
        ae: 'fall',
        outDur: dur => clamp(dur * 0.4, 0.45, 0.9),
        minDur: 0.9,
        apply(env, it, p) {
            const G = wgeo(env, it), n = G.nW, T = 0.62, H = env.H;
            const lh = G.vert ? null : (G.box.y1 - G.box.y0);
            glyphs(it, (i, g) => {
                const j = G.map[i];
                if (j < 0) return null;
                const b = G.wb[j], k = kOf(G, g, it), [gx, gy] = gpos(g, it);
                const q = E.inOutCubic(clamp((p * (1 / T) - j / Math.max(1, n) * 0.7) / 0.3 + 0));
                // 塔位：从第一个词起向上堆叠，对齐行中心
                const h = G.vert ? (b.x1 - b.x0) : Math.max(1e-3, b.y1 - b.y0);
                const tx = G.vert ? G.box.cx - (j - (n - 1) / 2) * h * 1.05 : G.box.cx,
                    ty = G.vert ? G.box.cy : G.box.cy - (j - (n - 1) / 2) * h * 1.05;
                const hop = -bellK(q) * (lh || h) * 0.8;
                let dx = (tx - b.cx) * k * q, dy = (ty - b.cy) * k * q + hop * k;
                const fall = clamp((p - T) / (1 - T));
                dy += E.inQuad(fall) * H * 1.2;
                const rot = fall * (j % 2 ? 8 : -8);
                if (fall >= 1) return HIDE;
                return {dx, dy, rot, a: 1 - J.smooth(0.8, 1, fall)};
            });
        },
    });

    reg('hold', 'knWordPulse', {
        // 按拍轮流：一次一个词鼓一下，沿整行循环
        name: '語ごとの拍', tags: ['pop', 'graphic'], w: 1, ae: 'breathe',
        apply(env, it, amt) {
            const G = wgeo(env, it), n = G.nW;
            if (n < 1) return;
            const per = 0.52, s0 = beatSince(env, per), cur = ((beatIdx(env, per) % n) + n) % n;
            const pulse = Math.exp(-s0 * 4) * 0.14 * amt * (0.4 + mot(env));
            if (pulse < 0.002) return;
            glyphs(it, (i, g) => {
                const j = G.map[i];
                if (j !== cur) return null;
                const b = G.wb[j], k = kOf(G, g, it), [gx, gy] = gpos(g, it), s = 1 + pulse;
                return {s, dx: (gx - b.cx * k) * (s - 1), dy: (gy - b.cy * k) * (s - 1)};
            });
        },
    });

    reg('hold', 'knCounterRock', {
        // 相邻词反向摇摆，像咬合的齿轮
        name: '逆回転ゆれ', tags: ['pop', 'calm', 'graphic'], w: 0.9, ae: 'wave',
        apply(env, it, amt) {
            const G = wgeo(env, it), n = G.nW, t = env.ltb ?? env.lt;
            const A = 4 * amt * (0.4 + mot(env)) * Math.sin(t * TAU * 0.55);
            if (Math.abs(A) < 0.05) return;
            glyphs(it, (i, g) => {
                const j = G.map[i];
                if (j < 0) return null;
                const th = A * (j % 2 ? -1 : 1) * (n === 1 ? 0.6 : 1);
                const b = G.wb[j], k = kOf(G, g, it), [gx, gy] = gpos(g, it);
                const [rx, ry] = rotV(gx - b.cx * k, gy - b.cy * k, th);
                return {dx: b.cx * k + rx - gx, dy: b.cy * k + ry - gy, rot: th};
            });
        },
    });

    reg('hold', 'knWordRide', {
        // 一道慢波沿行推进；每个词整体骑在波上，随坡度倾斜
        name: '語の波乗り', tags: ['calm', 'emotional', 'pop'], w: 0.9, ae: 'wave',
        apply(env, it, amt) {
            const G = wgeo(env, it), t = env.ltb ?? env.lt, A = it.size * 0.07 * amt * (0.4 + mot(env));
            if (A < 0.2) return;
            glyphs(it, (i, g) => {
                const j = G.map[i];
                if (j < 0) return null;
                const b = G.wb[j], k = kOf(G, g, it), [gx, gy] = gpos(g, it);
                const ph = t * 3.1 - j * 1.25, off = A * Math.sin(ph), th = Math.cos(ph) * 3.5 * amt;
                const [rx, ry] = rotV(gx - b.cx * k, gy - b.cy * k, th);
                return G.vert ? {
                    dx: b.cx * k + rx - gx + off,
                    dy: b.cy * k + ry - gy,
                    rot: th
                } : {dx: b.cx * k + rx - gx, dy: b.cy * k + ry - gy + off, rot: th};
            });
        },
    });

    reg('hold', 'knTickShift', {
        // 每拍横移一格，像秒针：弹到位、微微过冲、停住
        name: '刻みシフト', tags: ['graphic', 'pop', 'editorial'], w: 0.8, ae: 'jitter',
        apply(env, it, amt) {
            const per = 0.5, s0 = beatSince(env, per), idx = beatIdx(env, per);
            const d = it.size * 0.05 * amt * (0.4 + mot(env));
            if (d < 0.2) return;
            const from = idx % 2 ? 1 : -1, e = E.outBack(clamp(s0 / 0.09), 2.6);
            const x = lerp(-from, from, e) * d;
            if (it.vertical) it.y += x; else it.x += x;
        },
    });

    reg('hold', 'knBeatLean', {
        // 每拍词向前向后交替倾倒，再弹回直立
        name: '拍で傾く', tags: ['pop', 'glitch', 'graphic'], w: 0.8, ae: 'jitter',
        apply(env, it, amt) {
            const G = wgeo(env, it), s0 = beatSince(env, 0.55), idx = beatIdx(env, 0.55);
            const A = 18 * amt * (0.4 + mot(env)) * Math.exp(-s0 * 4.5) * Math.cos(s0 * 15);
            if (Math.abs(A) < 0.1) return;
            glyphs(it, (i) => {
                const j = G.map[i];
                if (j < 0) return null;
                return {skew: A * ((j + idx) % 2 ? 1 : -1)};
            });
        },
    });

    reg('hold', 'knGapBreath', {
        // 词间距一呼一吸，词本身不动
        name: '語間の呼吸', tags: ['calm', 'editorial', 'emotional'], w: 0.9, ae: 'breathe',
        apply(env, it, amt) {
            const G = wgeo(env, it), n = G.nW, t = env.ltb ?? env.lt;
            if (n < 2) return;
            const A = it.size * 0.16 * amt * (0.4 + mot(env)) * (0.5 - 0.5 * Math.cos(t * TAU * 0.4));
            if (A < 0.2) return;
            glyphs(it, (i) => {
                const j = G.map[i];
                if (j < 0) return null;
                const o = (j - (n - 1) / 2) * A;
                return G.vert ? {dy: o} : {dx: o};
            });
        },
    });

})();
