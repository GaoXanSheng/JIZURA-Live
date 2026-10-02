using System.Text;
using EmbedIO;

namespace JizuraLive.Server;

/// <summary>
///     API 防跨站请求中间件。服务只绑 127.0.0.1，但受害者浏览器里任何网页
///     都能向 http://127.0.0.1:{port}/api/* 发跨站请求——比如
///     &lt;img src="/api/ne/logout"&gt; 静默登出、text/plain「简单请求」POST 改写设置。
///     依据 Chromium 的 Sec-Fetch-Site / Origin 头拦截跨站请求；
///     无这些头的非浏览器客户端（本机脚本、dev 工具）不受影响。
///     只管 /api 前缀，静态资源放行；注册在最前，命中即终结请求。
/// </summary>
internal sealed class ApiGuardModule : WebModuleBase
{
    public ApiGuardModule() : base("/api")
    {
    }

    public override bool IsFinalHandler => false;

    protected override Task<bool> OnRequestAsync(IHttpContext context)
    {
        var site = context.Request.Headers["Sec-Fetch-Site"];
        if (string.Equals(site, "cross-site", StringComparison.OrdinalIgnoreCase))
            return Reject(context, "cross-site");

        var origin = context.Request.Headers["Origin"];
        if (!string.IsNullOrEmpty(origin) && Uri.TryCreate(origin, UriKind.Absolute, out var o))
        {
            var self = context.Request.Url;
            var sameAuthority = string.Equals(o.Authority, self.Authority,
                StringComparison.OrdinalIgnoreCase);
            // 回环变体放行：vite dev 代理转发时 Origin 指向 dev server（localhost:5173）
            var loopback = o.Host is "127.0.0.1" or "::1" or "localhost";
            if (!sameAuthority && !loopback)
                return Reject(context, "origin=" + o.Authority);
        }

        return Task.FromResult(false);
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
