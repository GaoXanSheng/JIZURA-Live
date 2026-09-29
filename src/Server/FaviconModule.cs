using System.IO;
using System.Windows;
using EmbedIO;

namespace JizuraLive.Server;

// --------------------------------------------------------------------
// /favicon 与 /favicon.ico：直接返回应用图标（assets/app.ico，
// 已由 csproj 以 WPF Resource 嵌入程序集）。
//
// 浏览器在页面没有 <link rel="icon"> 标签时，会按约定自动请求
// /favicon.ico，因此网页源码无需任何改动。
// 返回 false 表示未处理，继续交给后续模块（静态站点/API）。
// --------------------------------------------------------------------

public sealed class FaviconModule : WebModuleBase
{
    private byte[]? _icon;

    public FaviconModule() : base("/")
    {
    }

    public override bool IsFinalHandler => true;

    protected override async Task<bool> OnRequestAsync(
        IHttpContext context)
    {
        string path =
            Uri.UnescapeDataString(
                context.Request.Url.AbsolutePath)
                .TrimEnd('/')
                .ToLowerInvariant();

        if (path != "/favicon" &&
            path != "/favicon.ico")
        {
            return false;
        }

        byte[] icon = LoadIcon();

        context.Response.StatusCode = 200;
        context.Response.ContentType = "image/x-icon";
        context.Response.ContentLength64 = icon.Length;
        context.Response.Headers["Cache-Control"] =
            "public, max-age=86400";

        await context.Response.OutputStream.WriteAsync(
            icon, 0, icon.Length);

        return true;
    }

    private byte[] LoadIcon()
    {
        if (_icon != null)
            return _icon;

        using var stream =
            Application
                .GetResourceStream(
                    new Uri(
                        "pack://application:,,,/assets/app.ico"))
                .Stream;

        using var ms =
            new MemoryStream();

        stream.CopyTo(ms);

        _icon =
            ms.ToArray();

        return _icon;
    }
}
