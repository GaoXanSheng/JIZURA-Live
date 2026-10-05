// 追加音频表现包：山脊线（能量历史）、随音高游移（hold）、ASCII 化（fx）、
// 心电图（decor）、换台（trans）。全部 extra:true —— 带「新增」徽章，
// 受「使用新增特效」开关管控，不碰任何既有手法的定义。
(() => {
    'use strict';
    const E = J.E, clamp = J.clamp;
    const PK = 'audioFx';
    const DEG = J.DEG;

    const U = env => Math.min(env.W, env.H) / 1080;
    const isDark = c => J.lum(c) < 0.45;
    const layC = (sc, k) => J.mix(sc.bg, sc.fg, k);
    const bs = rng => rng.int(1, 1e9);
    const monoF = env => (env.st.fonts.mono && env.st.fonts.mono[0]) || 'mono';
    const fmtTime = t => `${String(Math.floor(t / 60)).padStart(2, '0')}:${String(Math.floor(t % 60)).padStart(2, '0')}`;

    // ---------------- bg：音频山脊线 ----------------
    // 能量历史画成 Joy Division 式层叠山脊：前排=现在，后排=过去，整片随歌流动。
    // 无能量数据时退化为慢速噪声山脊（当静态背景用）。
    const bgReg = (k, d) => J.register('bg', k, Object.assign({}, d, {
        draw(env, P) {
            const ctx = env.ctx;
            ctx.save();
            try {
                d.draw(env, P || {}, ctx);
            } finally {
                ctx.restore();
            }
        }
    }), PK);

    bgReg('ridgeAudio', {
        name: 'オーディオ稜線', tags: ['calm', 'emotional', 'editorial'], w: 1.1, extra: true,
        plan: rng => ({
            seed: bs(rng),
            n: rng.int(9, 13),
            win: rng.range(2.4, 3.4),
            amp: rng.range(0.16, 0.22),
            mode: rng.pick(['wide', 'wide', 'center'])
        }),
        draw(env, P, ctx) {
            const {W, H, sc, plan} = env, t = env.t, n = P.n || 11, sd = P.seed || 1;
            const e = E.outCubic(clamp(env.lt / 0.9));
            if (e <= 0.003) return;
            const energy = plan && plan.energy, rate = plan && plan.energyRate;
            const eAt = ts => {
                if (!energy || !rate || !energy.length) return null;
                const f = Math.round(ts * rate);
                return f >= 0 && f < energy.length ? energy[f] : null;
            };
            const hasAudio = eAt(0) !== null || eAt(5) !== null;
            const val = (ts, r) => {
                const v = eAt(ts);
                if (v != null) return clamp(v);
                return Math.pow(0.5 + 0.5 * J.noise1(ts * 0.7 + r * 0.8, sd + r * 13, 3), 1.7) * 0.5;
            };
            const beat = env.beat;
            const beatPulse = beat ? Math.exp(-beat.since * 5.5) : 0;

            const x0 = W * 0.05, x1 = W * 0.95;
            const y0 = H * 0.14, y1 = H * 0.9;
            const gap = (y1 - y0) / (n - 1);
            const m = 96;
            const amp = H * (P.amp || 0.19);
            const step = (P.win || 2.8) / n;   // 每行时间错后：前排=现在，后排=过去
            const fillC = J.rgba(sc.bg, 0.88);
            ctx.lineJoin = 'round';
            for (let r = 0; r < n; r++) {   // 后排（上）先画，前排（下）后画，靠填充互相遮挡
                const base = y0 + r * gap;
                const tEnd = t - (n - 1 - r) * step;
                const front = r === n - 1;
                const grow = E.outCubic(clamp(e * 1.5 - r / n * 0.5));
                if (grow <= 0.003) continue;
                const lc = front ? sc.accent : layC(sc, 0.5 + 0.25 * r / n);
                const pts = [];
                for (let j = 0; j <= m; j++) {
                    const uu = j / m, x = x0 + (x1 - x0) * uu;
                    const envp = P.mode === 'wide'
                        ? 0.3 + 0.7 * Math.pow(Math.sin(Math.PI * uu), 1.4)
                        : 0.12 + 0.88 * Math.exp(-Math.pow((uu - 0.5) / 0.26, 2));
                    const ts = tEnd - (1 - uu) * (P.win || 2.8);
                    const v = hasAudio
                        ? Math.pow(val(ts, r), 1.25)
                        : val(ts, r);
                    pts.push([x, base - amp * envp * v * 2.1 * grow]);
                }
                ctx.fillStyle = fillC;
                ctx.beginPath();
                ctx.moveTo(x0, base + 1);
                for (const p of pts) ctx.lineTo(p[0], p[1]);
                ctx.lineTo(x1, base + 1);
                ctx.closePath();
                ctx.fill();
                ctx.strokeStyle = lc;
                ctx.globalAlpha = (front ? 0.95 : 0.8) * e;
                ctx.lineWidth = Math.max(1, U(env) * (front ? 0.0038 + beatPulse * 0.003 : 0.0028));
                ctx.beginPath();
                pts.forEach((p, q) => (q ? ctx.lineTo(p[0], p[1]) : ctx.moveTo(p[0], p[1])));
                ctx.stroke();
                ctx.globalAlpha = 1;
            }
        }
    });

    // ---------------- hold：随音高游移 ----------------
    // 整行随实时音高相对近期均值上下浮移——歌词"跟着旋律唱"。
    // 无音高数据时退化为慢速漂移，保持有生命感。
    const isHex = c => typeof c === 'string' && /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.test(c);
    const HIDE = Object.freeze({hide: true});

    function merged(fns) {
        return (i, g, n) => {
            let o = null;
            for (let k = 0; k < fns.length; k++) {
                const r = fns[k](i, g, n);
                if (!r) continue;
                if (r.hide) return HIDE;
                if (!o) o = {dx: 0, dy: 0, rot: 0, s: 1, a: 1};
                if (r.dx) o.dx += r.dx;
                if (r.dy) o.dy += r.dy;
                if (r.rot) o.rot += r.rot;
                if (r.s != null) o.s *= r.s;
                if (r.a != null) o.a *= r.a;
                if (r.sx != null) o.sx = (o.sx == null ? 1 : o.sx) * r.sx;
                if (r.sy != null) o.sy = (o.sy == null ? 1 : o.sy) * r.sy;
                if (r.blur) o.blur = (o.blur || 0) + r.blur;
                if (r.color) o.color = r.color;
            }
            if (!o) return null;
            if (o.a <= 0.003 || Math.abs(o.s) < 0.004) return HIDE;
            return o;
        };
    }

    function addC(it, fn) {
        if (!it.charFns) it.charFns = [];
        const prev = it.charFns.splice(0);
        prev.push(fn);
        it.charFns.push(merged(prev));
    }

    const motionK = env => clamp((env.fx && env.fx.motion != null ? env.fx.motion : 0.7) / 0.7, 0, 1.6);
    const midiOf = hz => 69 + 12 * Math.log2(hz / 440);

    const H = {};
    H.pitchDrift = {
        name: '音程で揺れる', tags: ['calm', 'emotional'], w: 0.9, extra: true,
        apply(env, it, amt) {
            const plan = env.plan;
            const pitch = plan && plan.pitch, rate = plan && plan.pitchRate;
            const k = amt * motionK(env);
            if (k < 0.01) return;
            const sz = it.size, sy0 = it.sy || 1;
            let d = 0;
            if (pitch && pitch.length && rate) {
                const tAbs = (env.cut ? env.cut.start : 0) + env.ltb;
                const med = win => {
                    const vals = [];
                    const f0 = Math.max(0, Math.floor((tAbs - win) * rate));
                    const f1 = Math.min(pitch.length, Math.ceil((tAbs + win) * rate));
                    for (let f = f0; f < f1; f++) if (pitch[f] > 0) vals.push(midiOf(pitch[f]));
                    if (!vals.length) return null;
                    vals.sort((a, b) => a - b);
                    return vals[vals.length >> 1];
                };
                const now = med(0.12), near = med(1.6);
                if (now != null && near != null) d = clamp((now - near) / 9, -1, 1);
                else if (now != null) d = clamp((now - 64) / 14, -0.6, 0.6);
            } else {
                d = 0.45 * J.noise1(env.ltb * 0.6, (env.cut && env.cut.seed | 0) || 1);
            }
            addC(it, () => ({dy: -d * sz * sy0 * 0.42 * k, rot: d * 1.5 * k}));
        },
    };
    for (const k of Object.keys(H)) J.register('hold', k, H[k], PK);

    // ---------------- fx：ASCII 化 ----------------
    // 全屏后期：把已渲好的整帧转成 ASCII 字符网格，事件两端自淡入淡出。
    // 复用 scratch 预拷帧（S），不改任何既有 fx。
    const RAMP = " .'`^\",:;Il!i><~+_-?][}{1)(|\\/tfjrxnuvczXYUJCLQ0OZmwqpdbkhao*#MW&8%B@$";
    const BOOST = 1.4, LUM_SKIP = 8;
    J.register('fx', 'ascii', {
        name: 'ASCII化', tags: ['glitch', 'graphic'], w: 0.7, extra: true,
        dur: 3.2, amp: 1, scratch: true,
        draw(ctx, ev, k, I) {
            const {cw, ch, S} = I;
            if (!S) return;
            const e = Math.min(1, k / 0.12, (1 - k) / 0.12);
            if (e <= 0.01) return;
            const monoKey = I.st && I.st.fonts && I.st.fonts.mono && I.st.fonts.mono[0] || 'mono';
            const fontPx = Math.max(8, Math.round(ch * 0.021));
            const cellH = Math.round(fontPx * 1.08);
            ctx.save();
            ctx.font = J.fontCSS ? J.fontCSS(monoKey, fontPx) : `${fontPx}px Consolas, monospace`;
            ctx.textAlign = 'center';
            ctx.textBaseline = 'middle';
            const cellW = Math.max(4, ctx.measureText('M').width);
            const cols = Math.max(8, Math.floor(cw / cellW)), rows = Math.max(6, Math.floor(ch / cellH));
            const small = I.tmp(cols, rows);
            const sctx = small.getContext('2d');
            sctx.imageSmoothingEnabled = true;
            sctx.imageSmoothingQuality = 'high';
            sctx.clearRect(0, 0, cols, rows);
            sctx.drawImage(S, 0, 0, cols, rows);
            let data;
            try {
                data = sctx.getImageData(0, 0, cols, rows).data;
            } catch (err) {
                ctx.restore();
                return;
            }
            ctx.setTransform(1, 0, 0, 1, 0, 0);
            ctx.clearRect(0, 0, cw, ch);
            const n = RAMP.length;
            let lastFill = '';
            for (let r = 0; r < rows; r++) {
                const y = (r + 0.5) * cellH;
                for (let c = 0; c < cols; c++) {
                    const i = (r * cols + c) * 4;
                    const R = data[i], G = data[i + 1], B = data[i + 2];
                    const lum = 0.2126 * R + 0.7152 * G + 0.0722 * B;
                    const idx = Math.round(lum / 255 * (n - 1));
                    if (idx <= 0 || lum < LUM_SKIP) continue;
                    const fill = 'rgb(' + Math.min(255, Math.round(R * BOOST)) + ',' +
                        Math.min(255, Math.round(G * BOOST)) + ',' + Math.min(255, Math.round(B * BOOST)) + ')';
                    if (fill !== lastFill) {
                        ctx.fillStyle = fill;
                        lastFill = fill;
                    }
                    ctx.fillText(RAMP[idx], (c + 0.5) * cellW, y);
                }
            }
            if (e < 0.995) {   // 边缘与原画面交叉淡化
                ctx.globalAlpha = 1 - e;
                ctx.drawImage(S, 0, 0);
                ctx.globalAlpha = 1;
            }
            ctx.restore();
        },
    }, PK);

    // ---------------- decor：心电图 ----------------
    // 底部一条扫过的心电折线：平线走带、节拍处爆双相尖峰，右侧 BPM 读数。
    // 无节拍数据时改为能量包络小波。
    J.register('decor', 'ecg', {
        name: '心電図', tags: ['graphic', 'glitch', 'pop'], w: 0.8, layer: 'front', extra: true,
        draw(env, bb0, P) {
            if (env.pass !== 'main') return;
            const {W, H, sc, plan} = env, u = U(env);
            const o = 1 - E.inCubic(clamp(env.pOut));
            const inA = E.outExpo(clamp(env.lt / 0.5));
            const a = o * inA;
            if (a <= 0.005 || env.lt < 0) return;
            const ctx = env.ctx;
            const beats = plan && plan.beats && plan.beats.length ? plan.beats : null;
            const energy = plan && plan.energy, erate = plan && plan.energyRate;
            const x0 = W * 0.08, x1 = W * 0.92, y = H * (P && P.low ? 0.88 : 0.84);
            const win = 3.4, m = 150, amp = H * 0.055;
            const seed = (env.cut && env.cut.seed | 0) || 1;

            // ts 时刻的脉冲高度：节拍双相尖峰（无节拍→能量包络）
            const spike = ts => {
                if (beats) {
                    let lo = 0, hi = beats.length - 1, bi = -1;
                    while (lo <= hi) {
                        const mid = (lo + hi) >> 1;
                        if (beats[mid] <= ts) {
                            bi = mid;
                            lo = mid + 1;
                        } else hi = mid - 1;
                    }
                    let s = 0;
                    for (let b = Math.max(0, bi - 1); b <= Math.min(beats.length - 1, bi + 1); b++) {
                        const dt = ts - beats[b];
                        if (dt >= -0.035 && dt < 0) s = Math.max(s, dt / 0.035);
                        else if (dt >= 0 && dt < 0.055) s = Math.max(s, 1 - (dt / 0.055) * 1.55);
                    }
                    return s;
                }
                if (energy && erate) {
                    const f = Math.round(ts * erate);
                    return f >= 0 && f < energy.length ? clamp(energy[f] * 1.4 - 0.35) * 0.8 : 0;
                }
                return 0.25 + 0.25 * J.noise1(ts * 3, seed);
            };

            // 底网格：基线 + 时间刻度
            ctx.strokeStyle = J.rgba(sc.fg, 0.14 * a);
            ctx.lineWidth = Math.max(1, u);
            ctx.beginPath();
            ctx.moveTo(x0, y);
            ctx.lineTo(x1, y);
            for (let i = 0; i <= 8; i++) {
                const gx = x0 + (x1 - x0) * i / 8;
                ctx.moveTo(gx, y - 4 * u);
                ctx.lineTo(gx, y + 4 * u);
            }
            ctx.stroke();

            // 心电折线：左=过去，右=现在；线尾亮点
            const pts = [];
            for (let j = 0; j <= m; j++) {
                const uu = j / m;
                const ts = env.t - (1 - uu) * win;
                if (ts < 0) continue;
                const noise = 0.06 * J.noise1(ts * 26, seed);
                pts.push([x0 + (x1 - x0) * uu, y - amp * (spike(ts) + noise)]);
            }
            if (pts.length < 2) return;
            const glow = J.rgba(isDark(sc.bg) ? sc.accent : layC(sc, 0.35), 0.35 * a);
            for (const [col, lw] of [[glow, 5 * u], [sc.accent, Math.max(1.2, 1.8 * u)]]) {
                ctx.strokeStyle = col;
                ctx.lineWidth = lw;
                ctx.lineJoin = 'round';
                ctx.beginPath();
                pts.forEach((p, q) => (q ? ctx.lineTo(p[0], p[1]) : ctx.moveTo(p[0], p[1])));
                ctx.stroke();
            }
            const last = pts[pts.length - 1];
            ctx.fillStyle = sc.accent;
            ctx.globalAlpha = a;
            ctx.beginPath();
            ctx.arc(last[0], last[1], 3 * u, 0, DEG * 360);
            ctx.fill();
            ctx.globalAlpha = 1;

            // BPM 读数
            const beat = env.beat;
            if (beat && beat.len > 0.2) {
                ctx.fillStyle = sc.sub;
                ctx.font = J.fontCSS ? J.fontCSS(monoF(env), Math.round(15 * u)) : `${Math.round(15 * u)}px Consolas, monospace`;
                ctx.textAlign = 'right';
                ctx.textBaseline = 'alphabetic';
                ctx.globalAlpha = a * 0.9;
                ctx.fillText(Math.round(60 / beat.len) + ' BPM', x1, y - amp - 10 * u);
                ctx.globalAlpha = 1;
            }
        },
    }, PK);

    // ---------------- trans：换台 ----------------
    // 电视换台：垂直同步滚动 + 撕裂条 + 中段雪花爆窗。
    const trReg = (k, d) => J.register('trans', k, Object.assign({}, d, {
        draw(ctx, A, B, p, I) {
            ctx.save();
            try {
                if (!(p > 0)) ctx.drawImage(A, 0, 0);
                else if (p >= 1) ctx.drawImage(B, 0, 0);
                else d.draw(ctx, A, B, p, I, I.P || {});
            } finally {
                ctx.restore();
            }
        }
    }), PK);

    trReg('channelSwitch', {
        name: 'チャンネル切替', tags: ['glitch'], w: 0.9, dur: 0.34, extra: true,
        plan: rng => ({seed: bs(rng)}),
        draw(ctx, A, B, p, I, P) {
            const {cw, ch} = I, sd = (I.seed | 0) ^ (P.seed || 1), step = I.step | 0;
            const src = p < 0.5 ? A : B;
            const kp = p < 0.5 ? p * 2 : (p - 0.5) * 2;   // 前半滚走 A，后半滚入 B
            // 垂直同步滚动（模 wrap 两段贴回）
            const roll = Math.floor(((kp * 1.3) % 1) * ch);
            ctx.drawImage(src, 0, 0, cw, ch, 0, roll - ch, cw, ch);
            ctx.drawImage(src, 0, 0, cw, ch, 0, roll, cw, ch);
            // 撕裂横条：随机几条横向错位
            for (let i = 0; i < 4; i++) {
                const yy = ch * J.r(sd, step, i, 1);
                const hh = ch * J.rr(0.02, 0.08, sd, i, 2);
                const dx = J.rs(sd, step, i, 3) * cw * 0.08 * (0.4 + kp);
                if (Math.abs(dx) > 1) ctx.drawImage(src, 0, yy, cw, hh, dx, yy, cw, hh);
            }
            // 中段雪花爆窗
            const burst = p > 0.3 && p < 0.7 ? 1 - Math.abs(p - 0.5) / 0.2 : 0;
            if (burst > 0.03) {
                const w = cw * 0.62 * burst, h = ch * 0.5 * burst;
                const bx = (cw - w) / 2, by = (ch - h) / 2;
                ctx.save();
                ctx.beginPath();
                ctx.rect(bx, by, w, h);
                ctx.clip();
                ctx.fillStyle = '#080808';
                ctx.fillRect(bx, by, w, h);
                const nS = 420;
                for (let i = 0; i < nS; i++) {
                    const v = Math.floor(40 + J.r(sd, step, i, 4) * 215);
                    ctx.fillStyle = `rgb(${v},${v},${v})`;
                    const s = 3 + J.r(sd, step, i, 5) * 6;
                    ctx.fillRect(bx + J.r(sd, step, i, 6) * (w - s), by + J.r(sd, step, i, 7) * (h - s), s, s);
                }
                ctx.restore();
            }
            // 滚动亮带（垂直同步前沿）+ 轻噪横纹
            const bandY = roll;
            ctx.fillStyle = `rgba(255,255,255,${0.14 + burst * 0.2})`;
            ctx.fillRect(0, bandY - ch * 0.012, cw, ch * 0.024);
            ctx.fillStyle = `rgba(0,0,0,${0.06 * kp})`;
            for (let yy = (step * 7) % 6; yy < ch; yy += 6) ctx.fillRect(0, yy, cw, 1);
        },
    }, PK);

})();
