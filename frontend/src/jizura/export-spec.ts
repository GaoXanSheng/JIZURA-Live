// 导出勾选模型（纯函数，零依赖）：OutTab 的勾选组合 → 校验 → 可执行参数（ExportSpec），
// MCP/agent-api 的旧 kind 也归一到同一份 spec。导出编排（export.ts）只认 spec，
// 引擎与 Worker 只吃 spec 拆出来的原始选项——三层各改各的，互不牵连。

export type ExportFormat = 'mp4' | 'webm' | 'png';
export type BgStyle = 'off' | 'green' | 'black';

// 导出面板的勾选状态（持久化在 project.exportCfg）
export interface ExportCfg {
    format: ExportFormat;
    visuals: boolean;   // 渲染画面（歌词动画层）
    subs: boolean;      // 云字幕烤入
    bg: boolean;        // 背景（false = 透明底，仅 WebM/PNG）
    bgStyle: BgStyle;   // 背景形态（bg=true 时生效；off=常规）
    audio: boolean;     // 歌曲：MP4 内嵌 / 其它格式转另存 WAV
    layered: boolean;   // PNG 分层输出（back/ front/ 双目录）
    toFile: boolean;    // MP4 大视频直接写入文件
}

export const EXPORT_BG_STYLES: { value: BgStyle; label: string }[] = [
    {value: 'off', label: '常规'},
    {value: 'green', label: '绿幕'},
    {value: 'black', label: '黑幕'},
];

export const DEFAULT_EXPORT_CFG: ExportCfg = {
    format: 'mp4', visuals: true, subs: false, bg: true,
    bgStyle: 'off', audio: true, layered: false, toFile: false,
};

// 可执行导出参数：直接对应引擎/Worker 的原始选项
export interface ExportSpec {
    kind: 'mp4' | 'mp4file' | 'webm' | 'png';
    transparent: boolean;
    layer: 'front' | null;
    noHud: boolean;
    layers: boolean;          // png 分层（back/ front/）
    customDrawOnly: boolean;  // 纯字幕层（无渲染画面，只画字幕，必须主线程）
    bakeSubs: boolean;        // overlay 烤入字幕
    embedAudio: boolean;      // mp4 内嵌音轨
    keyBg: BgStyle;
}

// buildSpec 的环境事实（调用方从 S/J 取，纯模块不做副作用查询）
export interface SpecCtx {
    subOn: boolean;       // 字幕面板 content !== 'off'
    audioLoaded: boolean; // 已载入歌曲
}

export type SpecResult =
    | { ok: true; spec: ExportSpec; wavToo: boolean }
    | { ok: false; error: string };

export function normalizeCfg(raw: any): ExportCfg {
    const o = raw && typeof raw === 'object' ? raw : {};
    const bool = (v: any, d: boolean) => typeof v === 'boolean' ? v : d;
    const format: ExportFormat = o.format === 'webm' || o.format === 'png' ? o.format : 'mp4';
    const bgStyle: BgStyle = o.bgStyle === 'green' || o.bgStyle === 'black' ? o.bgStyle : 'off';
    return {
        format,
        visuals: bool(o.visuals, true),
        subs: bool(o.subs, false),
        bg: bool(o.bg, true),
        bgStyle,
        audio: bool(o.audio, true),
        layered: bool(o.layered, false),
        toFile: bool(o.toFile, false),
    };
}

export function buildSpec(cfg: ExportCfg, ctx: SpecCtx): SpecResult {
    if (!cfg.visuals && !cfg.subs) {
        return {ok: false, error: '至少勾选「渲染画面」或「字幕」，否则没有可导出的内容'};
    }
    if (cfg.subs && !ctx.subOn) {
        return {ok: false, error: '字幕渲染未开启：请先在「字幕」面板选择要导出的字幕内容（翻译/音译）'};
    }
    if (cfg.format === 'mp4' && (!cfg.visuals || !cfg.bg)) {
        return {ok: false, error: 'MP4 不支持透明/无背景导出：请改用 WebM 视频，或把背景勾上（可用绿幕/黑幕形态）'};
    }
    // mp4 大视频直存只在浏览器支持文件句柄时有意义， capability 由调用方折进 toFile
    const base = {
        transparent: false, layer: null as 'front' | null, noHud: false,
        layers: false, customDrawOnly: false, bakeSubs: cfg.subs,
        embedAudio: false, keyBg: (cfg.bg ? cfg.bgStyle : 'off') as BgStyle,
    };
    if (cfg.format === 'mp4') {
        return {
            ok: true,
            wavToo: false,
            spec: {...base, kind: cfg.toFile ? 'mp4file' : 'mp4', embedAudio: cfg.audio},
        };
    }
    if (cfg.format === 'webm') {
        // 勾背景=不透明成片（带风格背景与 HUD）；不勾=透明 alpha，且只画前景层；
        // 纯字幕层没有「背景」可言，永远透明底
        const frontOnly = cfg.visuals && !cfg.bg;
        const customDrawOnly = !cfg.visuals;
        return {
            ok: true,
            wavToo: cfg.audio,
            spec: {
                ...base,
                kind: 'webm',
                transparent: customDrawOnly ? true : !cfg.bg,
                layer: frontOnly ? 'front' : null,
                noHud: frontOnly,
                customDrawOnly,
                bakeSubs: cfg.subs && cfg.visuals,
            },
        };
    }
    // png：勾背景=不透明整帧（可选分层 back/front）；不勾=透明前景（frontOnly，无 HUD）；
    // 只勾字幕=透明底纯字幕序列。分层必须是透明 PNG（back/ front/ 都是拿来叠的）
    const layers = cfg.layered && cfg.visuals && cfg.bg;
    const frontOnly = cfg.visuals && !cfg.bg && !layers;
    return {
        ok: true,
        wavToo: cfg.audio,
        spec: {
            ...base,
            kind: 'png',
            transparent: layers ? true : !(cfg.visuals && cfg.bg),
            layer: frontOnly ? 'front' : null,
            noHud: frontOnly,
            layers,
            customDrawOnly: !cfg.visuals,
            bakeSubs: cfg.subs && cfg.visuals,
        },
    };
}

// MCP/agent-api 的旧 kind（本勾选模型上线前的 9 种按钮）→ 等价 spec，行为与旧版逐一对齐：
// 旧版里只有 mp4/mp4file 烤字幕（v0.10 字幕回归修复），webm/png 家族一律不带字幕
export function specFromLegacyKind(kind: string, project: any): ExportSpec | null {
    const keyBg = (p: any): BgStyle => p && (p.keyBg === 'green' || p.keyBg === 'black') ? p.keyBg : 'off';
    switch (kind) {
        case 'mp4':
            return {
                kind: 'mp4', transparent: false, layer: null, noHud: false, layers: false,
                customDrawOnly: false, bakeSubs: true,
                embedAudio: !!project && project.includeAudio !== false, keyBg: keyBg(project),
            };
        case 'mp4file':
            return {
                kind: 'mp4file', transparent: false, layer: null, noHud: false, layers: false,
                customDrawOnly: false, bakeSubs: true,
                embedAudio: !!project && project.includeAudio !== false, keyBg: keyBg(project),
            };
        case 'webm':
            return {
                kind: 'webm', transparent: true, layer: null, noHud: false, layers: false,
                customDrawOnly: false, bakeSubs: false, embedAudio: false, keyBg: 'off',
            };
        case 'webmfront':
            return {
                kind: 'webm', transparent: true, layer: 'front', noHud: true, layers: false,
                customDrawOnly: false, bakeSubs: false, embedAudio: false, keyBg: 'off',
            };
        case 'subwebm':
            return {
                kind: 'webm', transparent: true, layer: null, noHud: false, layers: false,
                customDrawOnly: true, bakeSubs: true, embedAudio: false, keyBg: 'off',
            };
        case 'png':
            return {
                kind: 'png', transparent: false, layer: null, noHud: false, layers: false,
                customDrawOnly: false, bakeSubs: false, embedAudio: false, keyBg: keyBg(project),
            };
        case 'pnga':
            return {
                kind: 'png', transparent: true, layer: null, noHud: false, layers: false,
                customDrawOnly: false, bakeSubs: false, embedAudio: false, keyBg: 'off',
            };
        case 'pngl':
            return {
                kind: 'png', transparent: true, layer: null, noHud: false, layers: true,
                customDrawOnly: false, bakeSubs: false, embedAudio: false, keyBg: 'off',
            };
        case 'pngf':
            return {
                kind: 'png', transparent: true, layer: 'front', noHud: true, layers: false,
                customDrawOnly: false, bakeSubs: false, embedAudio: false, keyBg: 'off',
            };
        default:
            return null;
    }
}
