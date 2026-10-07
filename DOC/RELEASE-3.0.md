# JIZURA Live 3.0（纯 C# 版）发布说明

## 架构变更（相对 2.1）
- **移除 CEF/Chromium**：渲染、编辑器 UI、导出全部为 .NET 原生
- **移除 EmbedIO**：唯一网络端点为 /mcp（HttpListener，BCL）
- **移除 frontend/**（Vue3 SPA）：编辑器为 WPF 原生三栏界面
- 渲染引擎：SkiaSharp CPU 光栅逐文件移植自原 Canvas2D 引擎（frontend 可从 git 历史溯源）
- 导出：ffmpeg 管道（mp4/webm[透明]/png 序列/wav；ffmpeg.exe 需在 PATH 或程序目录）

## 保留兼容
- .jizuralive 工程格式（JZLV 容器）双向兼容
- Spout 发送器名（JIZURA-live / -LYRICS 双通道）
- MCP 端点 /mcp（17 工具，HttpListener）
- settings.json / 日志目录

## 已知差异（对照 2.1）
- 字幕烤入（subwebm/bakeSubs）待字幕层移植；导出规格会以清晰文案拒绝
- 歌单页/歌词版本切换未搬（客户端已就绪）
- ASCII化 fx 待渲染器 scratch 插件路径（条目已登记）

## 校验
- 242+ 测试全绿（含 KRC/导出/对拍/注册表冒烟）
- plan 生成与真实 JS 引擎逐位对拍（ParityGoldenTests）
- 真实网络 E2E：云音乐搜索/装载/KRC、SMTC 跟随
