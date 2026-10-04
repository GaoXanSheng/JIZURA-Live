// 跟随外部播放器（SMTC 桥）。音频由播放器自己出声，这里只拿进度驱动渲染：
// 轮询 /api/smtc/state 重锚定本地时钟（复用 engine 的无音频时间路径，state.follow 打开），
// 切歌自动匹配歌词——酷狗 KRC 逐字优先，网易云 LRC 兜底，匹配结果落 localStorage 缓存。
import {reactive} from 'vue';
import {bus} from '../jizura/bus';
import {S} from '../jizura/state';
import {playRate} from '../jizura/engine';
import {api, fmtDur} from './api';
import {resetLyricBinding, toast} from '../jizura/look';
import {adoptKgLyric, adoptNeLyric} from './lyricStore';
import type {Song, Source} from './types';

export type FollowStatus = 'off' | 'no-session' | 'no-meta' | 'matching' | 'ok' | 'no-match' | 'error';

export interface FollowSession {
    appId: string;
    appName: string;
    title: string;
    artist: string;
    playing: boolean;
}

export const follow = reactive({
    on: false,
    status: 'off' as FollowStatus,
    detail: '',      // 状态补充说明（错误原因/时长不一致提示）
    appName: '',
    title: '',
    artist: '',
    matchedAs: '',   // 匹配到的歌曲：来源·歌名
    fmt: '' as '' | 'krc' | 'lrc' | 'ne',
    autoSelected: true,
    selectedAppId: '',
    sessions: [] as FollowSession[],
    offset: 0,       // 手动对齐偏移（秒），正数 = 歌词时间轴向前赶
    estimated: false, // 播放器不提供真实进度（如酷狗 20.x），走墙钟估算
});

// 元数据要连续稳定 2 秒才匹配：播放器切歌时常分多次更新（先清空再填新值），立刻搜会搜到上一首
const DEBOUNCE_MS = 2000;
const POLL_MS = 300;

let timer = 0;        // 轮询兜底定时器（SSE 不可用时）
let pollBusy = false;
let es: EventSource | null = null;
let candidateKey = '';
let candidateAt = 0;
let matchedKey = '';   // 已发起匹配的歌（含进行中），防止重复匹配
let matchSeq = 0;
let savedLoop: typeof S.loop = false;

// ===== 匹配 =====

const BAD_WORDS = ['live', '伴奏', 'inst', 'karaoke', 'cover', 'remix', '翻唱', '现场', 'concert', '纯音乐', 'dj版', 'instrumental'];

function norm(s: string): string {
    return String(s || '').toLowerCase().replace(/[\s\p{P}\p{S}]+/gu, '');
}

// SMTC 标题常带垃圾后缀（(Official Video)/【MV】…），匹配前剥掉；只剥含 junk 词的括段，不动正经歌名
function cleanTitle(t: string): string {
    return String(t || '')
        .replace(/[（(【\[][^）)】\]]*[）)】\]]/g, seg =>
            /official|mv|lyric|audio|video|歌词|高清|完整版|hq|4k/i.test(seg) ? '' : seg)
        .trim();
}

// 播放器没给歌手时的兜底：浏览器标签页常把「歌手 - 歌名」塞进标题
function splitTitleArtist(title: string, artist: string): [string, string] {
    if (artist || !title) return [title, artist];
    const m = title.match(/^(.+?)\s*[-–—]\s*(.+)$/);
    if (m) return [m[2].trim(), m[1].trim()]; // 惯例：歌手在前
    return [title, artist];
}

interface CacheEntry {
    source: Source;
    song: Song;
    ts: number;
}

function cacheLoad(): Record<string, CacheEntry> {
    try {
        return JSON.parse(localStorage.getItem('jizura.followCache') || '{}') || {};
    } catch {
        return {};
    }
}

function cacheGet(key: string): CacheEntry | null {
    return cacheLoad()[key] || null;
}

function cachePut(key: string, e: CacheEntry) {
    try {
        const all = cacheLoad();
        all[key] = e;
        const keys = Object.keys(all);
        if (keys.length > 300) {
            keys.sort((a, b) => (all[a].ts || 0) - (all[b].ts || 0));
            for (const k of keys.slice(0, keys.length - 300)) delete all[k];
        }
        localStorage.setItem('jizura.followCache', JSON.stringify(all));
    } catch { // 超配额就算了
    }
}

// 从搜索结果里挑最像的：标题/歌手相似度为主，曲目时长接近 SMTC EndTime 是强信号；
// Live/伴奏/翻唱等扣分。分数太低宁可返回 null 让用户手动搜，错词比没词糟糕
function pickSong(list: Song[], title: string, artist: string, durSec: number): Song | null {
    const tn = norm(title), an = norm(artist);
    let best: Song | null = null, bestScore = -1e9;
    list.forEach((it, idx) => {
        const n = norm(it.name), sg = norm(it.singer || '');
        let sc = idx * -0.5;
        if (n === tn) sc += 60;
        else if (n && (n.includes(tn) || tn.includes(n))) sc += 30;
        if (an && sg) {
            if (sg === an) sc += 30;
            else if (sg.includes(an) || an.includes(sg)) sc += 15;
        }
        for (const bad of BAD_WORDS) if (n.includes(bad)) sc -= 25;
        if (durSec > 0 && (it.duration || 0) > 0) {
            const d = Math.abs((it.duration || 0) - durSec);
            if (d <= 3) sc += 20;
            else if (d <= 10) sc += 8;
            else if (d > 30) sc -= 10;
        }
        if (sc > bestScore) {
            bestScore = sc;
            best = it;
        }
    });
    return bestScore >= 30 && best ? best : null;
}

async function findSong(title: string, artist: string, durSec: number): Promise<CacheEntry | null> {
    const key = norm(title) + '|' + norm(artist);
    const hit = cacheGet(key);
    if (hit) return hit;
    for (const source of ['kugou', 'netease'] as Source[]) {
        try {
            const j = await api.search(source, `${title} ${artist}`.trim());
            if (!j.ok) continue;
            const pick = pickSong(j.list || [], title, artist, durSec);
            if (pick) {
                const e: CacheEntry = {source, song: pick, ts: Date.now()};
                cachePut(key, e);
                return e;
            }
        } catch { // 这个源挂了试下一个
        }
    }
    return null;
}

async function matchAndAdopt(cur: { title: string; artist: string; durationMs: number }) {
    const seq = ++matchSeq;
    const [rawTitle, rawArtist] = splitTitleArtist(cur.title, cur.artist);
    const title = cleanTitle(rawTitle);
    const artist = cleanTitle(rawArtist);
    follow.status = 'matching';
    follow.detail = '';
    follow.matchedAs = '';
    follow.fmt = '';
    try {
        const found = await findSong(title, artist, cur.durationMs / 1000);
        if (seq !== matchSeq) return; // 期间又切歌了
        if (!found) {
            follow.status = 'no-match';
            follow.detail = '没找到足够像的歌，可在「搜索」页手动搜（注意跟随模式下不要载入音频）';
            return;
        }
        const {source, song} = found;
        const ly = await api.lyric(source, song);
        if (seq !== matchSeq) return;
        if (!ly.ok || !ly.content) {
            follow.status = 'no-match';
            follow.detail = '找到了歌但歌词接口没返回内容';
            return;
        }
        resetLyricBinding(); // 换歌了：旧歌词的逐行时间/覆盖绑定的是旧行号
        if (source === 'netease') {
            await adoptNeLyric(ly); // 内部 replan（无音频也能建 plan）
            follow.fmt = 'ne';
        } else {
            follow.fmt = await adoptKgLyric(ly, false, false);
        }
        follow.matchedAs = (source === 'kugou' ? '酷狗' : '网易云') + '·' + song.name;
        follow.status = 'ok';
        // 时长核对：渲染时长与播放器差太多多半是 VIP 试听截断或不同版本
        const planDur = S.plan?.duration || 0;
        const srtcDur = cur.durationMs / 1000;
        if (planDur > 0 && srtcDur > 0 && Math.abs(planDur - srtcDur) > 3)
            follow.detail = `时长不一致（歌词 ${fmtDur(planDur)} / 播放器 ${fmtDur(srtcDur)}），可能是试听截断或不同版本`;
    } catch (e: any) {
        if (seq === matchSeq) {
            follow.status = 'error';
            follow.detail = '匹配失败：' + (e.message || e);
        }
    }
}

// ===== 时钟 =====

// 直接驱动 engine 的无音频时间路径：不碰 AP（可能还有本地音频在场，跟随期间一律忽略），
// 不走 engine.play/pause（它们会动 AP 和循环）。SMTC playing 时每次轮询重锚定 t0，
// 两次锚定之间本地按 performance.now() 平滑推进，漂移有界
function driveClock(run: boolean, liveSec: number) {
    if (!S.plan) return;
    const pr = playRate() || 1;
    S.t = Math.max(0, liveSec);
    S.t0 = performance.now() - S.t * 1000 / pr;
    if (S.playing !== run) {
        S.playing = run;
        bus.emit('playstate');
    }
    S.need = true;
    bus.emit('playhead');
}

// ===== 传输：SSE 推送优先，失败回退轮询 =====
// SSE：变化即时推 + 每秒心跳（快照自带 PositionLiveMs 重锚定），比 300ms 轮询
// 延迟更低、请求更少；轮询兜底覆盖旧版程序（没有该端点）与连接异常

async function poll() {
    if (!follow.on || pollBusy) return;
    pollBusy = true;
    try {
        const r = await fetch('/api/smtc/state');
        applyState(await r.json());
    } catch {
        if (follow.on) {
            follow.status = 'error';
            follow.detail = '连不上本机服务';
        }
    } finally {
        pollBusy = false;
    }
}

function startPolling() {
    void poll();
    if (!timer) timer = window.setInterval(poll, POLL_MS);
}

function stopPolling() {
    if (timer) {
        clearInterval(timer);
        timer = 0;
    }
}

function startSse() {
    try {
        es = new EventSource('/api/smtc/events');
    } catch {
        startPolling();
        return;
    }

    es.onmessage = e => {
        if (!follow.on) return;
        try {
            applyState(JSON.parse(e.data));
        } catch { // 脏帧丢弃，等下一条
        }
    };
    es.onerror = () => {
        // 端点不存在（旧版程序）或连接断开：本会话退回轮询，不再反复重试 SSE
        stopSse();
        startPolling();
    };
}

function stopSse() {
    if (es) {
        es.onerror = null;
        es.close();
        es = null;
    }
}

// ===== 开关 =====

export function followStart() {
    if (follow.on) return;
    follow.on = true;
    follow.status = 'no-session';
    follow.detail = '';
    matchedKey = '';
    candidateKey = '';
    savedLoop = S.loop;
    S.loop = false;   // 循环会和外部播放器的真实进度打架
    S.follow = true;  // engine 忽略本地音频时钟，尾段不暂停不循环
    localStorage.setItem('jizura.followOn', '1');
    startSse();
    toast('跟随已开启：正在检测播放器…');
}

export function followStop() {
    if (!follow.on) return;
    follow.on = false;
    stopSse();
    stopPolling();
    if (S.playing) driveClock(false, S.t);
    S.loop = savedLoop;
    S.follow = false;
    localStorage.removeItem('jizura.followOn');
    follow.status = 'off';
    follow.detail = '';
    toast('跟随已停止（当前歌词保留在工程里）');
}

// ===== 状态应用 =====

function applyState(st: any) {
    follow.autoSelected = !!st.autoSelected;
    follow.selectedAppId = st.selectedAppId || '';
    follow.sessions = (st.sessions || []).map((s: any) => ({
        appId: s.appId, appName: s.appName, title: s.title, artist: s.artist,
        playing: s.status === 'Playing',
    }));

    if (!st.ok) {
        follow.status = 'error';
        follow.detail = st.error || 'SMTC 不可用';
        return;
    }

    const cur = st.current;
    if (!cur || (!cur.title && !cur.artist)) {
        // 没有会话/会话没有元数据：暂停渲染时间轴，歌词画面停在原地
        if (S.playing) driveClock(false, S.t);
        follow.status = cur ? 'no-meta' : 'no-session';
        follow.detail = cur ? '' : '未检测到正在播放的播放器（酷狗/网易云等需在前台或后台运行）';
        return;
    }

    follow.appName = cur.appName || '';
    follow.title = cur.title || '';
    follow.artist = cur.artist || '';

    // 切歌检测（去抖）：新歌一露头立刻冻结时间轴并清掉旧歌的匹配信息，
    // 元数据连续稳定 2 秒后才发起匹配（播放器切歌时常分多次更新元数据）
    const key = norm(follow.title) + '|' + norm(follow.artist);
    if (key !== matchedKey) {
        if (key !== candidateKey) {
            candidateKey = key;
            candidateAt = Date.now();
            if (S.playing) driveClock(false, S.t);
            follow.status = 'matching';
            follow.detail = '';
            follow.matchedAs = '';
            follow.fmt = '';
        } else if (Date.now() - candidateAt >= DEBOUNCE_MS && follow.status === 'matching') {
            matchedKey = key;
            void matchAndAdopt(cur);
        }
    }

    // 匹配中冻结时间轴（旧歌词不能跟着新歌跑）；匹配成功继续跟，
    // 匹配失败也继续跟——用户手动在「搜索」页载入歌词后无缝接上
    if (follow.status === 'matching') return;
    follow.estimated = !!cur.estimated;
    // 估算进度在歌播完到切歌之间会继续走，钳到 plan 结束附近防时间轴越跑越远
    const liveSec = Math.min((cur.positionLiveMs || 0) / 1000 + follow.offset, (S.plan?.duration || 0) + 1);
    driveClock(cur.status === 'Playing', liveSec);
}

export function followToggle() {
    follow.on ? followStop() : followStart();
}

export function followSelectSession(appId: string) {
    void fetch('/api/smtc/select?appId=' + encodeURIComponent(appId), {method: 'POST'});
}

// 匹配失败/匹配错歌后重试自动匹配（清掉已匹配标记，下一轮轮询重新走去抖匹配）
export function followRematch() {
    matchedKey = '';
    candidateKey = '';
    candidateAt = 0;
    void poll();
}

export function followNudge(delta: number) {
    follow.offset = Math.round((follow.offset + delta) * 10) / 10;
    localStorage.setItem('jizura.followOffset', String(follow.offset));
}

// 刷新进程后自动恢复（boot 里调用）；手动停止过就不再自动开
export function followRestore() {
    try {
        const o = parseFloat(localStorage.getItem('jizura.followOffset') || '0');
        if (isFinite(o)) follow.offset = o;
    } catch {
    }
    if (localStorage.getItem('jizura.followOn')) followStart();
}
