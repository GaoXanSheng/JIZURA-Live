using System.IO;
using System.Net;
using System.Text;
using JizuraLive.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.App.Mcp;

// POST /mcp 的 BCL 端点（对位旧 src/Server/McpModule.cs，语义逐条照搬；EmbedIO 已整体移除）。
// 无状态 Streamable HTTP：一个请求一个 JSON 响应；通知（notifications/*）回 202 空体；
// GET/DELETE（SSE 通道）不支持，回 405。跨站防护与旧 ApiGuardModule.CrossSiteDetail 同规则。
//
// 只绑回环：HttpListener 前缀限定 127.0.0.1 / localhost（HTTP.sys 对回环前缀免 ACL 提权，
// 非管理员也能起）；同时注册 localhost 前缀让 `curl localhost:8642` 这类调用照旧可用。
// 端口从 8642 起向后探测 10 个，与旧 EmbeddedServer 一致（配置写死 8642，仅被占时漂移）。
public sealed class McpHttpServer : IDisposable
{
    private const string EndpointPath = "/mcp";
    private const int PortBase = 8642;
    private const int PortTries = 10;

    // 响应不能带 BOM：Node 系 MCP 客户端 JSON.parse 会被 BOM 呛死
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly McpTools _tools;
    private Task? _loop;

    private McpHttpServer(int port, McpTools tools)
    {
        Port = port;
        _tools = tools;
    }

    public int Port { get; }

    /// <summary>从 8642 起探测 10 个端口，全部被占返回 null（App 据此退出）。</summary>
    public static McpHttpServer? Start(McpTools tools)
    {
        for (var port = PortBase; port < PortBase + PortTries; port++)
        {
            var server = StartOn(port, tools);
            if (server != null) return server;
        }
        return null;
    }

    /// <summary>指定端口启动（测试用：随机空闲端口）。端口不可用返回 null。</summary>
    public static McpHttpServer? StartOn(int port, McpTools tools)
    {
        var server = new McpHttpServer(port, tools);
        if (server.TryStart()) return server;
        server.Dispose();
        return null;
    }

    private bool TryStart()
    {
        var ipPrefix = $"http://127.0.0.1:{Port}/";
        var loopPrefix = $"http://localhost:{Port}/";
        try
        {
            _listener.Prefixes.Add(ipPrefix);
            _listener.Prefixes.Add(loopPrefix);
            _listener.Start();
        }
        catch (Exception ex)
        {
            // localhost 前缀可能被单独占用/策略拦下：退一步只挂回环 IP
            Log.Debug($"MCP 端口 {Port} 双前缀不可用（{ex.Message}），改用 {ipPrefix}");
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add(ipPrefix);
                _listener.Start();
            }
            catch (Exception ex2)
            {
                Log.Debug($"MCP 端口 {Port} 不可用：{ex2.Message}");
                return false;
            }
        }
        _loop = Task.Run(LoopAsync);
        Log.Info($"[Mcp] 监听 {ipPrefix}{EndpointPath.TrimStart('/')}（仅回环）");
        return true;
    }

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return; // Stop/Dispose 之后 GetContextAsync 抛错，退出循环
            }
            _ = Task.Run(() => HandleAsync(ctx));
        }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            // 端点只认精确路径（旧 McpModule 的 base 前缀匹配会放过 /mcpx 之类）
            if (!string.Equals(ctx.Request.Url?.AbsolutePath, EndpointPath, StringComparison.OrdinalIgnoreCase))
            {
                await WriteJsonAsync(ctx, 404,
                    "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32603,\"message\":\"未知路径（MCP 端点只有 /mcp）\"}}");
                return;
            }

            var crossSite = CrossSiteDetail(ctx);
            if (crossSite != null)
            {
                Log.Warn($"[Mcp] 已拦截跨站请求（{crossSite}）：{ctx.Request.Url}");
                await WriteJsonAsync(ctx, 403,
                    "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32603,\"message\":\"已拦截来自其他网站的请求\"}}");
                return;
            }

            if (!string.Equals(ctx.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.Headers["Allow"] = "POST";
                await WriteJsonAsync(ctx, 405,
                    "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32603,\"message\":\"仅支持 POST\"}}");
                return;
            }

            string body;
            using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
                body = await reader.ReadToEndAsync();

            JToken request;
            try
            {
                request = JToken.Parse(body);
            }
            catch (JsonException ex)
            {
                Log.Warn("[Mcp] 请求不是合法 JSON：" + ex.Message);
                await WriteJsonAsync(ctx, 200, ErrorJson(null, -32700, "请求不是合法 JSON：" + ex.Message));
                return;
            }

            // 协议只收单个 JSON-RPC 2.0 对象；数组（批量）或标量按 Invalid Request 回给客户端
            if (request.Type != JTokenType.Object)
            {
                Log.Warn("[Mcp] 请求不是 JSON-RPC 对象（批量请求不支持）");
                await WriteJsonAsync(ctx, 200,
                    ErrorJson(null, -32600, "请求必须是单个 JSON-RPC 2.0 对象（不支持批量）"));
                return;
            }

            var method = request.Value<string>("method") ?? "?";
            Log.Info("[Mcp] " + (method == "tools/call"
                ? "tools/call " + request["params"]?["name"]
                : method));

            var response = _tools.Rpc((JObject)request);
            if (response == null)
            {
                // 通知：202 空体（无响应体）
                ctx.Response.StatusCode = 202;
                ctx.Response.ContentType = "application/json; charset=utf-8";
                ctx.Response.ContentLength64 = 0;
                ctx.Response.Close();
                return;
            }
            await WriteJsonAsync(ctx, 200, response.ToString(Formatting.None));
        }
        catch (Exception ex)
        {
            Log.Error("[Mcp] 请求处理失败", ex);
            try
            {
                await WriteJsonAsync(ctx, 200, ErrorJson(null, -32603, "执行失败：" + ex.Message));
            }
            catch
            {
                // 响应可能已开始，放弃
            }
        }
    }

    // 跨站判定：本站/非浏览器请求返回 null，否则返回拒绝原因（旧 ApiGuardModule.CrossSiteDetail 逐条照搬）。
    // 服务只绑回环，但受害者浏览器里任何网页都能向 http://127.0.0.1:{port}/mcp 发跨站请求：
    // 按 Chromium 的 Sec-Fetch-Site/Origin 头拦截；无这些头的本机脚本不受影响
    private static string? CrossSiteDetail(HttpListenerContext ctx)
    {
        var site = ctx.Request.Headers["Sec-Fetch-Site"];
        if (string.Equals(site, "cross-site", StringComparison.OrdinalIgnoreCase))
            return "cross-site";

        var origin = ctx.Request.Headers["Origin"];
        if (!string.IsNullOrEmpty(origin) && Uri.TryCreate(origin, UriKind.Absolute, out var o))
        {
            var sameAuthority = string.Equals(o.Authority, ctx.Request.Url?.Authority,
                StringComparison.OrdinalIgnoreCase);
            // 回环地址放行：本机开发工具 / vite dev 代理转发时 Origin 指向 dev server
            var loopback = o.Host is "127.0.0.1" or "::1" or "localhost";
            if (!sameAuthority && !loopback)
                return "origin=" + o.Authority;
        }

        return null;
    }

    private static string ErrorJson(JToken? id, int code, string message) =>
        new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["error"] = new JObject { ["code"] = code, ["message"] = message }
        }.ToString(Formatting.None);

    private static async Task WriteJsonAsync(HttpListenerContext ctx, int status, string json)
    {
        var bytes = Utf8NoBom.GetBytes(json);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        ctx.Response.ContentLength64 = bytes.Length;
        await ctx.Response.OutputStream.WriteAsync(bytes);
        ctx.Response.Close();
    }

    public void Dispose()
    {
        try
        {
            _cts.Cancel();
            _listener.Stop();
            _listener.Close();
        }
        catch (Exception ex)
        {
            Log.Debug("MCP 服务 Dispose 失败：" + ex.Message);
        }
        _cts.Dispose();
    }
}
