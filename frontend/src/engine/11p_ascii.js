(() => {
    'use strict';

    // ASCII 字符画滤镜：整帧画面先走正常渲染管线画进离屏画布，
    // 再降采样成字符网格——亮度映射字符密度阶梯，颜色取单元格均色并提亮补偿。
    // 歌词、背景、特效全部变成 ASCII，是真正的「画面转字符画」后处理。
    // 画布获取统一走 document.createElement（导出 Worker 的 shim 会换成 OffscreenCanvas）。
    const RAMP = " .'`^\",:;Il!i><~+_-?][}{1)(|\\/tfjrxnuvczXYUJCLQ0OZmwqpdbkhao*#MW&8%B@$";
    const BOOST = 1.4;    // 字符只盖住单元格的一小部分面积，整体提亮补偿
    const LUM_SKIP = 8;   // 亮度低于此值画空格（暗背景→留黑/留透明）

    let buf = null;       // 复用：全分辨率离屏画布（正常管线的渲染目标）
    let small = null;     // 复用：cols×rows 采样画布（读像素用）
    let smallCtx = null;

    function ensure(W, H, cols, rows) {
        if (!buf || buf.width !== W || buf.height !== H) {
            buf = document.createElement('canvas');
            buf.width = W;
            buf.height = H;
        }
        if (!small || small.width !== cols || small.height !== rows) {
            small = document.createElement('canvas');
            small.width = cols;
            small.height = rows;
            smallCtx = small.getContext('2d', {willReadFrequently: true});
        }
    }

    window.J = window.J || {};

    // renderInner(targetCtx, opt)：由 09_render 回调，把正常管线的一帧画进 targetCtx
    window.J.asciiFrame = function (ctx, plan, t, opt, renderInner) {
        const W = plan.W || 1920;
        const H = plan.H || 1080;
        const scale = opt.scale || 1;
        const cw = ctx.canvas.width, ch = ctx.canvas.height;

        // 字符网格：等宽字体决定密度
        const fontPx = Math.max(8, Math.round(H * 0.023));
        const cellH = Math.round(fontPx * 1.08);
        ctx.save();
        ctx.setTransform(1, 0, 0, 1, 0, 0);
        ctx.clearRect(0, 0, cw, ch);
        ctx.setTransform(scale, 0, 0, scale, 0, 0);
        ctx.font = window.J.fontCSS ? window.J.fontCSS('mono', fontPx) : `${fontPx}px Consolas, monospace`;
        ctx.textAlign = 'center';
        ctx.textBaseline = 'middle';
        const cellW = Math.max(4, ctx.measureText('M').width);
        const cols = Math.max(8, Math.floor(W / cellW));
        const rows = Math.max(6, Math.floor(H / cellH));

        ensure(W, H, cols, rows);

        // 1) 正常管线渲整帧进离屏（强制不透明、无分层，保证转换吃到完整画面）
        const bctx = buf.getContext('2d');
        bctx.setTransform(1, 0, 0, 1, 0, 0);
        bctx.clearRect(0, 0, W, H);
        renderInner(bctx, Object.assign({}, opt, {asciiInner: 1, scale: 1, transparent: false, layer: null}));

        // 2) 降采样到 cols×rows 再读像素（tiny getImageData，逐像素读全帧会拖垮帧率）
        smallCtx.imageSmoothingEnabled = true;
        smallCtx.imageSmoothingQuality = 'high';
        smallCtx.clearRect(0, 0, cols, rows);
        smallCtx.drawImage(buf, 0, 0, cols, rows);
        const data = smallCtx.getImageData(0, 0, cols, rows).data;

        // 3) 逐格绘制：亮度→字符，颜色→单元格均色（提亮后取整）
        const n = RAMP.length;
        let lastFill = '';
        for (let r = 0; r < rows; r++) {
            const y = (r + 0.5) * cellH;
            for (let c = 0; c < cols; c++) {
                const i = (r * cols + c) * 4;
                const R = data[i], G = data[i + 1], B = data[i + 2];
                const lum = 0.2126 * R + 0.7152 * G + 0.0722 * B;
                const idx = Math.round(lum / 255 * (n - 1));
                if (idx <= 0 || lum < LUM_SKIP) continue;   // 暗部留空：背景不糊字符
                const fill = 'rgb(' + Math.min(255, Math.round(R * BOOST)) + ',' +
                    Math.min(255, Math.round(G * BOOST)) + ',' +
                    Math.min(255, Math.round(B * BOOST)) + ')';
                if (fill !== lastFill) {
                    ctx.fillStyle = fill;
                    lastFill = fill;
                }
                ctx.fillText(RAMP[idx], (c + 0.5) * cellW, y);
            }
        }
        ctx.restore();
    };

    if (window.J && window.J.STYLES) {
        window.J.STYLES.ascii = {
            name: 'ASCII字符画', desc: '整个画面实时转成ASCII——亮度定字符密度，色彩保留',
            schemes: [
                {
                    bg: '#040704',
                    fg: '#d8ffe0',
                    sub: '#2e8f4a',
                    accent: '#33ff66',
                    accent2: '#d8ffe0',
                    ink: '#d8ffe0',
                    dim: '#0a1a0e',
                    ghostA: '#33ff66',
                    ghostB: '#d8ffe0'
                },
            ],
            fonts: {display: ['mono'], serif: ['mono'], body: ['mono'], mono: ['mono']},
            texture: {grain: 0.15, paper: 0, scan: 0}, ghost: 0,
            bias: {},
            decor: {}, hud: false,
            ascii: true,  // 09_render.js 整帧先走正常管线再经 asciiFrame 转成字符画
        };
        if (window.J.STYLE_ORDER && !window.J.STYLE_ORDER.includes('ascii')) {
            window.J.STYLE_ORDER.push('ascii');
        }
    }

})();
