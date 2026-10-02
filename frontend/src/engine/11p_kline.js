(() => {
    'use strict';

    const CANDLE_DURATION = 0.3; // 一根蜡烛多少秒
    let cachedPlanId = null;
    let candleData = null; // { candles, ma5, ma10, maxVol, lo, hi }
    let lyricList = null;  // 滚动字幕行（剔除标题卡与制作名单，按 start 排序）

    // 视口（固定）：范围在 prepareCandles 里按全曲范围一次算好，全程锁定——
    // 不跟踪、不平滑、无逐帧状态，机制上杜绝上下晃动
    let viewLo = 0, viewHi = 100;

    // A股配色约定：红涨绿跌。阳线空心、阴线实心。
    const UP_COLOR = '#ff3b47';     // 涨（音高上行）
    const DOWN_COLOR = '#00e07a';   // 跌（音高下行）
    const FLAT_COLOR = '#c9d3e3';   // 平盘（窗口内无有效音高）
    const BG_COLOR = '#0a0e17';

    function fmtTime(s) {
        if (window.J && window.J.fmtTime) return window.J.fmtTime(s);
        const m = Math.floor(s / 60);
        const sec = Math.floor(s % 60);
        return `${m.toString().padStart(2, '0')}:${sec.toString().padStart(2, '0')}`;
    }

    const midiOf = hz => 69 + 12 * Math.log2(hz / 440);

    // 蜡烛数据：音高驱动 OHLC（高音涨/低音跌），能量当成交量。
    // 数值保持原始域（pitch=MIDI 半音，energy=0..100），纵向范围交给视口自适应。
    // 窗口内全为无声帧（休止/间奏）就是横走的"停牌"蜡烛
    function prepareCandles(plan) {
        const pitch = plan.pitch && plan.pitch.length ? plan.pitch : null;
        const energy = plan.energy && plan.energy.length ? plan.energy : null;
        const rate = pitch ? plan.pitchRate : plan.energyRate;
        if (!rate) return null;
        const nFrames = pitch ? pitch.length : energy.length;
        const win = Math.max(1, Math.round(CANDLE_DURATION * rate));
        const numCandles = Math.max(1, Math.ceil(nFrames / win));

        const eAt = f => (energy && f < energy.length ? energy[f] : 0);

        // 全曲第一个有效音高：开头停牌蜡烛的锚定价（避免假价格台阶）
        let firstMidi = 60;
        if (pitch) {
            for (let f = 0; f < pitch.length; f++) {
                if (pitch[f] > 0) {
                    firstMidi = midiOf(pitch[f]);
                    break;
                }
            }
        }

        const candles = [];
        let lastClose = null;
        for (let i = 0; i < numCandles; i++) {
            const s = i * win, e = Math.min(nFrames, s + win);
            let volume = 0;
            for (let f = s; f < e; f++) volume += eAt(f);
            volume /= (e - s);

            let open = null, close = null, high = -Infinity, low = Infinity;
            if (pitch) {
                const vals = [];
                for (let f = s; f < e; f++) if (pitch[f] > 0) vals.push(midiOf(pitch[f]));
                if (vals.length) {
                    // 稳健聚合：开=前1/3中位、收=后1/3中位、高低=90/10分位。
                    // 直接取 min/max 会被八度误检/滑音毛刺撑成满格柱子，K线形态就毁了
                    const sorted = vals.slice().sort((a, b) => a - b);
                    const q = p => sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * p))];
                    const k = Math.max(1, Math.ceil(vals.length / 3));
                    const head = vals.slice(0, k).sort((a, b) => a - b);
                    const tail = vals.slice(-k).sort((a, b) => a - b);
                    open = head[Math.floor(head.length / 2)];
                    close = tail[Math.floor(tail.length / 2)];
                    high = q(0.9);
                    low = q(0.1);
                }
            } else {
                open = eAt(s) * 100;
                close = eAt(e - 1) * 100;
                for (let f = s; f < e; f++) {
                    const m = eAt(f) * 100;
                    if (m > high) high = m;
                    if (m < low) low = m;
                }
            }

            if (open === null) {                       // 停牌：横盘在最后成交价
                const px = lastClose !== null ? lastClose : firstMidi;
                candles.push({time: i * CANDLE_DURATION, open: px, close: px, high: px, low: px, volume});
                continue;
            }
            lastClose = close;
            candles.push({time: i * CANDLE_DURATION, open, close, high, low, volume});
        }

        let maxVol = 0.001;
        for (const c of candles) if (c.volume > maxVol) maxVol = c.volume;

        // 全曲固定视口：一次算好锁定范围（8% 余量，最小范围兜底），全程不缩放不跟踪
        let glo = Infinity, ghi = -Infinity;
        for (const c of candles) {
            if (c.low < glo) glo = c.low;
            if (c.high > ghi) ghi = c.high;
        }
        if (!Number.isFinite(glo) || !Number.isFinite(ghi)) {
            glo = 0;
            ghi = 100;
        }
        const minRange = pitch ? 16 : 30;
        const pad = Math.max((ghi - glo) * 0.08, minRange * 0.15);
        glo -= pad;
        ghi += pad;
        if (ghi - glo < minRange) {
            const mid = (ghi + glo) / 2;
            glo = mid - minRange / 2;
            ghi = mid + minRange / 2;
        }

        const ma = (n) => candles.map((c, i) => {
            if (i < n - 1) return null;
            let sum = 0;
            for (let j = 0; j < n; j++) sum += candles[i - j].close;
            return sum / n;
        });

        return {candles, ma5: ma(5), ma10: ma(10), maxVol, lo: glo, hi: ghi};
    }

    // 视口锁定：全曲范围一次算好，全程不动（消除一切上下晃动）
    function lockView() {
        viewLo = candleData.lo;
        viewHi = candleData.hi;
    }

    window.J = window.J || {};
    window.J.KLINE_RENDERER = function (ctx, plan, t, opt = {}) {
        const W = plan.W || 1920;
        const H = plan.H || 1080;
        const scale = opt.scale || 1;
        const cw = ctx.canvas.width, ch = ctx.canvas.height;

        ctx.save();
        // 画布清底 + scale 变换：导出/预览画布 ≠ plan 设计尺寸时同样铺满 100% 画面
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.clearRect(0, 0, cw, ch);
        ctx.setTransform(scale, 0, 0, scale, 0, 0);
        if (!opt.transparent) {
            ctx.fillStyle = BG_COLOR;
            ctx.fillRect(0, 0, W, H);
        }

        if (!plan.energy && !plan.pitch) {
            ctx.fillStyle = '#00e676';
            ctx.font = window.J.fontCSS ? window.J.fontCSS('mono', Math.round(H * 0.037)) : '40px Consolas, monospace';
            ctx.textAlign = 'center';
            ctx.textBaseline = 'middle';
            ctx.fillText('NO AUDIO DATA · 载入歌曲后生成K线', W / 2, H / 2);
            ctx.restore();
            return;
        }

        if (cachedPlanId !== plan) {
            candleData = prepareCandles(plan);
            lyricList = (plan.cuts || []).filter(c => c.text);   // 标题/制作名单也按普通行进队列
            cachedPlanId = plan;
        }
        if (!candleData || !candleData.candles.length) {
            ctx.restore();
            return;
        }
        const {candles, ma5, ma10, maxVol} = candleData;

        let beatPulse = 0;
        if (plan.beats && plan.beats.length) {
            for (let i = plan.beats.length - 1; i >= 0; i--) {
                const b = plan.beats[i];
                if (b <= t && t - b < 0.2) {
                    beatPulse = 1.0 - (t - b) / 0.2;
                    break;
                } else if (b < t - 0.2) break;
            }
        }

        const timeH = Math.round(H * 0.034);                      // 底部时间轴
        const volH = Math.round(H * 0.1);                         // 成交量带
        const gap = Math.round(H * 0.008);
        const chartX = Math.round(H * 0.014);
        const chartY = Math.round(H * 0.016);
        const chartW = W - chartX * 2;
        const chartH = H - chartY - volH - gap - timeH - gap;
        const volY = chartY + chartH + gap;

        const visibleCandles = 60;
        const candleWidth = chartW / visibleCandles;
        const candleSpacing = candleWidth * 0.18;
        const candleBodyWidth = Math.max(1, candleWidth - candleSpacing);

        const currentIdxFloat = t / CANDLE_DURATION;
        // 数据尾停靠：蜡烛唱完后视角钉在最后几根上，不再继续左移扫过空白（"视角回退"）
        const endCap = Math.max(0, candles.length - visibleCandles * 0.92);
        const targetScrollIdx = Math.min(currentIdxFloat - visibleCandles * 0.7, endCap);
        const startIdx = Math.max(0, targetScrollIdx);
        const startIdxInt = Math.floor(startIdx);
        const offsetX = (startIdx - startIdxInt) * candleWidth;

        // 视口固定：全曲范围一次算好锁死，蜡烛只横向滚动、纵向永不移动
        lockView();
        const padY = chartH * 0.05;
        const getY = val => chartY + padY + (1 - (val - viewLo) / (viewHi - viewLo)) * (chartH - padY * 2);

        ctx.strokeStyle = `rgba(255,255,255,${0.09 + beatPulse * 0.16})`;
        ctx.lineWidth = 1;
        ctx.beginPath();
        for (let i = 0; i <= 10; i++) {
            const y = chartY + (i / 10) * chartH;
            ctx.moveTo(chartX, y);
            ctx.lineTo(chartX + chartW, y);
        }
        for (let i = 0; i <= visibleCandles + 1; i++) {
            const x = chartX + i * candleWidth - offsetX;
            if (x >= chartX && x <= chartX + chartW) {
                ctx.moveTo(x, chartY);
                ctx.lineTo(x, chartY + chartH);
                ctx.moveTo(x, volY);
                ctx.lineTo(x, volY + volH);
            }
        }
        ctx.stroke();

        ctx.save();
        ctx.beginPath();
        ctx.rect(chartX, chartY, chartW, chartH);
        ctx.clip();

        const ma5Points = [], ma10Points = [];
        const candleColor = c => {
            if (c.close > c.open) return UP_COLOR;      // 高音上收 → 红涨
            if (c.close < c.open) return DOWN_COLOR;    // 低音下收 → 绿跌
            return FLAT_COLOR;
        };

        for (let i = 0; i <= visibleCandles + 1; i++) {
            const idx = startIdxInt + i;
            if (idx >= candles.length) break;
            const c = candles[idx];
            const cx = chartX + i * candleWidth - offsetX;
            const centerX = cx + candleWidth / 2;
            const color = candleColor(c);
            const isBull = c.close > c.open;
            const isFlat = c.close === c.open;

            // 影线
            ctx.strokeStyle = color;
            ctx.lineWidth = Math.max(1, candleWidth * 0.08);
            ctx.beginPath();
            ctx.moveTo(centerX, getY(c.high));
            ctx.lineTo(centerX, getY(c.low));
            ctx.stroke();

            // 实体：阳线空心、阴线实心（A股样式）
            const bodyTop = getY(Math.max(c.open, c.close));
            const bodyH = Math.max(1, Math.abs(getY(c.open) - getY(c.close)));
            ctx.beginPath();
            ctx.rect(cx + candleSpacing / 2, bodyTop, candleBodyWidth, bodyH);
            if (isFlat) {
                ctx.globalAlpha = 0.55;
                ctx.fillStyle = color;
                ctx.fill();
                ctx.globalAlpha = 1;
            } else if (isBull) {
                ctx.fillStyle = BG_COLOR;
                ctx.fill();
                ctx.lineWidth = Math.max(1, candleWidth * 0.09);
                ctx.strokeStyle = color;
                ctx.stroke();
            } else {
                ctx.fillStyle = color;
                ctx.fill();
            }

            if (ma5[idx] !== null) ma5Points.push({x: centerX, y: getY(ma5[idx])});
            if (ma10[idx] !== null) ma10Points.push({x: centerX, y: getY(ma10[idx])});
        }

        const drawMA = (points, color) => {
            if (points.length < 2) return;
            ctx.strokeStyle = color;
            ctx.lineWidth = Math.max(1.5, H * 0.0016);
            ctx.beginPath();
            ctx.moveTo(points[0].x, points[0].y);
            for (let i = 1; i < points.length; i++) ctx.lineTo(points[i].x, points[i].y);
            ctx.stroke();
        };
        drawMA(ma5Points, '#ffd740');
        drawMA(ma10Points, '#00e5ff');

        // 现价中心线：K线围绕它流动
        const curIdx = Math.min(candles.length - 1, Math.max(0, Math.floor(currentIdxFloat)));
        const priceY = getY(candles[curIdx].close);
        ctx.setLineDash([10, 8]);
        ctx.strokeStyle = 'rgba(0,229,255,0.5)';
        ctx.lineWidth = Math.max(1, H * 0.0014);
        ctx.beginPath();
        ctx.moveTo(chartX, priceY);
        ctx.lineTo(chartX + chartW, priceY);
        ctx.stroke();
        ctx.setLineDash([]);
        ctx.restore();

        ctx.save();
        ctx.beginPath();
        ctx.rect(chartX, volY, chartW, volH);
        ctx.clip();
        for (let i = 0; i <= visibleCandles + 1; i++) {
            const idx = startIdxInt + i;
            if (idx >= candles.length) break;
            const c = candles[idx];
            const cx = chartX + i * candleWidth - offsetX;
            const color = candleColor(c);
            const vH = Math.max(1, (c.volume / maxVol) * volH);
            ctx.globalAlpha = 0.55;
            ctx.fillStyle = color;
            ctx.fillRect(cx + candleSpacing / 2, volY + volH - vH, candleBodyWidth, vH);
        }
        ctx.globalAlpha = 1;
        ctx.restore();

        // 时间游标：尾段视角停靠后游标继续向右滑到数据尽头，贴着最后一根蜡烛停住
        const cursorX = Math.min(chartX + chartW - candleWidth * 0.5,
            chartX + (currentIdxFloat - startIdx) * candleWidth);
        if (cursorX >= chartX && cursorX <= chartX + chartW) {
            ctx.strokeStyle = '#00e5ff';
            for (let i = 1; i <= 3; i++) {
                ctx.lineWidth = i * 2 + beatPulse * 4;
                ctx.globalAlpha = 0.2 / i + beatPulse * 0.2;
                ctx.beginPath();
                ctx.moveTo(cursorX, chartY);
                ctx.lineTo(cursorX, volY + volH);
                ctx.stroke();
            }
            ctx.globalAlpha = 1.0;
            ctx.lineWidth = 2;
            ctx.beginPath();
            ctx.moveTo(cursorX, chartY);
            ctx.lineTo(cursorX, volY + volH);
            ctx.stroke();

            ctx.fillStyle = '#00e5ff';
            ctx.font = window.J.fontCSS ? window.J.fontCSS('mono', Math.round(timeH * 0.52)) : '14px Consolas, monospace';
            ctx.textAlign = 'center';
            ctx.textBaseline = 'top';
            ctx.fillText(fmtTime(t), Math.min(chartX + chartW - 40, Math.max(chartX + 40, cursorX)), volY + volH + gap);
        }

        // 歌词：固定间距槽位排列，当前行居中，上/下一格一行
        if (plan.cuts && plan.cuts.length > 0) {
            const centerY = chartY + chartH * 0.5;
            const spacing = Math.round(H * 0.075);    // 固定行距：与歌词的实际时间间隔无关
            const fontPx = Math.round(H * 0.042);
            const lyricX = chartX + chartW / 2;
            const maxW = chartW * 0.92;

            ctx.textAlign = 'center';
            ctx.textBaseline = 'middle';
            ctx.shadowColor = 'rgba(0,0,0,0.78)';
            ctx.shadowBlur = Math.round(H * 0.012);

            // 滚动字幕：固定间距槽位。聚焦行(纯函数) = 已开始的最后一行；
            // 换行时整体在 0.44s 内平滑上移一格（smoothstep），行间垂直距离恒等于 spacing
            let idx = -1;
            for (let i = 0; i < lyricList.length; i++) if (t >= lyricList[i].start) idx = i;
            let curF = idx;
            const next = idx + 1 < lyricList.length ? lyricList[idx + 1] : (idx < 0 ? lyricList[0] : null);
            if (next && t > next.start - 0.22) {
                const k = Math.max(0, Math.min(1, (t - (next.start - 0.22)) / 0.44));
                curF = idx + k * k * (3 - 2 * k);
            }

            for (let i = 0; i < lyricList.length; i++) {
                const d = i - curF;
                const ad = Math.abs(d);
                if (ad > 3.2) continue;                       // 一屏最多上下各三行
                const alpha = ad <= 0.5 ? 1 : Math.max(0, 0.45 - (ad - 1) * 0.15);
                if (alpha <= 0.015) continue;
                const y = centerY + d * spacing;
                if (y < chartY || y > chartY + chartH) continue;
                const c = lyricList[i];
                ctx.globalAlpha = alpha;
                ctx.fillStyle = ad <= 0.5 ? '#ffffff' : '#dfe6f0';
                let fp = fontPx;
                ctx.font = window.J.fontCSS ? window.J.fontCSS('mono', fp) : `bold ${fp}px Consolas, monospace`;
                const w = ctx.measureText(c.text).width;
                if (w > maxW) {                               // 超宽行缩字号，不折行
                    fp = Math.max(Math.round(fontPx * 0.5), Math.floor(fontPx * maxW / w));
                    ctx.font = window.J.fontCSS ? window.J.fontCSS('mono', fp) : `bold ${fp}px Consolas, monospace`;
                }
                ctx.fillText(c.text, lyricX, y);
            }
            ctx.globalAlpha = 1;
            ctx.shadowBlur = 0;
            ctx.shadowColor = 'rgba(0,0,0,0)';
        }

        ctx.restore();
    };

    if (window.J && window.J.STYLES) {
        window.J.STYLES.kline = {
            name: 'K线·音高', desc: '音高K线——高音涨低音跌，红涨绿跌，能量当成交量',
            schemes: [
                {
                    bg: '#0a0e17',
                    fg: '#e0e6f0',
                    sub: '#4a5568',
                    accent: '#ff3b47',
                    accent2: '#00e07a',
                    ink: '#e0e6f0',
                    dim: '#151c2a',
                    ghostA: '#ff3b47',
                    ghostB: '#00e07a'
                },
            ],
            fonts: {display: ['mono'], serif: ['mono'], body: ['mono'], mono: ['mono']},
            texture: {grain: 0.15, paper: 0, scan: 0.3}, ghost: 0,
            bias: {},
            decor: {}, hud: false,
            kline: true,  // 09_render.js 画满K线背景，歌词走正常随机特效管线
        };
        if (window.J.STYLE_ORDER && !window.J.STYLE_ORDER.includes('kline')) {
            window.J.STYLE_ORDER.push('kline');
        }
    }

})();
