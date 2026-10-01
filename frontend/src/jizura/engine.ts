/* 引擎驱动层：replan / 渲染循环 / 播放控制 / 字体预热 / 音频装载。
   原 12_ui.js 的对应逻辑，DOM 操作替换为 bus 事件通知。 */
import {J} from '../engine';
import {analyzeAudioOffThread} from './audio-analyze';
import {activeLoopRange, AP, refreshLoopHold, S} from './state';
import {autosave} from './project';
import {bus} from './bus';

const HUD_CHARS = '0123456789:./-_()【】・No.LYRICRECUNTITLEDXYlinebpminterlude—─／ ';

export const fixedMq = window.matchMedia ? window.matchMedia('(min-width: 1181px) and (min-height: 620px)') : null;
document.documentElement.classList.add('fixed-ok');

export function fixedLayout() {
    return !!(fixedMq && fixedMq.matches) && document.documentElement.classList.contains('fixed-ok');
}

/* ---- 视图挂载（StageCanvas 组件调用） ---- */
let viewEl: HTMLElement | null = null;
let viewCanvas: HTMLCanvasElement | null = null;

export function attachView(el: HTMLElement, canvas: HTMLCanvasElement) {
    viewEl = el;
    viewCanvas = canvas;
    S.need = true;
}

export function audioLike() {
    const T = S.project.timing;
    if (S.audio) {
        const a = Object.assign({}, S.audio);
        if (T.bpm > 0) a.beats = J.beatGrid(T.bpm, T.beatOffset || 0, S.audio.duration);
        return a;
    }
    if (T.bpm > 0) return {beats: J.beatGrid(T.bpm, T.beatOffset || 0, 600)};
    return null;
}

export function replan() {
    if (import.meta.env.DEV) jankRecord('replan', 0);
    S.plan = J.plan(S.project, audioLike());
    // 旧项目锁定的行（只有 seed）：现在拍快照，从今往后保持原样
    for (const [i, o] of Object.entries(S.project.overrides || {})) {
        const ov: any = o;
        if (ov && ov.lock && !Array.isArray(ov.lockedCuts)) {
            const snap = J.lineSnapshot(S.plan, +i);
            if (snap) ov.lockedCuts = snap;
        }
    }
    if (S.t > S.plan.duration) S.t = 0;
    refreshLoopHold();
    lastCutIdx = -2;
    S.need = true;
    autosave();
    ensureFonts();
    bus.emit('replan');
    clearTimeout(warmTimer);
    warmTimer = setTimeout(warm, 450);
}

let replanTimer = 0;
export const replanSoon = (ms = 220) => {
    clearTimeout(replanTimer);
    replanTimer = setTimeout(replan, ms);
};

/* ---- 临近 morph 档位预热 ----
   seek/play 后，播放头前方 3 秒内的第一个 morph 切分：后台把它的全部 e 档位
   位图提前画好（idle 分档让出）。循环/行循环反复经过同一 morph 时首圈即命中；
   线性播放时每次进入新 morph 前 ~0.5s 就绪。miss 时回退现绘（同旧路径成本）。 */
let nearWarmTimer = 0, nearWarmJob = 0;

function warmupNearMorphs() {
    clearTimeout(nearWarmTimer);
    nearWarmTimer = setTimeout(() => {
        if (!S.plan || !viewCanvas) return;
        const c = S.plan.cuts.find((c: any) => c.morph && c.index > 0 && S.plan.cuts[c.index - 1] && c.start >= S.t - 0.05 && c.start - S.t < 3);
        if (!c) return;
        const job = ++nearWarmJob;
        const A = S.plan.cuts[c.index - 1];
        const cw = viewCanvas.width, ch = viewCanvas.height, scale = cw / S.plan.W;
        const idle = (window as any).requestIdleCallback ? (f: any) => (window as any).requestIdleCallback(f, {timeout: 200}) : (f: any) => setTimeout(() => f(null), 20);
        let q = 0;
        warmReset(10, '预渲染位图');
        const step = (deadline: any) => {
            if (job !== nearWarmJob || S.exporting) {
                warmState.active = false;
                return;
            }
            try {
                const L = S.renderer.morphLogs(S.plan, A, c, cw, ch, scale, {});
                while (q <= 9 && (!deadline || deadline.timeRemaining() > 4)) {
                    S.renderer.morphShotBuild(S.plan, A, c, cw, ch, scale, q / 9, true, L);
                    q++;
                    warmStep();
                }
            } catch { /* 兜底 */
            }
            if (q <= 9) idle(step);
        };
        idle(step);
    }, 60);
}

/* 预热：空闲时预分解 piece 动画要用的字形 + morph 帧位图（全部 e 档位），
   播放/镜像不卡顿。morph 缓存是首次播放卡顿的大头：morphLogs 递归两整帧 +
   每个档位的逐字重绘，正好落在播放里。
   播放中不跑全局预热：新 plan 下的冷渲染（morphLogs 递归两整帧 30-80ms、
   逐字光栅化）作为 idle 块落地时会直接顶掉播放帧预算（实测切换特效后 rAF
   p50 26.7ms→最大 386ms）。播放时只留 warmupNearMorphs 的预算化预热，
   全局预热挪到 pause() 补跑。 */
let warmTimer = 0, warmJob = 0;

/* 预热进度（画面 loading 条的数据源）：warm() 与 warmupNearMorphs() 共用。
   播放/导出中不预热（见上），所以条只在暂停/空闲时出现，随进度走完自动消失。 */
export const warmState = {active: false, done: 0, total: 0, phase: ''};

function warmReset(total: number, phase: string) {
    warmState.active = total > 0;
    warmState.done = 0;
    warmState.total = total;
    warmState.phase = phase;
    if (warmState.active) S.need = true;
}

function warmStep(n = 1) {
    if (!warmState.active) return;
    warmState.done = Math.min(warmState.total, warmState.done + n);
    if (warmState.done >= warmState.total) warmState.active = false;
    S.need = true;   // 暂停态下 draw 很便宜，让进度条动起来
}

function warm() {
    if (S.playing || S.exporting) {
        warmState.active = false;
        return;
    }   // 播放/导出中：全局预热让路，pause() 里补跑
    const job = ++warmJob;
    // 与上游 JIZURA 同语义：piece 类切分空闲时预渲（fast:true，每片 1-3ms 无感），
    // 把首播的字形光栅化提前踩热。切莫扩成 fast:false 全量渲染——本机冷态单帧
    // 300-700ms，预热窗口自己就变成 0.3-0.7s 一顿的卡顿风暴（实测教训）。
    // 全部切分的入场中段帧也预渲（fast:true，只为字形光栅化回填，非画质路径）。
    const cuts = S.plan.cuts.filter((c: any) => !c.special);
    const morphs = S.plan.cuts.filter((c: any) => c.morph && c.index > 0 && S.plan.cuts[c.index - 1]);
    if (!viewCanvas) return;
    warmReset(morphs.length + cuts.length, '预热缓存');
    const cv = document.createElement('canvas');
    cv.width = viewCanvas.width;
    cv.height = viewCanvas.height;
    const ctx = cv.getContext('2d')!;
    const scale = cv.width / S.plan.W;
    let i = 0, mi = 0;
    const idle = (window as any).requestIdleCallback ? (f: any) => (window as any).requestIdleCallback(f, {timeout: 400}) : (f: any) => setTimeout(() => f(null), 40);
    const step = (deadline: any) => {
        // 播放开始必须立刻让路：预热是重活（真实滤镜路径），撞上播放会把主线程
        // 砸出秒级冻结（实测冷启动点播放后 rAF 空隙 6.7s）。pause() 会重新排期补跑。
        if (job !== warmJob || S.exporting || S.playing) {
            warmState.active = false;
            return;
        }
        // 字体没就绪就预热的话，位图里嵌的是 fallback 字形且不会自己失效——等就绪
        if (morphs.length && document.fonts && document.fonts.status !== 'loaded') {
            warmState.phase = '字体加载中';
            S.need = true;   // 0% 条没有琥珀填充，不脉冲一下的话这个阶段完全不可见
            idle(step);
            return;
        }
        do {
            if (mi < morphs.length) {
                const c = morphs[mi++];
                // 只预热 morphLogs（字形起止）：首次递归渲染两整帧+字形光栅化是首播大卡。
                // 位图档位不在这里全量预热（LRU 装不下全歌档位），由 warmupNearMorphs 按需填
                warmState.phase = 'morph 布局';
                try {
                    S.renderer.morphLogs(S.plan, S.plan.cuts[c.index - 1], c, cv.width, cv.height, scale, {});
                } catch { /* 兜底 */
                }
                warmStep();
            } else {
                const c = cuts[i++];
                if (!c) break;
                const piece = c.enter === 'assemble' || ['explode', 'fall', 'drift'].includes(c.exit);
                const ts: number[] = [c.start + Math.min(c.inDur * 0.7, c.dur * 0.25)];   // 入场中段
                if (piece) {
                    ts.push(c.start + Math.min(c.inDur * 0.3, c.dur * 0.2));              // piece 拆解帧
                    if (c.outDur > 0) ts.push(c.end - c.outDur * 0.5);                    // piece 退场合体帧
                }
                warmState.phase = '切分首帧';
                for (const t of ts) {
                    try {
                        // fast:true（上游同语义）：预热只为字形光栅化回填；滤镜/辉光是每帧
                        // 即时成本，预热单帧也防不住，fast:false 反而让预热窗口自身卡顿
                        S.renderer.frame(ctx, S.plan, t, {
                            scale: cv.width / S.plan.W,
                            fast: true,
                            noHud: true,
                            noGhost: true
                        });
                    } catch {
                    }
                }
                warmStep();
            }
        } while ((mi < morphs.length || i < cuts.length) && deadline && deadline.timeRemaining() > 24);
        if (mi < morphs.length || i < cuts.length) idle(step);
        else warmState.active = false;
    };
    idle(step);
}

let fontKey = '';
let thumbFonts = false;

export function invalidateFontKey() {
    fontKey = '';
}

export async function ensureFonts() {
    const txt = S.project.lyrics + (S.project.title || '') + (S.project.artist || '') + HUD_CHARS;
    const keys = J.fontsOfPlan(S.plan);
    const key = txt + '|' + keys.join(',') + '|' + Object.keys(J.FONTS).length + '|' + J.lang;
    if (key === fontKey) return;
    fontKey = key;
    showMsg('正在加载字体…');
    try {
        await J.ensureFonts(txt, keys);
    } catch {
    }
    // 字体就绪前若做过渲染/预热，产物里嵌的是 fallback 字形——morph 缓存作废重建
    S.renderer.morphCache = null;
    S.renderer.morphShotCache = null;
    showMsg(null);
    S.need = true;
    bus.emit('fonts');
    clearTimeout(warmTimer);
    warmTimer = setTimeout(warm, 120);   // 新字体下重跑预热
    loadThumbFonts();
}

/* 风格缩略图需要每个风格 display 字体的两个字形 — 面板可见时才拉取 */
export function loadThumbFonts() {
    if (thumbFonts) return;
    const visible = document.querySelector<HTMLCanvasElement>('canvas[data-k]');   // styleGrid id 在 Tailwind 迁移时丢失，改按瓦片判定
    if (!visible || !visible.offsetParent) return;
    thumbFonts = true;
    J.ensureFonts('字面', [...new Set(J.STYLE_ORDER.map((k: string) => J.STYLES[k].fonts.display[0]))])
        .then(() => bus.emit('fonts')).catch(() => {
    });
}

export function showMsg(m: string | null) {
    bus.emit('msg', m);
}

/* ---- 视口尺寸 ---- */
export function sizeViewport() {
    if (!viewEl || !viewCanvas || !S.plan) return;
    const vp = viewEl, c = viewCanvas;
    const ar = S.plan.W / S.plan.H;
    let cssW = vp.clientWidth || 800, cssH = cssW / ar;
    const maxH = fixedLayout() ? Math.max(160, vp.clientHeight - 2) : Math.max(220, window.innerHeight * 0.68);
    if (cssH > maxH) {
        cssH = maxH;
        cssW = cssH * ar;
    }
    const dpr = Math.min(2, window.devicePixelRatio || 1);
    const pw = Math.round(Math.min(S.plan.W, cssW * dpr)), ph = Math.round(pw / ar);
    if (c.width !== pw || c.height !== ph) {
        c.width = pw;
        c.height = ph;
    }
    c.style.width = cssW + 'px';
    c.style.height = cssH + 'px';
    S.need = true;
}

/* 中央を空ける的预览参考线（仅预览，导出不含） */
function drawCenterGuide(ctx: CanvasRenderingContext2D, k: number) {
    const P = S.plan, z = P.zones;
    if (!z || z.length < 2) return;
    const pos = S.project.centerZone || 'center';
    // 留白矩形：单侧留白 = 文字带之外的那条边；中央 = 两条文字带之间的区域
    let r: number[];
    if (pos === 'left') r = [0, 0, z[0].x, P.H];
    else if (pos === 'right') r = [z[0].x + z[0].w, 0, P.W - z[0].x - z[0].w, P.H];
    else if (pos === 'top') r = [0, 0, P.W, z[0].y];
    else if (pos === 'bottom') r = [0, z[0].y + z[0].h, P.W, P.H - z[0].y - z[0].h];
    else if (P.W >= P.H) {
        const left = z[0].x + z[0].w, right = z[1].x;
        r = [left, 0, Math.max(2, right - left), P.H];
    } else {
        const top = z[0].y + z[0].h, bottom = z[1].y;
        r = [0, top, P.W, Math.max(2, bottom - top)];
    }
    ctx.save();
    ctx.setTransform(k, 0, 0, k, 0, 0);
    ctx.setLineDash([14, 10]);
    ctx.lineWidth = 2 / k * 1.5;
    ctx.strokeStyle = 'rgba(255,255,255,0.55)';
    ctx.strokeRect(r[0] + 4, r[1] + 4, r[2] - 8, r[3] - 8);
    ctx.setLineDash([]);
    ctx.restore();
}

/* 预热 loading 条：一键随机/载入后的冷缓存空闲预热进度（warmState 驱动）。
   只画编辑器主画面——Spout 镜像/透明通道、/render 镜像页与导出不画。 */
function drawWarmBar(ctx: CanvasRenderingContext2D, k: number) {
    const W = S.plan.W, H = S.plan.H;
    const p = warmState.total > 0 ? Math.min(1, warmState.done / warmState.total) : 0;
    const w = Math.min(W * 0.36, 620), h = Math.max(5, Math.round(H * 0.006));
    const x = (W - w) / 2, y = H - h - Math.max(12, H * 0.018);
    ctx.save();
    ctx.setTransform(k, 0, 0, k, 0, 0);
    ctx.fillStyle = 'rgba(10,10,12,0.62)';
    ctx.fillRect(x - 3, y - 3, w + 6, h + 6);
    ctx.fillStyle = 'rgba(255,255,255,0.16)';
    ctx.fillRect(x, y, w, h);
    ctx.fillStyle = '#f5a50c';
    ctx.fillRect(x, y, w * p, h);
    const fs = Math.max(11, Math.round(H * 0.013));
    ctx.font = J.fontCSS('mono', fs);
    ctx.textAlign = 'center';
    ctx.textBaseline = 'bottom';
    ctx.fillStyle = 'rgba(255,255,255,0.8)';
    ctx.fillText(`${warmState.phase} ${Math.round(p * 100)}%`, W / 2, y - fs * 0.45);
    ctx.restore();
}

/* 卡顿黑匣子（dev）：慢帧/长任务/rAF 空隙/关键操作全部记录到 window.__jlJank，
   复现卡顿后 CDP 直读，含切分手法、预热/预览状态、可见性现场。 */
function jankRecord(kind: string, ms: number, extra?: any) {
    const arr: any[] = (window as any).__jlJank || ((window as any).__jlJank = []);
    if (arr.length > 400) arr.shift();
    const c = S.plan ? J.cutAt(S.plan, S.t) : null;
    arr.push({
        at: Math.round(performance.now()), kind, ms: Math.round(ms),
        t: +S.t.toFixed(2), playing: S.playing, loop: S.loop,
        cut: c ? `${c.index}:${c.layout}/${c.enter}/${c.hold}/${c.exit}/${c.cam}` : null,
        warm: `${warmState.active ? warmState.done + '/' + warmState.total : 'off'}`,
        vis: document.visibilityState, ...(extra || {}),
    });
}

if (import.meta.env.DEV) {
    try {
        new PerformanceObserver(list => {
            for (const e of list.getEntries()) if (e.duration > 60) jankRecord('longtask', e.duration);
        })
            .observe({entryTypes: ['longtask']});
    } catch { /* 老内核 */
    }
}

export function draw() {
    if (!viewCanvas || !S.plan) return;
    const c = viewCanvas, ctx = c.getContext('2d')!;
    const t0 = performance.now();
    S.renderer.frame(ctx, S.plan, S.t, {
        scale: c.width / S.plan.W,
        fast: S.playing && S.slow,
        transparent: !!window.__jl_spout_transparent
    });
    if (S.plan.centerFree && !window.__jl_spout_mirror) drawCenterGuide(ctx, c.width / S.plan.W);
    if (warmState.active && !S.exporting && !window.__jl_spout_mirror && !window.__jl_spout_transparent && !location.pathname.startsWith('/render')) drawWarmBar(ctx, c.width / S.plan.W);
    const dt = performance.now() - t0;
    if (import.meta.env.DEV && dt > 40) jankRecord('draw', dt);
    S.slow = S.playing ? (dt > 30 ? true : dt < 14 ? false : S.slow) : false;
    updateCutInfo();
    bus.emit('frame');
}

export function tick(now: number) {
    requestAnimationFrame(tick);
    if (import.meta.env.DEV) {
        // rAF 空隙：主线程干净但呈现层卡（合成/GPU/遮挡节流）也会在这里露出来
        if ((tick as any).__last && now - (tick as any).__last > 60) jankRecord('rafGap', now - (tick as any).__last);
        (tick as any).__last = now;
    }
    if (S.exporting) return;
    if (S.playing) {
        // rAF 时间戳可能早于 play()/seek() 打的 t0 → clamp 防负数
        let t = Math.max(0, S.audio ? AP.time() : (now - S.t0) / 1000);
        const range = activeLoopRange();
        if (t >= range.end - 1e-3) {
            if (S.loop && !S.tap) {
                seek(range.start);
                t = range.start;
            } else {
                pause();
                t = Math.min(t, S.plan.duration - 1e-3);
                if (S.tap) bus.emit('tapend');
            }
        }
        S.t = t;
        S.need = true;
        bus.emit('playhead');
    }
    if (S.need) {
        S.need = false;
        draw();
    }
}

/* ---- 播放控制 ---- */
export function play() {
    if (import.meta.env.DEV) jankRecord('play', 0);
    refreshLoopHold();
    if (S.audio) AP.play(S.audio.buffer, S.t);
    else S.t0 = performance.now() - S.t * 1000;
    S.playing = true;
    warmupNearMorphs();   // 即将经过的 morph 档位提前备好，首播不现绘
    bus.emit('playstate');
}

export function pause() {
    S.playing = false;
    AP.stop();
    S.need = true;
    // 播放期间被搁置的全局预热现在补跑（replan 触发的那次已在 warm() 里让路跳过）
    clearTimeout(warmTimer);
    warmTimer = setTimeout(warm, 250);
    bus.emit('playstate');
}

export function seek(t: number) {
    if (import.meta.env.DEV) jankRecord('seek', 0, {to: +t.toFixed(2)});
    S.t = J.clamp(t, 0, Math.max(0, S.plan.duration - 1e-3));
    if (S.audio) {
        if (S.playing) AP.play(S.audio.buffer, S.t);
    } else S.t0 = performance.now() - S.t * 1000;
    refreshLoopHold();
    S.need = true;
    updateCutInfo();
    warmupNearMorphs();
    bus.emit('playhead');
}

export function togglePlay() {
    S.playing ? pause() : play();
}

/* ---- 当前切分信息（原 updateCutInfo 的非 DOM 部分） ---- */
let lastCutIdx = -2;

export function updateCutInfo() {
    if (!S.plan) return;
    const cut = J.cutAt(S.plan, S.t);
    const idx = cut ? cut.index : -1;
    const li = cut ? cut.line : -1;
    if (li !== S.curLine) {
        S.curLine = li;
        followLine(li);
    }
    if (idx === lastCutIdx) return;
    lastCutIdx = idx;
    bus.emit('cutchange', cut);
}

/* ---- 播放行跟随滚动（组件通过 bus 监听） ---- */
let listTouched = 0;

function followLine(li: number) {
    bus.emit('curline', li);
    if (!S.playing || li < 0 || !fixedLayout()) return;
    if (performance.now() - listTouched < 2500) return;
    bus.emit('scrolltoline', li);
}

/* ---- 音频装载 ---- */
let audioSeq = 0;

export async function loadAudioFile(f: File, restored = false) {
    const my = ++audioSeq;   // 只有最新一次选择生效（更早的慢分析直接丢弃）
    bus.emit('audioname', '解析中…');
    try {
        pause();
        const a = await analyzeAudioOffThread(f);   // 数值计算在 Worker：主线程不卡
        if (my !== audioSeq) return false;
        S.audio = a;
        bus.emit('audioname', `${f.name}（${J.fmtTime(S.audio.duration)}・约${S.audio.bpm}BPM）` + (restored ? '・上次的歌曲' : ''));
        S.project.audioName = f.name;
        if (!restored && J.saveSong) J.saveSong(f);   // 存进浏览器：刷新后导出不会丢歌
        S.project.timing.snap = true;
        bus.emit('replan-sync');                      // 让面板上的 BPM 占位等刷新
        replan();
        return true;
    } catch (err: any) {
        if (my !== audioSeq) return false;
        bus.emit('audioname', '无法读取：' + err.message);
        S.audio = null;
        return false;
    }
}

/* ---- 启动 ---- */
export function bootEngine() {
    requestAnimationFrame(tick);
    window.addEventListener('resize', () => {
        sizeViewport();
        bus.emit('viewresize');
    });
}
