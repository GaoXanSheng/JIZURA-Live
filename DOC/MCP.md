# MCP 接入文档

字面 Live 通过 MCP（Model Context Protocol）把编辑器暴露给本地 AI 代理（ZCode、Claude 等流式 HTTP 客户端）。代理可以用 17 个工具读状态、写歌词、调风格手法、控制预览、发起导出——无需接触 UI。

## 架构

```
MCP 客户端（ZCode 等）
    │  POST /mcp   JSON-RPC 2.0（无状态 Streamable HTTP）
    ▼
McpHttpServer（BCL HttpListener，仅绑定 127.0.0.1:8642~8651 与 localhost 别名）
    │  跨站防护 → JSON 校验 → UI 线程分发
    ▼
McpTools（17 个工具直调编辑器状态；协议语义与工具实现的唯一事实源）
    │  EditorState.Mutate / ReplanNow（plan 同步重算）· PreviewEngine · Planner · ExportJob
    ▼
编辑器状态（Project / Plan / 引擎 registry 与注册表）
```

分层原则：**协议与工具语义在 C#（`src/App/Mcp/`）**，编辑器状态只在 `EditorState`（工程/历史/revision）与 `PreviewEngine`（播放钟/渲染）里。改工具行为去改 `src/App/Mcp/McpTools.cs`。
（M6 之前协议语义在页面里由 `frontend/src/jizura/agent-api.ts` 承担、C# 经 CEF `EvaluateScriptAsync` 桥接；本分支已整体改为直调，`agent-api.ts` 仅作语义参照冻结保留。）

## 请求生命周期

1. 客户端 POST JSON-RPC 2.0 单对象（不支持批量；数组/标量回 `-32600`）到 `/mcp`。
2. McpHttpServer 校验：
   - 精确路径 `/mcp`（其他路径回 404 + JSON-RPC 错误）；
   - 跨站防护（`Sec-Fetch-Site: cross-site` 或非本站/非回环 `Origin` → 403，对位旧 `ApiGuardModule.CrossSiteDetail`）；
   - `JToken.Parse` 失败 → HTTP 200 + JSON-RPC `-32700`；
   - GET / DELETE（SSE 通道）→ 405 + `Allow: POST`。
3. 请求交给 `McpTools.Rpc`（UI 线程经 `Dispatcher.Invoke` 分发；无 Application 的测试环境同步执行）：
   - 工具均为直调：读 `EditorState`/`Plan`/`PreviewEngine`，写走 `EditorState.Mutate` + `ReplanNow()`（同步重规划，保证返回值里的 revision 与 plan 一致）；
   - 通知（`notifications/*`）→ HTTP 202 空体；
   - 响应对象 → HTTP 200，**无 BOM** UTF-8（Node 系客户端要求）。
4. 会话无关：无状态，不签发 `Mcp-Session-Id`。

工具级错误（未知方法 `-32601`、参数/revision `-32602`、其他 `-32603`）由 `McpTools.Rpc` 的 catch 统一映射。

## revision 契约

- 写工具（`set_lyrics` 之后的全部写操作）必须带 `get_state` 返回的最新 `revision`；schema 里 `revision` 必填的工具缺参即拒，带值与 `EditorState.Revision` 不符即拒，错误码 `-32602`（消息含 `stale_revision` 或「revision 过期」）。
- 写操作返回值里附带递增后的 `revision`，代理可直接用它做下一次写。
- 导出进行中（`start_export` 起的任务未结束）只挡写操作：`get_state` / `list_options` / `get_export_status` 等只读工具照常可用，否则任务没法轮询。

## 客户端接入

ZCode 工作区配置（`.zcode/config.json`，已被 .gitignore 忽略，按机器各自配置）：

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

- 工具在客户端表现为 `mcp__jizura__get_state` 等（服务器名 `jizura` + 工具名，**无 `jizura_` 前缀**）。
- 端口：应用从 8642 起向后探测 10 个端口；配置写死 8642，仅当被占时漂移，此时需手改配置。
- 会话启动时自动连接；应用没开时连接失败属正常，启动应用后在 设置 → MCP 重连。

## 17 个工具

`get_state` · `list_options` · `set_lyrics` · `update_settings` · `set_timing` · `edit_line` · `edit_cut` · `set_techniques` · `set_locks` · `randomize` · `history` · `preview` · `tap_sync` · `project` · `start_export` · `get_export_status` · `reset_project`

工具描述与 inputSchema 由 `McpTools.ToolsJson` 提供（`tools/list` 原样返回，结构/文案对位 agent-api.ts）。语义细节见 `src/App/Mcp/McpTools.cs` 各 `Tool*` 方法。

C# 版与旧页面版的已知差异（都写在对应工具的 description 里）：

| 工具/字段 | 差异 |
|---|---|
| `update_settings.quality` | 未接入（Project 模型无该字段，导出画质由 FFmpeg 按分辨率/帧率自动定）：传入返回 `-32602` invalid_input |
| `update_settings.exportRange` | 已接入。入参 `from/to` 是 1 基行号（对位 Vue，换算成切分范围后存进 `project.exportRange`）；`get_state` 回吐的是 0 基切分索引 |
| `preview.loop` | 支持 `all` / `line` / `cut` / `range` / `off`（`line` 循环当前整行，界面「切分循环」；`cut` 循环当前单个切分，界面「行循环」；`range` 循环导出范围，C# 扩展档；`off` 不循环）；`volume` / `muted` 生效（预览出声增益，不写设置、导出不变） |
| `preview` 返回值 `loop` | `all` / `line` / `cut` / `range` / `off` |
| `start_export.kind=subwebm` | 已支持纯字幕层透明 WebM 视频导出（需已装载字幕数据且未关闭字幕显示） |
| `start_export` mp4 | 已支持烤入字幕（工程存在字幕数据且未关闭字幕显示时自动烤入） |
| `start_export.dir` 缺省 | 落 `%USERPROFILE%\Videos\JIZURA`（桌面版没有浏览器下载目录） |
| `randomize` / `tap_sync` | `randomize all` 走 `src/Engine/Omakase.cs`（08b_omakase 的 C# 移植，含主题/锁定）；`tap_sync` 与界面共用 `TapSession` 打点会话 |
| `list_options.description` | C# 注册条目没有 `desc` 字段（未移植）：描述统一为空串 |

## 本地验证（curl）

```bash
# 握手：应返回 serverInfo {name: "jizura-live", version: ...}
curl -s -X POST http://127.0.0.1:8642/mcp -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}'

# 工具清单：应有 17 个工具
curl -s -X POST http://127.0.0.1:8642/mcp -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/list"}'

# 通知：应返回 202
curl -s -o /dev/null -w "%{http_code}" -X POST http://127.0.0.1:8642/mcp \
  -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","method":"notifications/initialized"}'

# 工具调用：直读编辑器状态
curl -s -X POST http://127.0.0.1:8642/mcp -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_state","arguments":{"detail":false}}}'
```

## 代码地图

| 位置 | 职责 |
|---|---|
| `src/App/Mcp/McpHttpServer.cs` | 端点：HttpListener（127.0.0.1 + localhost，8642 起探 10 个端口）、路径/方法/跨站/JSON 校验、202/405/403 语义、响应编码（Utf8NoBom）、Start/Dispose |
| `src/App/Mcp/McpTools.cs` | 协议语义（initialize / tools/list / tools/call）、17 工具直调、revision 契约、导出任务状态 |
| `src/Engine/Omakase.cs` | 08b_omakase.js 移植：氛围/主题表、一键随机、锁定保留；`Reg.RandomOk` 委托在此接线（追加分/和風/套装的随机放行） |
| `src/App/EditorState.cs` | 工程/plan/双历史/revision 单一事实源；M6 补了 `ReplanNow` 与外观历史的读数/回退接口供 MCP 直调 |
| `src/App/PreviewEngine.cs` | 播放钟与渲染（`Play/Pause/Seek/Loop`） |
| `src/Export/ExportJob.cs` | 导出编排（`start_export` 直接跑它，后台 Task + 进度状态） |
| `src/App/Views/TapPanel.xaml.cs`（`TapSession`） | 打点同步会话（`tap_sync` 与界面共用） |
| `frontend/src/jizura/agent-api.ts` | 旧页面版工具语义参照（CEF 已移除，仅作移植对拍） |

## 故障排查

| 现象 | 原因 / 处理 |
|---|---|
| 连接拒绝 | 应用未启动；启动后到客户端重连 |
| 404 + 「未知路径」 | 请求路径不是 `/mcp`（本版没有 `/api/*`，已随 EmbedIO 移除） |
| 400 Bad Request（无 JSON 体） | 用 `localhost` 之外的 Host 名访问（端点只注册 127.0.0.1 / localhost 前缀） |
| `未知工具：<名>` | 工具名没有 `jizura_` 前缀，以 tools/list 返回为准 |
| `stale_revision` / 「revision 过期」 | 写操作的 revision 过期，重新 `get_state` 取最新值 |
| 响应不是 JSON（HTML） | 旧版本构建；重新 dotnet build 后重启应用 |
| 端口漂移 | 8642 被其他程序占用，应用换到 86xx，更新客户端配置 |
| 导出中写操作被拒 | `start_export` 的任务仍在 running：先轮询 `get_export_status` |
