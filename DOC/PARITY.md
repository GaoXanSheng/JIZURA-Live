# 纯 C# 引擎 ↔ 真实 JS 引擎 对抗验证报告

日期：2026-10-07（pure-csharp 分支，M2a 后夜间对抗验证）
方法：**无头 Edge 运行原版 JS 引擎产黄金值**（frontend/src/engine 原文件，非重写），
与纯 C# 引擎做同输入对拍。工具链在 `tools/parity/`。

## 对齐措施（控制变量）

| 变量 | 处理 |
|---|---|
| 分词 | JS 侧禁用 `Intl.Segmenter`（C# 用字符类回退，表面一致） |
| 表现包 | JS 侧只加载核心文件（C# 当前注册面 = 01…10b，无 11p_*） |
| 字体 | 双侧统一映射到同一系统字体（harness `?famsys=1` ↔ C# 兜底映射），字体差异剥离 |
| Math.random 纹理 | JS 自身连跑两次对照（js-vs-js 地板 0.22/255），证明 C# 差异非随机性 |

## 结果

### A. plan 全量 JSON 精确对拍 —— **21/21 EXACT MATCH**

- smoke + fuzz00–19（种子确定性生成）：12 种风格 × 7 画幅 × LRC 混排/间奏/手切/强调/
  BPM+snap+行时刻/合成音频能量节拍/unify/typeset/horror/多语言，全覆盖
- cuts/lines/events/timing/fx/params 逐字段逐位一致（容差 1e-6，float32 最短表示往返）

### B. 排版层探针 —— 精确一致

- `J.measure` 量宽：三字体 key 全同
- `J.layoutText` 逐字符 x/y/w/h：10 单元全部 Δ<0.05

### C. 帧级结构比对 —— 结构一致，残差为平台 AA 特性

| 对比 | t=1.2 | t=2.5 | t=4.0 |
|---|---|---|---|
| JS vs JS（地板） | 0.22/255 | 0.22/255 | 0.21/255 |
| C# vs JS | ~9.9 | ~12.1 | ~9.1 |

- 全帧差异图（3× 放大）：**仅字形边缘 1px AA 光晕**，字形主体/背景/色差/装饰全黑（一致）
- 局部放大裁剪对比：肉眼不可分辨
- 结论：残差是 Chromium 文本栈与 SkiaSharp 文本栈的抗锯齿/伽马特性差，属平台固有；
  该指标作为**回归金丝雀**使用（结构性 bug 会让分数爆炸：diag 旋转 bug 时 t=1.2 曾达 64）

## 对抗验证抓到并修复的 bug

1. **弧度/角度混用 ×3**（JS `ctx.rotate(deg * J.DEG)` 收弧度，C# `RotateDegrees` 收度数）：
   - `Renderer.cs` 相机旋转（影响所有运镜）
   - `JLayouts.cs` diag 斜带的带子旋转（t=1.2 帧差 64→9.9 的元凶）
   - `JLayouts.cs` 另一处逐字旋转
2. **字体解析面不一致**：C# 侧 mincho 落到与 JS 不同的字体（探针 264.99 vs 193.60 抓获），
   补齐与 PreviewEngine 同款的系统字体兜底映射
3. `/api/app/render/restart` 的历史 ERROR 为修复前残留，复测 3/3 通过、新日志零错误

## 长跑压测

- 应用连续运行 6+ 分钟：API 探活 12/12 全 200，Spout 双通道持续出帧
  （主通道 67–80 帧/5s ≈ 15fps CPU 光栅；字幕通道 ~8fps@LyricsFps=10），零异常零崩溃
- 优雅关停无 teardown AV

## 常驻回归

`tests/JizuraLive.Engine.Tests/ParityGoldenTests.cs`：5 个模糊工程的 JS 黄金值入库存档
（fixtures/parity/，275KB），`dotnet test` 即对拍，23/23 通过。
黄金值重生成：`node tools/parity/run-js.cjs fuzzNN --famsys --noframes`

## 已知未闭合项（记入路线图）

- 文本 AA 特性差（上述 C 项）：M2 字体管线接通自托管 woff2 后可与浏览器更接近，但不会归零
- C# 引擎 segments 用字符类回退（TODO ICU word segmentation）：对齐表面后不影响确定性，
  分词质量在 M5 polish
- 用户字体 IndexedDB 持久化（M3 落 %LOCALAPPDATA%）
