# src/Engine — 纯 C# 渲染引擎（SkiaSharp）

移植自 `frontend/src/engine/*.js`（Canvas 2D，~59k 行）。**文件顺序即依赖顺序**，
对位表如下（`frontend/src/engine/index.ts` 的导入序）。迁移期间 frontend/ 冻结保留作参照，
每个文件移植完成后在此表打勾。

## 移植对照表

| 目标文件 | 源文件 | 内容 | 状态 |
|---|---|---|---|
| `J.cs`+`Rng.cs` | 01_util.js | 工具函数/mulberry32（位级对拍通过） | ✅ |
| `JLang.cs` | 02b_lang.js | 日/中/韩/英分语种（行为对拍零差异） | ✅ |
| `JFonts.cs` | 02_fonts.js | 字体目录23条逐字对拍；ensureFonts/用户字体（IndexedDB 部分 M3） | ✅部分 |
| `JText.cs` | 03_text.js | typesetLine/layoutText + GlyphCache（矢量路径版） | ☐ |
| `JStyles.cs` | 04_styles.js | 风格/主题/配色 | ☐ |
| `JAnim.cs` | 05_anim.js | easing/动画原语 | ☐ |
| `Registry.cs` | 05b_registry.js | 10 组注册表 + 核心条目种子 + Env | ✅ |
| `JLayouts.cs` | 06_layouts.js | 19 个内置 layout（draw item 流水线待 05_anim/03_text） | ✅大部分 |
| `JDecor.cs` | 07_decor.js | 内置 decor | ☐ |
| `Planner.cs` | 08_planner.js | Plan 全量（segments 用字符类回退，TODO ICU） | ✅ |
| `Omakase.cs` | 08b_omakase.js | 一键随机 | ☐ |
| `Renderer.cs` | 09_render.js | Frame(SKCanvas, plan, t, opts) 主管线 | ☐ |
| `JAudio.cs` | 10_audio.js | BeatGrid 已移植；分析算法直接用 C# AudioAnalyzer，WAV 在 src/Export | 部分 |
| `AudioFx.cs` | 10b_audiofx.js | 效果器架 v2 → NAudio ISampleProvider DSP 图（实时/离线同路径；biquad/延迟/LFO/分块 FFT 卷积自实现；sanitize 16 例 node 对拍零差异；HRTF→等功率声像近似） | ✅ |
| `Export.cs` | 11_export.js | 导出编排（编码走 src/Export） | ☐ |
| `Packs/*.cs` | 11p_*.js | 全部表现包（layoutsA–D/enter/enterB/exit/exitB/decor/decorB/fxB/horror1–3/kinetic1–3/typo1–3/looks/styles/treattrans/bgcamB/kline/ascii/audiofx） | ☐ |
| `Sets.cs` | 11q_sets.js | 预设集 | ☐ |
| `ZhLabels.cs` | zh-labels.js | 简中标签覆盖 | ☐ |

## 移植约定

1. **Canvas2D → SkiaSharp 直译**：`fillStyle/strokeStyle` → `SKPaint.Color`；路径指令同构
   （moveTo/lineTo/bezier/quadratic 一一对应）；`globalAlpha` → `paint.Alpha`；
   `ctx.filter: blur()/drop-shadow()` → `SKImageFilter.CreateBlur/CreateDropShadow`；
   `globalCompositeOperation` → `SKBlendMode`；`drawImage` → `canvas.DrawImage/DrawBitmap`。
   共性模式收敛到 `C2` 静态助手，**不做永久性 Canvas2D 兼容层**。
2. **GlyphCache 改矢量**：JS 版按 alpha 掩膜连通域拆笔画；C# 版用 `SKFont.GetGlyphPath`
   取矢量路径后做连通域拆分，分辨率无关、质量更好。
3. **随机数**：JS 版大量使用种子随机（mulberry32 等）保证同 seed 逐帧一致；
   C# 移植必须逐算法对齐（不能换 Random 实现），否则表现包动画轨迹会漂移。
4. **注册表语义不变**：`name`（日文标签）/`tags`（氛围）/`w`（权重）/`pack`/`extra`/`special`
   与 `J.register` 的替换告警行为保持一致——TechTab、omakase、MCP `list_options` 都依赖它。
5. **plan 数据结构逐字段对齐**：`J.plan()` 产物是渲染/导出/MCP 三方契约，字段名不改
   （导出 AE JSON、.jizuralive 兼容都压在它上面）。
6. **双渲染分支**：`st.kline → KLINE_RENDERER`、`st.ascii → asciiFrame` 独立于主管线，
   移植时保持独立类，不并入 Renderer 主流程。
