/* Worker 环境桩：必须在引擎 import 之前求值（ESM 静态 import 按序求值保证这一点）。
   引擎的三点浏览器依赖在这里以最小等价物补齐，引擎文件零改动：
     1. window.J 挂载            → window = self
     2. document.createElement('canvas')（全引擎 ~30 处，含 02_fonts 模块加载期的度量上下文）
                                 → OffscreenCanvas（补 toBlob，PNG 导出走它）
     3. 字体：document.fonts + <link> stylesheet
                                 → WorkerGlobalScope.fonts（FontFaceSet）+ fetch CSS 解析成 FontFace
   其余 document 触点（hidden / documentElement.lang / body+a 下载）按主页面语义给桩值。 */

import {parseFontFaces} from './fontcss';

const g: any = globalThis;

if (typeof g.window === 'undefined') g.window = g;

function makeCanvas(w?: number, h?: number): any {
    const c: any = new OffscreenCanvas(Math.max(1, w || 300), Math.max(1, h || 150));
    // 引擎 PNG 路径用 canvas.toBlob；OffscreenCanvas 对应 convertToBlob
    c.toBlob = (cb: any, type?: string, quality?: number) => {
        c.convertToBlob({type: type || 'image/png', quality})
            .then((b: Blob) => cb(b), () => cb(null));
    };
    return c;
}

/* <link rel=stylesheet> 等价物：href 可写（attachFamily 失败时会改写为 CDN 地址并依赖
   浏览器"改 href 即重载"的行为），appendChild 到 head 时才发起加载。
   CSS 注册 FontFace 后立即 onload——不在此处逐个 load 字体文件，
   真正的子集加载由 ensureFonts 的 fonts.load(spec, 文本) 按 unicode-range 触发（与主页面一致）。 */
function makeLinkShim(): any {
    const l: any = {rel: '', __isLink: true, onload: null, onerror: null, _href: '', _attached: false};
    Object.defineProperty(l, 'href', {
        get: () => l._href,
        set: (v: string) => {
            l._href = v;
            if (l._attached) loadStylesheet(l);
        },
    });
    l.__attach = () => {
        l._attached = true;
        loadStylesheet(l);
    };
    return l;
}

async function loadStylesheet(l: any) {
    try {
        const r = await fetch(l._href);
        if (!r.ok) throw new Error('font css ' + r.status);
        const css = await r.text();
        const faces = parseFontFaces(css, l._href);
        if (!faces.length) throw new Error('no @font-face: ' + l._href);
        for (const f of faces) {
            const ff = new FontFace(f.family, `url(${f.url})`, {
                weight: f.weight,
                ...(f.unicodeRange ? {unicodeRange: f.unicodeRange} : {}),
            } as FontFaceDescriptors);
            g.fonts.add(ff);   // 不 await ff.load()：子集按需加载交给 fonts.load
        }
        if (l.onload) l.onload();
    } catch (e) {
        if (l.onerror) l.onerror(e);
    }
}

g.document = {
    hidden: false,
    // 08_planner 只区分 lang === 'en'；主页面 index.html 是 zh-CN，保持一致
    documentElement: {lang: 'zh-CN'},
    head: {
        appendChild(el: any) {
            if (el && el.__isLink) el.__attach();
        },
    },
    fonts: g.fonts,   // WorkerGlobalScope.fonts（FontFaceSet）
    createElement(tag: string): any {
        const t = String(tag).toLowerCase();
        if (t === 'canvas') return makeCanvas();
        if (t === 'link') return makeLinkShim();
        return {};   // 'a'（J.saveFile 下载）等不应在 worker 出现，防御性返回
    },
};

export {};
