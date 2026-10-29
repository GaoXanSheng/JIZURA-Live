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

public sealed record CloudLyricOption(string Id, string AccessKey, string Label,
    bool Trans = false, bool Roma = false)
{
    public override string ToString() => Label;
}

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
        if (!result.Ok) throw new CloudMusicException(result.Error ?? L.T("StageUi.Media.SearchFailed"));
        return result.List;
    }

    public static async Task<List<CloudLyricOption>> GetLyricOptionsAsync(KgSong song)
    {
        var source = string.IsNullOrWhiteSpace(song.Source) ? Kugou : song.Source;
        var options = new List<CloudLyricOption>();
        if (source == Netease)
        {
            options.Add(new CloudLyricOption("orig", "", L.T("CloudMusic.LyricVersionOriginalDefault")));
            options.Add(new CloudLyricOption("trans", "", L.T("CloudMusic.LyricVersionTrans"), Trans: true));
            options.Add(new CloudLyricOption("roma", "", L.T("CloudMusic.LyricVersionRoma"), Roma: true));
            options.Add(new CloudLyricOption("trans_roma", "", L.T("CloudMusic.LyricVersionTransRoma"),
                Trans: true, Roma: true));
            return options;
        }

        options.Add(new CloudLyricOption("", "", L.T("CloudMusic.LyricVersionDefault")));
        try
        {
            var candidates = await ServerContext.KuGou.GetLyricCandidatesAsync(song.Hash, song.AlbumId);
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                var label = !string.IsNullOrWhiteSpace(candidate.Nick) ? candidate.Nick
                    : !string.IsNullOrWhiteSpace(candidate.From) ? candidate.From
                    : L.F("CloudMusic.LyricVersionNumber", i + 1);
                options.Add(new CloudLyricOption(candidate.Id, candidate.AccessKey, label));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[Cloud] 歌词版本获取失败：" + ex.Message);
        }

        return options;
    }

    public static async Task<CloudLoadedSong> LoadAsync(KgSong song, string quality = "320",
        CloudLyricOption? lyricOption = null,
        Action<string>? progress = null, CancellationToken ct = default)
    {
        var source = string.IsNullOrWhiteSpace(song.Source) ? Kugou : song.Source;
        progress?.Invoke(L.T("StageUi.Media.FetchingPlayUrl"));
        KgUrlResponse urlResponse = source == Netease
            ? await ServerContext.Netease.GetUrlAsync(song.Id, quality)
            : await ServerContext.KuGou.GetUrlAsync(song.Hash, song.AlbumId, song.AlbumAudioId,
                int.TryParse(quality, out var kgQuality) ? kgQuality : 320);
        if (!urlResponse.Ok || string.IsNullOrWhiteSpace(urlResponse.Url))
            throw new CloudMusicException(urlResponse.Error ?? L.T("StageUi.Media.NoPlayableUrl"));

        var ext = Regex.Replace(urlResponse.Ext ?? "mp3", "[^a-z0-9]", "", RegexOptions.IgnoreCase).ToLowerInvariant();
        if (ext.Length == 0) ext = "mp3";
        var fileName = AudioFileName(song.Singer, song.Name, ext);
        progress?.Invoke(L.T("StageUi.Media.DownloadingAudio"));
        var audioPath = await DownloadAsync(urlResponse.Url, fileName, ct);

        progress?.Invoke(L.T("StageUi.Media.FetchingLyrics"));
        var lrc = "";
        var note = L.T("StageUi.Media.LyricsNotFound");
        try
        {
            var lyric = source == Netease
                ? await ServerContext.Netease.GetLyricAsync(song.Id)
                : await ServerContext.KuGou.GetLyricAsync(song.Hash, lyricOption?.Id, lyricOption?.AccessKey,
                    song.AlbumId);
            if (lyric.Ok && !string.IsNullOrWhiteSpace(lyric.Content))
            {
                if (source == Netease)
                {
                    var trans = lyricOption?.Trans == true && !string.IsNullOrWhiteSpace(lyric.Trans)
                        ? Krc.B64Text(lyric.Trans!)
                        : null;
                    var roma = lyricOption?.Roma == true && !string.IsNullOrWhiteSpace(lyric.Roma)
                        ? Krc.B64Text(lyric.Roma!)
                        : null;
                    var baseLrc = Krc.B64Text(lyric.Content);
                    lrc = trans is null && roma is null
                        ? baseLrc
                        : Krc.MergeVariantLrc(baseLrc, trans, roma);
                }
                else if (string.Equals(lyric.Fmt, "krc", StringComparison.OrdinalIgnoreCase))
                {
                    lrc = Krc.KrcToLrc(Krc.Decrypt(Convert.FromBase64String(lyric.Content)), false, false);
                }
                else
                {
                    lrc = Krc.B64Text(lyric.Content);
                }

                note = string.IsNullOrWhiteSpace(lrc) ? L.T("StageUi.Media.LyricsEmpty") : L.T("StageUi.Media.LyricsFilled");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("[Cloud] 歌词获取失败（继续装载音频）：" + ex.Message);
            note = L.T("StageUi.Media.LyricsFetchFailed");
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
            throw new CloudMusicException(L.T("StageUi.Media.PlayUrlHostNotAllowed"));

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
                throw new CloudMusicException(L.F("StageUi.Media.AudioDownloadHttpFailed", (int)response.StatusCode));
            if (!AllowedHost(finalHost))
                throw new CloudMusicException(L.T("StageUi.Media.AudioRedirectHostNotAllowed"));

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

            if (new FileInfo(tmp).Length == 0) throw new CloudMusicException(L.T("StageUi.Media.AudioDownloadEmpty"));
            File.Move(tmp, path, true);
            Log.Info("[Cloud] 音频已下载：" + path);
            return path;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CloudMusicException(L.T("StageUi.Media.AudioDownloadTimeout"));
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch { }
        }
    }
}
