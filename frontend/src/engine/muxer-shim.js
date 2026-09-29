/* mp4-muxer 是普通脚本格式（顶层 var Mp4Muxer = IIFE），没有 ESM 导出；
   以裸函数执行后取其局部全局量挂回 window，保持引擎文件零改动。
   （不能 import * as —— 那会得到空命名空间，覆盖后 StreamTarget/Muxer 全丢） */
import muxerSource from './vendor/mp4-muxer.min.js?raw';

const factory = new Function(muxerSource + '\n;return Mp4Muxer;');
window.Mp4Muxer = factory();
