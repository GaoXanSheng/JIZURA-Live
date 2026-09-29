/// <reference types="vite/client" />

// 歌词引擎以 IIFE 形式挂载在 window.J 上（原样保留，不做模块化改写）
declare interface Window {
  J: any;
  Mp4Muxer?: any;
  __jl_spout_mirror?: boolean;
  __jl_spout_transparent?: boolean;
}
