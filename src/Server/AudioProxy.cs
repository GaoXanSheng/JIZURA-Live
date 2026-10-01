using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using EmbedIO;

namespace JizuraLive.Server;

// 酷狗 / 网易云共用的音频流代理：只放行白名单域名的 http(s) 地址，透传 Range。
internal static class AudioProxy
{
    private static readonly string[] HostSuffixes = ["kugou.com", "kgimg.com", "kglink.cn", "music.126.net"];

    public static bool AllowedHost(string host)
    {
        return HostSuffixes.Any(s =>
            string.Equals(host, s, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + s, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task StreamAsync(IHttpContext ctx, HttpClient http, string? target)
    {
        var res = ctx.Response;
        if (!Uri.TryCreate(target ?? "", UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") ||
            !AllowedHost(uri.Host))
        {
            res.StatusCode = 400;
            await ctx.SendStringAsync("{\"ok\":false,\"error\":\"不允许的地址\"}", "application/json; charset=utf-8",
                Encoding.UTF8);
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var upstreamReq = new HttpRequestMessage(HttpMethod.Get, uri);
        var range = ctx.Request.Headers["Range"];
        if (!string.IsNullOrEmpty(range))
            upstreamReq.Headers.TryAddWithoutValidation("Range", range);

        using var upstream = await http.SendAsync(upstreamReq,
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!upstream.IsSuccessStatusCode && upstream.StatusCode != HttpStatusCode.PartialContent)
        {
            res.StatusCode = 502;
            await ctx.SendStringAsync(
                $"{{\"ok\":false,\"error\":\"音频源返回 {(int)upstream.StatusCode}\"}}",
                "application/json; charset=utf-8", Encoding.UTF8);
            return;
        }

        res.StatusCode = (int)upstream.StatusCode;
        var ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        res.ContentType = ext?.Length > 1 && ServerContext.MimeMap.TryGetValue(ext, out var mime)
            ? mime
            : upstream.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        if (upstream.Content.Headers.ContentLength is { } len) res.ContentLength64 = len;
        if (upstream.Content.Headers.ContentRange is { } cr)
            res.Headers["Content-Range"] = cr.ToString();

        await using var src = await upstream.Content.ReadAsStreamAsync(cts.Token);
        await src.CopyToAsync(res.OutputStream, 81920, cts.Token);
    }
}