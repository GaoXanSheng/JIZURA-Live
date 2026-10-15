<div align="center">

  <img src="./assets/icon.png" alt="JIZURA Live PV Logo" width="128" />

  <h1>JIZURA Live PV（字面 Live PV）</h1>

  <p>面向 PV 与 VTube Studio 联动的实时歌词动效合成工作台</p>
</div>

## 这是什么

Windows 桌面工作台：本地音频 + LRC 歌词 + 图层/效果图，在固定设计画布上做 GPU 合成并实时预览，
预留 VTS 虚拟角色与 Spout 联动入口。目标形态是「PV 导演工作台」，当前处于**从 CPU 光栅向 GPU
渲染迁移**的中间态。能用的和没做的都写在下面，不夸大。

## 当前状态

**能用：** WPF 工作台；D3D11 + Direct2D + DirectWrite 渲染（`src/Render`）；视差运镜、图层栈合成、
环境光融合，按设计画布求值再 letterbox 贴窗；23 种 GPU 效果 + 9 个预设（Bloom 支持多 pass）；
LRC 解析与逐字排版；本地音频时钟（NAudio）；Spout 输入/输出入口、VTS 追踪探针。

**还没做 / 不完整（不要当成已有功能）：** 时间轴、关键帧、多轨编辑、连线画布——**未实现**，
目前只有进度滑条与效果列表；工程保存只存场景 JSON，效果文档到 Dispose 才另存，音频/LRC 路径与
撤销历史未闭环；Spout 输出主窗口没有创建或提交，`IsActive` 只代表初始化成功；VTS 主窗口没有
会话，探针演示不等于产品接入；顶层字幕 `SubtitleProvider.Render` 只清透明底，不绘字；音画同步
取解码游标，未做设备级脉冲校准。未经验证：主工作台实际操作、OBS 接收、真实 VTS 动作、长时间显存曲线。

## 构建与测试

需要 Windows + .NET SDK 10.0（版本锁在 `global.json`）。项目是 `net10.0-windows` 的 WPF x64 应用。

```bash
dotnet build JizuraLive.csproj -t:Rebuild -nologo
dotnet test tests/JizuraLive.Engine.Tests/JizuraLive.Engine.Tests.csproj -nologo
dotnet build tools/RenderProbe/RenderProbe.csproj -nologo
```

- GPU 测试在无 D3D11 设备的机器上判为**已跳过**（不是通过），日志写明原因。
- golden 对拍基准**缺失即失败**。有意变更画面后更新基准：设 `JIZURA_GOLDEN_UPDATE=1` 重跑对应
  测试，肉眼复核新图，再与 `.png` 一起提交。不允许自动生成后当作回归通过。
- `tools/RenderProbe` 开窗跑实帧渲染并打印帧率，CI 里只构建不运行。

## 目录结构

| 路径 | 内容 |
|---|---|
| `src/App/` | WPF 入口、工作台窗口、i18n 词库 |
| `src/Render/` | 渲染核心：Gpu（D3D11/交换链/效果链）、Draw（Immediate2D）、Effects、Text |
| `src/Pv/` | PV 管线与舞台：`Stage/` 合成器，`Model/` 场景与图层模型 |
| `src/Engine/` | 歌词时间轴、字幕、图层抽象 |
| `src/Vts/` `src/Spout/` `src/Logging/` | VTS 协议、Spout 收发、日志 |
| `tests/JizuraLive.Engine.Tests/` | 引擎与渲染测试（xUnit），golden 基准在同级 `golden/` |
| `tools/RenderProbe/` | 呈现链路验证程序（子窗口 + flip 交换链 + 实测帧率） |
| `DOC/` `native/` | 架构文档（见下）；SpoutDX 原生依赖与 angle |

## 文档

- [`DOC/PV-ENGINE-ARCHITECTURE.md`](DOC/PV-ENGINE-ARCHITECTURE.md) — 当前实现与差距、逐帧路径、待解决的运行时问题
- [`DOC/PV-DIRECTOR-SYSTEM.md`](DOC/PV-DIRECTOR-SYSTEM.md) — 目标执行契约与功能分解
- [`DOC/IMPLEMENTATION-ROADMAP.md`](DOC/IMPLEMENTATION-ROADMAP.md) — 交付顺序与验证纪律
