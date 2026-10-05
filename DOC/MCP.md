# MCP 接入文档

字面 Live 通过 MCP（Model Context Protocol）把编辑器暴露给本地 AI 代理（ZCode、Claude 等流式 HTTP 客户端）。代理可以用 17 个工具读状态、写歌词、调风格手法、控制预览、发起导出——无需接触 UI。

## 架构

```
MCP 客户端（ZCode 等）
    │  POST /mcp   JSON-RPC 2.0（无状态 Streamable HTTP）
    ▼
EmbeddedServer（EmbedIO，仅绑定 127.0.0.1:8642~8651）
    │  McpModule：跨站防护 → JSON 校验 → UI 线程分发
    ▼
MainWindow.EvaluateMcpAsync（CefSharp EvaluateScriptAsync，60s 超时）
    │  JSON.stringify(window.J.mcp.rpc(JSON.parse(<body>)))
    ▼
页面内 window.J.mcp.rpc（frontend/src/jizura/agent-api.ts）
    │  协议语义（initialize / tools/list / tools/call）+ 17 个工具的唯一事实源
    ▼
编辑器状态（S.project / S.plan / 引擎 registry）
```

分层原则：**协议语义与工具实现只在页面**（agent-api.ts），**C# 只负责端点、防跨站与 UI 线程分发**。改工具行为去改 agent-api.ts，不要在 C# 里加业务逻辑。

## 请求生命周期

1. 客户端 POST JSON-RPC 2.0 单对象（不支持批量；数组/标量回 `-32600`）到 `/mcp`。
2. McpModule 校验：
   - 精确路径 `/mcp`（base 前缀可能放过 `/mcpx` 之类）；
   - 跨站防护（`Sec-Fetch-Site: cross-site` 或非本站 `Origin` → 403），逻辑与 `/api` 共用（`ApiGuardModule.CrossSiteDetail`）；
   - `JToken.Parse` 失败 → HTTP 200 + JSON-RPC `-32700`。
3. 请求体经 Newtonsoft 序列化成字符串字面量嵌入脚本再 `JSON.parse`（不把原始文本当 JS 字面量，杜绝注入歧义），派发到 UI 线程执行。
4. 页面返回值：
   - 对象 → HTTP 200，响应体即 JSON-RPC 响应（**无 BOM**，Node 系客户端要求）；
   - `null` / `"null"`（notifications/*）→ HTTP 202 + 空体；
   - 页面抛异常 / 未加载完成 → HTTP 200 + JSON-RPC `-32603`（客户端可稍后重试）。
5. GET / DELETE（SSE 通道）→ 405。会话无关：无状态，不签发 `Mcp-Session-Id`。

工具级错误（未知方法 `-32601`、参数/revision `-32602`、其他 `-32603`）由页面内 `mcpRpc` 的 catch 统一映射。

## 挂载顺序（EmbeddedServer.cs）

```
ApiGuardModule（/api 前缀防跨站，非终结）
→ McpModule（/mcp，终结处理器 ★）
→ StaticSiteModule（静态站 + SPA 回退，非终结）
→ WebApiModule（/api/* 控制器，终结）
```

★ McpModule 必须是终结处理器：EmbedIO 对「已处理但响应未开始」的非终结模块会继续放行，202（无体）会被 StaticSite 的 SPA 回退覆盖成 index.html。给 202 显式发空体是双保险。

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

- 工具在客户端表现为 `mcp__jizura__get_state` 等（服务器名 `jizura` + 页面内工具名，**无 `jizura_` 前缀**）。
- 端口：应用从 8642 起向后探测 10 个端口；配置写死 8642，仅当被占时漂移，此时需手改配置。
- 会话启动时自动连接；应用没开时连接失败属正常，启动应用后在 设置 → MCP 重连。

## 本地验证（curl）

```bash
# 握手：应返回 serverInfo {name: "jizura-live", version: ...}
curl -s -X POST http://127.0.0.1:8642/mcp -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}'

# 通知：应返回 202
curl -s -o /dev/null -w "%{http_code}" -X POST http://127.0.0.1:8642/mcp \
  -H "Content-Type: application/json" -d '{"jsonrpc":"2.0","method":"notifications/initialized"}'

# 工具调用：真实往返到页面
curl -s -X POST http://127.0.0.1:8642/mcp -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"get_state","arguments":{"detail":false}}}'
```

## 代码地图

| 位置 | 职责 |
|---|---|
| `src/Server/McpModule.cs` | 端点：路径/方法/跨站/JSON 校验、202/405/403 语义、响应编码（Utf8NoBom） |
| `src/Server/McpModule.cs`（McpBridge） | HTTP → 页面求值委托的注入点，MainWindow 启动时赋值、关闭时置空 |
| `src/App/MainWindow.xaml.cs` | `EvaluateMcpAsync`：UI 线程分发 + `EvaluateScriptAsync`，页面未就绪时抛错 |
| `src/Server/EmbeddedServer.cs` | 模块挂载顺序 |
| `src/Server/ApiGuardModule.cs` | `CrossSiteDetail`：/api 与 /mcp 共用的跨站判定 |
| `frontend/src/jizura/agent-api.ts` | 协议语义、17 个工具、revision 契约、代理提示词（`J.mcp.prompt()`） |
| `frontend/src/jizura/boot.ts` | 副作用导入 agent-api（挂载 `window.J.mcp`） |

## 故障排查

| 现象 | 原因 / 处理 |
|---|---|
| 连接拒绝 | 应用未启动；启动后到客户端重连 |
| `-32603 页面尚未加载完成` | 页面还在初始化，稍候重试（initialize/tools/list 也要等页面就绪） |
| `未知工具：<名>` | 工具名没有 `jizura_` 前缀，以 tools/list 返回为准 |
| `stale_revision` | 写操作的 revision 过期，重新 `get_state` 取最新值 |
| 响应不是 JSON（HTML） | 旧版本构建；重新 dotnet build（含前端）后重启应用 |
| 端口漂移 | 8642 被其他程序占用，应用换到 86xx，更新客户端配置 |
