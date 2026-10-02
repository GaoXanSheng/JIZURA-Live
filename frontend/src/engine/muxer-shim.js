// mp4-muxer 是普通脚本格式，顶层 var Mp4Muxer = IIFE，没有 ESM 导出；
// import * as 只会拿到空命名空间，StreamTarget/Muxer 全丢。
// 所以按 raw 读进来当函数跑一遍，取局部全局量挂回 window，引擎文件零改动
import muxerSource from './vendor/mp4-muxer.min.js?raw';

const factory = new Function(muxerSource + '\n;return Mp4Muxer;');
window.Mp4Muxer = factory();
