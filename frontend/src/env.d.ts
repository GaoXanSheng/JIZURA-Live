/// <reference types="vite/client" />

// version 来自 package.json，vite define 编译期注入（见 vite.config.ts）
declare const __APP_VERSION__: string;

// 歌词引擎是 IIFE 挂在 window.J 上的，刻意保持原样不模块化
declare interface Window {
    J: import('./engine/jizura-engine').JizuraEngine;
    Mp4Muxer?: any;
    __jl_spout_mirror?: boolean;
    __jl_spout_transparent?: boolean;
    __jl_no_sub?: boolean;
}
