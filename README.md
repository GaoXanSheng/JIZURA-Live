<div align="center">

  <img src="./DOC/icon.png" alt="JIZURA Live Logo" width="128" />

  <h1>JIZURA Live（字面 Live）</h1>

  <p>Windows 平台下的歌词 PV 动效生成与实时排版工具</p>

  <p>
    <a href="./LICENSE"><img src="https://img.shields.io/badge/License-GPL%20v2-blue.svg" alt="License: GPL v2" /></a>
    <a href="https://dotnet.microsoft.com/"><img src="https://img.shields.io/badge/.NET-10.0-512BD4.svg" alt=".NET 10" /></a>
    <a href="https://vuejs.org/"><img src="https://img.shields.io/badge/Frontend-Vue%203%20%2B%20Vite-4FC08D.svg" alt="Vue 3" /></a>
    <img src="https://img.shields.io/badge/Platform-Windows%20x64-0078D6.svg" alt="Platform" />
  </p>

  <br />

  <img src="./DOC/screenshot.png" alt="JIZURA Live 界面预览" width="880" />

</div>

---

## 项目简介

JIZURA Live 是一款运行在 Windows 平台上的歌词 PV 动效生成与实时排版工具。它支持导入音频与歌词文本，通过内置的排版规划器、动效手法与相机运镜快速生成视觉化动态歌词，并提供视频导出与直播推流支持。

当前 `main` 分支版本为 **2.2.0**，采用 **WPF + CefSharp（Chromium 152）** 桌面宿主与 **Vue 3 / Vite** 前端工程结合的混合架构。构建时前端产物直接作为嵌入式资源（`EmbeddedResource`）编译进主程序集中，通过内置内存 Web 服务器（EmbedIO）对外提供服务，兼具现代前端动效交互表现力与原生桌面底层性能。

---

## 核心特性

- **歌词与节拍同步**：
  - 完整支持标准 LRC 时间戳与行内切分记法（`/` 切分、`*强调*`、`!闪烁`、`[间奏 N]` 等）。
  - 支持音频 BPM 分析以及手动打点同步（Tap Sync）。
  - **SMTC 外部播放器进度同步**：内置 Windows 系统媒体传输控件（SMTC）监听，通过 SSE 实时捕获网易云音乐、QQ音乐、PotPlayer 等外部播放器的播放进度并自动对齐打点。

- **实时推流输出（Spout2）**：
  - 集成 SpoutDX 纹理共享功能，在 CefSharp 离屏渲染管线中截获 Direct3D 11 共享纹理，可直接输出至 OBS Studio、Resolume 等导播与 VJ 软件，作为低延迟动态歌词源。

- **云端音乐与字体生态**：
  - 支持本地音频文件，并内置酷狗音乐、网易云音乐的在线搜索、歌单解析、音频代理与扫码登录支持。
  - 具备字体自动回退机制，扫描本机与本地字体库，必要时可自动按需匹配开源字体。

- **MCP 协议驱动（Model Context Protocol）**：
  - 内置基于 EmbedIO 的无状态 HTTP MCP 服务（默认端口 8642），向 AI 代理（如 ZCode、Claude）暴露 17 个标准化操作工具，通过 CefSharp 脚本桥接直通页面状态，支持纯文本无头操控完成歌词制作、风格微调与导出全流程。

---

## 运行与开发环境

### 系统与环境要求
- **操作系统**：Windows 10 64 位（版本 19041 或更高版本）
- **后端运行/编译框架**：.NET 10.0 SDK（`win-x64`）
- **前端工具链**：Node.js 18+ 及 npm（编译前端静态资源需要）
- **图形要求**：支持 DirectX 11 的显卡

### 运行时原生依赖
- **SpoutDX**：`SpoutDX.dll` 与 `SpoutDXWrapper.dll` 已存放在 `native/` 目录下，项目编译时会自动复制到输出根目录。
- **CefSharp**：基于 NuGet 自动还原与管理，构建时会自动部署 Chromium 运行组件；项目包含子进程图标修补脚本。
- **注意**：本项目视频编码已内置原生 WebCodecs 支持，**不需要安装 FFmpeg**。

---

## 构建与发布

### 1. 还原与完整编译
MSBuild 在编译时会自动检查前端源码变动，并在需要时调用 `npm install` 与 `npm run build` 打包前端资源：

```bash
# 调试构建（自动构建前端并嵌入程序集）
dotnet build

# 发行构建
dotnet build -c Release
```

### 2. 独立前端开发
若只进行前端界面的调整，可直接在 `frontend/` 目录下启动 Vite 开发服务器：

```bash
cd frontend
npm install
npm run dev
```

前端开发服务器将代理 API 请求至本地正在运行的 C# 后端服务。

### 3. 跳过前端构建的快速编译
如果前端源码未改动或已预先生成好嵌入产物，可传递参数跳过 npm 执行以加快编译速度：

```bash
dotnet build /p:SkipFrontendBuild=true
```

### 4. 自包含发布
生成不依赖目标机器 .NET 运行时的独立运行包：

```bash
dotnet publish -c Release -r win-x64 --self-contained
```

发布产物将输出至 `bin/publish/` 目录。

### 5. 打包安装程序
项目提供了 Inno Setup 配置文件 `build.iss`。在完成发布后，使用 Inno Setup 6 编译该脚本即可在 `Output/` 目录下生成单文件安装包：

```bash
iscc build.iss
```

---

## 目录结构

```
JIZURA-Live/
├── assets/                 # 应用图标、静态字体资源
├── frontend/               # Vue 3 + Vite + TypeScript 前端项目
│   ├── src/
│   │   ├── cloud/          # 酷狗/网易云与字幕对接
│   │   ├── components/     # UI 视图组件
│   │   ├── engine/         # 核心动效、渲染与 WebCodecs 导出底层（11_export.js）
│   │   ├── jizura/         # 状态管理、MCP 协议实现（agent-api.ts）、导出调度
│   │   └── worker/         # 导出专用的 Web Worker（export-worker.ts）
├── native/                 # 原生动态链接库（SpoutDX 纹理共享组件）
├── scripts/                # 自动化辅助脚本
├── src/
│   ├── App/                # WPF 宿主程序、主窗口（CefSharp 容器）、设置管理
│   ├── Cef/                # CefSharp 运行时初始化与配置
│   ├── Logging/            # 日志记录模块（log4net 封装）
│   ├── Server/             # 本地服务模块
│   │   ├── Controllers/    # REST 控制器（包含免弹窗文件落盘 ExportSaveController）
│   │   ├── KuGou/          # 酷狗音乐客户端与加密解密实现
│   │   ├── Netease/        # 网易云音乐客户端与加密解密实现
│   │   ├── Smtc/           # Windows SMTC 系统媒体进度监听服务
│   │   ├── EmbeddedServer.cs # EmbedIO 本地自宿主 HTTP 服务
│   │   ├── McpModule.cs    # MCP 协议端点（/mcp）
│   │   └── StaticSiteModule.cs # 嵌入式资源静态站托管模块
│   └── Spout/              # SpoutDX 纹理共享与跨进程管道
├── tests/                  # 单元测试与对拍验证工程
├── tools/                  # 辅助工具与测试工程文件
├── DOC/                    # 文档、图元资产与协议规范
│   ├── icon.png            # 应用高清图标
│   ├── screenshot.png      # 软件运行界面截图
│   ├── MCP.md              # MCP 接入协议与架构文档
│   └── SKILL.md            # AI 代理操作技能指南
├── build.iss               # Inno Setup 打包脚本
├── JizuraLive.csproj       # 项目主工程文件
└── LICENSE                 # 开源许可证文件（GPL-2.0）
```

---

## MCP 接入说明

JIZURA Live 启动后会在后台开启 HTTP MCP 服务（默认监听 `127.0.0.1:8642`，端口被占用时自动向后顺延）。

### 客户端配置示例
在支持 MCP 的客户端（例如 ZCode 的 `.zcode/config.json` 或 Claude Desktop 配置文件）中添加：

```json
{
  "mcp": {
    "servers": {
      "jizura": {
        "type": "http",
        "url": "http://127.0.0.1:8642/mcp"
      }
    }
  }
}
```

### 接口快速测试
在应用运行状态下，可通过终端发送标准 JSON-RPC 2.0 请求进行握手验证：

```bash
# 握手测试
curl -s -X POST http://127.0.0.1:8642/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}'

# 获取当前工具列表（17 个工具）
curl -s -X POST http://127.0.0.1:8642/mcp \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'
```

关于完整的 MCP 工具定义、工作流协议与 revision 状态机制，请参阅 [DOC/MCP.md](DOC/MCP.md) 与 [DOC/SKILL.md](DOC/SKILL.md)。

---

## 许可证

本项目依据 **GNU General Public License v2 (GPL-2.0)** 许可协议分发与使用。完整协议文本请参阅项目根目录下的 [LICENSE](LICENSE) 文件。
