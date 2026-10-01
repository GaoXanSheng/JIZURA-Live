/* AI 代理（MCP）桥：页面内实现 MCP 协议语义与 17 个 jizura_* 工具。
   C# 的 /mcp 端点把 JSON-RPC 原样桥进 window.J.mcp.rpc()（EvaluateScriptAsync），
   因此工具定义/校验/执行的唯一事实源在这里；C# 只负责端点、鉴权与 UI 线程分发。
   工具名与上游 JIZURA PR38 同名同语义，代理提示词可通用。 */
import {J} from '../engine';
import {AP, S} from './state';
import {bus} from './bus';
import {invalidateFontKey, pause, play, replan, replanSoon, seek} from './engine';
import {flushSave} from './project';
import {
    clearLyrics,
    commit,
    edGo,
    histGo,
    histInfo,
    locksOf,
    markCutQuiet,
    omakase,
    rerollCurrentCut,
    setCutTech,
    setExportRange,
    setOv,
    startTap,
    stopTap,
    tapBack,
    tapNow
} from './look';
import {runExport} from './export';

export interface McpTool {
    name: string;
    description: string;
    inputSchema: any;
    run: (a: any) => any;
    readOnly?: boolean
}

let revision = 0;
bus.on('replan', () => {
    revision++;
});

const fail = (code: string, message: string): never => {
    throw Object.assign(new Error(message), {code});
};
const check = (ok: any, message: string) => {
    if (!ok) fail('invalid_input', message);
};
const opt = (a: any, k: string) => (a && Object.prototype.hasOwnProperty.call(a, k)) ? a[k] : undefined;
const lineIdx = (n: number) => {
    check(Number.isInteger(n) && n >= 1 && n <= S.plan.lines.length, '行不存在：' + n);
    return n - 1;
};

/* ---------- 工具实现 ---------- */

function toolGetState(a: any) {
    const detail = a.detail !== false;
    const c = S.plan ? J.cutAt(S.plan, S.t) : null;
    const base = {
        revision,
        playing: S.playing,
        t: +S.t.toFixed(3),
        duration: S.plan ? +S.plan.duration.toFixed(3) : 0,
        loop: S.loop,
        loading: {
            audio: !!S.project.audioName && !S.audio,
            fonts: document.fonts.status !== 'loaded',
            exporting: !!S.exporting,
            tap: !!S.tap
        },
        history: histInfo(),
    };
    if (!detail) return base;
    return {
        ...base,
        project: {
            title: S.project.title, artist: S.project.artist, lang: S.project.lang || 'auto', style: S.project.style,
            seed: S.project.seed, aspect: S.project.aspect, resolution: S.project.res, fps: S.project.fps,
            bpm: S.project.timing && S.project.timing.bpm, audioName: S.project.audioName || null,
            lyrics: S.project.lyrics, timing: S.project.timing, overrides: S.project.overrides || {},
            enabled: S.project.enabled, exportRange: S.project.exportRange || null,
        },
        lines: S.plan.lines.map((ln: any, i: number) => ({
            line: i + 1,
            start: +ln.start.toFixed(3),
            end: +ln.end.toFixed(3),
            interlude: !!ln.interlude,
            text: ln.text,
            manual: !!(S.project.timing.lineTimes && S.project.timing.lineTimes[i] != null),
            layout: (S.project.overrides[i] || {}).layout || null,
            cuts: S.plan.cuts.filter((x: any) => x.line === i).length,
            locked: !!((S.project.overrides[i] || {}).lock),
        })),
        cut: c ? {
            cut: c.index + 1, line: c.line + 1, text: c.text, start: +c.start.toFixed(3), end: +c.end.toFixed(3),
            layout: c.layout, enter: c.enter, hold: c.hold, exit: c.exit, cam: c.cam, trans: c.trans || null,
        } : null,
    };
}

function toolListOptions(a: any) {
    const cat = a.category;
    if (cat === 'settings') return {note: '设置字段见 update_settings 的说明；style/font/各手法组用 list_options 查询 ID'};
    const table: any = cat === 'style' ? J.STYLES : cat === 'font' ? J.FONTS : J.registry(cat);
    check(table, '未知分类：' + cat);
    let items = Object.entries(table).filter(([, v]: any) => !v.special).map(([id, v]: any) => ({
        id, label: v.name || v.label || id, description: (v.desc || '').slice(0, 200),
        enabled: cat === 'style' || cat === 'font' ? undefined : S.project.enabled[cat] ? S.project.enabled[cat][id] !== false : undefined,
        eligibleForRandom: cat === 'style' || cat === 'font' ? undefined : J.randomOk(S.project, cat, id),
    }));
    if (a.query) items = items.filter((x: any) => (x.id + ' ' + x.label + ' ' + (x.description || '')).toLowerCase().includes(String(a.query).toLowerCase()));
    const page = Math.max(1, a.page | 0 || 1), size = Math.min(100, Math.max(1, a.pageSize | 0 || 30));
    return {total: items.length, page, items: items.slice((page - 1) * size, page * size)};
}

function toolSetLyrics(a: any) {
    if (a.clear === true) {
        clearLyrics();
        return {cleared: true};
    }
    check(typeof a.text === 'string', '提供 text 或 clear=true');
    S.project.lyrics = a.text;
    bus.emit('lyrics-sync');
    replan();
    flushSave();
    return {lines: S.plan.lines.length, cuts: S.plan.cuts.length};
}

const FX_KEYS = ['motion', 'glitch', 'chroma', 'decor', 'density', 'texture', 'bgSwitch'];

function toolUpdateSettings(a: any) {
    const p = a.settings || {};
    const P = S.project;
    if (p.title !== undefined) P.title = String(p.title).slice(0, 1000);
    if (p.artist !== undefined) P.artist = String(p.artist).slice(0, 1000);
    if (p.lang !== undefined) P.lang = p.lang;
    if (p.style !== undefined) {
        check(J.STYLES[p.style], '未知风格：' + p.style);
        P.style = p.style;
    }
    if (p.seed !== undefined) P.seed = p.seed | 0;
    if (p.fx) for (const k of FX_KEYS) if (p.fx[k] !== undefined) P.fx[k] = +p.fx[k];
    if (p.fx && p.fx.flash !== undefined) P.fx.flash = !!p.fx.flash;
    if (p.fx && p.fx.hud !== undefined) P.fx.hud = p.fx.hud;
    if (p.fonts) for (const role of ['display', 'serif', 'body']) {
        if (p.fonts[role] !== undefined) {
            check(p.fonts[role] === '' || J.FONTS[p.fonts[role]], '未知字体：' + p.fonts[role]);
            if (p.fonts[role]) P.fonts[role] = p.fonts[role]; else delete P.fonts[role];
        }
    }
    for (const k of ['extra', 'wa', 'horror', 'typo', 'kinetic', 'unify', 'typeset', 'centerFree', 'includeAudio']) if (p[k] !== undefined) P[k] = !!p[k];
    if (p.centerDir !== undefined) P.centerDir = p.centerDir;
    if (p.keyBg !== undefined) P.keyBg = p.keyBg;
    if (p.aspect !== undefined) check(['16:9', '9:16', '4:3', '3:4', '1:1', '4:5', '21:9'].includes(p.aspect), 'bad aspect'), P.aspect = p.aspect;
    if (p.res !== undefined) P.res = p.res;
    if (p.fps !== undefined) P.fps = p.fps;
    if (p.quality !== undefined) P.quality = p.quality;
    if (p.exportRange !== undefined) {
        if (p.exportRange === null) S.project.exportRange = null;
        else {
            const i = lineIdx(p.exportRange.from), j2 = lineIdx(p.exportRange.to);
            setExportRange(Math.min(i, j2), false);
            setExportRange(Math.max(i, j2), true);
        }
    }
    invalidateFontKey();
    replan();
    commit();
    flushSave();
    return {applied: Object.keys(p)};
}

function toolSetTiming(a: any) {
    const T = S.project.timing;
    if (a.settings) {
        if (a.settings.bpm !== undefined) T.bpm = +a.settings.bpm || 0;
        if (a.settings.offset !== undefined) T.offset = +a.settings.offset;
        if (a.settings.lineScale !== undefined) T.lineScale = +a.settings.lineScale;
        if (a.settings.snap !== undefined) T.snap = !!a.settings.snap;
    }
    for (const t of a.times || []) {
        const i = lineIdx(t.line);
        if (!T.lineTimes) T.lineTimes = {};
        if (t.time == null) delete T.lineTimes[i]; else T.lineTimes[i] = +t.time;
    }
    if (a.clear && T.lineTimes) T.lineTimes = {};
    replan();
    return {bpm: T.bpm, manualLines: Object.keys(T.lineTimes || {}).length};
}

function toolEditLine(a: any) {
    const i = lineIdx(a.line);
    const ln = S.plan.lines[i];
    if (a.text !== undefined) {
        const rowList = S.project.lyrics.replace(/\r/g, '').split('\n');
        const src = ln.src != null ? ln.src : i;
        const row = rowList[src] || '';
        const pre = (row.match(/^\s*(?:\[\d+:\d+(?:[.:]\d+)?\])*/) || [''])[0];
        rowList[src] = pre + a.text;
        S.project.lyrics = rowList.join('\n');
        bus.emit('lyrics-sync');
    }
    if (a.layout !== undefined) {
        check(a.layout === '' || J.LAYOUTS[a.layout], '未知布局：' + a.layout);
        setOv(i, {layout: a.layout || undefined});
    }
    if (a.cuts !== undefined) {
        check(a.cuts >= 0 && a.cuts <= 6, 'cuts 0-6');
        setOv(i, {cuts: a.cuts || undefined, single: undefined});
    }
    if (a.lock !== undefined) {
        if (a.lock) setOv(i, {
            lock: true,
            lockedSeed: S.plan.lines[i].seed,
            lockedCuts: J.lineSnapshot(S.plan, i) || undefined
        });
        else setOv(i, {lock: false, lockedSeed: undefined, lockedCuts: undefined});
    }
    replan();
    flushSave();
    return {line: a.line, applied: Object.keys(a).filter(k => k !== 'revision' && k !== 'line')};
}

function toolEditCut(a: any) {
    const i = lineIdx(a.line);
    const cuts = S.plan.cuts.filter((c: any) => c.line === i && J.LAYOUTS[c.layout] && !J.LAYOUTS[c.layout].special);
    check(a.cut >= 1 && a.cut <= cuts.length, '该行不存在此切分');
    const cell = cuts[a.cut - 1];
    check(a.key !== undefined || a.quiet !== undefined, '提供 key 或 quiet');
    if (a.key !== undefined) {
        check(a.key === '' || (J.registry(a.group) && J.registry(a.group)[a.key] && !J.registry(a.group)[a.key].special), '未知手法：' + a.group + '/' + a.key);
        setCutTech(i, cell.k, a.group, a.key);
    }
    if (a.quiet !== undefined) markCutQuiet(i, cell.k, [a.group], !!a.quiet);
    replan();
    return {cut: cell.k + 1, line: a.line, group: a.group, key: a.key !== undefined ? a.key : 'unchanged'};
}

function toolSetTechniques(a: any) {
    check(J.registry(a.group), '未知组：' + a.group);
    const en = S.project.enabled[a.group] || (S.project.enabled[a.group] = {});
    const keys = a.keys || J.order(a.group).filter((k: string) => J.registry(a.group)[k] && !J.registry(a.group)[k].special);
    for (const k of keys) check(J.registry(a.group)[k], '未知手法：' + a.group + '/' + k);
    for (const k of keys) en[k] = !!a.enabled;
    S.project.mood = null;
    replan();
    return {group: a.group, enabled: a.enabled, keys: keys.length};
}

function toolSetLocks(a: any) {
    const locks = locksOf();
    const table: any = a.kind === 'tech' ? locks.tech : locks.params;
    for (const k of a.keys) {
        check(k === 'none' || table[k] !== undefined || a.kind === 'params', '未知锁定项：' + k);
        table[k] = !!a.locked;
    }
    replanSoon(60);
    return {kind: a.kind, locked: a.keys};
}

function toolRandomize(a: any) {
    if (a.target === 'all') {
        omakase();
        return {target: 'all'};
    }
    if (a.target === 'line') {
        check(a.line !== undefined, '提供 line');
        const i = lineIdx(a.line);
        check(!S.plan.lines[i].interlude, '间奏行无法重抽');
        seek(S.plan.lines[i].start + 0.001);
        rerollCurrentCut('omakase');
        return {target: 'line', line: a.line};
    }
    if (a.target === 'cut') {
        check(a.line !== undefined && a.cut !== undefined, '提供 line 与 cut');
        const i = lineIdx(a.line);
        const cuts = S.plan.cuts.filter((c: any) => c.line === i && J.LAYOUTS[c.layout] && !J.LAYOUTS[c.layout].special);
        check(a.cut >= 1 && a.cut <= cuts.length, '该行不存在此切分');
        seek(cuts[a.cut - 1].start + Math.min(cuts[a.cut - 1].dur * 0.5, cuts[a.cut - 1].inDur + 0.05));
        rerollCurrentCut(a.strategy === 'shuffle' ? 'shuffle' : 'omakase');
        return {target: 'cut', line: a.line, cut: a.cut};
    }
    fail('invalid_input', 'unsupported target（本版本支持 all/line/cut）');
}

function toolPreview(a: any) {
    if (a.action === 'play') play();
    if (a.action === 'pause') pause();
    if (a.time !== undefined) seek(+a.time);
    if (a.loop !== undefined) {
        check(['all', 'line', 'cut', 'off'].includes(a.loop), 'bad loop');
        S.loop = a.loop;
    }
    if (a.volume !== undefined) AP.setVol(+a.volume, null);
    if (a.muted !== undefined) AP.setVol(null, !!a.muted);
    return {playing: S.playing, t: +S.t.toFixed(2), loop: S.loop};
}

function toolTap(a: any) {
    if (a.action === 'start') {
        const i = a.line ? lineIdx(a.line) : 0;
        startTap(S.plan.lines[i] ? S.plan.lines[i].start : 0);
    } else if (a.action === 'record') tapNow();
    else if (a.action === 'back') tapBack();
    else stopTap();
    return {active: !!S.tap};
}

function toolProject(a: any) {
    if (a.action === 'get') return {project: S.project};
    if (a.action === 'save') {
        flushSave();
        return {saved: 'localStorage（jizura.project.v1）；完整 .jizuralive 包用界面的「保存」按钮'};
    }
    fail('invalid_input', '本版本 project 仅支持 get/save（import 请用界面的「打开」）');
}

let exportJob: any = null;

function toolStartExport(a: any) {
    check(['mp4', 'png', 'pnga', 'pngl'].includes(a.kind), 'kind 限 mp4/png/pnga/pngl（mp4file 直存请用界面按钮）');
    check(!S.exporting, '已有导出在进行');
    const job: any = {id: 'job-' + Date.now(), kind: a.kind, status: 'running'};
    exportJob = job;
    runExport(a.kind).then(() => {
        job.status = 'completed';
    }).catch((e: any) => {
        job.status = 'failed';
        job.error = String(e && e.message || e);
    });
    return {job};
}

function toolExportStatus() {
    return {job: exportJob};
}

function toolReset() {
    bus.emit('open-reset');
    return {status: 'needs_user_action', nextAction: '在应用里确认「初始化」对话框（工具不会绕过确认）'};
}

/* ---------- 工具注册表 ---------- */

const T = (name: string, description: string, inputSchema: any, run: (a: any) => any, readOnly = false): McpTool => ({
    name,
    description,
    inputSchema,
    run,
    readOnly
});
const obj = (props: any = {}, required: string[] = []) => ({
    type: 'object',
    properties: props,
    required,
    additionalProperties: false
});
const int = (min: number, max: number) => ({type: 'integer', minimum: min, maximum: max});
const num = (min: number, max: number) => ({type: 'number', minimum: min, maximum: max});
const str = (maxLength = 200) => ({type: 'string', maxLength});
const choice = (values: string[]) => ({type: 'string', enum: values});
const GROUPS = J.GROUP_KEYS as string[];

const TOOLS: McpTool[] = [
    T('get_state', '读取编辑器完整状态：工程、行（1 起始）、切分、当前切分、加载与导出状态。detail=false 为轻量状态', obj({detail: {type: 'boolean'}}), toolGetState, true),
    T('list_options', '查询 ID 与本地化名称。category：style/font 或手法组（layout/enter/hold/exit/decor/treat/bg/cam/fx/trans）。支持分页与关键词', obj({
        category: choice(['style', 'font', ...GROUPS]),
        query: str(100),
        page: int(1, 10000),
        pageSize: int(1, 100)
    }, ['category']), toolListOptions, true),
    T('set_lyrics', '整体替换歌词（支持 LRC 与记法：/ 切分、*强调*、! 闪烁、| 注释、[间奏 N]）；或 clear=true 清空歌词与逐行时间', obj({
        text: str(200000),
        clear: {type: 'boolean'}
    }), toolSetLyrics),
    T('update_settings', '按字段更新设置（只传需要改的）：曲名/歌手/语言/风格/seed/fx 参数/字体角色/套装开关/画幅/分辨率/fps/画质/键控背景/留白/导出范围。发现可选值用 list_options',
        obj({
            settings: obj({
                title: str(1000),
                artist: str(1000),
                lang: choice(['auto', 'ja', 'en', 'zh-Hant', 'zh-Hans', 'ko']),
                style: choice(J.STYLE_ORDER as string[]),
                seed: int(-2147483648, 2147483647),
                fx: obj({
                    motion: num(0, 1),
                    glitch: num(0, 1),
                    chroma: num(0, 1),
                    decor: num(0, 1),
                    density: num(0, 1),
                    texture: num(0, 1),
                    bgSwitch: num(0, 1),
                    flash: {type: 'boolean'},
                    hud: choice(['auto', 'on', 'off'])
                }),
                fonts: obj({display: str(80), serif: str(80), body: str(80)}),
                extra: {type: 'boolean'},
                wa: {type: 'boolean'},
                horror: {type: 'boolean'},
                typo: {type: 'boolean'},
                kinetic: {type: 'boolean'},
                unify: {type: 'boolean'},
                typeset: {type: 'boolean'},
                centerFree: {type: 'boolean'},
                includeAudio: {type: 'boolean'},
                centerDir: choice(['tb', 'lr']),
                keyBg: choice(['off', 'green', 'black']),
                aspect: choice(['16:9', '9:16', '4:3', '3:4', '1:1', '4:5', '21:9']),
                res: int(360, 2160),
                fps: int(12, 120),
                quality: choice(['standard', 'high', 'max']),
                exportRange: {
                    type: 'object',
                    properties: {from: int(1, 100000), to: int(1, 100000)},
                    required: ['from', 'to']
                },
            }),
        }, ['settings']),
        toolUpdateSettings),
    T('set_timing', '更新 BPM/偏移/行时缩放，设置或清除手动行时间（秒）。行引用需要 revision', obj({
        settings: obj({
            bpm: num(0, 300),
            offset: num(0, 86400),
            lineScale: num(0.3, 4),
            snap: {type: 'boolean'}
        }),
        times: {
            type: 'array',
            items: obj({
                line: int(1, 100000),
                time: {type: ['number', 'null'], minimum: 0, maximum: 86400}
            }, ['line', 'time'])
        },
        clear: {type: 'boolean'},
        revision: int(0, Number.MAX_SAFE_INTEGER)
    }), toolSetTiming),
    T('edit_line', '编辑一行：text 替换歌词正文（保留 LRC 时间戳前缀）；layout/cuts/lock 单独传。空 layout、cuts=0 恢复自动', obj({
        revision: int(0, Number.MAX_SAFE_INTEGER),
        line: int(1, 100000),
        text: str(10000),
        layout: str(80),
        cuts: int(0, 6),
        lock: {type: 'boolean'}
    }, ['revision', 'line']), toolEditLine),
    T('edit_cut', '设置某行内第 N 个切分的手法（组=layout/enter/hold/exit/decor/treat/bg/cam/trans）。key="" 恢复自动；quiet 控制附加效果抑制', obj({
        revision: int(0, Number.MAX_SAFE_INTEGER),
        line: int(1, 100000),
        cut: int(1, 1000),
        group: choice(['layout', 'enter', 'hold', 'exit', 'decor', 'treat', 'bg', 'cam', 'trans']),
        key: str(80),
        quiet: {type: 'boolean'}
    }, ['revision', 'line', 'cut', 'group']), toolEditCut),
    T('set_techniques', '整组启用/禁用手法。省略 keys 表示整组（与界面批量按钮一致）', obj({
        group: choice(GROUPS as string[]),
        keys: {type: 'array', items: str(80)},
        enabled: {type: 'boolean'}
    }, ['group', 'enabled']), toolSetTechniques),
    T('set_locks', '设置随机锁定：tech=手法组 / params=效果参数', obj({
        kind: choice(['tech', 'params']),
        keys: {type: 'array', items: str(40)},
        locked: {type: 'boolean'}
    }, ['kind', 'keys', 'locked']), toolSetLocks),
    T('randomize', '随机重抽：all=一键随机（含风格/氛围/配色）；line=重抽一行；cut=重抽一个切分（strategy: omakase/shuffle）。line/cut 需要 revision 与 1 起始引用，显式重抽会解除该行锁定（与界面一致）', obj({
        target: choice(['all', 'line', 'cut']),
        revision: int(0, Number.MAX_SAFE_INTEGER),
        line: int(1, 100000),
        cut: int(1, 1000),
        strategy: choice(['omakase', 'shuffle'])
    }, ['target']), toolRandomize),
    T('history', '撤销/重做：kind=edit（歌词与时间）与 look（外观）是两条独立历史', obj({
        kind: choice(['edit', 'look']),
        direction: choice(['back', 'forward'])
    }, ['kind', 'direction']), a => {
        const d = a.direction === 'back' ? -1 : 1;
        if (a.kind === 'edit') edGo(d); else histGo(d);
        return {kind: a.kind, moved: d};
    }),
    T('preview', '播放/暂停/定位（秒）/循环模式/预览音量与静音', obj({
        action: choice(['play', 'pause']),
        time: num(0, 86400),
        loop: choice(['all', 'line', 'cut', 'off']),
        volume: num(0, 1),
        muted: {type: 'boolean'}
    }, ['action']), toolPreview),
    T('tap_sync', '打点同步：start 从某行开始 / record 记录当前播放头 / back 回退一次 / stop 结束', obj({
        action: choice(['start', 'record', 'back', 'stop']),
        line: int(1, 100000),
        revision: int(0, Number.MAX_SAFE_INTEGER)
    }, ['action']), toolTap),
    T('project', '读取当前工程 JSON 或写入 localStorage 自动保存（完整 .jizuralive 包与音频/字体请用界面「保存/打开」）', obj({action: choice(['get', 'save'])}, ['action']), toolProject),
    T('start_export', '启动导出（WebCodecs，前端完成）：mp4 / png 序列 zip / pnga 透明 / pngl 分层。立即返回任务，用 get_export_status 轮询', obj({kind: choice(['mp4', 'png', 'pnga', 'pngl'])}, ['kind']), toolStartExport),
    T('get_export_status', '读取最近一次导出任务状态（running/completed/failed）', obj(), toolExportStatus, true),
    T('reset_project', '打开不可逆的「初始化」确认框（工具不会绕过确认，需用户在界面确认）', obj(), toolReset),
];

/* ---------- 协议语义（MCP Streamable HTTP, stateless） ---------- */

function execTool(t: McpTool, a: any) {
    check(J.ui && S.project && S.plan, '编辑器尚未初始化');
    check(!S.exporting, '导出进行中，请稍后再试');
    return t.run(a);
}

function rpcMethod(method: string, params: any): any {
    if (method === 'initialize') {
        return {
            protocolVersion: params && params.protocolVersion || '2025-06-18',
            capabilities: {tools: {listChanged: true}},
            serverInfo: {name: 'jizura-live', version: '1.0.0'},
        };
    }
    if (method === 'notifications/initialized' || method.startsWith('notifications/')) return undefined;
    if (method === 'ping') return {};
    if (method === 'tools/list') return {
        tools: TOOLS.map(t => ({
            name: t.name,
            description: t.description,
            inputSchema: t.inputSchema,
            annotations: {readOnlyHint: !!t.readOnly}
        }))
    };
    if (method === 'tools/call') {
        check(params && params.name, 'tools/call 缺少 name');
        const t = TOOLS.find(x => x.name === params.name);
        if (!t) return {content: [{type: 'text', text: '未知工具：' + params.name}], isError: true};
        const out = execTool(t, params.arguments || {});
        return {content: [{type: 'text', text: JSON.stringify(out === undefined ? {ok: true} : out)}], isError: false};
    }
    fail('method_not_found', '未知方法：' + method);
}

export function mcpRpc(body: any) {
    try {
        check(body && typeof body === 'object' && body.jsonrpc === '2.0' && typeof body.method === 'string', 'bad JSON-RPC');
        if (body.method.startsWith('notifications/')) return null;   // 通知：无响应
        const result = rpcMethod(body.method, body.params);
        return {jsonrpc: '2.0', id: body.id ?? null, result};
    } catch (e: any) {
        const code = e && e.code === 'method_not_found' ? -32601 : e && e.code === 'invalid_input' || e && e.code === 'stale_revision' ? -32602 : -32603;
        return {jsonrpc: '2.0', id: (body && body.id) ?? null, error: {code, message: String(e && e.message || e)}};
    }
}

/* ---------- 提示词（复制给 AI 代理） ---------- */

function promptText() {
    const tools = TOOLS.map(t => '- ' + t.name + '：' + t.description).join('\n');
    return [
        '你在操作「字面 Live」——一个本地歌词 PV 生成器（WPF + CEF 桌面应用，前端已通过 MCP 暴露编辑接口）。',
        '你的任务是用下列工具为用户制作并调整歌词 PV。',
        '',
        '## 工作流',
        '1. jizura_get_state 读取状态（loading 各项为 false 才继续编辑）。',
        '2. jizura_list_options 查风格/字体/手法的 ID（category：style/font/layout/enter/hold/exit/decor/treat/bg/cam/fx/trans）。',
        '3. jizura_set_lyrics 写歌词（支持 LRC 时间戳与记法：/ 切分、*强调*、! 闪烁、| 注释、[间奏 N]、# 注释）。',
        '4. jizura_update_settings 定风格/画幅/配色/字体；jizura_set_timing 定 BPM 与手动行时间。',
        '5. 用 revision + 1 起始的行/切分号做 jizura_edit_line / jizura_edit_cut / jizura_randomize 微调。',
        '6. jizura_preview 控制播放确认观感；完成后 jizura_start_export 导出（mp4/png 序列），jizura_get_export_status 轮询。',
        '',
        '## 工具清单',
        tools,
        '',
        '## 约束',
        '- 行与切分编号对代理是 1 起始；写操作必须带 get_state 返回的最新 revision（stale_revision 就重新读取）。',
        '- 音频/字体/工程包文件由用户通过界面投入，工具不接收文件路径。',
        '- reset_project 只会打开确认框，绝不绕过用户确认。',
        '- 修改歌词的 undo 与外观的 previous/next 是两条独立历史。',
    ].join('\n');
}

/* ---------- 暴露 ---------- */
(window as any).J = (window as any).J || {};
(window as any).J.mcp = {
    status: 'ready',
    tools: () => TOOLS.map(t => ({name: t.name, description: t.description})),
    prompt: promptText,
    call(name: string, args: any) {
        const t = TOOLS.find(x => x.name === name || x.name === 'jizura_' + name);
        if (!t) throw new Error('未知工具：' + name);
        return execTool(t, args || {});
    },
    rpc: mcpRpc,
};
