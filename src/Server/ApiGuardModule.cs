using System.Text;
using EmbedIO;
using JizuraLive.Logging;

namespace JizuraLive.Server;

// /api 防跨站守卫。服务只绑 127.0.0.1，但受害者浏览器里任何网页都能向
// http://127.0.0.1:{port}/api/* 发跨站请求：img src 静默登出、text/plain「简单请求」改写设置。
// 按 Chromium 的 Sec-Fetch-Site/Origin 头拦截；无这些头的非浏览器客户端（本机脚本、dev 工具）不受影响。
// 只管 /api 前缀，注册在最前，命中即终结
internal sealed class ApiGuardModule : WebModuleBase
{
    public ApiGuardModule() : base("/api")
    {
    }

    public override bool IsFinalHandler => false;

    protected override Task<bool> OnRequestAsync(IHttpContext context)
    {
        var detail = CrossSiteDetail(context);
        return detail == null ? Task.FromResult(false) : Reject(context, detail);
    }

    // 跨站判定：本站/非浏览器请求返回 null，否则返回拒绝原因。/api 与 /mcp 共用
    internal static string? CrossSiteDetail(IHttpContext context)
    {
        var site = context.Request.Headers["Sec-Fetch-Site"];
        if (string.Equals(site, "cross-site", StringComparison.OrdinalIgnoreCase))
            return "cross-site";

        var origin = context.Request.Headers["Origin"];
        if (!string.IsNullOrEmpty(origin) && Uri.TryCreate(origin, UriKind.Absolute, out var o))
        {
            var self = context.Request.Url;
            var sameAuthority = string.Equals(o.Authority, self.Authority,
                StringComparison.OrdinalIgnoreCase);
            // 回环地址放行：vite dev 代理转发时 Origin 指向 dev server（localhost:5173）
            var loopback = o.Host is "127.0.0.1" or "::1" or "localhost";
            if (!sameAuthority && !loopback)
                return "origin=" + o.Authority;
        }

        return null;
    }

    private static async Task<bool> Reject(IHttpContext context, string detail)
    {
        Log.Warn($"[ApiGuard] 已拦截跨站请求（{detail}）：{context.Request.Url}");
        context.Response.StatusCode = 403;
        await context.SendStringAsync(
            "{\"ok\":false,\"error\":\"已拦截来自其他网站的请求\"}",
            "application/json; charset=utf-8", Encoding.UTF8);
        return true;
    }
}
