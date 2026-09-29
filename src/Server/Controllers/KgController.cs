using System.IO;
using System.Net.Http;
using System.Net;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;

namespace JizuraLive.Server;






public sealed class KgController : WebApiController
{
    [Route(HttpVerbs.Get, "/kg/search")]
    public async Task<KgSearchResponse> Search(
        [QueryField("kw")] string? kw, [QueryField("page")] string? page)
    {
        int p = Math.Max(1, int.TryParse(page, out var v) ? v : 1);
        return await ServerContext.KuGou.SearchAsync(kw ?? "", p);
    }

    [Route(HttpVerbs.Get, "/kg/url")]
    public async Task<KgUrlResponse> Url(
        [QueryField("hash")] string? hash,
        [QueryField("album_id")] string? albumId,
        [QueryField("album_audio_id")] string? albumAudioId,
        [QueryField("quality")] string? quality)
    {
        if (string.IsNullOrEmpty(hash)) return new KgUrlResponse { Ok = false, Error = "缺少 hash" };
        int q = int.TryParse(quality, out var v) ? v : 128;
        return await ServerContext.KuGou.GetUrlAsync(hash!, albumId ?? "0", albumAudioId ?? "0", q);
    }

    [Route(HttpVerbs.Get, "/kg/lyric")]
    public Task<KgLyricResponse> Lyric(
        [QueryField("hash")] string? hash,
        [QueryField("id")] string? id,
        [QueryField("accesskey")] string? accesskey,
        [QueryField("album_id")] string? albumId) =>
        ServerContext.KuGou.GetLyricAsync(hash, id, accesskey, albumId ?? "");

    [Route(HttpVerbs.Get, "/kg/qr")]
    public Task<KgQrResponse> Qr() => ServerContext.KuGou.QrAsync();

    [Route(HttpVerbs.Get, "/kg/qr/check")]
    public Task<KgQrCheckResponse> QrCheck([QueryField("key")] string? key) =>
        ServerContext.KuGou.QrCheckAsync(key ?? "");

    [Route(HttpVerbs.Get, "/kg/login")]
    public KgLoginResponse Login() => ServerContext.KuGou.LoginState();

    [Route(HttpVerbs.Get, "/kg/logout")]
    public KgLoginResponse Logout() => ServerContext.KuGou.Logout();

    
    [Route(HttpVerbs.Get, "/kg/audio")]
    public async Task Audio([QueryField("u")] string? target)
    {
        var res = HttpContext.Response;
        if (!Uri.TryCreate(target ?? "", UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https") ||
            !KuGouClient.AllowedAudioHost(uri.Host))
        {
            res.StatusCode = 400;
            await HttpContext.SendStringAsync("{\"ok\":false,\"error\":\"不允许的地址\"}", "application/json; charset=utf-8", System.Text.Encoding.UTF8);
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var upstreamReq = new HttpRequestMessage(HttpMethod.Get, uri);
        string? range = HttpContext.Request.Headers["Range"];
        if (!string.IsNullOrEmpty(range))
            upstreamReq.Headers.TryAddWithoutValidation("Range", range);

        using var upstream = await ServerContext.KuGou.Http.SendAsync(upstreamReq,
            HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!upstream.IsSuccessStatusCode && upstream.StatusCode != HttpStatusCode.PartialContent)
        {
            res.StatusCode = 502;
            await HttpContext.SendStringAsync(
                "{{\"ok\":false,\"error\":\"音频源返回 {(int)upstream.StatusCode}\"}}",
                "application/json; charset=utf-8", System.Text.Encoding.UTF8);
            return;
        }

        res.StatusCode = (int)upstream.StatusCode;
        string? ext = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
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
