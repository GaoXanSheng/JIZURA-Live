using System.IO;
using JizuraLive.Server.KuGou;
using JizuraLive.Server.Netease;
using JizuraLive.Server.Smtc;

namespace JizuraLive.Server;

internal static class ServerContext
{
    public static readonly Dictionary<string, string> MimeMap = new()
    {
        [".html"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json",
        [".xml"] = "text/xml; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8",
        [".krc"] = "text/plain; charset=utf-8",
        [".lrc"] = "text/plain; charset=utf-8",
        [".mp3"] = "audio/mpeg",
        [".flac"] = "audio/flac",
        [".ogg"] = "audio/ogg",
        [".m4a"] = "audio/mp4",
        [".wav"] = "audio/wav",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".ico"] = "image/x-icon",
        [".svg"] = "image/svg+xml",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2"
    };

    public static KuGouClient KuGou { get; } = new();

    public static NeteaseClient Netease { get; } = new();

    public static SmtcService Smtc { get; } = new();

    public static string? MimeOf(string path)
    {
        return MimeMap.TryGetValue(Path.GetExtension(path).ToLowerInvariant(), out var m) ? m : null;
    }
}