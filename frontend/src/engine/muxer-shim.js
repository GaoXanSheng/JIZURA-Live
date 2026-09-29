/* mp4-muxer 以 UMD 全局 Mp4Muxer 被 11_export.js 引用；
   这里把 vendor 副本挂回 window，保持引擎文件零改动。 */
import * as Mp4Muxer from './vendor/mp4-muxer.min.js';

window.Mp4Muxer = Mp4Muxer;
