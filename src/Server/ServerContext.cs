using System.IO;

namespace JizuraLive.Server;

internal static class ServerContext
{
    public static string WwwRoot { get; } = Path.Combine(AppContext.BaseDirectory, "wwwroot");

    public static KuGouClient KuGou { get; } = new();

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
        [".svg"] = "image/svg+xml",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
    };

    public static string? MimeOf(string path) =>
        MimeMap.TryGetValue(Path.GetExtension(path).ToLowerInvariant(), out var m) ? m : null;
}
