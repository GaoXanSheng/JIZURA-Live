# 字面 Live — 前端（Vue 3 + Vite + TypeScript）

歌词动效引擎的桌面端前端。引擎核心（约 7 万行 canvas 渲染/规划/动效代码，
`src/engine/`）原样保留、零改动；UI 层全部由 Vue 组件构成。

## 常用命令

```bash
npm install        # 首次安装依赖（Node ≥ 18）
npm run dev        # 开发服务器 http://localhost:5173
                   #   /kg /ne API 自动代理到正在运行的桌面应用(127.0.0.1:8642)
npm run build      # 构建产物输出到 ../wwwroot/dist（桌面端 EmbedIO 直接 serve）
npm run check      # vue-tsc 类型检查
```

日常调试：先启动桌面应用（提供本机 API），再 `npm run dev` 用浏览器打开
5173 端口；发布验收：`npm run build` 后直接运行桌面应用。

## 目录结构

```
src/
├── engine/          # 歌词引擎（JIZURA 原版 JS，IIFE 挂 window.J，按序副作用导入，勿改写）
│   └── index.ts     # 导入顺序 = 原 build.py 拼接顺序，顺序即依赖顺序
├── jizura/          # 控制层（原 12_ui.js 的非 DOM 逻辑移植）
│   ├── state.ts     #   S 状态 / WebAudio 播放器 / 循环范围 / 模式切换
│   ├── project.ts   #   项目持久化（localStorage jizura.project.v1，老项目兼容）
│   ├── engine.ts    #   replan / 渲染循环 / 播放控制 / 音频装载
│   ├── look.ts      #   外观历史(◀▶) / 歌词编辑历史(Ctrl+Z) / 锁 / 一键随机 / TAP
│   ├── preview.ts   #   手法卡实时预览渲染循环
│   ├── export.ts    #   MP4/PNG 导出管线
│   └── boot.ts      #   启动序列 + J.ui/J.uiApi 宿主契约（Spout 镜像依赖）
├── cloud/           # 云端音乐（原 11b_kggkrc.js 移植）
│   ├── api.ts       #   /kg/* /ne/* 客户端
│   ├── krc.ts       #   KRC 解密 + krcToLrc + 网易云 LRC 对齐（纯函数）
│   ├── lyricStore.ts#   歌词源状态 / 音译翻译映射 / 多版本切换
│   ├── songLoader.ts#   搜索结果 → 音频+歌词 载入管线
│   └── subtitle.ts  #   渲染叠加字幕（设置存 localStorage jizura.kgsub）
├── stores/          # Pinia：editor(响应式镜像) / cloud(云端音乐)
├── components/      # UI 组件（layout/lyrics/stage/panels/cloud/common）
└── styles/          # tokens.css(设计 token) / base.css(控件) / app.css(布局)
```

## 架构约定

- **引擎零改动**：`engine/` 下的 JS 是上游 JIZURA 的核心资产，只通过
  `window.J` 通信；升级上游时直接替换文件、调整 index.ts 导入顺序即可。
- **非响应式运行时**：`S.t` 每帧变化，刻意不进 Vue 响应式系统；组件通过
  `jizura/bus.ts` 的事件（frame/replan/cutchange/toast…）按需订阅。
- **DOM 契约**：预览 canvas 必须保留 `id="view"`（Spout 镜像脚本
  `SpoutPipeline.cs` 注入 JS 直接抓取该元素并重排）。
- **存储键兼容**：`jizura.project.v1` / `jizura.kgsub` / `jizura.kgsubmap` /
  IndexedDB `jizura`（歌曲缓存）与旧版完全一致。
- 服务端只改过一处：`StaticSiteModule` 把 `/` 指向 `dist/index.html`；
  `/legacy` 仍提供旧版单文件页作对照。

## 旧版（GitHub Pages 七语言构建）已退役

原 build.py + app/i18n 七语言单文件构建已删除；本分支为桌面端定制，
界面只保留简体中文，引擎内注册名（风格/手法等）保持日文原名。
