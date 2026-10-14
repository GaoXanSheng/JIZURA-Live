<div align="center">

  <img src="./DOC/icon.png" alt="JIZURA Live PV Logo" width="128" />

  <h1>JIZURA Live PV（字面 Live PV）</h1>

  <p>面向 PV 与 VTube Studio 联动的实时歌词动效合成工作台</p>

  <p>
    <a href="./LICENSE"><img src="https://img.shields.io/badge/License-GPL%20v2-blue.svg" alt="License: GPL v2" /></a>
    <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10.0-512BD4.svg" alt=".NET 10" /></a>
    <img src="https://img.shields.io/badge/Platform-Windows%20x64-0078D6.svg" alt="Platform" />
  </p>

  <br />

  <img src="./DOC/screenshot.png" alt="JIZURA Live PV 界面预览" width="880" />

</div>

---

## 项目简介

JIZURA Live PV 是一款运行在 Windows 平台上的歌词 PV 动效合成工作台：左侧云歌单选曲即出 PV，中间舞台实时合成多路画面，右侧是 PV 合成控制台（多路 Spout 源、VTS 联动、环境光影与节拍特效）。

当前版本为 **3.2（Pure C# / PV 独立工作台）**。该版本移除了早期版本依赖的 CEF（Chromium Embedded Framework）浏览器内核与 Web 前端架构，基于原生 WPF 与 SkiaSharp 重写了完整的用户界面与自绘渲染管线；并在 3.2 中把界面整体收敛为**纯 PV 工作台形态**（不再提供传统编辑器/播放器模式切换），用户数据与配置独立存放于 `%LOCALAPPDATA%\JizuraLivePV\`（统一配置文件 `pv_config.json`），与 JIZURA Live 完全隔离。

---

## 核心特性

- **纯 C# 原生渲染管线**：核心渲染引擎基于 SkiaSharp 构建，支持入场、驻留、退场、背景、装饰、转场与摄像机手法组合。支持 Direct3D 11 / ANGLE GPU 光栅化，缺乏硬件支持时自动平滑回退至 CPU 光栅。
- **云歌单驱动出 PV**：左侧歌单栏选曲（酷狗/网易云），自动下载 + 解码分析 + 取词，PV 跟着云歌单实时渲染；空格即播放/暂停。
- **实时推流输出（Spout2）**：集成 SpoutDX 纹理共享功能，可在播放时将渲染画面直接输出至 OBS Studio、Resolume 等导播与 VJ 软件，作为低延迟动态歌词源。
- **外部画面合成（Spout 输入）**：可接收 VTube Studio、游戏、桌面捕获等程序发布的 Spout 纹理，作为实时层合成进输出画面——层序固定为 **PV 画面 → 人物 → 字幕**：歌词/装饰的画面在下、人物居中、KRC 字幕始终压在最上层（烤进主通道，单路推流也看得清字），整幅再一起吃工程的颗粒/扫描线/暗角纹理后期。合成在本软件内完成，下游（Shoost/OBS）只需接一路流。
- **多路源实时合成**：自动枚举系统里正在输出的 Spout 发送端，最多 4 路源各自设置位置/缩放/旋转/不透明度/镜像/模糊/自发光/色相，按列表顺序合成；**整幅一起吃镜头**（背景定义 + 歌词 + 人物由当前切分的同一台摄影机拍下，cut 切换时整幅画面一起切镜）。右侧「PV」面板里的参数即时生效，不用重启渲染引擎。
- **VTube Studio 联动（插件 API）**：接入 VTS Public API（`ws://127.0.0.1:8001`，授权一次即存令牌）——面板里可直接调模型的**大小/位置/旋转**（源画面被 VTS 窗口裁掉时用它把整个角色缩进画面，带「全身取景」一键），并可列出当前模型的热键：手动触发，或设为**云歌单换歌时自动触发**（角色跟着歌换表情/动作）。
- **音频与字体生态**：支持云端音乐搜索/歌单载入与本地音频；内置字体回退机制，扫描本地与系统字体库，必要时可自动按需匹配开源字体。

---

## 运行与开发环境

### 系统与环境要求
- **操作系统**：Windows 10 64 位（版本 19041 或更高版本）
- **开发框架**：.NET 10.0 SDK（`win-x64`）
- **图形要求**：支持 DirectX 11 的显卡

### 运行时原生依赖
1. **SpoutDX**：`SpoutDX.dll` 与 `SpoutDXWrapper.dll` 已存放在 `native/` 目录下，项目编译时会自动复制到输出根目录。
2. **ANGLE 运行库**（可选 GPU 加速）：存放在 `native/angle/`（包含 `d3dcompiler_47.dll`、`libEGL.dll`、`libGLESv2.dll`），编译时会复制到输出目录下的 `angle/` 文件夹。如未找到，应用会自动使用 CPU 光栅模式运行。

---

## 构建与发布

### 1. 还原与编译
在项目根目录下执行：

```bash
# 调试构建
dotnet build

# 发行构建
dotnet build -c Release
```

### 2. 自包含发布
生成不依赖目标机器 .NET 运行时的独立运行包：

```bash
dotnet publish -c Release -r win-x64 --self-contained
```

发布产物将输出至 `bin/publish/` 目录。

### 3. 打包安装程序
项目提供了 Inno Setup 配置文件 `build.iss`。在完成发布后，使用 Inno Setup 6 编译该脚本即可在 `Output/` 目录下生成单文件安装包：

```bash
iscc build.iss
```

---

## 目录结构

```
JIZURA-Live/
├── assets/                 # 应用图标等静态资源（字体不随包，联网按需拉取）
├── native/                 # 原生动态链接库（SpoutDX、ANGLE）
├── src/
│   ├── App/                # WPF 桌面宿主、PV 工作台窗口与视图、编辑器状态
│   │   ├── Cloud/          # 云端音频与歌词接口、PV 会话（云歌单驱动）
│   │   ├── Controls/       # 自定义控件与界面组件
│   │   ├── I18n/           # 多语言词库（内置简中本地化）
│   │   └── Views/          # 视图面板（歌单栏、正在播放、PV 控制台、设置等）
│   ├── Engine/             # 核心排版与 SkiaSharp 渲染引擎
│   │   ├── Packs/          # 手法与效果资产预设
│   │   ├── JFonts.cs       # 字体扫描、解析与网络回退
│   │   ├── JLayouts.cs     # 文本排版与几何布局算法
│   │   ├── Omakase.cs      # 智能风格搭配与随机生成算法
│   │   ├── Planner.cs      # 时间线与切分规划器
│   │   └── Renderer.cs     # 帧级画布绘制与图层渲染器
│   ├── Logging/            # 日志记录模块（log4net 封装）
│   ├── Pv/                 # PV 场景模型与多图层合成器（视差/环境光影/节拍特效）
│   ├── Server/             # 云音乐接口与辅助服务
│   ├── Spout/              # Spout 收发（SpoutDX 共享纹理跨进程互操作：SpoutOut 输出 / SpoutIn 外部输入）
│   └── Vts/                # VTube Studio 插件 API 客户端（取景控制与热键联动）
├── tests/                  # 单元测试与对拍验证工程
├── tools/                  # 辅助工具与测试工程文件
├── DOC/                    # 文档、图元资产与协议规范
│   ├── icon.png            # 应用高清图标
│   └── screenshot.png      # 软件运行界面截图
├── build.iss               # Inno Setup 打包脚本
├── JizuraLive.csproj       # 项目主工程文件
└── LICENSE                 # 开源许可证文件（GPL-2.0）
```

---

## 用户数据目录

运行时的设置、缓存与登录态统一存放于 `%LOCALAPPDATA%\JizuraLivePV\`（可用环境变量 `JIZURA_PV_DATA_DIR` 重定向）：

```
%LOCALAPPDATA%\JizuraLivePV\
├── config\pv_config.json   # 统一配置：窗口/渲染、Spout、VTS 联动、PV 合成参数
├── config\autosave.json    # 工程自动保存
├── cache\                  # 封面 / 音频缓存
├── cloud\                  # 云音乐下载与歌词缓存
├── fonts\                  # 用户字体库
└── logs\                   # 运行日志
```

该目录与 JIZURA Live 的 `%LOCALAPPDATA%\JizuraLive\` 完全隔离，两个程序可并存互不影响。

---

## 许可证

本项目依据 **GNU General Public License v2 (GPL-2.0)** 许可协议分发与使用。完整协议文本请参阅项目根目录下的 [LICENSE](LICENSE) 文件。
