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

    // 流式代理专用客户端：HttpClient.Timeout 覆盖整个请求周期（ResponseHeadersRead
    // 下也包含内容流读取），共享客户端的 100s 上限会把长曲传输拦腰截断。
    // 无限超时 + 拷贝阶段的停滞看门狗兜底死连接
    private static readonly HttpClient StreamHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static bool AllowedHost(string host)
    {
        return HostSuffixes.Any(s =>
            string.Equals(host, s, StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("." + s, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task StreamAsync(IHttpContext ctx, string? target)
    {
        var http = StreamHttp;
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

        // 限时只覆盖握手；响应体拷贝不限总时长——直播中一首长 FLAC / 慢 CDN
        // 曲目超过 60 秒被硬性掐断是用户可感知的播放中断。
        // 拷贝阶段用「每次读至少 1 字节就续命 60s」的停滞看门狗兜底死连接
        using var handshakeCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var watchdogCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var upstreamReq = new HttpRequestMessage(HttpMethod.Get, uri);
        var range = ctx.Request.Headers["Range"];
        if (!string.IsNullOrEmpty(range))
            upstreamReq.Headers.TryAddWithoutValidation("Range", range);

        using var upstream = await http.SendAsync(upstreamReq,
            HttpCompletionOption.ResponseHeadersRead, handshakeCts.Token);

        // HttpClient 默认跟随重定向：跳转可能落到白名单之外，以最终请求 URI 复检
        var finalHost = upstream.RequestMessage?.RequestUri?.Host ?? uri.Host;
        if (!upstream.IsSuccessStatusCode && upstream.StatusCode != HttpStatusCode.PartialContent)
        {
            res.StatusCode = 502;
            await ctx.SendStringAsync(
                $"{{\"ok\":false,\"error\":\"音频源返回 {(int)upstream.StatusCode}\"}}",
                "application/json; charset=utf-8", Encoding.UTF8);
            return;
        }

        if (!AllowedHost(finalHost))
        {
            res.StatusCode = 400;
            await ctx.SendStringAsync("{\"ok\":false,\"error\":\"音频源重定向到不允许的域名\"}",
                "application/json; charset=utf-8", Encoding.UTF8);
            return;
        }

        res.StatusCode = (int)upstream.StatusCode;
        // 最终 URI 在白名单内已验证，取其扩展名辅助猜 MIME
        var finalPath = upstream.RequestMessage?.RequestUri?.AbsolutePath ?? uri.AbsolutePath;
        var ext = Path.GetExtension(finalPath).ToLowerInvariant();
        res.ContentType = ext?.Length > 1 && ServerContext.MimeMap.TryGetValue(ext, out var mime)
            ? mime
            : upstream.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        if (upstream.Content.Headers.ContentLength is { } len) res.ContentLength64 = len;
        if (upstream.Content.Headers.ContentRange is { } cr)
            res.Headers["Content-Range"] = cr.ToString();

        await using var src = await upstream.Content.ReadAsStreamAsync(watchdogCts.Token);
        var buffer = new byte[81920];
        try
        {
            while (true)
            {
                watchdogCts.CancelAfter(TimeSpan.FromSeconds(60));
                var read = await src.ReadAsync(buffer, watchdogCts.Token);
                if (read == 0) break;
                await res.OutputStream.WriteAsync(buffer, 0, read, watchdogCts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // 60 秒零数据（读/写停滞）：死连接，正常断开即可（响应已开始，无法再写错误体）
            Log.Debug($"[AudioProxy] 传输停滞超时断开：{uri.Host}");
        }
        catch (IOException ex)
        {
            // 客户端切歌/暂停中断下载、或上游连接复位——都是常规路径
            Log.Debug($"[AudioProxy] 流中断（客户端或上游断开）：{ex.Message}");
        }
    }
}
