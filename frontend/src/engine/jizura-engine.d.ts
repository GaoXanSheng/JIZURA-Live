export interface JizuraEngine {
    // from 01_util.js
    clamp: (v: number, min: number, max: number) => number;
    lerp: (a: number, b: number, t: number) => number;
    inv: (a: number, b: number, v: number) => number;
    smooth: (t: number) => number;
    TAU: number;
    DEG: number;
    E: number;
    sid: () => string;
    h: (v: number) => number;
    r: () => number;
    rs: (min: number, max: number) => number;
    rr: (min: number, max: number) => number;
    pick: <T>(arr: T[]) => T;
    rng: (seed: number) => () => number;
    noise1: (x: number) => number;
    hex: (h: string) => [number, number, number];
    rgba: (r: number, g: number, b: number, a?: number) => string;
    mix: (c1: [number, number, number], c2: [number, number, number], t: number) => [number, number, number];
    lum: (c: [number, number, number]) => number;
    toHex: (r: number, g: number, b: number) => string;
    hsl: (h: number, s: number, l: number) => [number, number, number];
    toHsl: (r: number, g: number, b: number) => [number, number, number];
    contrast: (c1: [number, number, number], c2: [number, number, number]) => number;
    fitContrast: (c: [number, number, number], bg: [number, number, number]) => [number, number, number];
    GHOST_PAIRS: any;
    randomPalette: (bg?: string) => any;
    isKanji: (c: string) => boolean;
    isHira: (c: string) => boolean;
    isKata: (c: string) => boolean;
    isSmallKana: (c: string) => boolean;
    isPunct: (c: string) => boolean;
    isLatin: (c: string) => boolean;
    VERT_ROTATE: any;
    romaji: (t: string) => string;
    fmtTime: (t: number) => string;

    // from 02_fonts.js
    FONTS: Record<string, any>;
    addUserFont: (key: string, label: string, family: string, weight: number) => void;
    SAFE_FONT_KEY: RegExp;
    safeFamily: (f: string) => string;
    loadFontFile: (f: File) => Promise<any>;
    restoreUserFonts: (list: any[]) => Promise<any[]>;
    missingUserFonts: (keys: string[]) => string[];
    fontCSS: (key: string) => string;
    fontsOfPlan: (plan: any) => string[];
    ensureFonts: (txt: string, keys: string[]) => Promise<void>;
    metrics: (key: string) => any;
    glyphs: (txt: string, key: string) => any;
    fragments: (txt: string, key: string) => any;

    // from 02b_lang.js
    LANG_LABEL: Record<string, string>;
    detectLang: (txt: string) => string;
    resolveLang: (txt: string, fallback?: string) => string;
    LANG_FACES: Record<string, any>;
    lang: string;
    setLang: (l: string) => void;
    POOLS: Record<string, any>;
    pool: (k: string) => any;
    faceOf: (l: string) => any;
    langBaseFaces: (l: string) => any;
    segLocale: (l: string) => any;

    // from 03_text.js
    PID: number;
    PT: number;
    TYPESET: any;
    setTypeset: (t: any) => void;
    typesetLine: (line: any, env: any) => any;
    layoutText: (item: any, env: any) => any;
    drawItem: (ctx: CanvasRenderingContext2D, item: any, t: number, env: any) => void;
    decoTextKind: (t: string) => 'no' | 'time' | null;
    hideDecoText: (env: any, text: string) => boolean;
    weightNow: (w: number, t: number) => number;
    varFontCSS: (key: string, w: number) => string;
    textPattern: (ctx: CanvasRenderingContext2D, item: any, env: any) => any;
    measure: (item: any, env: any) => any;
    fitSize: (w: number, h: number, tw: number, th: number) => number;

    // from 04_styles.js
    STYLES: Record<string, any>;
    STYLE_ORDER: string[];
    resolveStyle: (s: string) => any;
    KEY_BG: any;
    keyMode: (project: any) => string;

    // from 05_anim.js
    itemBands: (item: any) => any;
    itemBox: (item: any) => any;
    ENTER: Record<string, any>;
    HOLD: Record<string, any>;
    EXIT: Record<string, any>;
    combineChar: (c: any, env: any) => any;
    combinePiece: (p: any, env: any) => any;

    // from 05b_registry.js
    TREAT: Record<string, any>;
    TREAT_ORDER: string[];
    BG: Record<string, any>;
    BG_ORDER: string[];
    CAMERA: Record<string, any>;
    CAMERA_ORDER: string[];
    FXE: Record<string, any>;
    FXE_ORDER: string[];
    TRANS: Record<string, any>;
    TRANS_ORDER: string[];
    GROUP_KEYS: string[];
    registry: (g: string) => any;
    order: (g: string) => string[];
    register: (g: string, k: string, v: any) => void;
    taggedWith: (g: string, tag: string) => string[];

    // from 06_layouts.js
    mainDraw: (ctx: CanvasRenderingContext2D, plan: any, t: number, env: any) => void;
    drawFx: (ctx: CanvasRenderingContext2D, plan: any, t: number, env: any) => void;
    splitLines: (txt: string) => string[];
    glyphCount: (txt: string) => number;
    fontsOf: (item: any) => string[];
    unionBB: (items: any[]) => any;
    centerBB: (bb: any) => any;
    LAYOUTS: Record<string, any>;
    LAYOUT_ORDER: string[];
    ENTER_ORDER: string[];
    HOLD_ORDER: string[];
    EXIT_ORDER: string[];

    // from 07_decor.js
    DECOR: Record<string, any>;
    DECOR_ORDER: string[];
    drawHUD: (ctx: CanvasRenderingContext2D, plan: any, t: number, env: any) => void;

    // from 08_planner.js
    SAMPLE_LYRICS: string;
    defaultProject: () => any;
    komaOf: (f: any) => any;
    stepDur: (bpm: number, div: number) => number;
    parseLyrics: (txt: string) => any[];
    parseOrderV1: (txt: string) => number[] | null;
    isWordLike: (t: string) => boolean;
    joinWords: (arr: string[]) => string;
    isLatinText: (t: string) => boolean;
    segments: (lines: any[]) => any[];
    chunkText: (txt: string, max: number) => string[];
    phraseChunks: (lines: any[]) => any[];
    computeTiming: (lines: any[], bpm: number, offset: number) => void;
    plan: (project: any, audio: any) => any;
    lineSnapshot: (plan: any, i: number) => any;
    sideZones: (plan: any) => any;
    previewPlan: (project: any, g: string, k: string) => any;
    designSize: (project: any) => [number, number];
    outputSize: (project: any) => [number, number];

    // from 08b_omakase.js
    MOODS: Record<string, any>;
    THEMES: Record<string, any>;
    THEME_ORDER: string[];
    omakase: (project: any, rnd?: () => number, themeId?: string | null) => any;

    // from 09_render.js
    cutAt: (plan: any, t: number) => any;
    Renderer: any; // TODO: refine

    // from 10_audio.js
    analyzeAudio: (buf: AudioBuffer) => Promise<any>;
    audioWav: (buf: AudioBuffer, dur: number, offset: number) => Blob;
    /* 音频特效（预览与导出共用图；定义见 11_export.js） */
    readonly AUDIO_EQ_BANDS: number[];
    sanitizeAudioFx: (fx: any) => { preset: 'none' | 'pan' | 'spatial' | 'orbit' | 'eq'; rate: number; depth: number; eq: number[] };
    buildAudioFx: (ctx: BaseAudioContext, cfg: any, opts?: { songDur?: number; fromT?: number; toT?: number; anchorT?: number }) =>
        { input: AudioNode; output: AudioNode; channels: number } | null;
    saveSong: (f: File) => Promise<void>;
    loadSong: () => Promise<File | null>;
    saveFontData: (k: string, b: ArrayBuffer) => Promise<void>;
    loadFontData: (k: string) => Promise<ArrayBuffer | null>;
    forgetSong: () => Promise<void>;
    beatGrid: (bpm: number, offset: number, dur: number) => number[];

    // from 11_export.js
    saveFile: (name: string, blob: Blob) => Promise<void>;
    lrcText: (project: any, lines: any[], range: { from: number; to: number; t0: number } | null) => string;
    videoBitrate: number;
    pickVideoCodec: (w: number, h: number, fps: number, br: number) => Promise<any>;
    videoAttempts: number;
    pickAudioCodec: () => Promise<any>;
    exportSpan: (plan: any, range: any) => any;
    exportMP4: (opts: any) => Promise<any>;
    exportPNGZip: (opts: any) => Promise<any>;
    exportWebM: (opts: any) => Promise<any>;
    AE_MAP: any;
    planForAE: (plan: any) => any;

    // from 11q_sets.js
    BASE_PACKS: any;
    BASE_STYLES: any;
    EXTRA_FONTS: any;
    WA: any;
    SETS: Record<string, any>;
    SET_ORDER: string[];
    setOn: (project: any, s: string) => void;
    randomOk: (project: any, g: string, k: string) => boolean;

    // ui interop
    ui?: any;
    uiApi?: any;
}
