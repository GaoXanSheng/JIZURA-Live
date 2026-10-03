/* 引擎导入顺序 = 原 build.py 的文件名排序拼接顺序。
   每个文件都是自包含 IIFE，只通过 window.J 通信；顺序即依赖顺序。 */
import './muxer-shim.js';
import './01_util.js';
import './02b_lang.js';
import './02_fonts.js';
import './03_text.js';
import './04_styles.js';
import './05_anim.js';
import './05b_registry.js';
import './06_layouts.js';
import './07_decor.js';
import './08_planner.js';
import './08b_omakase.js';
import './09_render.js';
import './10_audio.js';
import './10b_audiofx.js'; // 音频特效 v2：效果器架 + 预设 + 变速（11_export 的 resample 依赖它）
import './11_export.js';
import './11p_bgcamB.js';
import './11p_decor.js';
import './11p_decorB.js';
import './11p_enter.js';
import './11p_enterB.js';
import './11p_exit.js';
import './11p_exitB.js';
import './11p_fxB.js';
import './11p_horror1.js';
import './11p_horror2.js';
import './11p_horror3.js';
import './11p_kinetic1.js';
import './11p_kinetic2.js';
import './11p_kinetic3.js';
import './11p_layoutsA.js';
import './11p_layoutsB.js';
import './11p_layoutsC.js';
import './11p_layoutsD.js';
import './11p_looks.js';
import './11p_styles.js';
import './11p_treattrans.js';
import './11p_typo1.js';
import './11p_typo2.js';
import './11p_typo3.js';
import './11q_sets.js';
import './11p_kline.js'; // K线（股票蜡烛图）风格：完全独立的音频驱动渲染器
import './zh-labels.js'; // 简中标签覆盖（风格/部件名/示例歌词），必须在所有引擎文件之后

export const J: any = (globalThis as any).J;
