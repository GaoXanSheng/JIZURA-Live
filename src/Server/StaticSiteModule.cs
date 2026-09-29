using System.IO;
using System.Reflection;
using EmbedIO;

namespace JizuraLive.Server;

/// <summary>
/// 静态站点：前端产物以 EmbeddedResource（jizura-www/ 前缀）嵌在程序集内，
/// 从内存直接 serve——磁盘上没有可被随手改动的散装前端文件。
/// 磁盘 wwwroot 仅作兜底（favicon 等）与旧版 /legacy 对照页。
/// </summary>
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
        string path = Uri.UnescapeDataString(context.Request.Url.AbsolutePath);

        // 新前端：嵌入产物优先；旧构建/异常情况下回退磁盘 dist，再回退旧版单文件页
        if (path == "/" || path == "/index.html")
        {
            if (await SendEmbeddedAsync(context, "index.html")) return true;
            if (File.Exists(Path.Combine(ServerContext.WwwRoot, "dist", "index.html")))
                return await SendFileAsync(context, Path.Combine("dist", "index.html"), "text/html; charset=utf-8");
            return await SendFileAsync(context, Path.Combine("zh-hans", "index.html"), "text/html; charset=utf-8");
        }

        // 旧版单文件页（重构过渡期对照验收用）
        if (path == "/legacy" || path == "/legacy/" || path == "/legacy/index.html")
            return await SendFileAsync(context, Path.Combine("zh-hans", "index.html"), "text/html; charset=utf-8");

        string rel = path.TrimStart('/');
        if (rel.Length == 0 || rel.Contains("..")) return false;

        if (await SendEmbeddedAsync(context, rel)) return true;

        // 磁盘兜底：wwwroot 下的静态文件（favicon.ico、旧版资源等）
        if (ServerContext.MimeOf(rel) is { } mime)
        {
            string full = Path.GetFullPath(Path.Combine(ServerContext.WwwRoot, rel));
            if (full.StartsWith(ServerContext.WwwRoot) && File.Exists(full))
                return await SendFileAsync(context, rel, mime);
        }

        // SPA history 路由回退：/render 等无扩展名路径回嵌入 index.html
        if (!rel.Contains('.') && await SendEmbeddedAsync(context, "index.html")) return true;

        return false;
    }

    private static async Task<bool> SendEmbeddedAsync(IHttpContext context, string rel)
    {
        Stream? stream;
        try { stream = Asm.GetManifestResourceStream(EmbeddedPrefix + rel); }
        catch { return false; }
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

    private static async Task<bool> SendFileAsync(IHttpContext context, string rel, string mime)
    {
        string full = Path.Combine(ServerContext.WwwRoot, rel);
        if (!File.Exists(full)) return false;

        context.Response.StatusCode = 200;
        context.Response.ContentType = mime;
        context.Response.ContentLength64 = new FileInfo(full).Length;
        context.Response.Headers["Cache-Control"] = "no-cache";
        await using var fs = File.OpenRead(full);
        await fs.CopyToAsync(context.Response.OutputStream);
        return true;
    }
}
