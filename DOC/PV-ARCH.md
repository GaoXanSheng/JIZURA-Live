# PV 舞台：架构规划（调研 + 分层重构）

> 状态：**提案**（本文只写规划与迁移步骤，代码按阶段 PR 推进）
> 背景：PV 模式上线后引入的耦合被指「软件越来越混合起来了，PV 是单独的东西」——本文给出调研结论与分层架构。

---

## 0. 调研说明（来源与局限）

- **本机没有外网**（WebFetch 连接超时），所以下文「常见 PV 是什么」不是抓来的网页，而是：
  1. 领域常识（Niconico / Vocaloid / 歌ってみた / Vtuber 直播生态里 PV 的通行做法）；
  2. 本仓库与产业里的既有名词证据：README 的「歌词 PV 动效生成与实时排版工具」「输出一路流给 **Shoost**/OBS」「实时推流输出（Spout2）」、`tools/` 下的 Spout 接收端样例、机器上实际装着 **VTube Studio**（`D:\SteamLibrary\...\VTube Studio`，插件 API 1.35.10）、代码里对位旧 TS 前端的注释。
- 结论如果与你的实际工作流不符，以你的工作流为准——文档第 3 节的分层方案与第 5 节的迁移步骤都不依赖第 1 节的具体名词。

## 1. 常见 PV 是什么

**PV（promotional video）在这个圈子里 = 一首歌的「配歌词的动画影像」**，不是直播画面本身。它的构成通常就这几样：

| 组成 | 说明 | 典型来源 |
|---|---|---|
| 歌词排版动效 | 逐字/逐行的出现、强调、跳切、字形变形（PV 的主体） | AviUtl / After Effects / 专用歌词 PV 生成器（如 Shoost） |
| 背景与装饰 | 底纹、图形、装饰元素、K 线/波形等 | 同上 |
| 运镜与转场 | 推拉、手持、地震、节拍打击；cut 之间的转场 | 同上（本项目就是把这些注册成 cam/trans 手法） |
| 特效后期 | 色差、闪白、扫描线、颗粒、暗角 | 同上（本项目的 post/FX） |

**直播（歌枠）时**，PV 和角色是**两个独立图层**，由合成器（OBS）叠起来：

```
[ 歌词 PV 层 ]        ← PV 生成器输出（本项目 / Shoost），整首歌一条时间线
[ 角色层（人物） ]     ← VTube Studio → Spout
[ 字幕层（KRC 逐字） ] ← 字幕生成器/文本源（本项目有独立 -LYRICS 通道）
[ 其他（游戏/桌面） ]  ← 采集
        ↓ 合成
     OBS → 直播
```

要点（也是这次返工的原因）：

1. **PV 是「一首歌的一条时间线」**，有自己的产物（画面）与自己的时间轴；它**不知道**角色/字幕/采集源的存在。
2. **角色、字幕、采集是「实时源」**，有自己的时钟（VTS 的 60fps 实时、KRC 的逐字时间）。它们**不参与** PV 的排版规划，也不需要被 PV 的引擎「理解」。
3. 两者的汇合点是**合成器的图层栈**（OBS 的 source 列表），不是 PV 引擎内部。图层栈里每一层可以独立变换/滤镜/混合，**镜头（运镜）要么作用于整栈（导演视角），要么逐层单独设置**——这两种在 OBS 里就是「场景变换」与「源变换」的区别。
4. 「让 PV 引擎去画角色」在工作流上是反的：角色被卷进 PV 的渲染顺序里，PV 的样式一改（换风格、换布局、留白区变化）角色就受影响；反过来也要求引擎知道 Spout、字幕、VTS 这些它不该知道的事——**这就是「混合起来」的根源**。

## 2. 现状审计（哪里混在一起了）

按耦合强度排序，全部有代码证据：

| # | 耦合 | 证据 | 后果 |
|---|---|---|---|
| 1 | **角色画在引擎内部**：实时层在 `Renderer.FrameCore` 里按「背景之后、歌词之前/之上」的位置画 | `src/Engine/Renderer.cs`（`DrawLive`、`camCtx`/`frameCam` 分支） | 引擎要懂 Spout 源；层序成了引擎里的硬编码；整帧镜头要改引擎主循环 |
| 2 | **字幕烤进主通道**是 SpoutOut 里的补丁 | `src/Spout/SpoutOut.cs`（`BakeSubtitle`/`EnsureSubLayer`） | 字幕逻辑有两条路（独立通道 + 烤入），预览侧还要判断是否跳过叠加层 |
| 3 | **PV 设置塞进编辑器设置** | `src/App/Settings.cs` 的 `Pv` 键（同一个 settings.json） | 编辑器与 PV 场景互相干扰（迁移、保存、UI 都在一处） |
| 4 | **PV 面板是编辑器右栏的一个页签** | `src/App/Views/RightPanel.xaml`（第 4 个 `TabPv`）、`PvTab.xaml(.cs)` | PV 的控制台混在「风格/特效/手法/导出」里，编辑工程时也在刷新源列表/连接状态 |
| 5 | **PV 模式复用播放器骨架** | `MainWindow.SetMode`（三档共用一个窗口布局与 `CloudPlayerSession`） | PV 与「跟着云歌单放歌」绑死；本地文件/本地歌单无法驱动 PV；模式切换会牵动编辑布局 |
| 6 | **云模块反向引用 VTS** | `CloudPlayerSession.AdoptForPlayAsync` 里 `App.MainInstance?.Vts.TriggerHotkey(...)` | 云歌单（音乐来源）不该知道 VTS（角色软件） |
| 7 | 单一引擎闸/单一进程/单一窗口 | `EngineRenderGate` | 预览、Spout、导出、PV 全串行；PV 想独立帧率/分辨率就得动全局 |

## 3. 目标架构：PV 舞台（PvStage）

一句话：**把 PV 从「一个模式」升级成「一个子系统」——自己的窗口、自己的场景文档、自己的图层栈与合成器；引擎降级为这个栈里的一层（歌词层）。**

```
┌──────────────────────────── PV 舞台窗口（独立 Window） ────────────────────────────┐
│  PvScene（pv.json：图层栈 + 输出设置 + 驱动器绑定）                                 │
│                                                                                   │
│   ┌ 图层栈（自上而下合成） ─────────────────────────────────────────────────────┐  │
│   │ L4 字幕层      SubtitleProvider ← KRC/工程行（SubtitleLayer 原样复用）      │  │
│   │ L3 角色层      SpoutProvider("VTubeStudioSpout") + VTS 能力（取景/热键）    │  │
│   │ L2 歌词 PV 层  LyricPvProvider ← 引擎（Renderer 渲到离屏面）                │  │
│   │ L1 底衬        ColorProvider（底色/渐变，可选）                              │  │
│   └───────────────────────────────────────────────────────────────────────────┘  │
│   ↓ PvCompositor（每帧：按栈取图 → 逐层变换/滤镜/混合 → 相机 → 输出）              │
│   ↓ PvOutput → Spout 发送端（主通道 + 可选分层通道）                              │
└───────────────────────────────────────────────────────────────────────────────────┘
        ▲ IPvDriver（时钟与「现在该渲染哪首歌」的来源）
        │   CloudPlaylistDriver（现有云歌单） / LocalPlaylistDriver / ManualDriver / EditorDriver
        └── 编辑器的「送到 PV 舞台」动作：把当前工程 + 计划交给 EditorDriver，PV 舞台立即实拍
```

### 3.1 关键接口（草案）

```csharp
/// 图层提供者：PV 舞台只认「给我这一帧要画的图 + 它自己的时钟」
interface IPvLayerProvider : IDisposable
{
    string Key { get; }                    // 稳定标识（配置里引用）
    long Version { get; }                  // 内容版本（换歌/换源/改参数 → 变）
    // 泵线程调用：把该层画/取到目标画布（设计坐标系 = 输出画布），返回是否出了新内容
    bool Pump(PvFrameContext ctx);
}

/// 驱动器：PV 舞台与「音乐从哪来」解耦
interface IPvDriver
{
    event Action Changed;                  // 换歌/时钟来源变化
    IReadOnlyList<PvTrack> Queue { get; }  // 可播放列表（PV 舞台只管展示与切换）
    PvTrack? Current { get; }
    void Play/Pause/Next/Seek(...);
    double Time { get; }                   // 当前这首歌的时间（秒）
}

/// 源能力（VTS 取景/热键这类「不只是收图」的源，挂在 SpoutProvider 上）
interface IPvSourceCapability
{
    string Title { get; }
    void BuildUi(Panel host);              // 由能力自己出 UI（PV 面板不再写 VTS 专属代码）
    void OnTrackChanged(PvTrack track);    // 换歌钩子（热键触发在这里，云模块不再引用 VTS）
}
```

### 3.2 各模块归属（新目录结构）

```
src/Pv/                         # 新：PV 舞台（独立子系统）
  PvScene.cs                    #   场景文档（图层栈/输出/驱动器绑定）→ config/pv.json
  PvCompositor.cs               #   图层合成器（变换/滤镜/混合/相机/输出）
  PvStageWindow.xaml(.cs)       #   独立窗口：预览 + 图层列表 + 输出状态
  Layers/
    LyricPvProvider.cs          #   引擎歌词层（Renderer → 离屏面；引擎不再认识外部源）
    SpoutSourceProvider.cs      #   Spout 源层（SpoutIn 原样复用，一路一个 provider）
    SubtitleProvider.cs         #   字幕层（SubtitleLayer 原样复用，含分层输出）
    ColorProvider.cs            #   底衬层
  Drivers/
    CloudPlaylistDriver.cs      #   适配现有 CloudPlayService（唯一的云耦合点）
    LocalPlaylistDriver.cs      #   本地文件/文件夹
    EditorDriver.cs             #   编辑器「送到 PV 舞台」
src/Vts/                        # 已有：VTS 插件 API → 作为 SpoutSourceProvider 的能力插件
src/Engine/                     # 回归纯渲染：删掉 Live/FrameCamera/字幕烤入相关分支
src/Spout/                      # 回归纯收发：删掉 BakeSubtitle/EnsureSubLayer 主通道分支
src/App/                        # 编辑器/播放器：删掉 PvTab 与 Settings.Pv（迁去 pv.json）
```

### 3.3 与现状的差异（一句一条）

- **引擎不再知道角色/字幕**：`RenderOpts.Live`/`FrameCamera`/`DrawLive` 全部删除；引擎只对外提供「渲好的一帧」。
- **合成器拥有图层栈**：层序、每层变换/滤镜/混合、相机归属（整栈 or 逐层）都是**数据**（pv.json），不是代码里的 if。
- **字幕回到「一层」**：主通道烤入的补丁删除；要单路推流就是「字幕层在栈顶」，要分层输出就额外发一路透明通道。
- **PV 有自己的窗口与设置**：编辑器的 settings.json 不再有 Pv 键；PV 场景独立存档、独立版本、独立迁移。
- **驱动器可选**：云歌单只是驱动之一；本地文件、编辑器实拍、手动都走同一接口，PV 舞台不依赖云模块。
- **VTS 是源的能力插件**：`src/Vts` 通过 `IPvSourceCapability` 暴露取景/热键，UI 由能力自建；云模块不再引用它。

## 4. 为什么这么做（收益 / 代价）

**收益**
1. 「混合」消失：引擎、源、字幕、音乐来源四者只通过两层接口（Provider/Driver）相遇，谁都改不动别人。
2. PV 舞台可独立演进：换风格/加层/加滤镜不动引擎；引擎做性能优化不动 PV。
3. 可测试：Compositor 的层序/变换/相机是纯数据驱动的纯函数，能被单测钉住（现在的层序是渲染顺序的副作用）。
4. 未来可拆进程：PV 舞台 = Spout 输出 + 一条控制通道，独立进程只差一个启动器（崩溃隔离、多场景并行）。
5. 编辑器的「送到 PV 舞台」让「做完 PV 直接开播」变成一步，而不是现在的模式切换 + 隐式状态。

**代价 / 风险**
1. 一次性重构量不小（引擎里的 live/frameCam 分支、SpoutOut 的字幕烤入、PvTab、Settings.Pv 都要动），需要分期且每期跑全量测试与真机对比。
2. 多一层离屏面：歌词层不再直接画进输出面时，1080p 多一次 blit（GPU 光栅下 ~1ms 量级；CPU 回退路径要重新量）。
3. 独立窗口意味着窗口/焦点/全屏/多显示器这套要重新处理（现有 `WndProc`/`WM_GETMINMAXINFO` 逻辑可复用）。
4. 短期会出现「两套入口」（旧 PV 模式 + 新 PV 舞台），迁移期要明确哪个是准。

## 5. 迁移分期（每期独立可交付）

**P1 — 抽出层与驱动器接口（不动现状行为）**
- 新增 `src/Pv/` 骨架：`PvScene`/`PvCompositor`/`IPvLayerProvider`/`IPvDriver` + `LyricPvProvider`（内部仍调现有 `Renderer.Frame`，把 live/frameCam 参数照旧透传）。
- `PvCompositor` 的层序/变换/相机先按「现状等价」实现，加单测钉住（层序、整帧镜头、字幕在顶）。
- 交付判据：把现有 PV 模式的出图路径改为「经 Compositor 走一遍」，**输出逐帧与现状一致**（对比 PNG 有测试）。

**P2 — 引擎与 Spout 去耦合**
- 画布侧：`LyricPvProvider` 改为渲到自己的离屏面；Compositor 负责把它和 Spout 源层合成。
- 删除 `RenderOpts.Live/LiveFollowCamera/FrameCamera`、`Renderer.DrawLive`、`SpoutOut.BakeSubtitle` 的主通道分支；`-LYRICS` 通道保留为「分层输出」能力。
- 交付判据：全量测试 + PV 合成图与 P1 一致（同层序、同镜头）。

**P3 — 独立 PV 舞台窗口与 pv.json**
- 新窗口（复用现有舞台呈现/镜像/预览字幕叠加的控件与手法），图层列表 UI（增删层、拖序、逐层参数），输出/源状态条。
- `PvScene` 落 `config/pv.json`；`Settings.Pv` 迁移进它并把编辑器的 settings.json 清干净。
- 删除 `PvTab`（PV 的 UI 只在新窗口里）；标题栏第三档保留，但语义从「换一套编辑器布局」变成「打开 PV 舞台窗口」——模式不再是编辑器的一个状态。
- 编辑器加「送到 PV 舞台」按钮（走 `EditorDriver`）。
- 交付判据：编辑器操作不触发任何 PV 刷新；PV 舞台开关不影响编辑器预览。

**P4 — 驱动器与能力插件**
- `CloudPlaylistDriver`（包住 `CloudPlayService`）+ `LocalPlaylistDriver` + `ManualDriver`；`CloudPlayerSession` 回归播放器专用，删掉对 VTS 的引用。
- `src/Vts` 改为 `IPvSourceCapability` 实现（取景/热键 UI 自建），加 `OnTrackChanged` 钩子；PV 面板不再写 VTS 专属代码。
- 交付判据：同一 PV 场景分别用云歌单/本地文件驱动都能出图；换歌热键触发在驱动层可测。

## 6. 验收清单（每期都跑）

1. `dotnet test`（全量，含 i18n 守门）与 `dotnet build` 全绿。
2. 真机：VTS 源 + 云歌单，抓主通道帧对比层序（PV 底 → 人物 → 字幕）与整帧镜头位移。
3. 关掉 VTS（无源）时：PV 正常出图、无保活空转、状态条显示「等待源」。
4. 编辑器侧回归：预览/导出/缩略图不受 PV 场景影响（导出仍不含外部源）。
5. 性能：GPU 光栅 1080p@60 渲染耗时与重构前同量级（±1ms 内），CPU 回退路径记录基线。
