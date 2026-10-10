// 冒烟测试：stub window/canvas，验证 11p_ascii.js（ASCII 字符画滤镜）无运行时错误、注册顺序正确
globalThis.window = globalThis;

// --- stub J 引擎工具 ---
window.J = {
    fontCSS: (key, px) => `${px}px "Test Mono", monospace`,
    STYLES: {},
    STYLE_ORDER: ['noir'],
};

// --- stub canvas：getContext 返回可注入像素的 2D 桩 ---
function makeCtx(cv) {
    return {
        canvas: cv,
        font: '', textAlign: '', textBaseline: '',
        fillStyle: '',
        fills: [],                       // 收集 fillText 调用
        innerCalls: [],                  // 收集 drawImage 的源
        pixels: null,                    // getImageData 返回的数据（测试可注入）
        measureText(s) { return {width: 12}; },
        clearRect() {}, setTransform() {}, save() {}, restore() {},
        drawImage(src) { this.innerCalls.push(src); },
        fillText(ch, x, y) { this.fills.push({ch: String(ch), x, y, style: this.fillStyle}); },
        getImageData(x, y, w, h) {
            return {data: this.pixels || new Uint8ClampedArray(w * h * 4)};
        },
        getContext(type) { return this; },
    };
}
const contexts = [];
globalThis.document = {
    createElement(tag) {
        const cv = {width: 300, height: 150, _ctx: null};
        cv.getContext = () => (cv._ctx || (cv._ctx = makeCtx(cv), contexts.push(cv._ctx), cv._ctx));
        return cv;
    },
};

require('../../frontend/src/engine/11p_ascii.js');

const assert = require('assert');
assert(window.J.asciiFrame, 'asciiFrame 未注册');
assert(window.J.STYLES.ascii, 'STYLES.ascii 未注册');
assert(window.J.STYLE_ORDER.indexOf('ascii') > window.J.STYLE_ORDER.indexOf('noir'), 'ascii 必须追加在已有风格之后');
console.log('STYLE_ORDER =', window.J.STYLE_ORDER.join(', '));

const plan = {W: 1920, H: 1080, fps: 30, fx: {}, events: [], cuts: []};

// 1) 滤镜走通：renderInner 恰好被调一次，带 asciiInner 防递归标记 + scale 1
let innerOpts = null;
const targetCtx = makeCtx({width: 1920, height: 1080});
window.J.asciiFrame(targetCtx, plan, 1.5, {scale: 1}, (ctx, o) => {
    innerOpts = o;
    assert.strictEqual(ctx.canvas.width, 1920, '离屏画布应等于 plan 设计宽');
});
assert(innerOpts, 'renderInner 未被回调');
assert.strictEqual(innerOpts.asciiInner, 1, '必须带防递归标记');
assert.strictEqual(innerOpts.scale, 1, '离屏渲染按设计尺寸 scale=1');
assert.strictEqual(innerOpts.transparent, false, '离屏渲染必须不透明');

// 2) 像素→字符：注入渐变图案，亮格出字符、黑格跳过
const bufCtx = contexts[contexts.length - 2];   // small 采样画布的 ctx（顺序：buf 先建、small 后建）
const cols = bufCtx.canvas.width, rows = bufCtx.canvas.height;
const px = new Uint8ClampedArray(cols * rows * 4);
for (let i = 0; i < cols * rows; i++) {
    const v = (i % 7 === 0) ? 255 : 0;          // 少数亮格
    px[i * 4] = v; px[i * 4 + 1] = v; px[i * 4 + 2] = v; px[i * 4 + 3] = 255;
}
bufCtx.pixels = px;
window.J.asciiFrame(targetCtx, plan, 2.0, {}, () => {});
assert(targetCtx.fills.length > 0, '亮格应绘制字符');
assert(targetCtx.fills.length < cols * rows / 3, '黑格不应绘制（跳过暗部）');
assert(targetCtx.fills.every(f => f.ch.trim().length > 0), '绘制的必须是可打印字符');

// 3) 离屏画布复用：换帧不重建（createElement 调用数不增长），尺寸变了才重建
const createdBefore = contexts.length;
window.J.asciiFrame(targetCtx, plan, 3.0, {}, () => {});
assert.strictEqual(contexts.length, createdBefore, '同尺寸复用离屏画布');
window.J.asciiFrame(targetCtx, {...plan, W: 1080, H: 1920}, 3.0, {}, () => {});
assert(contexts.length > createdBefore, '尺寸变化应重建离屏画布');

console.log('OK: ASCII 滤镜冒烟测试全部通过');
