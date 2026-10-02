using System.IO;
using System.Text;
using EmbedIO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Server;

/// <summary>
///     MCP（Model Context Protocol）端点。POST /mcp 收 JSON-RPC 2.0，
///     原样桥进 CEF 页面的 window.J.mcp.rpc()——协议语义与 jizura_* 工具的
///     唯一事实源在 frontend/src/jizura/agent-api.ts，这里只做防跨站、
///     JSON 解析与 UI 线程分发。
///     无状态 Streamable HTTP：一个请求一个 JSON 响应；通知（notifications/*）
///     回 202 空体；GET/DELETE（SSE 通道）不支持，回 405。
///     页面未加载完成时以 JSON-RPC 错误应答，客户端稍后重试即可。
/// </summary>
internal sealed class McpModule : WebModuleBase
{
    private const string EndpointPath = "/mcp";

    /// <summary>EmbedIO 的 SendStringAsync 会带上编码 preamble（BOM），
    /// Node 系 MCP 客户端 JSON.parse 会被 BOM 呛死，这里必须用无 BOM 编码。</summary>
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    public McpModule() : base(EndpointPath)
    {
    }

    /// <summary>终结处理器：/mcp 的任何响应（含没写响应体的 202）都必须在此结束。
    /// EmbedIO 对非终结模块会继续放行到下一个模块——StaticSite 的 SPA 回退
    /// 会把 202 这类"已处理但响应未开始"的请求覆盖成 index.html。</summary>
    public override bool IsFinalHandler => true;

    protected override async Task<bool> OnRequestAsync(IHttpContext context)
    {
        // base 前缀匹配可能放过 /mcpx 之类，端点只认精确路径
        if (!string.Equals(context.Request.Url.AbsolutePath, EndpointPath,
                StringComparison.OrdinalIgnoreCase))
            return false;

        var crossSite = ApiGuardModule.CrossSiteDetail(context);
        if (crossSite != null)
            return await RejectAsync(context, crossSite);

        if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            return await MethodNotAllowedAsync(context);

        return await HandlePostAsync(context);
    }

    private async Task<bool> HandlePostAsync(IHttpContext context)
    {
        string body;
        using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync();
        }

        JToken request;
        try
        {
            request = JToken.Parse(body);
        }
        catch (JsonException ex)
        {
            Log.Warn("[Mcp] 请求不是合法 JSON：" + ex.Message);
            return await ErrorAsync(context, null, -32700, "请求不是合法 JSON：" + ex.Message);
        }

        var method = request.Value<string>("method") ?? "?";
        Log.Info("[Mcp] " + (method == "tools/call"
            ? "tools/call " + request["params"]?["name"]
            : method));

        if (McpBridge.EvaluateAsync == null)
            return await ErrorAsync(context, request["id"], -32603, "应用窗口尚未创建，请稍后重试");

        // 请求体经 Newtonsoft 序列化成字符串字面量再 JSON.parse：
        // 不把原始文本当 JS 字面量嵌入，避免任何注入歧义
        var script = "JSON.stringify(window.J.mcp.rpc(JSON.parse("
                     + JsonConvert.SerializeObject(body) + ")))";

        string? resultJson;
        try
        {
            resultJson = await McpBridge.EvaluateAsync(script);
        }
        catch (Exception ex)
        {
            Log.Error("[Mcp] 页面求值失败：" + method, ex);
            return await ErrorAsync(context, request["id"], -32603, "页面执行失败：" + ex.Message);
        }

        // null / "null" 都是通知（mcpRpc 对 notifications/* 返回 null，无响应体）。
        // 202 也要显式发一个空体：响应不开始就无法确认 EmbedIO 已终结请求
        if (resultJson == null || resultJson == "null")
        {
            context.Response.StatusCode = 202;
            await context.SendStringAsync("", "application/json; charset=utf-8", Utf8NoBom);
            return true;
        }

        context.Response.StatusCode = 200;
        await context.SendStringAsync(resultJson, "application/json; charset=utf-8", Utf8NoBom);
        return true;
    }

    private static async Task<bool> ErrorAsync(IHttpContext context, JToken? id, int code,
        string message)
    {
        var response = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["error"] = new JObject { ["code"] = code, ["message"] = message }
        };
        context.Response.StatusCode = 200;
        await context.SendStringAsync(response.ToString(Formatting.None),
            "application/json; charset=utf-8", Utf8NoBom);
        return true;
    }

    private static async Task<bool> MethodNotAllowedAsync(IHttpContext context)
    {
        context.Response.StatusCode = 405;
        context.Response.Headers["Allow"] = "POST";
        await context.SendStringAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32603,\"message\":\"仅支持 POST\"}}",
            "application/json; charset=utf-8", Utf8NoBom);
        return true;
    }

    private static async Task<bool> RejectAsync(IHttpContext context, string detail)
    {
        Log.Warn($"[Mcp] 已拦截跨站请求（{detail}）：{context.Request.Url}");
        context.Response.StatusCode = 403;
        await context.SendStringAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":null,\"error\":{\"code\":-32603,\"message\":\"已拦截来自其他网站的请求\"}}",
            "application/json; charset=utf-8", Utf8NoBom);
        return true;
    }
}

/// <summary>
///     HTTP 端点与 CEF 页面之间的桥：MainWindow 启动时注入求值委托，
///     McpModule 通过它在页面里执行脚本并取回 JSON.stringify 的结果
///     （null 表示页面无返回，即通知）。
/// </summary>
internal static class McpBridge
{
    public static Func<string, Task<string?>>? EvaluateAsync { get; set; }
}
