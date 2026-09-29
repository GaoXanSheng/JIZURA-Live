using System.IO;
using EmbedIO;

namespace JizuraLive.Server;

internal sealed class StaticSiteModule : WebModuleBase
{
    public StaticSiteModule() : base("/")
    {
    }

    public override bool IsFinalHandler => false;

    protected override async Task<bool> OnRequestAsync(IHttpContext context)
    {
        string path = Uri.UnescapeDataString(context.Request.Url.AbsolutePath);

        if (path == "/" || path == "/index.html")
            return await SendFileAsync(context, Path.Combine("zh-hans", "index.html"), "text/html; charset=utf-8");

        string rel = path.TrimStart('/');
        if (rel.Length > 0 && !rel.Contains("..") && ServerContext.MimeOf(rel) is { } mime)
        {
            string full = Path.GetFullPath(Path.Combine(ServerContext.WwwRoot, rel));
            if (full.StartsWith(ServerContext.WwwRoot) && File.Exists(full))
                return await SendFileAsync(context, rel, mime);
        }

        return false;
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
