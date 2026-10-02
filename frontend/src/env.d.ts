/// <reference types="vite/client" />

// package.json 的 version 经 vite define 编译期注入（见 vite.config.ts）
declare const __APP_VERSION__: string;

// 歌词引擎以 IIFE 形式挂载在 window.J 上（原样保留，不做模块化改写）
declare interface Window {
    J: import('./engine/jizura-engine').JizuraEngine;
    Mp4Muxer?: any;
    __jl_spout_mirror?: boolean;
    __jl_spout_transparent?: boolean;
    __jl_no_sub?: boolean;
}
