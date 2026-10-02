// planner：歌词 → 行 → 词块 → 带时刻的切分 + 事件
(() => {
    'use strict';

    J.SAMPLE_LYRICS = `夜明けの色を/覚えてる
ほどけた声が遠くで鳴った
ねえ、まだ間に合うかな
*透明*なままじゃ終われない!`;

    J.defaultProject = () => ({
        version: 1,
        timingOrder: 2, // 歌词行按书写顺序，围绕 LRC 时间戳排序（v0.10）
        themeId: null, // 一键随机的主题（J.THEMES 的 key），只有一键随机读它
        title: '', artist: '',
        lyrics: J.SAMPLE_LYRICS,
        style: 'noir', mood: null,
        extra: false, // 随机可以选用初版之后追加的手法（追加分）
        wa: true, // 和風纹样（提灯・障子・家紋…），在 extra 之后生效
        horror: false, // 独立于 extra 的手法集：ホラー（同时会启用ホラー情绪）
        typo: true, // 文字PV系 typographic 手法
        kinetic: true, // キネティック 手法
        lang: 'auto', // 歌词语言: 'auto' | 'ja' | 'zh-Hant' | 'zh-Hans' | 'ko'，决定各 font key 用哪些字体
        keyBg: 'off', // 抠像合成背景: 'off' | 'green' グリーンバック | 'black' ブラックバック
        unify: false, // 統一感：段落调色板、重复行同款、キメ、モーフ、太さ
        typeset: false, // 文字整列：假名疏排、小粒子/首字放大、西文字号、提前 0.2s、整体收敛
        centerDir: 'tb', // 竖屏中央留白方向: 'tb' = 上下, 'lr' = 左右
        centerFree: false, // 中央を空ける：切分排进两侧条带（左右或上下），中间留给角色
        centerZone: 'center', // 留白位置: center / left / right / top / bottom（设置页自定义）
        seed: 20260922,
        aspect: '16:9', res: 1080, fps: 24,
        fx: {
            motion: 0.7,
            glitch: 0.55,
            chroma: 0.7,
            decor: 0.5,
            density: 0.55,
            texture: 0.6,
            flash: true,
            onTwos: true,
            koma: 12,
            hud: 'auto',
            bgSwitch: 0.35,
            hideNo: false, // 不显示装饰性编号（No.01 等；歌词里自带的数字不受影响）
            hideTime: false // 不显示装饰性时刻（00:12.34 等）
        },
        enabled: Object.fromEntries(J.GROUP_KEYS.map(g => [g, Object.fromEntries(J.order(g).map(k => [k, true]))])),
        timing: {bpm: 0, offset: 0.4, snap: true, tail: 0.9, lineTimes: {}, lineScale: 1},
        overrides: {},
        locks: {tech: {}, params: {}}, // 随机/重抽不能动的组和值（只在 UI 侧用）
        colors: {enabled: false},
        fonts: {},
    });

    // 动画步长：koma 是 24fps 时间轴上每秒作画张数（12 = 二拍一，8 = 三拍一，0 = 每个输出帧）
    J.komaOf = fx => (fx.koma != null ? +fx.koma : (fx.onTwos === false ? 0 : 12));
    J.stepDur = (fx, fps) => {
        const k = J.komaOf(fx);
        return k > 0 ? 1 / k : 1 / (fps || 24);
    };

    J.parseLyrics = (raw) => {
        const lines = [];
        const meta = {};
        let pendingGap = false;
        const rows = String(raw || '').replace(/\r/g, '').split('\n');
        for (let ri = 0; ri < rows.length; ri++) {
            const s0 = rows[ri].trim();
            if (!s0) {
                if (lines.length) pendingGap = true;
                continue;
            }
            if (s0.startsWith('#')) continue;
            const mm = s0.match(/^\[(ti|ar|al|by|offset):(.*)\]$/i);
            if (mm) {
                meta[mm[1].toLowerCase()] = mm[2].trim();
                continue;
            }
            let s = s0;
            const times = [];
            let m;
            while ((m = s.match(/^\[(\d+):(\d+(?:[.:]\d+)?)\]/))) {
                times.push(+m[1] * 60 + parseFloat(m[2].replace(':', '.')));
                s = s.slice(m[0].length);
            }
            s = s.trim();
            // 間奏：[間奏] / [間奏 8]（8 秒），也认 [interlude] [inst] [间奏] [간주]；没歌词，只出背景和装饰
            const im = s.match(/^\[\s*(間奏|间奏|interlude|instrumental|inst|간주)(?:\s*[:：]?\s*(\d+(?:\.\d+)?)\s*(?:s|sec|秒|초)?)?\s*\]$/i);
            if (im) {
                const base = {
                    text: '',
                    interlude: true,
                    secs: im[2] ? parseFloat(im[2]) : null,
                    note: null,
                    impact: false,
                    emph: [],
                    manual: null,
                    gapBefore: pendingGap,
                    src: ri
                };
                pendingGap = false;
                if (times.length) times.forEach(t => lines.push(Object.assign({}, base, {lrc: t})));
                else lines.push(Object.assign({}, base, {lrc: null}));
                continue;
            }
            let note = null;
            const bar = s.indexOf('|');
            if (bar >= 0) {
                note = s.slice(bar + 1).trim() || null;
                s = s.slice(0, bar).trim();
            }
            let impact = false;
            if (/[!！]$/.test(s) && s.length > 1) {
                impact = true;
                s = s.slice(0, -1).trim();
            }
            const emph = [];
            s = s.replace(/\*([^*]+)\*/g, (_, w) => {
                emph.push(w);
                return w;
            });
            let manual = null;
            if (s.includes('/')) {
                manual = s.split('/').map(x => x.trim()).filter(Boolean);
                const latin = manual.some(x => /[A-Za-z]/.test(x));
                s = manual.join(latin ? ' ' : '');
            }
            if (!s) continue;
            const base = {text: s, note, impact, emph, manual, gapBefore: pendingGap, src: ri};
            pendingGap = false;
            if (times.length) times.forEach(t => lines.push(Object.assign({}, base, {lrc: t})));
            else lines.push(Object.assign({}, base, {lrc: null}));
        }
        // LRC：按时间排序；无戳行（间奏、手工补的行）跟在文本里前一行后面，旧版会把它们挪到最后（v0.10 起）
        // 混合 LRC 不再要求整份都有戳才排：有戳行用戳，无戳行保持书写位置
        if (lines.some(l => l.lrc != null)) {
            let key = -1, k = 0;
            lines.forEach(l => {
                if (l.lrc != null) {
                    key = l.lrc;
                    k = 0;
                    l._key = l.lrc;
                } else l._key = key + 1e-6 * ++k;
            });
            lines.sort((a, b) => a._key - b._key);
            lines.forEach(l => {
                delete l._key;
            });
        }
        return {lines, meta};
    };
    // v0.10 之前的行序（无戳行排最后），只用来把老工程的逐行设置搬过来
    J.parseOrderV1 = (raw) => {
        const p = J.parseLyrics(raw), lines = p.lines.slice();
        if (!lines.some(l => l.lrc != null) || lines.every(l => l.lrc != null)) return null;
        const byRow = lines.map((l, i) => ({l, i}));
        const old = byRow.slice().sort((a, b) => ((a.l.lrc ?? 1e9) - (b.l.lrc ?? 1e9)) || (a.l.src - b.l.src));
        return old.map(x => x.i); // 旧索引 → 新索引
    };

    const segmenters = {}; // 每种歌词语言一个（J.segLocale: ja / zh-Hant / zh-Hans / ko）
    const segmenterOf = () => {
        if (typeof Intl === 'undefined' || !Intl.Segmenter) return null;
        const loc = J.segLocale ? J.segLocale() : 'ja';
        if (!(loc in segmenters)) {
            try {
                segmenters[loc] = new Intl.Segmenter(loc, {granularity: 'word'});
            } catch (e) {
                segmenters[loc] = null;
            }
        }
        return segmenters[loc];
    };
    const segType = s => {
        if (/^\s+$/.test(s)) return 'S';
        if ([...s].every(c => J.isPunct(c))) return 'P';
        if ([...s].some(c => J.isKanji(c))) return 'K';
        if ([...s].every(c => J.isHira(c) || c === 'ー')) return 'H';
        if ([...s].every(c => J.isKata(c) || c === 'ー')) return 'T';
        if (/[A-Za-z0-9]/.test(s)) return 'L';
        return 'O';
    };
    J.segments = (text) => {
        const segmenter = segmenterOf();
        if (segmenter) return [...segmenter.segment(text)].map(x => x.segment);
        const out = [];
        let cur = '', ct = '';
        for (const c of text) {
            const t = segType(c);
            if (cur && t !== ct && !(ct === 'K' && t === 'H')) {
                out.push(cur);
                cur = '';
            }
            cur += c;
            ct = t;
        }
        if (cur) out.push(cur);
        return out;
    };
    // 拉丁/韩文辅助：词间补空格，连字符后不补（never- + ending → never-ending）
    const WORDCH = /[A-Za-z\u00c0-\u024f0-9\uac00-\ud7af\u1100-\u11ff\u3130-\u318f]/;
    J.isWordLike = t => /^[A-Za-z\u00c0-\u024f0-9\uac00-\ud7af'’.,!?‐–—-]+$/.test(t) && WORDCH.test(t);
    J.joinWords = arr => {
        const latin = /[A-Za-z]/.test(arr.join('')), HG = /[\uac00-\ud7af]/;
        return arr.reduce((acc, w, i) => {
            if (!i) return w;
            if (/[-‐–—]$/.test(acc)) return acc + w;
            return acc + (latin || (HG.test([...acc].pop()) && HG.test([...w][0])) ? ' ' : '') + w;
        }, '');
    };
    // 以拉丁字母为主的文本（英语、印尼语、越南语……歌词）
    J.isLatinText = t => {
        const s = String(t || '').replace(/\s/g, '');
        if (!s) return false;
        const n = (s.match(/[A-Za-z\u00c0-\u024f\u1e00-\u1eff]/g) || []).length;
        return n / [...s].length >= 0.6;
    };
    J.chunkText = (text) => {
        const segs = J.segments(text);
        const chunks = [];
        let cur = null;
        const close = () => {
            if (cur && cur.s.trim()) chunks.push(cur.s.trim());
            cur = null;
        };
        for (const sg of segs) {
            const t = segType(sg);
            if (t === 'S') {
                close();
                continue;
            }
            if (t === 'P') {
                if (cur) cur.s += sg; else if (chunks.length) chunks[chunks.length - 1] += sg; else cur = {
                    s: sg,
                    k: 'P',
                    hasH: false
                };
                continue;
            }
            if (!cur) {
                cur = {s: sg, k: t, hasH: t === 'H'};
                continue;
            }
            if (t === 'H') {
                const len = [...sg].length;
                if (len <= 3 || (cur.k !== 'H' && !cur.hasH) || (cur.k === 'H' && [...cur.s].length + len <= 4)) {
                    cur.s += sg;
                    cur.hasH = true;
                    continue;
                }
                close();
                cur = {s: sg, k: 'H', hasH: true};
                continue;
            }
            if (t === 'K' && cur.k === 'K' && !cur.hasH && [...(cur.s + sg)].length <= 6) {
                cur.s += sg;
                continue;
            }
            if (t === 'T' && cur.k === 'T') {
                cur.s += sg;
                continue;
            }
            if (t === 'L' && cur.k === 'L') {
                cur.s += sg;
                continue;
            }
            close();
            cur = {s: sg, k: t, hasH: t === 'H'};
        }
        close();
        // 拆超长词块，合并落单的假名
        const out = [];
        for (const c of chunks) {
            const n = [...c].length;
            if (n > 10 && J.isWordLike(c)) {
                // 很长的拉丁/韩文词绝不在词中间切，只在连字符后面分（never-ending → never- / ending）
                const parts = c.split(/(?<=[-‐–—])(?=\S)/);
                if (parts.length > 1) parts.forEach(x => out.push(x));
                else out.push(c);
            } else if (n > 10) {
                J.splitLines(c, Math.ceil(n / Math.ceil(n / 8))).split('\n').forEach(x => out.push(x));
            } else out.push(c);
        }
        for (let i = out.length - 1; i > 0; i--) {
            if ([...out[i]].length === 1 && !J.isKanji(out[i])) {
                out[i - 1] += (WORDCH.test([...out[i - 1]].pop()) && WORDCH.test(out[i]) && !/[-‐–—]$/.test(out[i - 1]) ? ' ' : '') + out[i];
                out.splice(i, 1);
            }
        }
        return out.length ? out : [text];
    };

    // 英文歌词按短语切，不逐词（一个日语词块大约相当于 2–3 个英文单词）
    J.phraseChunks = (words) => {
        const out = [];
        let cur = [], letters = 0;
        const flush = () => {
            if (cur.length) out.push(J.joinWords(cur));
            cur = [];
            letters = 0;
        };
        for (const w of words) {
            const n = (w.match(/[A-Za-z\u00c0-\u024f0-9]/g) || []).length;
            cur.push(w);
            letters += n;
            if (letters >= 9 || cur.length >= 3 || /[,.;:!?]$/.test(w)) flush();
        }
        flush();
        // 结尾落单的短词并进上一段
        if (out.length >= 2 && out[out.length - 1].replace(/[^A-Za-z]/g, '').length <= 4) {
            const last = out.pop();
            out[out.length - 1] = J.joinWords([out[out.length - 1], last]);
        }
        return out.length ? out : words;
    };

    J.computeTiming = (project, parsed, audio) => {
        const T = project.timing || {};
        const lines = parsed.lines;
        const beat = T.bpm > 0 ? 60 / T.bpm : 0;
        // 固定时刻：手工设定的时间（打字/打点/拖动）优先于 LRC 标签，其余行走估算
        const fixed = lines.map((l, i) => {
            const man = T.lineTimes && T.lineTimes[i] != null ? +T.lineTimes[i] : null;
            if (man != null && isFinite(man)) return Math.max(0, man);
            return l.lrc != null && isFinite(l.lrc) ? l.lrc : null;
        });
        // 固定时刻不能倒退到更早的固定时刻之前（保存的值本身不动）
        let lastFix = -Infinity;
        for (let i = 0; i < fixed.length; i++) if (fixed[i] != null) {
            if (fixed[i] < lastFix + 0.05) fixed[i] = lastFix + 0.05;
            lastFix = fixed[i];
        }
        const natural = i => { // 第 i 行的估算时长
            const n = [...lines[i].text].length, L = lines[i];
            let d = L.interlude ? (L.secs > 0 ? L.secs : 4) : J.clamp(0.8 + n * 0.17, 1.3, 5.2) * (T.lineScale || 1);
            if (beat && !(L.interlude && L.secs > 0)) d = Math.max(2, Math.round(d / beat)) * beat;
            return d;
        };
        const gapOf = i => (i > 0 && i < lines.length && lines[i].gapBefore ? (beat ? beat * 2 : 0.8) : 0);
        const starts = new Array(lines.length);
        let i = 0, t = T.offset ?? 0.4;
        if (fixed.length && fixed[0] != null) t = fixed[0];
        while (i < lines.length) {
            if (fixed[i] != null) {
                starts[i] = fixed[i];
                t = fixed[i];
                i++;
                continue;
            }
            // 一段估算行 [i, j)，夹在上一个起点与下一个固定时刻之间（若有的话）
            let j = i;
            while (j < lines.length && fixed[j] == null) j++;
            if (i === 0 && j < lines.length && t >= fixed[j]) t = 0;
            const from = i > 0 ? starts[i - 1] : t;
            // 链式推进方式与旧版一致（无固定时刻时 plan 结果完全不变）
            for (let k = i; k < j; k++) starts[k] = k > 0 ? starts[k - 1] + natural(k - 1) + gapOf(k) : t;
            if (j < lines.length) {
                // 这些行都必须在下一个固定时刻之前开始：把它们挤进可用的空间里
                const room = fixed[j] - from, need = starts[j - 1] + natural(j - 1) + gapOf(j) - from;
                if (need > room) {
                    const f = room > 0 ? room / need : 0;
                    for (let k = i; k < j; k++) starts[k] = from + (starts[k] - from) * f;
                }
            }
            i = j;
        }
        const ends = starts.map((s, i) => {
            if (i < starts.length - 1) return Math.max(s + 0.35, starts[i + 1]);
            const n = [...lines[i].text].length, L = lines[i];
            let d = L.interlude ? (L.secs > 0 ? L.secs : 4) : J.clamp(0.8 + n * 0.17, 1.5, 5.2) * (T.lineScale || 1);
            if (beat && !(L.interlude && L.secs > 0)) d = Math.max(2, Math.round(d / beat)) * beat;
            return s + d;
        });
        let duration = (ends.length ? ends[ends.length - 1] : 3) + (T.tail ?? 0.9);
        if (audio && audio.duration && T.useAudioLength !== false) duration = Math.max(audio.duration, ends.length ? ends[ends.length - 1] + 0.2 : 1);
        return {starts, ends, duration};
    };

    const wkey = (obj, k, d = 1) => (obj && obj[k] != null ? obj[k] : d);

    function cutTechOf(ov, k) {
        const t = (ov.cutTech && (ov.cutTech[k] || ov.cutTech[String(k)])) || {};
        const fromLay = ov.cutLayouts && (ov.cutLayouts[k] || ov.cutLayouts[String(k)]);
        return fromLay && !t.layout ? Object.assign({}, t, {layout: fromLay}) : t;
    }

    J.plan = (project, audio) => {
        const st = J.resolveStyle(project);
        const fx = Object.assign({}, J.defaultProject().fx, project.fx || {});
        const parsed = J.parseLyrics(project.lyrics);
        const title = project.title || parsed.meta.ti || '';
        const artist = project.artist || parsed.meta.ar || '';
        const tm = J.computeTiming(project, parsed, audio);
        // 文字整列：歌词比人声早 0.2s 出现，提前读着才觉得合拍
        if (project.typeset) {
            const LEAD = 0.2;
            tm.starts = tm.starts.map(t => Math.max(0, t - LEAD));
            tm.ends = tm.ends.map((t, i) => Math.max(tm.starts[i] + 0.3, t - LEAD));
        }
        const [W, H] = J.designSize(project.aspect);
        // enabled：没显式关掉的都算开（老工程里新手法包的条目自动可用）；
        // 追加分/和風 开关决定随机能选什么（逐行 override 不受影响）
        const en = {};
        for (const g of J.GROUP_KEYS) {
            en[g] = {};
            const src = (project.enabled || {})[g] || {};
            for (const k of J.order(g)) en[g][k] = src[k] !== false && (!J.randomOk || J.randomOk(project, g, k));
        }
        // 中央を空ける（给角色留位）：每个切分排进侧边条带，宽帧左右、竖帧上下，逐行交替；中间只留整帧背景和屏幕特效
        const zones = project.centerFree ? J.sideZones(W, H, project.centerDir, project.centerGap, project.centerZone) : null;
        // 每个切分的歌词拆两半：前半进条带 0（左/上），后半进条带 1（右/下）
        const zoneOf = () => (zones ? Object.assign({}, zones[0]) : null);
        if (zones && en.bg) en.bg.bigChar = false; // 唯一会自己画歌词的背景（大字居中）
        const plan = {
            version: 1, generator: 'JIZURA', appVersion: '@VERSION@', title, artist, W, H, fps: project.fps || 24,
            duration: tm.duration, styleKey: project.style, style: st, fx, seed: project.seed,
            lines: [], cuts: [], events: [], beats: audio && audio.beats ? audio.beats.slice() : [],
            hud: fx.hud === 'on' ? true : fx.hud === 'off' ? false : !!st.hud,
            keyBg: J.keyMode ? J.keyMode(project) : null, // 'green' | 'black' | null，抠像合成背景
            centerFree: !!zones, zones,
            typeset: !!project.typeset, unify: !!project.unify,
            lang: J.resolveLang ? J.resolveLang(project) : 'ja', // 歌词语言（auto → 检测结果）
        };
        if (J.setLang) J.setLang(plan.lang); // 下面的切词和排版量测都用这个语言
        if (J.setTypeset) J.setTypeset(plan.typeset);
        const beats = plan.beats;
        const snap = (t) => {
            if (!beats.length || !(project.timing && project.timing.snap)) return t;
            let lo = 0, hi = beats.length - 1;
            while (lo < hi) {
                const mid = (lo + hi) >> 1;
                if (beats[mid] < t) lo = mid + 1; else hi = mid;
            }
            let best = t, bd = 0.13;
            for (const k of [lo - 1, lo]) if (k >= 0 && k < beats.length && Math.abs(beats[k] - t) < bd) {
                bd = Math.abs(beats[k] - t);
                best = beats[k];
            }
            return best;
        };
        const history = [], bgHistory = [], fxHistory = [];
        let schemeIdx = 0;
        const nSchemes = st.schemes.length;
        const addEvent = (t, type, amp, dur) => plan.events.push({t, type, amp, dur});
        const evOwner = new WeakMap();
        Object.defineProperty(plan, 'evOwner', {value: evOwner});

        // 片头标题卡
        const firstStart = tm.starts.length ? tm.starts[0] : 0;
        if (title && firstStart >= 1.1) {
            const rng = J.rng(J.h(project.seed, 999));
            plan.cuts.push(makeCut({
                text: title,
                note: artist,
                lineText: title,
                line: -1,
                start: 0.1,
                end: firstStart - 0.04,
                layout: 'title',
                enter: rng.pick(['blur', 'type', 'wipe', 'assemble']),
                exit: rng.pick(['blur', 'drift', 'wipe']),
                hold: 'still',
                params: J.LAYOUTS.title.plan(rng, {}, st),
                decor: [],
                scheme: 0,
                seed: J.h(project.seed, 999, 1)
            }));
        }

        // 統一感：段落、重复行、キメ 行和各段调色板，见下面的 makeUnify
        const U = plan.unify ? makeUnify(parsed.lines, {
            st,
            en,
            fx,
            history,
            lang: plan.lang,
            seed: project.seed
        }) : null;
        parsed.lines.forEach((ln, li) => {
            const s = tm.starts[li], e = tm.ends[li];
            const ov = (project.overrides || {})[li] || {};
            const lineSeed = ov.lock && ov.lockedSeed != null ? ov.lockedSeed : J.h(project.seed, li + 1, ov.seed | 0);
            const rng = J.rng(lineSeed);
            if (ln.interlude) { // [間奏]：只有背景、装饰和屏幕特效
                plan.lines.push({
                    index: li,
                    src: ln.src,
                    lrc: ln.lrc,
                    text: '',
                    interlude: true,
                    secs: ln.secs,
                    start: s,
                    end: e,
                    visEnd: e,
                    note: null,
                    impact: false,
                    emph: [],
                    chunks: [],
                    seed: lineSeed
                });
                const bg = ov.bg && J.BG[ov.bg] ? ov.bg : pickBg(rng, st, en, fx, bgHistory);
                bgHistory.push(bg);
                const dur = e - s, showTitle = dur >= 6 && !!(title || artist);
                plan.cuts.push(makeCut({
                    text: '',
                    lineText: '',
                    line: li,
                    start: s,
                    end: e,
                    layout: 'interlude',
                    enter: 'blur',
                    exit: 'blur',
                    hold: 'still',
                    inDur: 0.4,
                    outDur: 0.4,
                    params: {
                        variant: 'quiet',
                        showTitle,
                        titleText: showTitle ? [title, artist].filter(Boolean).join('  /  ') : ''
                    },
                    decor: Array.isArray(ov.decor) ? ov.decor.filter(id => J.DECOR[id]).map(id => decorParams(rng, id)) : pickDecor(rng, st, en, Object.assign({}, fx, {decor: Math.max(0.6, fx.decor)}), 'interlude', history),
                    scheme: schemeIdx,
                    seed: J.h(lineSeed, 405),
                    bg,
                    bgP: J.BG[bg] && J.BG[bg].plan ? J.BG[bg].plan(rng, st) : {},
                    cam: 'push',
                    camP: {}
                }));
                if (en.fx == null || en.fx.chroma !== false) addEvent(s, 'chroma', 1 + fx.chroma, 0.25);
                for (let t = s + 1.2; t < e - 0.8; t += J.clamp(dur / 4, 1.6, 3.2)) { // 长间奏加几个特效点缀，免得太死
                    const pick = pickFx(rng, st, en, fx, false, fxHistory, 'mid');
                    if (pick) {
                        const D2 = J.FXE[pick];
                        addEvent(t, pick, (D2.amp || 1) * 0.6, (D2.dur || 3) / 24);
                        fxHistory.push(pick);
                    }
                }
                return;
            }
            const n = [...ln.text.replace(/\s+/g, '')].length;
            const visEnd = Math.min(e, s + Math.max(3.6, n * 0.5 + 1.2));
            const D = visEnd - s;
            plan.lines.push({
                index: li,
                src: ln.src,
                lrc: ln.lrc,
                text: ln.text,
                start: s,
                end: e,
                visEnd,
                note: ln.note,
                impact: ln.impact,
                emph: ln.emph,
                chunks: null,
                seed: lineSeed
            });
            const chunks = ln.manual || (plan.lang === 'en' ? J.phraseChunks(J.chunkText(ln.text)) : J.chunkText(ln.text));
            plan.lines[li].chunks = chunks;
            const L = J.lerp(1.3, 0.5, fx.density);
            let nC = Math.round(D / L);
            const maxC = chunks.length + (chunks.length >= 2 && D > 2.0 ? 1 : 0);
            nC = J.clamp(nC, 1, Math.max(1, maxC));
            const ovAny = Object.keys(ov).some(k2 => !['lock', 'lockedSeed', 'seed', 'cutTech', 'cutLayouts', 'cutQuiet', 'cutTime'].includes(k2));
            const kime = !!(U && U.kime.has(li) && !ov.cuts);
            if (ov.single || kime) nC = 1;
            if (zones) nC = Math.max(1, Math.min(nC, Math.floor(chunks.length / 2))); // 中央を空ける：每个切分要拆成两半，所以每半至少留 2 个词
            // 指定切分数（按行）：就切这么多刀，块不够就继续拆
            const fixedN = ov.cuts > 0 ? Math.min(12, ov.cuts | 0) : 0;
            let chunks2 = chunks;
            if (fixedN) {
                nC = fixedN;
                chunks2 = splitToCount(chunks, fixedN);
            }
            let groups;
            const nG = Math.min(nC, chunks2.length);
            if (nG <= 1) groups = [ln.text];
            else groups = partition(chunks2, nG).map(g => J.joinWords(g));
            const recap = !fixedN && nC > groups.length && groups.length >= 2;
            let units = groups.map(g => ({text: g, w: [...g].length + 1.6}));
            if (recap) units.push({
                text: ln.text,
                w: (units.reduce((a, u) => a + u.w, 0) / units.length) * 1.25,
                recap: true
            });
            // 锁定的行也保留自己的切分，否则数量会跟着细密度滑块或おまかせ走
            if (ov.lock && Array.isArray(ov.lockedCuts) && ov.lockedCuts.length && ov.lockedCuts.every(c => c && typeof c.utext === 'string' && ln.text.includes(c.utext.trim())))
                units = ov.lockedCuts.map(c => ({text: c.utext, w: [...c.utext].length + 1.6, recap: !!c.recap}));
            const tot = units.reduce((a, u) => a + u.w, 0);
            // ロック：锁定的行原样保留锁定时的内容（版式、动态、装饰、配色、强调）；
            // 重抽别的行会动共享的「最近用过」状态，光靠 seed 不够
            const lockSpecs = ov.lock && Array.isArray(ov.lockedCuts) && ov.lockedCuts.length === units.length
            && ov.lockedCuts.every((c, k2) => c && c.utext === units[k2].text && J.LAYOUTS[c.layout]) ? ov.lockedCuts : null;
            let acc = s;
            const bounds = [s];
            units.forEach((u, k) => {
                acc += D * u.w / tot;
                bounds.push(k === units.length - 1 ? visEnd : acc);
            });
            for (let k = 1; k < bounds.length - 1; k++) {
                // 相邻边界间距 < 0.44s 时下界会大于上界，clamp 要先归序，防止出现 start>end 的负时长 cut
                const lo = bounds[k - 1] + 0.22, hi = bounds[k + 1] - 0.22;
                bounds[k] = J.clamp(snap(bounds[k]), Math.min(lo, hi), Math.max(lo, hi));
            }
            // 切分开始时刻（详细模式）：行首起的秒数，手工填或打点（Tab），保持顺序、互距至少 0.22s
            if (ov.cutTime && typeof ov.cutTime === 'object') {
                const nb = bounds.length - 1;
                for (let k = 1; k < nb; k++) {
                    const d = +ov.cutTime[k];
                    if (ov.cutTime[k] != null && isFinite(d)) bounds[k] = J.clamp(s + d, s + 0.22 * k, visEnd - 0.22 * (nb - k));
                }
                for (let k = 1; k < nb; k++) if (bounds[k] < bounds[k - 1] + 0.22) bounds[k] = bounds[k - 1] + 0.22;
                for (let k = nb - 1; k >= 1; k--) if (bounds[k] > bounds[k + 1] - 0.22) bounds[k] = bounds[k + 1] - 0.22;
            }
            // 配色方案按行切换
            if (nSchemes > 1 && li > 0 && (U ? U.sectionStart(li) && rng.chance(0.25 + fx.bgSwitch) : rng.chance(fx.bgSwitch * (ln.impact ? 1.8 : 1)))) schemeIdx = (schemeIdx + 1 + rng.int(0, nSchemes - 2)) % nSchemes;
            const emphLine = ln.impact || ln.emph.length > 0;
            // 背景图形按行选，偶尔在切分上重抽
            let lineBg = ov.bg && J.BG[ov.bg] ? ov.bg : pickBg(rng, st, en, fx, bgHistory);
            bgHistory.push(lineBg);
            let lineBgP = J.BG[lineBg] && J.BG[lineBg].plan ? J.BG[lineBg].plan(rng, st) : {};
            units.forEach((u, k) => {
                const cs = bounds[k], ce = bounds[k + 1], dur = ce - cs;
                const halves = zones ? splitHalf(u.text, plan.lang) : null; // 中央を空ける：「花が」｜「咲いた」
                const txt = halves ? halves[0] : u.text;
                const nn = Math.max(...(halves || [u.text]).map(t => [...t.replace(/\s+/g, '')].length));
                const emph = kime || ln.impact && (k === 0 || u.recap) || ln.emph.some(w => u.text.includes(w));
                const Z = zoneOf(), LW = Z ? Z.w : W, LH = Z ? Z.h : H; // 这个切分布局用的画面区域
                const UU = U && !ovAny ? U : null; // 逐行设置优先于統一感
                const tech = cutTechOf(ov, k); // 只作用于这个切分的指定
                let layout = ov.layout && J.LAYOUTS[ov.layout] ? ov.layout : pickLayout(rng, st, en, nn, dur, history, emph, u.recap, LH > LW);
                if (UU) layout = UU.layout(li, layout, {nn, dur, emph, kime, rng, portrait: LH > LW, recap: u.recap});
                let enter = ov.enter && J.ENTER[ov.enter] ? ov.enter : pickEnter(rng, st, en, layout, dur, history, emph, nn);
                let exit = ov.exit && J.EXIT[ov.exit] ? ov.exit : pickExit(rng, st, en, layout, dur, k === units.length - 1, history);
                let hold = ov.hold && J.HOLD[ov.hold] ? ov.hold : pickHold(rng, en, fx, history);
                let weightGrow = false;
                if (UU) {
                    enter = UU.enter(li, enter, {layout, dur, emph, kime, rng, nn});
                    exit = UU.exit(li, exit, {layout, dur, kime, rng});
                    hold = UU.hold(li, hold, {kime, rng});
                    weightGrow = UU.weightGrow({kime, nn, rng, dur});
                    if (weightGrow) enter = UU.softEnter(enter, rng);
                }
                const durs = (en2, ex2) => {
                    let a = J.clamp(dur * 0.36, 0.12, 0.6);
                    if (en2 === 'type') a = J.clamp(nn * 0.055 + 0.1, 0.15, dur * 0.65);
                    if (en2 === 'assemble') a = J.clamp(dur * 0.45, 0.22, 0.75);
                    if (J.ENTER[en2] && J.ENTER[en2].inDur) a = J.ENTER[en2].inDur(dur, nn);
                    if (en2 === 'cut') a = 0.12;
                    let b = ex2 === 'cut' ? 0 : J.clamp(dur * 0.3, 0.14, 0.55);
                    if (['explode', 'fall', 'drift'].includes(ex2)) b = J.clamp(dur * 0.38, 0.25, 0.7);
                    if (J.EXIT[ex2] && J.EXIT[ex2].outDur) b = J.EXIT[ex2].outDur(dur, nn);
                    if (a + b > dur * 0.92) {
                        const f = dur * 0.92 / (a + b);
                        a *= f;
                        b *= f;
                    }
                    return [a, b];
                };
                let [inDur, outDur] = durs(enter, exit);
                let sch = schemeIdx;
                if (!U && nSchemes > 1 && k > 0 && rng.chance(0.12 * fx.bgSwitch)) sch = (schemeIdx + 1) % nSchemes;
                let LD = J.LAYOUTS[layout];
                let params = LD.plan(rng, {text: txt, n: nn, W: LW, H: LH, dur}, st);
                let decor = Array.isArray(ov.decor) ? ov.decor.filter(id => J.DECOR[id]).map(id => decorParams(rng, id)) : pickDecor(rng, st, en, fx, layout, history);
                let treat = ov.treat && J.TREAT[ov.treat] ? ov.treat : pickTreat(rng, st, en, fx, LD, emph, history);
                if (UU) {
                    decor = UU.decor(li, decor, {layout, kime, rng});
                    treat = UU.treat(li, treat, {kime, rng, LD});
                }
                let treatP = J.TREAT[treat].plan ? J.TREAT[treat].plan(rng, st) : {};
                if (!ov.bg && k > 0 && !U && rng.chance(0.18 * fx.bgSwitch + 0.04)) {
                    lineBg = pickBg(rng, st, en, fx, bgHistory);
                    lineBgP = J.BG[lineBg].plan ? J.BG[lineBg].plan(rng, st) : {};
                }
                let bg = LD.busy && !(J.BG[lineBg] && J.BG[lineBg].subtle) ? 'none' : lineBg;
                let cam = ov.cam && J.CAMERA[ov.cam] ? ov.cam : pickCam(rng, st, en, fx, LD, emph, history);
                if (UU) cam = UU.cam(li, cam, {kime, emph, rng});
                let camP = J.CAMERA[cam].plan ? J.CAMERA[cam].plan(rng, st) : {};
                let cutSeed = J.h(lineSeed, k, 17);
                // 統一感：重复出现的行（サビ 等）和第一次完全一样
                const again = UU ? UU.again(li, k, txt) : null;
                if (again) {
                    ({layout, enter, exit, hold, params, decor, treat, treatP, cam, camP, weightGrow} = again);
                    sch = again.scheme;
                    cutSeed = again.seed;
                    LD = J.LAYOUTS[layout];
                    if (again.bg && again.bg !== 'none') {
                        bg = again.bg;
                        lineBgP = again.bgP;
                        lineBg = bg;
                    }
                    [inDur, outDur] = durs(enter, exit);
                }
                const LS = lockSpecs ? lockSpecs[k] : null;
                if (LS) {
                    ({layout, enter, exit, hold, params, decor, treat, treatP, cam, camP} = LS);
                    if (!J.ENTER[enter]) enter = 'blur';
                    if (!J.EXIT[exit]) exit = 'blur';
                    if (!J.HOLD[hold]) hold = 'still';
                    if (!J.TREAT[treat]) {
                        treat = 'none';
                        treatP = {};
                    }
                    if (!J.CAMERA[cam]) {
                        cam = 'push';
                        camP = {};
                    }
                    decor = (decor || []).filter(d => J.DECOR[d.id]);
                    weightGrow = !!LS.weightGrow;
                    sch = LS.scheme | 0;
                    cutSeed = LS.seed;
                    LD = J.LAYOUTS[layout];
                    bg = LS.bg && J.BG[LS.bg] ? LS.bg : 'none';
                    if (bg !== 'none') {
                        lineBg = bg;
                        lineBgP = LS.bgP || {};
                    }
                    inDur = LS.inDur;
                    outDur = LS.outDur;
                }
                // 单切分指定：叠加在抽取结果之上，用独立的随机流，改一个切分不会挪动其他切分
                // （下面的 history 记录的仍是画出来的东西，当作这里什么都没改）
                const drawn = {layout, enter, exit, hold, treat, cam, decor: decor.map(d => d.id)};
                let techBgP = null;
                if (tech.layout && J.LAYOUTS[tech.layout] && !J.LAYOUTS[tech.layout].special) {
                    layout = tech.layout;
                    LD = J.LAYOUTS[layout];
                    try {
                        params = LD.plan(J.rng(J.h(lineSeed, k, 91)), {text: txt, n: nn, W: LW, H: LH, dur}, st);
                    } catch (e) {
                    }
                }
                if (tech.enter && J.ENTER[tech.enter]) enter = tech.enter;
                if (tech.exit && J.EXIT[tech.exit]) exit = tech.exit;
                if (tech.hold && J.HOLD[tech.hold]) hold = tech.hold;
                if (tech.decor !== undefined) {
                    if (!tech.decor || tech.decor === 'none' || !J.DECOR[tech.decor]) decor = [];
                    else decor = [decorParams(J.rng(J.h(lineSeed, k, 92)), tech.decor)];
                }
                if (tech.treat && J.TREAT[tech.treat]) {
                    treat = tech.treat;
                    treatP = J.TREAT[treat].plan ? J.TREAT[treat].plan(J.rng(J.h(lineSeed, k, 93)), st) : {};
                }
                if (tech.bg && J.BG[tech.bg]) {
                    bg = tech.bg;
                    techBgP = J.BG[bg].plan ? J.BG[bg].plan(J.rng(J.h(lineSeed, k, 94)), st) : {};
                }
                if (tech.cam && J.CAMERA[tech.cam]) {
                    cam = tech.cam;
                    camP = J.CAMERA[cam].plan ? J.CAMERA[cam].plan(J.rng(J.h(lineSeed, k, 95)), st) : {};
                }
                if (tech.enter || tech.exit) { // 动作没变就保留（锁定的）时长
                    const [i2, o2] = durs(enter, exit);
                    if (tech.enter && J.ENTER[tech.enter]) inDur = i2;
                    if (tech.exit && J.EXIT[tech.exit]) outDur = o2;
                }
                // 切分间转场（顶掉上一刀的 exit 和这一刀的 enter）
                const prevCut = plan.cuts[plan.cuts.length - 1];
                let trans = null, transP = {}, transDur = 0, morph = null;
                const joinSaved = {enter, inDur, prevExit: prevCut && prevCut.exit, prevOut: prevCut && prevCut.outDur};
                // 锁定行保留自己的 exit：下一个未锁定行不许用转场/モーフ顶掉它
                const prevLockedOther = prevCut && prevCut.line !== li && !LS && ((project.overrides || {})[prevCut.line] || {}).lock;
                const canTrans = prevCut && Math.abs(prevCut.end - cs) < 0.06 && prevCut.layout !== 'interlude' && dur > 0.5 && !prevLockedOther;
                // 統一感：モーフ——同一行的下一部分从这一部分里长出来（共享字滑动，其余融化）
                if (LS) { // 锁定行：切分仍相接时沿用之前的接法
                    if (canTrans && LS.morph) {
                        morph = {dur: LS.morph.dur};
                        prevCut.exit = 'cut';
                        prevCut.outDur = 0;
                    } else if (canTrans && LS.trans && J.TRANS[LS.trans]) {
                        trans = LS.trans;
                        transP = LS.transP || {};
                        transDur = LS.transDur;
                        prevCut.exit = 'cut';
                        prevCut.outDur = 0;
                    }
                } else if (canTrans && UU && k > 0 && !kime && (again ? again.morph : rng.chance(u.recap ? 0.85 : 0.4))) {
                    morph = {dur: J.clamp(dur * 0.45, 0.28, 0.6)};
                    enter = 'cut';
                    inDur = 0.12;
                    prevCut.exit = 'cut';
                    prevCut.outDur = 0;
                } else if (canTrans && again && !ov.trans) { // 統一感：重复行的切分接法和第一次相同
                    if (again.trans && J.TRANS[again.trans]) {
                        trans = again.trans;
                        transP = again.transP || {};
                        transDur = again.transDur;
                        enter = 'cut';
                        inDur = 0.12;
                        prevCut.exit = 'cut';
                        prevCut.outDur = 0;
                    }
                } else if (canTrans) {
                    trans = ov.trans && J.TRANS[ov.trans] ? ov.trans : pickTrans(rng, st, en, fx, emph, history);
                    if (trans && UU) trans = UU.trans(li, trans, {rng});
                    if (trans) {
                        const TD = J.TRANS[trans];
                        transDur = J.clamp(TD.dur || 0.35, 0.12, Math.min(0.6, dur * 0.45));
                        transP = TD.plan ? TD.plan(rng, st) : {};
                        enter = 'cut';
                        inDur = 0.12;
                        prevCut.exit = 'cut';
                        prevCut.outDur = 0;
                    }
                }
                drawn.trans = trans;
                if (tech.trans === 'none') { // 单切分指定「つなぎなし」：撤销接缝
                    if (trans || morph) {
                        enter = joinSaved.enter;
                        inDur = joinSaved.inDur;
                        if (prevCut) {
                            prevCut.exit = joinSaved.prevExit;
                            prevCut.outDur = joinSaved.prevOut;
                        }
                    }
                    trans = null;
                    transP = {};
                    transDur = 0;
                    morph = null;
                } else if (tech.trans && J.TRANS[tech.trans] && canTrans) {
                    morph = null;
                    trans = tech.trans;
                    const TD = J.TRANS[trans];
                    transDur = J.clamp(TD.dur || 0.35, 0.12, Math.min(0.6, dur * 0.45));
                    transP = TD.plan ? TD.plan(J.rng(J.h(lineSeed, k, 96)), st) : {};
                }
                if (trans && canTrans) {
                    enter = 'cut';
                    inDur = 0.12;
                    prevCut.exit = 'cut';
                    prevCut.outDur = 0;
                }
                const cut = makeCut({
                    text: txt,
                    lineText: ln.text,
                    note: ln.note,
                    line: li,
                    start: cs,
                    end: ce,
                    layout,
                    enter,
                    exit,
                    hold,
                    inDur,
                    outDur,
                    params,
                    decor,
                    scheme: sch,
                    seed: cutSeed,
                    emph,
                    recap: !!u.recap,
                    words: J.chunkText(txt),
                    stagger: rng.range(0.025, 0.06),
                    treat,
                    treatP,
                    bg,
                    bgP: techBgP || (bg === lineBg ? lineBgP : {}),
                    cam,
                    camP,
                    trans,
                    transP,
                    transDur,
                    zone: Z,
                    utext: u.text
                });
                if (kime || (LS && LS.kime)) cut.kime = true;
                if (weightGrow) cut.weightGrow = true;
                if (morph) cut.morph = morph;
                if (UU) UU.remember(li, k, txt, cut);
                if (zones) splitCut(cut, halves, zones, st, dur, LS);
                // 文字整列：特效不叠加——至多一个装饰，上面不再叠文字处理
                if (plan.typeset) {
                    cut.decor = cut.decor.slice(0, 1);
                    if (cut.decor.length && cut.treat !== 'none') {
                        cut.treat = 'none';
                        cut.treatP = {};
                    }
                }
                plan.cuts.push(cut);
                const evMark = plan.events.length;
                // history 记抽取结果（含通常的接缝），单切分的改选才不会挪动后面的切分
                const hist = {layout, enter, exit, hold, treat, cam, trans, decor: decor.map(d => d.id)};
                for (const g of Object.keys(hist)) if (tech[g] !== undefined && drawn[g] !== undefined) hist[g] = g === 'enter' && drawn.trans ? 'cut' : drawn[g];
                history.push(hist);
                // 切分开始处的事件——时长按 24fps 时间轴计，任何输出帧率下观感一致
                const g = fx.glitch * (st.glitchBoost || 1);
                const fxOn = k2 => en.fx == null || en.fx[k2] !== false;
                const F = 1 / 24;
                if (fxOn('chroma')) addEvent(cs, 'chroma', 1.4 + rng.range(0, 2) * fx.chroma + (emph ? 2.5 : 0), 0.25);
                if (fxOn('slice') && rng.chance(g * 0.5 + (emph ? 0.3 : 0))) addEvent(cs, 'slice', 0.6 + rng.range(0, 0.8) * g + (emph ? 0.5 : 0), rng.pick([2, 3, 4]) * F);
                if (fxOn('block') && rng.chance(g * 0.22)) addEvent(cs + rng.range(0, 0.05), 'block', 0.5 + g, rng.pick([2, 4]) * F);
                if (fxOn('shake') && (emph || rng.chance(fx.motion * 0.18))) addEvent(cs, 'shake', (emph ? 1 : 0.5) * fx.motion, 0.3);
                if (fxOn('flash') && fx.flash && (ln.impact && k === 0 || kime)) addEvent(cs, 'flash', 1, 3 * F);
                if (kime) { // キメ：行落点上的重音
                    if (fxOn('zoom')) addEvent(cs, 'zoom', 1.1, 0.25);
                    if (fxOn('shake')) addEvent(cs + 0.04, 'shake', 1.1 * Math.max(0.5, fx.motion), 0.35);
                }
                if (fxOn('invert') && rng.chance(0.035 * g)) addEvent(cs, 'invert', 1, 2 * F);
                if (fxOn('zoom') && (emph && rng.chance(0.6) || rng.chance(0.06 * fx.motion))) addEvent(cs, 'zoom', 0.7 + 0.5 * fx.motion, 0.22);
                if (fxOn('mosaic') && rng.chance(0.04 * g)) addEvent(cs, 'mosaic', 1, 3 * F);
                if (fxOn('slice') && dur > 0.8 && rng.chance(g * 0.4)) addEvent(cs + rng.range(0.35, 0.8) * dur, 'slice', 0.4 + g * 0.4, 2 * F);
                // 新特效库：每个切分边界至多一个，偶尔切分中段加一个
                if (plan.cuts.length > 1 || k > 0 || li > 0) {
                    const pick = pickFx(rng, st, en, fx, emph, fxHistory, 'edge');
                    if (pick) {
                        const D2 = J.FXE[pick];
                        const d = (D2.dur || 4) * F;
                        addEvent(cs - (D2.pre ? D2.pre * F : 0), pick, (D2.amp || 1) * (0.7 + 0.5 * g + (emph ? 0.3 : 0)), d);
                        fxHistory.push(pick);
                    }
                }
                if (dur > 1.1) {
                    const pick = pickFx(rng, st, en, fx, emph, fxHistory, 'mid');
                    if (pick) {
                        const D2 = J.FXE[pick];
                        addEvent(cs + rng.range(0.4, 0.75) * dur, pick, (D2.amp || 1) * (0.5 + 0.4 * g), (D2.dur || 3) * F);
                    }
                }
                if (LS && Array.isArray(LS.events)) {
                    plan.events.splice(evMark);
                    for (const e of LS.events) plan.events.push({t: cs + e.dt, type: e.type, amp: e.amp, dur: e.dur});
                }
                // 文字整列：除色差外每个切分至多一个屏幕特效（キメ 行保留全部）
                if (plan.typeset && !cut.kime && !LS) {
                    const mine = plan.events.splice(evMark);
                    let extra = 0;
                    for (const e of mine) if (e.type === 'chroma' || e.type === 'shake' && emph || extra++ < 1) plan.events.push(e);
                }
                for (let q = evMark; q < plan.events.length; q++) evOwner.set(plan.events[q], cut); // 记下每个打点来自哪个切分（锁定用）
            });
            // 长空档里补间奏
            const nextStart = li < parsed.lines.length - 1 ? tm.starts[li + 1] : null;
            if (nextStart != null && nextStart - visEnd > 1.3 && !parsed.lines[li + 1].interlude) {
                const r2 = J.rng(J.h(lineSeed, 404));
                plan.cuts.push(makeCut({
                    text: title || '',
                    lineText: '',
                    line: li,
                    start: visEnd,
                    end: nextStart,
                    layout: 'interlude',
                    enter: 'blur',
                    exit: 'blur',
                    hold: 'still',
                    inDur: 0.3,
                    outDur: 0.3,
                    params: J.LAYOUTS.interlude.plan(r2),
                    decor: pickDecor(r2, st, en, Object.assign({}, fx, {decor: 1}), 'interlude'),
                    scheme: schemeIdx,
                    seed: J.h(lineSeed, 405)
                }));
            }
        });
        plan.cuts.sort((a, b) => a.start - b.start);
        plan.cuts.forEach((c, i) => {
            c.index = i;
            if (!zones || c.zone) return;
            if (c.layout === 'interlude') {
                c.params = Object.assign({}, c.params, {showTitle: false});
                return;
            } // 没歌词的占整帧
            c.zone = zoneOf();
        });
        plan.events.sort((a, b) => a.t - b.t);
        plan.energy = audio && audio.energy ? audio.energy : null;
        plan.energyRate = audio && audio.energyRate ? audio.energyRate : 0;
        plan.pitch = audio && audio.pitch && audio.pitch.length ? audio.pitch : null;
        plan.pitchRate = audio && audio.pitchRate ? audio.pitchRate : 0;
        return plan;
    };

    // 統一感：手写歌词视频的惯例
    // 空行隔出的每段保持一小套版式/动态/运镜/装饰，整段像一个想法；新段落可以换调色板和配色
    // 重复出现的行和第一次完全一样
    // 方向性动作左右上下交替，强的动作留给重要的行
    // キメ：以 ! 结尾的行；一个都没有时取重复段落的首行，给一个大切分
    // 行内各部分之间用モーフ，偶尔用太さ（细→粗）
    const DIR_PAIRS = {
        enter: [['slideL', 'slideR'], ['riseMask', 'dropMask'], ['trackIn', 'trackOut'], ['flipX', 'flipY']],
        exit: [['slideOutL', 'slideOutR'], ['sinkMask', 'riseOut'], ['flipOutX', 'flipOutY']],
        cam: [['panL', 'panR'], ['tiltUp', 'tiltDown'], ['dollyIn', 'pullOut']]
    };
    const STRONG = {
        enter: ['bounceBig', 'whip', 'spin', 'slingshot', 'crumple', 'stamp', 'zoom', 'glitchIn', 'scramble', 'spiralIn', 'rollIn', 'shuffle', 'windBlown', 'matrixRain', 'stopMotion', 'splitFlap'],
        cam: ['earthquake', 'shakeHard', 'crashZoom', 'barrelRoll', 'whipIn', 'snapPan', 'vertigo', 'roll', 'spiralIn', 'jelly', 'bounce', 'beatPunch', 'stepZoom']
    };
    const KIME = {
        layout: ['huge', 'huge', 'huge', 'columnsBig', 'columnsBig', 'halftoneBig', 'center'],
        enter: ['stamp', 'zoom', 'bounceBig', 'overexpose', 'slingshot', 'blur'],
        exit: ['zoomThrough', 'blur', 'shrink', 'zoomFar'],
        hold: ['still', 'pulse', 'heartbeat'],
        cam: ['beatPunch', 'crashZoom', 'dollyIn', 'push']
    };
    const SOFT_ENTER = ['blur', 'fadeStagger', 'trackIn', 'blurStagger', 'cut'];

    function makeUnify(lines, C) {
        const {st, en, fx, history} = C;
        const norm = t => String(t || '').replace(/[\s、。，．,.!！?？…・「」『』（）()"'“”‘’~〜ー―-]/g, '');
        // 段落：空行或间奏切开的块
        const sec = [], starts = new Set([0]);
        let si = 0;
        lines.forEach((ln, i) => {
            if (i > 0 && (ln.gapBefore || ln.interlude || lines[i - 1].interlude)) {
                si++;
                starts.add(i);
            }
            sec.push(si);
        });
        // 重复行：归一化文本相同的行
        const first = new Map(), repeatOf = [], count = new Map();
        lines.forEach((ln, i) => {
            const k = norm(ln.text);
            if (ln.interlude || k.length < 2) {
                repeatOf.push(null);
                return;
            }
            count.set(k, (count.get(k) || 0) + 1);
            if (first.has(k)) repeatOf.push(first.get(k)); else {
                first.set(k, i);
                repeatOf.push(null);
            }
        });
        // キメ 行
        const kime = new Set();
        lines.forEach((ln, i) => {
            if (ln.impact && !ln.interlude) kime.add(i);
        });
        if (!kime.size) lines.forEach((ln, i) => {
            if (starts.has(i) && !ln.interlude && (count.get(norm(ln.text)) || 0) >= 2) kime.add(i);
        });
        const ok = (g, k) => !!(k && en[g] && en[g][k] !== false && (g === 'layout' ? J.LAYOUTS[k] : g === 'enter' ? J.ENTER[k] : g === 'exit' ? J.EXIT[k] : g === 'hold' ? J.HOLD[k] : g === 'cam' ? J.CAMERA[k] : g === 'treat' ? J.TREAT[k] : g === 'trans' ? J.TRANS[k] : null));
        const pals = new Map(), last = new Map();
        const pal = i => {
            const s2 = sec[i];
            if (!pals.has(s2)) pals.set(s2, {
                layout: [],
                enter: [],
                exit: [],
                hold: [],
                cam: [],
                decor: [],
                treat: [],
                trans: []
            });
            return pals.get(s2);
        };
        // 每段最多存 max 个不同选择，满了之后大多复用
        const sticky = (i, g, v, max, rng, fits = () => true, reuse = 0.85) => {
            const P = pal(i)[g];
            if (P.length >= max && rng.chance(reuse)) {
                const pool = P.filter(k => ok(g, k) && fits(k));
                if (pool.length) return rng.pick(pool);
            }
            if (!P.includes(v) && P.length < max) P.push(v);
            return v;
        };
        // 段内方向交替
        const alternate = (i, g, v) => {
            const key = sec[i] + ':' + g, prev = last.get(key);
            for (const pr of DIR_PAIRS[g] || []) {
                const j = pr.indexOf(v);
                if (j >= 0 && prev && pr.includes(prev)) {
                    const w = pr[1 - pr.indexOf(prev)];
                    if (ok(g, w)) v = w;
                }
            }
            last.set(key, v);
            return v;
        };
        const strongRun = new Map(); // 上一刀是不是强动作
        const calm = (i, g, v, emph, repick, rng) => {
            const key = sec[i] + ':' + g;
            if (!emph && STRONG[g] && STRONG[g].includes(v) && (strongRun.get(key) || rng.chance(0.55))) {
                for (let t = 0; t < 4; t++) {
                    const w = repick();
                    if (!STRONG[g].includes(w)) {
                        v = w;
                        break;
                    }
                }
            }
            strongRun.set(key, !!(STRONG[g] && STRONG[g].includes(v)));
            return v;
        };
        const kimePick = (g, v, rng, fits = () => true) => {
            const pool = KIME[g].filter(k => ok(g, k) && fits(k));
            return pool.length ? rng.pick(pool) : v;
        };
        const specs = new Map();
        return {
            kime,
            sectionStart: i => starts.has(i),
            layout(i, v, o) {
                const fits = k => J.LAYOUTS[k] && J.LAYOUTS[k].fits(o.nn) && (!o.portrait || J.LAYOUTS[k].portrait !== 0);
                if (o.kime) return kimePick('layout', v, o.rng, k => J.LAYOUTS[k].fits(o.nn));
                return sticky(i, 'layout', v, 3, o.rng, fits, 0.9);
            },
            enter(i, v, o) {
                if (o.kime) return kimePick('enter', v, o.rng);
                v = sticky(i, 'enter', v, 2, o.rng);
                v = calm(i, 'enter', v, o.emph, () => pickEnter(o.rng, st, en, o.layout, o.dur, history, false, o.nn), o.rng);
                return alternate(i, 'enter', v);
            },
            exit(i, v, o) {
                if (o.kime) return kimePick('exit', v, o.rng);
                return alternate(i, 'exit', sticky(i, 'exit', v, 2, o.rng));
            },
            hold(i, v, o) {
                if (o.kime) return kimePick('hold', v, o.rng);
                return sticky(i, 'hold', v, 1, o.rng);
            },
            cam(i, v, o) {
                if (o.kime) return kimePick('cam', v, o.rng);
                v = sticky(i, 'cam', v, 2, o.rng);
                v = calm(i, 'cam', v, o.emph, () => pickCam(o.rng, st, en, fx, J.LAYOUTS.center, false, history), o.rng);
                return alternate(i, 'cam', v);
            },
            decor(i, list, o) {
                if (o.kime) return [];
                const P = pal(i).decor;
                if (P.length >= 2 && o.rng.chance(0.8)) {
                    const id = o.rng.pick(P);
                    return J.DECOR[id] ? [decorParams(o.rng, id)] : list;
                }
                for (const d of list) if (!P.includes(d.id) && P.length < 2) P.push(d.id);
                return list;
            },
            treat(i, v, o) {
                if (o.kime) return 'none';
                return sticky(i, 'treat', v, 1, o.rng, () => true, 0.75);
            },
            trans(i, v, o) {
                return sticky(i, 'trans', v, 2, o.rng);
            },
            weightGrow(o) {
                if (!/^(ja|en)$/.test(C.lang || 'ja') || o.nn > 16 || o.dur < 0.7) return false;
                return o.rng.chance(o.kime ? 0.45 : 0.14);
            },
            softEnter(v, rng) {
                const pool = SOFT_ENTER.filter(k => ok('enter', k));
                return pool.length ? rng.pick(pool) : v;
            },
            again(i, k, txt) {
                const f = repeatOf[i];
                if (f == null) return null;
                const sp = (specs.get(f) || [])[k];
                return sp && sp.text === txt ? sp : null;
            },
            remember(i, k, txt, cut) {
                if (!specs.has(i)) specs.set(i, []);
                specs.get(i)[k] = {
                    text: txt,
                    layout: cut.layout,
                    enter: cut.enter,
                    exit: cut.exit,
                    hold: cut.hold,
                    params: cut.params,
                    decor: cut.decor,
                    treat: cut.treat,
                    treatP: cut.treatP,
                    cam: cut.cam,
                    camP: cut.camP,
                    scheme: cut.scheme,
                    seed: cut.seed,
                    bg: cut.bg,
                    bgP: cut.bgP,
                    weightGrow: !!cut.weightGrow,
                    morph: !!cut.morph,
                    trans: cut.trans || null,
                    transP: cut.transP,
                    transDur: cut.transDur
                };
            },
        };
    }

    // 中央を空ける：一幅画面，歌词拆两半——「花が」在左（上）带，「咲いた」在右（下）带。两半用同一套
    // 版式/动态/装饰/运镜（同一批随机数），读起来是一张图，中间留给角色；后半晚一拍出现
    function splitHalf(text, lang) {
        const t = String(text || '').trim();
        const n = [...t.replace(/\s+/g, '')].length;
        // 尽量从词与词之间、靠近正中处分开（「花が」｜「咲いた」，"Good night," | "see you tomorrow"）
        const words = (lang === 'en' ? J.phraseChunks(J.chunkText(t)) : J.chunkText(t)).map(w => String(w));
        if (words.length >= 2) {
            const L = w => [...w.replace(/\s+/g, '')].length, total = words.reduce((a2, w) => a2 + L(w), 0);
            let acc = 0, best = 1, bd = 1e9;
            for (let k = 1; k < words.length; k++) {
                acc += L(words[k - 1]);
                const d = Math.abs(acc - total / 2);
                if (d < bd) {
                    bd = d;
                    best = k;
                }
            }
            const sep = /[A-Za-z]/.test(t) ? ' ' : '';
            return [words.slice(0, best).join(sep).trim(), words.slice(best).join(sep).trim()];
        }
        // 单个词：短的（和英文单词）两边都放整词，长的在中间附近切
        if (n <= 3 || /^[A-Za-z0-9'’-]+$/.test(t)) return [t, t];
        const two = splitToCount([t], 2);
        if (two.length < 2) return [t, t];
        let a = two[0].trim(), b = two.slice(1).join('').trim();
        // 后半不能以助词、标点或小假名开头：「夜明けの色を」｜「覚えてる」，而不是「…色」｜「を…」
        for (let g = 0; g < 3 && b.length > 1 && HEAD_BAD.test(b[0]); g++) {
            a += b[0];
            b = b.slice(1);
        }
        return [a, b];
    }

    const HEAD_BAD = /[、。，．,.!?！？…・ーっッゃゅょャュョぁぃぅぇぉァィゥェォをがはにでとのへもやよね」』）)]/;

    function splitCut(cut, halves, zones, st, dur, LS) {
        const LD = J.LAYOUTS[cut.layout], seed = J.h(cut.seed, 23);
        const planFor = (text, z) => LD.plan(J.rng(seed), {
            text,
            n: [...text.replace(/\s+/g, '')].length,
            W: z.w,
            H: z.h,
            dur
        }, st);
        cut.text = halves[0];
        cut.lineText = halves[0];
        cut.words = J.chunkText(halves[0]);
        cut.zone = Object.assign({}, zones[0]);
        cut.params = LS && LS.params && LS.twinParams ? LS.params : planFor(halves[0], zones[0]); // 锁定行用自己的参数
        const delay = Math.min(0.12, dur * 0.08);
        const twin = Object.assign({}, cut, {
            text: halves[1],
            lineText: halves[1],
            words: J.chunkText(halves[1]),
            zone: Object.assign({}, zones[1]),
            params: LS && LS.twinParams ? LS.twinParams : planFor(halves[1], zones[1]),
            start: cut.start + delay,
            bg: 'none',
            bgP: {},
            companion: true,
            trans: null,
            transP: {},
            transDur: 0,
            morph: cut.morph
        });
        twin.dur = twin.end - twin.start;
        delete twin.companion_;
        cut.companion = twin;
    }

    // ロック：把一行显示的东西存下来，便于原样保留（存在 project.overrides[line].lockedCuts）
    J.lineSnapshot = (plan, li) => {
        const cuts = plan.cuts.filter(c => c.line === li && c.utext != null);
        if (!cuts.length) return null;
        const S = JSON.parse(JSON.stringify(cuts.map(c => ({
            utext: c.utext,
            layout: c.layout,
            enter: c.enter,
            exit: c.exit,
            hold: c.hold,
            inDur: c.inDur,
            outDur: c.outDur,
            params: c.params,
            decor: c.decor,
            treat: c.treat,
            treatP: c.treatP,
            bg: c.bg,
            bgP: c.bgP,
            cam: c.cam,
            camP: c.camP,
            scheme: c.scheme,
            seed: c.seed,
            trans: c.trans,
            transP: c.transP,
            transDur: c.transDur,
            morph: c.morph || null,
            weightGrow: !!c.weightGrow,
            kime: !!c.kime,
            recap: !!c.recap,
            twinParams: c.companion ? c.companion.params : null,
            events: []
        }))));
        // 每个打点归属它所在的切分（紧贴切分开始前的算给这个切分）
        for (const e of plan.events) {
            // 打点属于制造它的那个切分，包括行尾之后才播放的（收尾特效不再丢失）
            const own = plan.evOwner && plan.evOwner.get(e);
            if (own) {
                const k = cuts.indexOf(own);
                if (k >= 0) S[k].events.push({
                    dt: +(e.t - cuts[k].start).toFixed(4),
                    type: e.type,
                    amp: e.amp,
                    dur: e.dur
                });
                continue;
            }
            let k = -1;
            // [start − 0.25, 下一刀 start − 0.25)：贴着下一刀前面的打点归下一刀
            for (let j = 0; j < cuts.length; j++) {
                const until = j < cuts.length - 1 ? cuts[j + 1].start - 0.25 : cuts[j].end - 0.3;
                if (e.t >= cuts[j].start - 0.25 && e.t < until) k = j;
            }
            if (k >= 0) S[k].events.push({dt: +(e.t - cuts[k].start).toFixed(4), type: e.type, amp: e.amp, dur: e.dur});
        }
        return S;
    };

    // side bands for 中央を空ける，设计像素。宽帧分左右第三（21:9 再窄一点），竖帧分上下，接近方形的算宽帧
    // gapPct：留白占画面宽/高的百分比（10..70，设置页自定义），缺省用原来的固定比例
    // zonePos：留白位置 center / left / right / top / bottom；单侧留白时文字排进剩余区域，
    // 切成上下（左右留白时）或左右（上下留白时）两个矩形
    J.sideZones = (W, H, dir, gapPct, zonePos) => {
        const gap = !(gapPct > 0) ? 0 : Math.min(70, Math.max(10, +gapPct)) / 100;
        const bw = Math.round(W * (gap || 0.3));
        const bh = Math.round(H * (gap || 0.33));
        const halfH = Math.round(H / 2), halfW = Math.round(W / 2);
        if (zonePos === 'left') {
            const w = bw;
            return [{x: w, y: 0, w: W - w, h: halfH, side: 'top'}, {
                x: w,
                y: halfH,
                w: W - w,
                h: H - halfH,
                side: 'bottom'
            }];
        }
        if (zonePos === 'right') {
            const w = bw;
            return [{x: 0, y: 0, w: W - w, h: halfH, side: 'top'}, {
                x: 0,
                y: halfH,
                w: W - w,
                h: H - halfH,
                side: 'bottom'
            }];
        }
        if (zonePos === 'top') {
            const h = bh;
            return [{x: 0, y: h, w: halfW, h: H - h, side: 'left'}, {
                x: halfW,
                y: h,
                w: W - halfW,
                h: H - h,
                side: 'right'
            }];
        }
        if (zonePos === 'bottom') {
            const h = bh;
            return [{x: 0, y: 0, w: halfW, h: H - h, side: 'left'}, {
                x: halfW,
                y: 0,
                w: W - halfW,
                h: H - h,
                side: 'right'
            }];
        }
        if (H > W * 1.1 && dir === 'lr') {
            const w = Math.round(W * (gap ? (1 - gap) / 2 : 0.34));
            return [{x: 0, y: 0, w, h: H, side: 'left'}, {x: W - w, y: 0, w, h: H, side: 'right'}];
        } // 竖长帧也分左右
        if (H > W * 1.1) {
            const h = Math.round(H * (gap || 0.33));
            return [{x: 0, y: 0, w: W, h, side: 'top'}, {x: 0, y: H - h, w: W, h, side: 'bottom'}];
        }
        const w = Math.round(W * (gap ? (1 - gap) / 2 : (W / H > 2 ? 0.3 : 0.36)));
        return [{x: 0, y: 0, w, h: H, side: 'left'}, {x: W - w, y: 0, w, h: H, side: 'right'}];
    };

    function makeCut(o) {
        const c = Object.assign({
            hold: 'still',
            inDur: 0.3,
            outDur: 0.25,
            stagger: 0.04,
            decor: [],
            params: {},
            scheme: 0,
            emph: false,
            words: [],
            note: null,
            treat: 'none',
            treatP: {},
            bg: 'none',
            bgP: {},
            cam: 'push',
            camP: {}
        }, o);
        c.dur = c.end - c.start;
        return c;
    }

    // 拆词块直到至少 n 块，每次拆最长的（拉丁文按词，其他按字）
    function splitToCount(chunks, n) {
        const out = chunks.slice();
        let guard = 0;
        while (out.length < n && guard++ < 64) {
            let bi = -1, bl = 1;
            out.forEach((c, i) => {
                const l = /\s/.test(c.trim()) ? c.trim().split(/\s+/).length : [...c].length;
                if (l > bl) {
                    bl = l;
                    bi = i;
                }
            });
            if (bi < 0) break;
            const c = out[bi].trim();
            let a, b;
            if (/\s/.test(c)) {
                const w = c.split(/\s+/), h = Math.ceil(w.length / 2);
                a = w.slice(0, h).join(' ');
                b = w.slice(h).join(' ');
            } else {
                // 有分词边界就取最靠近中间的（夜明け|の），否则按字切
                const ch = [...c], segs = J.segments ? J.segments(c) : [];
                let cut = Math.ceil(ch.length / 2);
                if (segs.length > 1) {
                    let acc = 0, best = -1, bd = 1e9;
                    for (let k = 0; k < segs.length - 1; k++) {
                        acc += [...segs[k]].length;
                        const d = Math.abs(acc - ch.length / 2);
                        if (d < bd) {
                            bd = d;
                            best = acc;
                        }
                    }
                    if (best > 0) cut = best;
                }
                a = ch.slice(0, cut).join('');
                b = ch.slice(cut).join('');
            }
            out.splice(bi, 1, a, b);
        }
        return out;
    }

    function partition(chunks, k) {
        const lens = chunks.map(c => [...c].length + 1);
        const tot = lens.reduce((a, b) => a + b, 0), target = tot / k;
        const groups = [];
        let cur = [], acc = 0, remainingGroups = k;
        chunks.forEach((c, i) => {
            const remainingChunks = chunks.length - i;
            if (cur.length && (acc + lens[i] / 2 > target || remainingChunks < remainingGroups) && groups.length < k - 1) {
                groups.push(cur);
                cur = [];
                acc = 0;
                remainingGroups--;
            }
            cur.push(c);
            acc += lens[i];
        });
        if (cur.length) groups.push(cur);
        return groups;
    }

    function novelty(history, key, val) {
        let w = 1;
        for (let i = history.length - 1, d = 0; i >= 0 && d < 6; i--, d++) if (history[i][key] === val) w *= d < 2 ? 0.2 : 0.6;
        return w;
    }

    const PORTRAIT_W = {
        vcols: 1.9,
        condensed: 1.3,
        huge: 1.3,
        center: 1.2,
        stack: 1.1,
        mixed: 0.7,
        marquee: 0.6,
        wave: 0.6,
        diag: 0.8,
        type: 0.8,
        gloss: 0.5
    };

    function pickLayout(rng, st, en, n, dur, history, emph, recap, portrait) {
        const cands = [];
        for (const k of J.LAYOUT_ORDER) {
            const L = J.LAYOUTS[k];
            if (!en.layout[k] || !L.fits(n)) continue;
            let w = wkey(st.bias.layout, k, L.w ?? 1) * novelty(history, 'layout', k);
            if (portrait) w *= L.portrait != null ? L.portrait : wkey(PORTRAIT_W, k, 1);
            if (emph && L.emph) w *= L.emph;
            if (emph && ['huge', 'center', 'tile', 'marquee', 'condensed'].includes(k)) w *= 2;
            if (recap && ['center', 'stack', 'marquee', 'tile', 'mixed', 'type', 'gloss'].includes(k)) w *= 1.8;
            if (dur < 0.5 && ['wave', 'ring', 'labels', 'gloss', 'type', 'tile'].includes(k)) w *= 0.3;
            if (dur < 0.5 && ['center', 'huge', 'condensed', 'vcols'].includes(k)) w *= 1.4;
            cands.push([k, w]);
        }
        if (!cands.length) return 'center';
        return rng.wpick(cands);
    }

    const LAYOUT_ENTER = {
        type: {type: 4, scramble: 1.5},
        ring: {pop: 2, spin: 2, cut: 1, assemble: 0.4, slice: 0.2, wipe: 0.2},
        labels: {cut: 3, pop: 1},
        wave: {pop: 1.5, drop: 1.5, blur: 1, slice: 0.3},
        tile: {assemble: 1.3, slice: 1.4, zoom: 1.4},
        huge: {zoom: 1.5, wipe: 1.5, slice: 1.4, stretch: 1.3, type: 0.2},
        mixed: {pop: 1.6, drop: 1.6, spin: 1.3},
        scatter: {pop: 1.5, spin: 1.5, drop: 1.2, assemble: 1.3},
        vcols: {assemble: 1.8, type: 1.2},
        pill: {wipe: 1.8, type: 1.2},
    };

    function pickEnter(rng, st, en, layout, dur, history, emph, n) {
        const cands = [];
        for (const k of J.ENTER_ORDER) {
            if (!en.enter[k]) continue;
            const D = J.ENTER[k];
            if (!D) continue;
            const LD = J.LAYOUTS[layout] || {};
            let w = wkey(st.bias.enter, k, D.w ?? 1) * novelty(history, 'enter', k) * wkey(LAYOUT_ENTER[layout] || LD.enterBias, k, 1);
            if (D.minDur && dur < D.minDur) w *= 0.15;
            if (D.maxChars && n > D.maxChars) w *= 0.2;
            if (k === 'cut') w *= 0.5;
            if (dur < 0.45 && ['type', 'assemble', 'drop', 'spin', 'pop', 'flicker'].includes(k)) w *= 0.25;
            if (dur < 0.45 && ['cut', 'slice', 'zoom', 'stretch'].includes(k)) w *= 1.8;
            if (k === 'type' && n > 18) w *= 0.3;
            if (emph && ['zoom', 'assemble', 'slice'].includes(k)) w *= 1.8;
            cands.push([k, w]);
        }
        return cands.length ? rng.wpick(cands) : 'cut';
    }

    function pickExit(rng, st, en, layout, dur, lastOfLine, history) {
        const cands = [];
        for (const k of J.EXIT_ORDER) {
            if (!en.exit[k]) continue;
            const D = J.EXIT[k];
            if (!D) continue;
            let w = wkey(st.bias.exit, k, D.w ?? 1) * novelty(history, 'exit', k);
            if (D.minDur && dur < D.minDur) w *= 0.15;
            if (k === 'cut') w *= dur < 0.6 ? 4 : lastOfLine ? 1.2 : 2.2;
            if (dur < 0.6 && k !== 'cut') w *= 0.4;
            if (['labels', 'ring', 'tile'].includes(layout) && ['explode', 'fall', 'drift'].includes(k)) w *= 0.3;
            cands.push([k, w]);
        }
        return cands.length ? rng.wpick(cands) : 'cut';
    }

    const HOLD_W = {still: 1, jitter: 1.2, drift: 1, breathe: 0.7, wave: 0.4, glitchtick: 0.9};

    function pickHold(rng, en, fx, history) {
        const cands = J.HOLD_ORDER.filter(k => en.hold[k] !== false && J.HOLD[k]).map(k => {
            const D = J.HOLD[k];
            let w = HOLD_W[k] != null ? HOLD_W[k] : (D.w ?? 0.8);
            if (k === 'jitter' || (D.tags && D.tags.includes('glitch'))) w *= 0.4 + fx.motion;
            if (k === 'glitchtick') w *= fx.glitch;
            return [k, w * novelty(history, 'hold', k)];
        });
        return cands.length ? rng.wpick(cands) : 'still';
    }

    function decorParams(rng, k) {
        return {
            id: k,
            seed: rng.int(1, 1e9),
            n: rng.int(1, 3) + (k === 'shapes' ? 3 : 0) + (k === 'sparks' ? 4 : 0),
            right: rng.chance(0.5),
            low: rng.chance(0.5),
            accent: rng.chance(0.4),
            corner: rng.chance(0.5),
            big: rng.chance(0.4),
            mode: rng.pick(['count', 'index']),
            from: rng.int(0, 20),
            to: rng.int(30, 999),
            v: rng.int(0, 5),
            r: rng()
        };
    }

    function pickDecor(rng, st, en, fx, layout, history = []) {
        const count = Math.round(fx.decor * 2.8 * rng.range(0.45, 1.15));
        const recent = new Set(history.slice(-2).flatMap(h => h.decor || []));
        const LD = J.LAYOUTS[layout] || {};
        const cands = J.DECOR_ORDER.filter(k => en.decor[k] && J.DECOR[k] && !(LD.busy && J.DECOR[k].layer === 'back' && !J.DECOR[k].subtle))
            .map(k => [k, wkey(st.decor, k, J.DECOR[k].w != null ? J.DECOR[k].w * 0.5 : 0.35) * (recent.has(k) ? 0.35 : 1)]);
        const out = [];
        for (let i = 0; i < count && cands.length; i++) {
            const k = rng.wpick(cands);
            cands.splice(cands.findIndex(c => c[0] === k), 1);
            out.push(decorParams(rng, k));
        }
        return out;
    }

// 文字处理：大多数时候不加，decor 滑块提高使用频率
    function pickTreat(rng, st, en, fx, LD, emph, history) {
        if (LD.treat === false) return 'none';
        if (!rng.chance(0.18 + 0.42 * (fx.decor ?? 0.5) + (emph ? 0.15 : 0))) return 'none';
        const cands = J.TREAT_ORDER.filter(k => k !== 'none' && en.treat && en.treat[k] !== false && J.TREAT[k] && (LD.treat !== 'safe' || J.TREAT[k].safe))
            .map(k => [k, wkey(st.bias && st.bias.treat, k, J.TREAT[k].w ?? 1) * novelty(history, 'treat', k)]);
        return cands.length ? rng.wpick(cands) : 'none';
    }

    function pickBg(rng, st, en, fx, bgHist) {
        if (!rng.chance(0.2 + 0.35 * (fx.decor ?? 0.5) + 0.2 * (fx.bgSwitch ?? 0.35))) return 'none';
        const last = bgHist.slice(-3);
        const cands = J.BG_ORDER.filter(k => k !== 'none' && en.bg && en.bg[k] !== false && J.BG[k])
            .map(k => [k, wkey(st.bias && st.bias.bg, k, J.BG[k].w ?? 1) * (last.includes(k) ? 0.25 : 1)]);
        return cands.length ? rng.wpick(cands) : 'none';
    }

    function pickCam(rng, st, en, fx, LD, emph, history) {
        const cands = J.CAMERA_ORDER.filter(k => en.cam && en.cam[k] !== false && J.CAMERA[k]).map(k => {
            const D = J.CAMERA[k];
            let w = wkey(st.bias && st.bias.cam, k, D.w ?? 1) * novelty(history, 'cam', k);
            if (D.strong) w *= 0.25 + 0.9 * (fx.motion ?? 0.7) + (emph ? 0.6 : 0);
            if (LD.cam === false && k !== 'push') w *= 0.05;
            return [k, w];
        });
        return cands.length ? rng.wpick(cands) : 'push';
    }

    function pickTrans(rng, st, en, fx, emph, history) {
        if (!J.TRANS_ORDER.length) return null;
        if (!rng.chance(0.1 + 0.22 * (fx.motion ?? 0.7) + (emph ? 0.08 : 0))) return null;
        const cands = J.TRANS_ORDER.filter(k => en.trans && en.trans[k] !== false && J.TRANS[k])
            .map(k => [k, wkey(st.bias && st.bias.trans, k, J.TRANS[k].w ?? 1) * novelty(history, 'trans', k)]);
        return cands.length ? rng.wpick(cands) : null;
    }

// kind 'edge' = 切分边界上的转场，'mid' = 切分中段的点缀
    function pickFx(rng, st, en, fx, emph, fxHist, kind) {
        const g = fx.glitch ?? 0.55;
        const p = kind === 'edge' ? 0.12 + 0.38 * g + 0.12 * (fx.motion ?? 0.7) + (emph ? 0.15 : 0) : 0.05 + 0.2 * g;
        if (!rng.chance(p)) return null;
        const last = fxHist.slice(-3);
        const cands = J.FXE_ORDER.filter(k => {
            const D = J.FXE[k];
            return D && !D.builtin && en.fx && en.fx[k] !== false && (kind === 'edge' ? D.edge !== false : D.mid);
        })
            .map(k => {
                const D = J.FXE[k];
                let w = wkey(st.bias && st.bias.fx, k, D.w ?? 1) * (last.includes(k) ? 0.2 : 1);
                if (D.glitchy) w *= 0.3 + g * 1.4;
                return [k, w];
            });
        return cands.length ? rng.wpick(cands) : null;
    }

    // 手法标签页缩略图用的单切分 plan（转场是两切分）
    J.previewPlan = (project, group, key) => {
        const enUI = typeof document !== 'undefined' && document.documentElement && document.documentElement.lang === 'en';
        const st = J.resolveStyle(project);
        const fx = Object.assign({}, J.defaultProject().fx, project.fx || {}, {
            glitch: group === 'fx' ? 0.85 : 0,
            chroma: group === 'fx' ? 0.9 : 0.22,
            flash: false, hud: 'off', texture: 0.3, bgSwitch: 0, motion: 0.75,
            decor: group === 'decor' ? 1 : 0,
        });
        const [W, H] = J.designSize(project.aspect || '16:9');
        const rng = J.rng(J.h(J.sid(String(group) + ':' + String(key)), 11, 22));
        let text = enUI ? 'Lyric' : '字面';
        if (group === 'layout') {
            const L0 = J.LAYOUTS[key];
            const n2 = [...text.replace(/\s+/g, '')].length;
            if (L0 && L0.fits && !L0.fits(n2)) text = enUI ? 'color of dawn' : '夜明けの色を';
            if (L0 && L0.fits && !L0.fits([...text.replace(/\s+/g, '')].length)) text = enUI ? 'I remember the color of dawn' : '夜明けの色を覚えてる';
        }
        const nn = [...text.replace(/\s+/g, '')].length;
        const dur = 2.4;
        let layout = group === 'layout' ? key : 'center';
        if (!J.LAYOUTS[layout] || J.LAYOUTS[layout].special) layout = 'center';
        let enter = group === 'enter' ? key : 'cut';
        let exit = group === 'exit' ? key : 'cut';
        let hold = group === 'hold' ? key : 'still';
        if (!J.ENTER[enter]) enter = 'cut';
        if (!J.EXIT[exit]) exit = 'cut';
        if (!J.HOLD[hold]) hold = 'still';
        if (group === 'layout' || group === 'decor' || group === 'treat' || group === 'bg') {
            if (enter === 'cut' && J.ENTER.pop) enter = 'pop';
            if (hold === 'still' && J.HOLD.breathe) hold = 'breathe';
            else if (hold === 'still' && J.HOLD.drift) hold = 'drift';
        }
        const LD = J.LAYOUTS[layout];
        let params = {};
        try {
            params = LD.plan(rng, {text, n: nn, W, H, dur}, st) || {};
        } catch (e) {
            params = {};
        }
        let inDur = enter === 'cut' ? 0.12 : 0.5;
        if (J.ENTER[enter] && J.ENTER[enter].inDur) try {
            inDur = J.ENTER[enter].inDur(dur, nn);
        } catch (e) {
        }
        let outDur = exit === 'cut' ? 0 : 0.5;
        if (J.EXIT[exit] && J.EXIT[exit].outDur) try {
            outDur = J.EXIT[exit].outDur(dur, nn);
        } catch (e) {
        }
        if (inDur + outDur > dur * 0.85) {
            const f = dur * 0.85 / Math.max(0.2, inDur + outDur);
            inDur *= f;
            outDur *= f;
        }
        const decor = (group === 'decor' && J.DECOR[key]) ? [decorParams(rng, key)] : [];
        let treat = group === 'treat' ? key : 'none';
        if (!J.TREAT[treat]) treat = 'none';
        const treatP = (J.TREAT[treat] && J.TREAT[treat].plan) ? (J.TREAT[treat].plan(rng, st) || {}) : {};
        let bg = group === 'bg' ? key : 'none';
        if (!J.BG[bg]) bg = 'none';
        const bgP = (J.BG[bg] && J.BG[bg].plan) ? (J.BG[bg].plan(rng, st) || {}) : {};
        let cam = group === 'cam' ? key : 'push';
        if (!J.CAMERA[cam]) cam = 'push';
        const camP = (J.CAMERA[cam] && J.CAMERA[cam].plan) ? (J.CAMERA[cam].plan(rng, st) || {}) : {};
        const events = [];
        if (group === 'fx' && J.FXE[key]) {
            const D2 = J.FXE[key];
            events.push({t: 0.04, type: key, amp: (D2.amp || 1.15) * 1.2, dur: ((D2.dur || 6) / 24)});
        }
        const cuts = [];
        if (group === 'trans' && J.TRANS[key]) {
            const TD = J.TRANS[key];
            const transDur = J.clamp(TD.dur || 0.35, 0.18, 0.7);
            const transP = TD.plan ? (TD.plan(rng, st) || {}) : {};
            const tA = enUI ? 'BEFORE' : '前のカット';
            const tB = enUI ? 'AFTER' : '字面';
            let pA = {}, pB = {};
            try {
                pA = J.LAYOUTS.center.plan(rng, {text: tA, n: [...tA].length, W, H, dur: 1.2}, st) || {};
            } catch (e) {
            }
            try {
                pB = J.LAYOUTS.center.plan(rng, {text: tB, n: [...tB].length, W, H, dur: 1.2}, st) || {};
            } catch (e) {
            }
            cuts.push(makeCut({
                text: tA,
                lineText: tA,
                line: 0,
                start: 0,
                end: 1.2,
                layout: 'center',
                enter: 'cut',
                exit: 'cut',
                hold: 'still',
                inDur: 0.12,
                outDur: 0,
                params: pA,
                decor: [],
                scheme: 0,
                seed: 1,
                words: J.chunkText(tA)
            }));
            cuts.push(makeCut({
                text: tB,
                lineText: tB,
                line: 1,
                start: 1.2,
                end: 2.4,
                layout: 'center',
                enter: 'cut',
                exit: 'cut',
                hold: 'still',
                inDur: 0.12,
                outDur: 0,
                params: pB,
                decor: [],
                scheme: Math.min(1, st.schemes.length - 1),
                seed: 2,
                words: J.chunkText(tB),
                trans: key,
                transP,
                transDur
            }));
        } else {
            cuts.push(makeCut({
                text, lineText: text, line: 0, start: 0, end: dur, layout, enter, exit, hold, inDur, outDur,
                params, decor, scheme: 0, seed: J.h(J.sid(String(key)), 9), words: J.chunkText(text),
                treat, treatP, bg, bgP, cam, camP, stagger: 0.04,
            }));
        }
        cuts.forEach((c, i) => {
            c.index = i;
        });
        return {
            version: 1, generator: 'JIZURA-preview', title: '', artist: '', W, H, fps: 24,
            duration: cuts[cuts.length - 1].end, styleKey: project.style, style: st, fx,
            lines: [], cuts, events, beats: [], hud: false, keyBg: null,
        };
    };

    J.designSize = (aspect) => {
        if (aspect === '9:16') return [1080, 1920];
        if (aspect === '1:1') return [1440, 1440];
        if (aspect === '4:5') return [1440, 1800];
        if (aspect === '21:9') return [2520, 1080];
        if (aspect === '4:3') return [1440, 1080];
        if (aspect === '3:4') return [1080, 1440];
        return [1920, 1080];
    };
    J.outputSize = (project) => {
        const [W, H] = J.designSize(project.aspect);
        const k = (project.res || 1080) / Math.min(W, H);
        return [Math.round(W * k / 2) * 2, Math.round(H * k / 2) * 2];
    };
})();
