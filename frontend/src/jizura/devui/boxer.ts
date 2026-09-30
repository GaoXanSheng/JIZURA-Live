/* 元素标框（DevTools 式）：重绘闪烁 + hover 元素详情。
   判定为启发式——浏览器不暴露单元素绘制事件：每帧 diff getBoundingClientRect，
   叠加 MutationObserver 的属性/文本/子树变更，两者命中其一即视为「疑似重绘」，
   池化 overlay 闪框（与 DevTools paint flashing 同样的定位），并累计变更计数。
   仅在面板开启且「元素标框」相关开关打开时运行。 */

export interface TopChanger {
    label: string;
    comp: string;
    total: number;
    win: number;        // 最近 1s 变更次数
    type: string;       // geom | style | content
    el: Element;
}

interface Rec {
    x: number;
    y: number;
    w: number;
    h: number;
    total: number;
    win: number;
    type: string;
    lastAt: number;
    dirty: boolean;
    pendingType: string;
}

const MAX_TRACKED = 800;      // 登记上限；超出只保视口内元素
const STALE_MS = 60_000;      // 60s 无变更且在视口外 → 逐出
const FLASH_MAX = 160;        // 闪框池上限
const FLASH_HOLD_MS = 60;     // 亮框保持时长，之后 CSS 过渡淡出

let running = false;
let flashOn = false;
let hoverOn = false;
let raf = 0, secTimer = 0;
const recs = new Map<Element, Rec>();
let mo: MutationObserver | null = null;
let rafPending = false, mx = 0, my = 0, hoverEl: Element | null = null;

/* ---- overlay 层：闪框池 + hover 框/标签 ---- */
let layer: HTMLDivElement | null = null;
let flashes: HTMLDivElement[] = [];
let flashIdx = 0;
let hoverBox: HTMLDivElement | null = null;
let hoverTip: HTMLDivElement | null = null;

function ensureLayer() {
    if (layer) return;
    layer = document.createElement('div');
    layer.setAttribute('data-devui', 'boxer-layer');
    layer.style.cssText = 'position:fixed;inset:0;pointer-events:none;z-index:6500;';
    document.body.appendChild(layer);
    for (let i = 0; i < FLASH_MAX; i++) {
        const d = document.createElement('div');
        d.style.cssText = 'position:fixed;border:1px solid rgba(63,191,95,.9);background:rgba(63,191,95,.16);'
            + 'opacity:0;transition:opacity .35s ease-out;display:none;';
        (d as any).__t = 0;
        layer.appendChild(d);
        flashes.push(d);
    }
    hoverBox = document.createElement('div');
    hoverBox.style.cssText = 'position:fixed;border:1px solid rgba(245,165,12,.95);background:rgba(245,165,12,.12);display:none;';
    layer.appendChild(hoverBox);
    hoverTip = document.createElement('div');
    hoverTip.style.cssText = 'position:fixed;background:#0c0c0e;border:1px solid #383840;color:#ece7e1;'
        + 'padding:4px 6px;white-space:pre;display:none;max-width:320px;box-shadow:0 4px 16px rgb(0 0 0/.4);';
    layer.appendChild(hoverTip);
}

function destroyLayer() {
    layer?.remove();
    layer = null;
    flashes = [];
    flashIdx = 0;
    hoverBox = null;
    hoverTip = null;
    hoverEl = null;
}

function flashRect(r: DOMRect) {
    if (!layer) return;
    const d = flashes[flashIdx++ % FLASH_MAX];
    clearTimeout((d as any).__t);
    d.style.display = 'block';
    d.style.left = `${Math.max(-2, r.x)}px`;
    d.style.top = `${Math.max(-2, r.y)}px`;
    d.style.width = `${Math.min(r.width, window.innerWidth + 4)}px`;
    d.style.height = `${Math.min(r.height, window.innerHeight + 4)}px`;
    d.style.opacity = '1';
    (d as any).__t = setTimeout(() => {
        d.style.opacity = '0';
    }, FLASH_HOLD_MS);
}

/* ---- 元素登记 ---- */
function admit(el: Element): Rec | null {
    let rec = recs.get(el);
    if (rec) return rec;
    if (recs.size >= MAX_TRACKED) {
        if (!sweep(false)) return null;           // 先逐出陈旧元素再试
        if (recs.size >= MAX_TRACKED) return null;
    }
    let r: DOMRect;
    try {
        r = el.getBoundingClientRect();
    } catch {
        return null;
    }
    if (!r.width && !r.height) return null;      // 不可见/零尺寸元素跳过
    rec = {
        x: r.x,
        y: r.y,
        w: r.width,
        h: r.height,
        total: 0,
        win: 0,
        type: '',
        lastAt: 0,
        dirty: false,
        pendingType: ''
    };
    recs.set(el, rec);
    return rec;
}

function mark(target: Node | null, type: string) {
    const el: Element | null = target instanceof Element ? target : target?.parentElement ?? null;   // characterData 变更的目标是文本节点
    if (!el || (el as HTMLElement).dataset?.devui === 'boxer-layer') return;
    const rec = recs.get(el) ?? (recs.size < MAX_TRACKED ? admit(el) : null);
    if (!rec) return;
    rec.dirty = true;
    if (type === 'content' || !rec.pendingType) rec.pendingType = type;
}

/* sweep：视口内元素优先补录；满了就逐出「陈旧且视口外」的。返回是否腾出过位置 */
function sweep(allowEvict: boolean): boolean {
    const vh = window.innerHeight;
    let freed = false;
    if (recs.size >= MAX_TRACKED && allowEvict) {
        const now = performance.now();
        for (const [el, rec] of recs) {
            if (now - rec.lastAt > STALE_MS && (rec.y + rec.h < 0 || rec.y > vh)) {
                recs.delete(el);
                freed = true;
                if (recs.size < MAX_TRACKED) break;
            }
        }
    }
    if (recs.size < MAX_TRACKED && document.body) {
        for (const el of document.body.querySelectorAll('*')) {
            if (recs.has(el)) continue;
            const r = el.getBoundingClientRect();
            if (r.bottom < 0 || r.top > vh) continue;
            admit(el);
            if (recs.size >= MAX_TRACKED) break;
        }
    }
    return freed;
}

/* ---- 每帧 diff ---- */
function moved(r: DOMRect, rec: Rec) {
    return Math.abs(r.x - rec.x) > 0.5 || Math.abs(r.y - rec.y) > 0.5 ||
        Math.abs(r.width - rec.w) > 0.5 || Math.abs(r.height - rec.h) > 0.5;
}

function frameLoop(now: number) {
    raf = requestAnimationFrame(frameLoop);
    const vh = window.innerHeight;
    for (const [el, rec] of recs) {
        if (!el.isConnected) {
            recs.delete(el);
            continue;
        }   // 已移出 DOM
        let r: DOMRect;
        try {
            r = el.getBoundingClientRect();
        } catch {
            recs.delete(el);
            continue;
        }
        const isGeom = moved(r, rec);
        if (!isGeom && !rec.dirty) continue;
        const type = rec.dirty && !isGeom ? rec.pendingType : 'geom';
        rec.total++;
        rec.win++;
        rec.type = type || 'geom';
        rec.lastAt = now;
        rec.x = r.x;
        rec.y = r.y;
        rec.w = r.width;
        rec.h = r.height;
        rec.dirty = false;
        rec.pendingType = '';
        if (flashOn && r.height > 0 && r.bottom > 0 && r.top < vh) flashRect(r);
    }
}

function sampleSecond() {
    for (const rec of recs.values()) rec.win = 0;
    sweep(true);
}

/* ---- hover 详情 ---- */
function onMove(e: MouseEvent) {
    mx = e.clientX;
    my = e.clientY;
    if (rafPending) return;
    rafPending = true;
    requestAnimationFrame(updateHover);
}

function updateHover() {
    rafPending = false;
    if (!hoverOn || !hoverBox || !hoverTip) return;
    const el = document.elementFromPoint(mx, my);
    if (!el || el === hoverEl) return;
    hoverEl = el;
    let r: DOMRect;
    try {
        r = el.getBoundingClientRect();
    } catch {
        return;
    }
    hoverBox.style.display = 'block';
    hoverBox.style.left = `${r.x}px`;
    hoverBox.style.top = `${r.y}px`;
    hoverBox.style.width = `${r.width}px`;
    hoverBox.style.height = `${r.height}px`;
    const rec = recs.get(el);
    const perSec = rec?.win ?? 0;
    const type = rec?.type ? ({geom: '几何', style: '样式', content: '内容'} as any)[rec.type] || rec.type : '—';
    hoverTip.style.display = 'block';
    hoverTip.textContent = [
        `${elLabel(el)}${vueComp(el) ? '  ⟨' + vueComp(el) + '⟩' : ''}`,
        `${Math.round(r.width)}×${Math.round(r.height)} @ ${Math.round(r.x)},${Math.round(r.y)}`,
        `变更累计 ${rec?.total ?? 0} · 最近 1s ${perSec} · 类型 ${type}`,
    ].join('\n');
    const tw = hoverTip.offsetWidth, th = hoverTip.offsetHeight;
    hoverTip.style.left = `${Math.min(mx + 12, window.innerWidth - tw - 6)}px`;
    hoverTip.style.top = `${Math.min(my + 14, window.innerHeight - th - 6)}px`;
}

/* ---- 元素描述 ---- */
export function elLabel(el: Element): string {
    const tag = el.tagName.toLowerCase();
    const id = el.id ? `#${el.id}` : '';
    const cls = el.classList.length ? `.${String(el.classList[0]).slice(0, 24)}` : '';
    return tag + id + cls;
}

export function vueComp(el: Element): string {
    let n: Element | null = el;
    while (n) {
        const inst = (n as any).__vueParentComponent;
        if (inst) {
            const name = inst.type?.__name || inst.type?.name;
            if (name) return String(name);
        }
        n = n.parentElement;
    }
    return '';
}

/* ---- 对外 API ---- */
export function startBoxer(flash: boolean, hover: boolean) {
    flashOn = flash;
    hoverOn = hover;
    if (running) return;
    if (!flash && !hover) return;
    running = true;
    recs.clear();
    ensureLayer();
    sweep(false);
    raf = requestAnimationFrame(frameLoop);
    secTimer = window.setInterval(sampleSecond, 1000);
    mo = new MutationObserver(muts => {
        for (const m of muts) mark(m.target as Element, m.type === 'attributes' ? 'style' : 'content');
    });
    mo.observe(document.documentElement, {attributes: true, characterData: true, childList: true, subtree: true});
    if (hover) window.addEventListener('mousemove', onMove, {passive: true});
}

export function stopBoxer() {
    if (!running) {
        flashOn = false;
        hoverOn = false;
        return;
    }
    running = false;
    flashOn = false;
    hoverOn = false;
    cancelAnimationFrame(raf);
    clearInterval(secTimer);
    mo?.disconnect();
    mo = null;
    window.removeEventListener('mousemove', onMove);
    destroyLayer();
    recs.clear();
}

export function setBoxerModes(flash: boolean, hover: boolean) {
    const was = running;
    flashOn = flash;
    hoverOn = hover;
    if (!was) {
        startBoxer(flash, hover);
        return;
    }
    if (hover) window.addEventListener('mousemove', onMove, {passive: true});
    else {
        window.removeEventListener('mousemove', onMove);
        if (hoverBox) hoverBox.style.display = 'none';
        if (hoverTip) hoverTip.style.display = 'none';
    }
    if (!flash && !hover) stopBoxer();
}

export function boxerActive() {
    return running;
}

export function boxerTracked() {
    return recs.size;
}

export function boxerTop(n: number): TopChanger[] {
    const out: TopChanger[] = [];
    for (const [el, rec] of recs) {
        if (rec.win <= 0 && rec.total <= 0) continue;
        out.push({label: elLabel(el), comp: vueComp(el), total: rec.total, win: rec.win, type: rec.type, el});
    }
    return out.sort((a, b) => b.win - a.win || b.total - a.total).slice(0, n);
}

export function boxerLocate(el: Element) {
    try {
        el.scrollIntoView({block: 'center', behavior: 'smooth'});
    } catch { /* 不可滚动容器 */
    }
    const r = el.getBoundingClientRect();
    flashRect(r);
    setTimeout(() => flashRect(el.getBoundingClientRect()), 400);
}
