using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using JizuraLive.App;
using JizuraLive.Logging;
using JizuraLive.Server;
using JizuraLive.Server.Models;
using JizuraLive.App.I18n;

namespace JizuraLive.App.Cloud;

public sealed class CloudMusicException(string message) : Exception(message);

public sealed record CloudLoadedSong(string AudioPath, string FileName, string Lrc, string Note);

public static class CloudMusicService
{
    public const string Kugou = "kugou";
    public const string Netease = "netease";

    private static readonly string[] HostSuffixes = ["kugou.com", "kgimg.com", "kglink.cn", "music.126.net", "126.net"];
    private static readonly HttpClient StreamHttp = new() { Timeout = Timeout.InfiniteTimeSpan };
    private static readonly HttpClient CoverHttp = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<List<KgSong>> SearchAsync(string source, string keyword, int page,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(keyword)) return new List<KgSong>();
        KgSearchResponse result;
        if (source == Netease)
            result = await ServerContext.Netease.SearchAsync(keyword, page);
        else
            result = await ServerContext.KuGou.SearchAsync(keyword, page);
        if (!result.Ok) throw new CloudMusicException(result.Error ?? L.T("搜索失败"));
        return result.List;
    }

    public static async Task<CloudLoadedSong> LoadAsync(KgSong song, string quality = "320",
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var source = string.IsNullOrWhiteSpace(song.Source) ? Kugou : song.Source;
        progress?.Invoke(L.T("正在获取播放地址…"));
        KgUrlResponse urlResponse = source == Netease
            ? await ServerContext.Netease.GetUrlAsync(song.Id, quality)
            : await ServerContext.KuGou.GetUrlAsync(song.Hash, song.AlbumId, song.AlbumAudioId,
                int.TryParse(quality, out var kgQuality) ? kgQuality : 320);
        if (!urlResponse.Ok || string.IsNullOrWhiteSpace(urlResponse.Url))
            throw new CloudMusicException(urlResponse.Error ?? L.T("没有可用的播放地址（可能需要登录或 VIP）"));

        var ext = Regex.Replace(urlResponse.Ext ?? "mp3", "[^a-z0-9]", "", RegexOptions.IgnoreCase).ToLowerInvariant();
        if (ext.Length == 0) ext = "mp3";
        var fileName = AudioFileName(song.Singer, song.Name, ext);
        progress?.Invoke(L.T("正在下载音频…"));
        var audioPath = await DownloadAsync(urlResponse.Url, fileName, ct);

        progress?.Invoke(L.T("正在获取歌词…"));
        var lrc = "";
        var note = L.T("，没有找到歌词");
        try
        {
            var lyric = source == Netease
                ? await ServerContext.Netease.GetLyricAsync(song.Id)
                : await ServerContext.KuGou.GetLyricAsync(song.Hash, null, null, song.AlbumId);
            if (lyric.Ok && !string.IsNullOrWhiteSpace(lyric.Content))
            {
                if (source == Netease)
                {
                    lrc = Krc.B64Text(lyric.Content);
                }
                else if (string.Equals(lyric.Fmt, "krc", StringComparison.OrdinalIgnoreCase))
                {
                    lrc = Krc.KrcToLrc(Krc.Decrypt(Convert.FromBase64String(lyric.Content)), false, false);
                }
                else
                {
                    lrc = Krc.B64Text(lyric.Content);
                }

                note = string.IsNullOrWhiteSpace(lrc) ? L.T("，歌词为空") : L.T("，歌词已填入");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[Cloud] 歌词获取失败（继续装载音频）：" + ex.Message);
            note = L.T("，歌词获取失败");
        }

        return new CloudLoadedSong(audioPath, fileName, lrc, note);
    }

    public static async Task<BitmapSource?> LoadCoverAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")) return null;

        var dir = Path.Combine(AppDirs.CacheDir, "cloud-covers");
        Directory.CreateDirectory(dir);
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16].ToLowerInvariant();
        var file = Path.Combine(dir, key);

        byte[] data;
        if (File.Exists(file) && new FileInfo(file).Length > 0)
        {
            data = await File.ReadAllBytesAsync(file);
        }
        else
        {
            data = await CoverHttp.GetByteArrayAsync(uri);
            if (data.Length > 0) await File.WriteAllBytesAsync(file, data);
        }

        using var stream = new MemoryStream(data);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    public static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' ? ' '
                : char.IsControl(ch) ? ' ' : ch);
        var value = Regex.Replace(sb.ToString(), @"\s+", " ").Trim().TrimEnd('.', ' ');
        if (value.Length == 0) value = "untitled";
        var stem = value.Split(' ')[0].ToUpperInvariant();
        if (Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$")) value = "_" + value;
        return value;
    }

    private static string AudioFileName(string singer, string name, string ext)
    {
        var stem = SanitizeFileName(string.IsNullOrWhiteSpace(singer) ? name : $"{singer} - {name}");
        return stem + "." + ext;
    }

    private static bool AllowedHost(string host) =>
        HostSuffixes.Any(s => string.Equals(host, s, StringComparison.OrdinalIgnoreCase)
                              || host.EndsWith("." + s, StringComparison.OrdinalIgnoreCase));

    private static async Task<string> DownloadAsync(string url, string fileName, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !AllowedHost(uri.Host))
            throw new CloudMusicException(L.T("播放地址不在允许的音频域名内"));

        Directory.CreateDirectory(AppDirs.CloudDir);
        var path = Path.Combine(AppDirs.CloudDir, fileName);
        if (File.Exists(path) && new FileInfo(path).Length > 0)
            return path;

        var tmp = path + ".part";
        try
        {
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshake.CancelAfter(TimeSpan.FromSeconds(30));
            using var watchdog = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var response = await StreamHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, handshake.Token);
            var finalHost = response.RequestMessage?.RequestUri?.Host ?? uri.Host;
            if (!response.IsSuccessStatusCode)
                throw new CloudMusicException(L.F("音频下载失败：HTTP {0}", (int)response.StatusCode));
            if (!AllowedHost(finalHost))
                throw new CloudMusicException(L.T("音频跳转到了不允许的域名"));

            await using (var source = await response.Content.ReadAsStreamAsync(watchdog.Token))
            await using (var target = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
            {
                var buffer = new byte[81920];
                while (true)
                {
                    watchdog.CancelAfter(TimeSpan.FromSeconds(60));
                    var read = await source.ReadAsync(buffer, watchdog.Token);
                    if (read == 0) break;
                    await target.WriteAsync(buffer.AsMemory(0, read), watchdog.Token);
                }
            }

            if (new FileInfo(tmp).Length == 0) throw new CloudMusicException(L.T("音频下载结果为空"));
            File.Move(tmp, path, true);
            Log.Info("[Cloud] 音频已下载：" + path);
            return path;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CloudMusicException(L.T("音频下载超时"));
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch { }
        }
    }
}
