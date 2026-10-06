// 引擎驱动层：replan / 渲染循环 / 播放控制 / 音频装载，DOM 操作换成 bus 事件通知。
// 无预热，与上游一致：morph/首帧全部在播放时惰性构建（morphCache LRU）。
import {J} from '../engine';
import {analyzeAudioOffThread} from './audio-analyze';
import {activeLoopRange, AP, refreshLoopHold, S} from './state';
import {autosave} from './project';
import {bus} from './bus';
import {getAutoDegrade, getNoPreview, previewWidthCap} from './previewres';

const HUD_CHARS = '0123456789:./-_()【】・No.LYRICRECUNTITLEDXYlinebpminterlude—─／ ';

export const fixedMq = window.matchMedia ? window.matchMedia('(min-width: 1181px) and (min-height: 620px)') : null;
document.documentElement.classList.add('fixed-ok');

export function fixedLayout() {
    return !!(fixedMq && fixedMq.matches) && document.documentElement.classList.contains('fixed-ok');
}

// 视图挂载（StageCanvas 组件调用）
let viewEl: HTMLElement | null = null;
let viewCanvas: HTMLCanvasElement | null = null;
// desynchronized 只为 Spout 镜像压延迟（RenderPage 有自己的开关）；编辑器开了反而
// 每帧强制 Finish 同步等 GPU（镜像满载时单次 20-47ms，即播放掉帧来源），默认关，?dsync=1 才开
const CTX_2D: CanvasRenderingContext2DSettings = {desynchronized: new URLSearchParams(location.search).get('dsync') === '1'};
// desynchronized 开启时的离屏整帧缓冲，见 draw() 内注释
let viewBuf: HTMLCanvasElement | null = null;
// S.slow 进入时刻：降质 episode 最短保持计时用
let slowAt = 0;

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
}

let replanTimer = 0;
export const replanSoon = (ms = 220) => {
    clearTimeout(replanTimer);
    replanTimer = setTimeout(replan, ms);
};

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
    // 字体就绪前若渲染过，产物嵌的是 fallback 字形，morph 缓存作废重建
    S.renderer.morphCache = null;
    S.renderer.morphShotCache = null;
    showMsg(null);
    S.need = true;
    bus.emit('fonts');
    loadThumbFonts();
}

// 风格缩略图需要每个风格 display 字体的两个字形，面板可见时才拉取
export function loadThumbFonts() {
    if (thumbFonts) return;
    const visible = document.getElementById('styleGrid');
    if (!visible || !visible.offsetParent) return;
    thumbFonts = true;
    J.ensureFonts('字面', [...new Set(J.STYLE_ORDER.map((k: string) => J.STYLES[k].fonts.display[0]))])
        .then(() => bus.emit('fonts')).catch(() => {
    });
}

export function showMsg(m: string | null) {
    bus.emit('msg', m);
}

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
    // 预览渲染尺寸档位（设置-高级）：'auto' 时上限=字幕设计宽，历史行为
    const pw = Math.round(Math.min(previewWidthCap(S.plan.W, S.plan.H), cssW * dpr)), ph = Math.round(pw / ar);
    if (c.width !== pw || c.height !== ph) {
        c.width = pw;
        c.height = ph;
    }
    c.style.width = cssW + 'px';
    c.style.height = cssH + 'px';
    S.need = true;
}

// 中央を空ける的预览参考线（仅预览，导出不含）
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

export function draw() {
    if (!viewCanvas || !S.plan) return;
    // 主预览关闭（设置-高级）：播放中整帧跳过画布绘制，画面停在最后一帧。
    // 但切分跟随与 frame 事件不能断——时间轴、播放器与 /render 镜像页的时间源都挂在上面
    if (S.playing && getNoPreview()) {
        S.slow = false;
        updateCutInfo();
        bus.emit('frame');
        return;
    }
    const c = viewCanvas;
    const t0 = performance.now();
    // desynchronized 的 2D 去闪烁：显示控制器可能扫到「clearRect 后未画完」的中间态。
    // preserveDrawingBuffer 是 WebGL 专属属性，2D 上下文不识别，帮不上忙；
    // 按文档建议整帧先画进离屏缓冲，再 clear+drawImage 一次上屏。?dsync=0 走原直画路径
    let target: CanvasRenderingContext2D;
    if (CTX_2D.desynchronized) {
        const b = viewBuf || (viewBuf = document.createElement('canvas'));
        if (b.width !== c.width || b.height !== c.height) {
            b.width = c.width;
            b.height = c.height;
        }
        target = b.getContext('2d')!;
    } else {
        target = c.getContext('2d', CTX_2D)!;
    }
    S.renderer.frame(target, S.plan, S.t, {
        scale: c.width / S.plan.W,
        fast: S.playing && S.slow,
        // 自动降质（设置-高级）：播放掉帧时连色差重影层一起跳过，省 2/3 的 drawCut
        noGhost: S.playing && S.slow && getAutoDegrade(),
        transparent: !!window.__jl_spout_transparent,
        stage: true   // 帧归因只记主舞台大画布（缩略图小画布不记）
    });
    if (S.plan.centerFree && !window.__jl_spout_mirror) drawCenterGuide(target, c.width / S.plan.W);
    if (CTX_2D.desynchronized) {
        const ctx = c.getContext('2d', CTX_2D)!;
        ctx.clearRect(0, 0, c.width, c.height);
        ctx.drawImage(viewBuf!, 0, 0);
    }
    const dt = performance.now() - t0;
    // slow episode 最短保持 1.2s 再恢复：阈值附近反复横跳时，fast 翻转会切换
    // morph 缓存桶（'f'/'n' 两边都冷）并让重影层闪现闪失
    if (!S.playing) S.slow = false;
    else if (dt > 30) {
        if (!S.slow) slowAt = t0;
        S.slow = true;
    } else if (dt < 14 && S.slow && performance.now() - slowAt > 1200) S.slow = false;
    updateCutInfo();
    bus.emit('frame');
}

export function tick(now: number) {
    requestAnimationFrame(tick);
    // 只有主线程逐帧导出（Worker 失败回退）才让预览循环让路；Worker 导出期间
    // 渲染/编码都在 Worker 线程，预览照常跑——时间轴可拖、播放/seek 画面实时刷新
    if (S.exporting && S.exportOnMain) return;
    // scrub 拖动中暂停推进：AP 已被 beginScrub 停掉，AP.time() 会归零，
    // 不能让它覆盖拖动中的 S.t（画面由 seek 的 need 驱动）
    if (S.playing && !S.scrubbing) {
        // rAF 时间戳可能早于 play()/seek() 打的 t0 → clamp 防负数
        // 跟随外部播放器（S.follow）时一律走无音频时钟：SMTC 每次轮询重锚定 t0
        let t = Math.max(0, S.audio && !S.follow ? AP.time() : (now - S.t0) / 1000 * playRate());
        const range = activeLoopRange();
        if (t >= range.end - 1e-3) {
            if (S.follow) {
                // 跟随模式：播完停在尾部，交给播放器切歌/暂停，本地不循环不暂停
            } else if (S.loop && !S.tap) {
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

// 打点同步中的播放速度（0.5×/0.75×）只在打点时生效，存的仍是歌曲本来时刻
export function playRate() {
    return S.tap && S.tap.rate > 0 ? S.tap.rate : 1;
}

// 实际出声速率 = 打点练习速率 × 音效变速（J.fxSpeed）。S.t 始终是歌曲原时刻，
// 速度只改变它前进的快慢——歌词/节拍天然同步
export function outRate() {
    return playRate() * (S.audio ? J.fxSpeed(S.project) : 1);
}

export function play() {
    refreshLoopHold();
    if (S.audio && !S.follow) AP.play(S.audio.buffer, S.t, outRate());
    else S.t0 = performance.now() - S.t * 1000 / playRate();
    S.playing = true;
    bus.emit('playstate');
}

export function pause() {
    S.playing = false;
    AP.stop();
    S.need = true;
    bus.emit('playstate');
}

export function seek(t: number) {
    S.t = J.clamp(t, 0, Math.max(0, S.plan.duration - 1e-3));
    if (S.audio && !S.follow) {
        // scrub 拖动中只挪时间不重启音频：每次 AP.play 都是 stop+新建
        // BufferSource（一次拖动上百个节点 + 每步一次可闻爆音），endScrub 恢复
        if (S.playing && !S.scrubbing) AP.play(S.audio.buffer, S.t, outRate());
    } else S.t0 = performance.now() - S.t * 1000 / playRate();
    refreshLoopHold();
    S.need = true;
    updateCutInfo();
    bus.emit('playhead');
}

// scrub 拖动降噪：进度条/时间轴拖动的 down/up 各调一次。期间 seek 只改画面，
// 音频静默跟随，pointerup 时若拖动前在播放则从当前位置恢复出声
export function beginScrub() {
    if (S.scrubbing) return;
    S.scrubbing = true;
    if (S.playing) AP.stop();   // 不动 S.playing：播放按钮不闪，tick 由 scrubbing 挡推进
    S.need = true;
}

export function endScrub() {
    if (!S.scrubbing) return;
    S.scrubbing = false;
    if (S.playing) {
        if (S.audio) AP.play(S.audio.buffer, S.t, outRate());
        else S.t0 = performance.now() - S.t * 1000 / playRate();
    }
    S.need = true;
}

export function togglePlay() {
    S.playing ? pause() : play();
}

// 当前切分信息
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

// 播放行跟随滚动（组件通过 bus 监听）
let listTouched = 0;

function followLine(li: number) {
    bus.emit('curline', li);
    if (!S.playing || li < 0 || !fixedLayout()) return;
    if (performance.now() - listTouched < 2500) return;
    bus.emit('scrolltoline', li);
}

// 音频装载
let audioSeq = 0;

export async function loadAudioFile(f: File, restored = false) {
    const my = ++audioSeq;   // 只有最新一次选择生效（更早的慢分析直接丢弃）
    bus.emit('audioname', '解析中…');
    try {
        pause();
        const a = await analyzeAudioOffThread(f);   // 数值分析不占主线程
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

export function bootEngine() {
    requestAnimationFrame(tick);
    window.addEventListener('resize', () => {
        sizeViewport();
        bus.emit('viewresize');
    });
}
