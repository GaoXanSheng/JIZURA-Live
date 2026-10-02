using System.IO;
using System.Reflection;
using EmbedIO;

namespace JizuraLive.Server;

// 前端产物以 EmbeddedResource（jizura-www/ 前缀）嵌在程序集内，从内存直接 serve，
// 磁盘上没有可被随手改动的散装前端文件。
// SPA history 路由（/render 等）回退到嵌入 index.html；api/ 前缀绝不回退，未匹配的 API 路径保持 404
internal sealed class StaticSiteModule : WebModuleBase
{
    private const string EmbeddedPrefix = "jizura-www/";
    private static readonly Assembly Asm = typeof(StaticSiteModule).Assembly;

    public StaticSiteModule() : base("/")
    {
    }

    public override bool IsFinalHandler => false;

    protected override async Task<bool> OnRequestAsync(IHttpContext context)
    {
        var rel = Uri.UnescapeDataString(context.Request.Url.AbsolutePath).TrimStart('/');

        if (rel == "" || rel == "index.html")
            return await SendEmbeddedAsync(context, "index.html");

        // 只拦".."路径段（真正的穿越写法）；子串检查会误杀文件名含 .. 的合法请求，
        // 如 Noto_Sans_JP_wght_100..900.css（可变字重区间）。
        // 嵌入资源直接查程序集清单，没有文件系统可逃逸，按段判断足够
        if (rel.Split('/', '\\').Any(seg => seg == "..")) return false;

        if (await SendEmbeddedAsync(context, rel)) return true;

        // SPA history 路由回退：/render 等无扩展名路径回嵌入 index.html。
        // api/ 前缀绝不回退，否则未匹配的 API 路径会拿到 HTML，前端 JSON 解析全挂
        if (!rel.Contains('.') && !rel.StartsWith("api/", StringComparison.OrdinalIgnoreCase)
                               && await SendEmbeddedAsync(context, "index.html")) return true;

        return false;
    }

    private static async Task<bool> SendEmbeddedAsync(IHttpContext context, string rel)
    {
        Stream? stream;
        try
        {
            stream = Asm.GetManifestResourceStream(EmbeddedPrefix + rel);
        }
        catch
        {
            return false;
        }

        if (stream == null) return false;

        await using (stream)
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = ServerContext.MimeOf(rel) ?? "application/octet-stream";
            context.Response.ContentLength64 = stream.Length;
            context.Response.Headers["Cache-Control"] = "no-cache";
            await stream.CopyToAsync(context.Response.OutputStream);
        }

        return true;
    }
}