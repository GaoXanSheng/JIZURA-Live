using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using JizuraLive.App.I18n;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Server;
using JizuraLive.Server.Models;

namespace JizuraLive.App.Cloud;

/// <summary>歌曲装载结果（成功路径）。</summary>
public sealed record CloudLoadResult(string AudioPath, string FileName, string LrcText, string LyricNote,
    bool Trial, double Duration, double Bpm, long AnalysisMs);

/// <summary>
/// 云端歌曲装载（对位 frontend/src/cloud/songLoader.ts + api.ts 的 url/audio/lyric 三步）：
/// 播放链接 → 音频下载（域名白名单 + 停滞看门狗）→ %LOCALAPPDATA%\JizuraLive\cloud\ 缓存文件
/// → AudioAnalyzer 分析 → State.SetAudio；歌词（KRC 解密 / LRC / 网易 LRC+翻译音译）→
/// State.Mutate 写 project.Lyrics；曲名/歌手写入工程。
/// 无 WPF 依赖：约定 LoadAsync 从 UI 线程发起（await 回 UI 线程后再碰 EditorState），
/// 网络/解码/分析全部在 Task.Run。失败抛 CloudException（Message 即用户可读提示）。
/// </summary>
public static class SongLoader
{
    /// <summary>带用户可读消息的失败（区别于意外异常）。</summary>
    public class CloudException(string message) : Exception(message);

    // 与旧版 AudioProxy 同一套白名单：只从这些域下载音频，防 url 接口被滥用当通用下载器
    private static readonly string[] HostSuffixes = ["kugou.com", "kgimg.com", "kglink.cn", "music.126.net"];

    private static bool AllowedHost(string host) =>
        HostSuffixes.Any(s => string.Equals(host, s, StringComparison.OrdinalIgnoreCase)
                              || host.EndsWith("." + s, StringComparison.OrdinalIgnoreCase));

    private static readonly HttpClient StreamHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>云音乐缓存目录（音频按「歌手 - 歌名」落盘，重复装载复用）。</summary>
    public static string CacheDir => AppDirs.CloudDir;

    // ---------------- 文件名 ----------------

    /// <summary>Windows 文件名非法字符与控制字符 → 空格；收尾的点/空格剥掉（目录 API 不收）。</summary>
    public static string SanitizeFileName(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(ch is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' ? ' '
                : char.IsControl(ch) ? ' ' : ch);
        var s = Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
        s = s.TrimEnd('.', ' ');
        if (s.Length == 0) s = "untitled";
        // Win32 保留设备名（CON/NUL/COM1…）单独会解析成设备
        var stem = s.Split(' ')[0].ToUpperInvariant();
        if (Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$")) s = "_" + s;
        return s;
    }

    /// <summary>缓存音频文件名：「歌手 - 歌名.ext」；歌手为空退化为「歌名.ext」。</summary>
    public static string AudioFileName(string singer, string name, string ext)
    {
        var stem = SanitizeFileName(string.IsNullOrWhiteSpace(singer) ? name : $"{singer} - {name}");
        ext = Regex.Replace(ext ?? "", @"[^a-z0-9]", "", RegexOptions.IgnoreCase).ToLowerInvariant();
        if (ext.Length == 0) ext = "mp3";
        return stem + "." + ext;
    }

    // ---------------- 装载主流程 ----------------

    /// <summary>
    /// 装载一首云端歌曲：url → 下载缓存 → 歌词转换 → 音频分析 → 写工程（歌词/曲名/歌手）+ SetAudio。
    /// progress 收人类步骤文案（搜索链接中…/下载中…/分析中…）。必须在 UI 线程调用。
    /// 音频任一步失败都先保留已取得的歌词（写入工程后 ReplanAsync 排进 plan），再向上抛错。
    /// </summary>
    public static async Task<CloudLoadResult> LoadAsync(EditorState state, string source, KgSong song,
        string quality, Action<string>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        progress?.Invoke(L.T("CloudSong.GetUrl"));
        var u = source == "netease"
            ? await ServerContext.Netease.GetUrlAsync(song.Id, quality)
            : await ServerContext.KuGou.GetUrlAsync(song.Hash, song.AlbumId, song.AlbumAudioId,
                int.TryParse(quality, out var q) ? q : 128);
        if (!u.Ok || string.IsNullOrEmpty(u.Url))
        {
            var name = source == "netease" ? L.T("Cloud.SourceNetease") : L.T("Cloud.SourceKugou");
            throw new CloudException(u.Error
                                     ?? (u.NeedVip == true
                                         ? L.F("CloudSong.NeedVip", name)
                                         : L.T("CloudSong.NoUrl")));
        }

        var ext = (u.Ext ?? "mp3").ToLowerInvariant();
        var fileName = AudioFileName(song.Singer, song.Name, ext);

        progress?.Invoke(L.T("CloudSong.Download"));
        var path = await DownloadAudioAsync(u.Url, fileName, ct);

        // 歌词：失败不拦音频（老版本同策略——有歌没词照样能做）
        string lrc = "";
        var lyricNote = "，没找到歌词";
        try
        {
            progress?.Invoke(L.T("CloudSong.GetLyric"));
            ct.ThrowIfCancellationRequested();
            var ly = source == "netease"
                ? await ServerContext.Netease.GetLyricAsync(song.Id)
                : await ServerContext.KuGou.GetLyricAsync(song.Hash, null, null, song.AlbumId);
            if (ly.Ok && !string.IsNullOrEmpty(ly.Content))
            {
                (lrc, lyricNote) = ConvertLyric(source, ly);
                // KRC 逐字 → 字幕层数据（预览叠加/导出烤字幕/Spout 字幕通道消费）；
                // 网易云 LRC 与酷狗普通 LRC 都无逐字 → 清空避免残留
                if (source == "kugou")
                {
                    try { state.SetSubtitles(Krc.ParseKgLyric(ly.Fmt, ly.Content)); }
                    catch (Exception ex) { Log.Debug("[Cloud] KRC 逐字解析失败（字幕层跳过）：" + ex.Message); }
                }
                else
                {
                    state.SetSubtitles(null);
                }
            }
        }
        catch (CloudException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Debug("[Cloud] 歌词加载失败（继续装载音频）：" + ex.Message);
            lyricNote = "，歌词加载失败";
        }

        // 写工程（音频装载成功与否都要落：老版本音频失败时已写入的歌词也得排进 plan）
        if (lrc.Length > 0)
            state.Mutate(p => p.Lyrics = lrc, L.T("CloudSong.MutateLyrics"), replan: false);
        state.Mutate(p =>
        {
            p.Title = song.Name;
            p.Artist = song.Singer;
        }, L.T("CloudSong.MutateTitleArtist"), replan: false, look: true);

        progress?.Invoke(L.T("CloudSong.Analyze"));
        AudioData audio;
        try
        {
            audio = await Task.Run(() =>
            {
                var mono = AudioAnalyzer.DecodeMonoAnalyzeRate(path, out var duration);
                var res = AudioAnalyzer.Analyze(mono, AudioAnalyzer.AnalyzeRate, duration);
                return new AudioData
                {
                    Duration = duration,
                    Bpm = res.Bpm,
                    Beats = res.Beats,
                    Energy = res.Energy,
                    EnergyRate = res.EnergyRate,
                    Peaks = res.Peaks,
                    Pitch = res.Pitch,
                    PitchRate = res.PitchRate,
                };
            }, ct);
        }
        catch (Exception ex)
        {
            state.ReplanAsync(); // 歌词已写入但音频装载失败：这里补一次重排
            throw new CloudException(L.F("CloudSong.LoadAudioFailed", ex.Message));
        }

        state.SetAudio(audio, path, fileName); // 内部 ReplanAsync，歌词与音频一次排进 plan
        sw.Stop();
        Log.Info($"[Cloud] 已装载：{song.Name} - {song.Singer} → {path}"
                 + $"（{J.FmtTime(audio.Duration)}，{audio.Bpm:0.0} BPM，{sw.ElapsedMilliseconds:#,0}ms{lyricNote}）");
        return new CloudLoadResult(path, fileName, lrc, lyricNote, u.Trial == true,
            audio.Duration, audio.Bpm, sw.ElapsedMilliseconds);
    }

    /// <summary>KRC 解密 → JIZURA LRC；LRC 直读；网易云 LRC + 翻译/音译按时间戳对齐。
    /// 返回 (LRC 文本, 提示语)。音译/翻译由设置里的 WantRoma/WantTrans 决定（歌词面板的勾选框）。</summary>
    public static (string Lrc, string Note) ConvertLyric(string source, KgLyricResponse ly)
    {
        var wantRoma = App.SettingsOf()?.WantRoma ?? true;
        var wantTrans = App.SettingsOf()?.WantTrans ?? true;
        if (source == "netease")
        {
            var lrc = Krc.NeParseLrc(Krc.B64Text(ly.Content!));
            List<Krc.NeRow> trans = string.IsNullOrEmpty(ly.Trans) ? [] : Krc.NeParseLrc(Krc.B64Text(ly.Trans));
            List<Krc.NeRow> roma = string.IsNullOrEmpty(ly.Roma) ? [] : Krc.NeParseLrc(Krc.B64Text(ly.Roma));
            var text = Krc.NeBuildLrc(lrc, trans, roma, wantRoma, wantTrans);
            var note = trans.Count > 0 || roma.Count > 0 ? "，歌词已填入（含注音）" : "，歌词已填入（无音译/翻译）";
            return (text, note);
        }

        var bin = Convert.FromBase64String(ly.Content!);
        if (ly.Fmt == "krc")
        {
            var krcText = Krc.Decrypt(bin);
            return (Krc.KrcToLrc(krcText, wantRoma, wantTrans), "，歌词已填入");
        }

        return (Krc.B64Text(ly.Content!), "，歌词已填入（普通 LRC，无音译/翻译）");
    }

    // ---------------- 音频下载 ----------------

    /// <summary>
    /// 下载音频到缓存目录（同名复用已有文件；大小&gt;0 视为有效）。域名白名单 + 重定向后复检 +
    /// 握手 30s / 读停滞 60s 看门狗（与旧版 AudioProxy 同套纪律，流落盘版）。
    /// </summary>
    public static async Task<string> DownloadAudioAsync(string url, string fileName, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !AllowedHost(uri.Host))
            throw new CloudException(L.T("CloudSong.BadUrl"));

        Directory.CreateDirectory(CacheDir);
        var path = Path.Combine(CacheDir, fileName);
        if (File.Exists(path) && new FileInfo(path).Length > 0)
        {
            Log.Info("[Cloud] 命中缓存：" + fileName);
            return path;
        }

        var tmp = path + ".part";
        try
        {
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(TimeSpan.FromSeconds(30));
            using var watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var upstream = await StreamHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
                handshakeCts.Token);

            // HttpClient 默认跟随重定向，跳转可能落到白名单之外，以最终请求 URI 复检
            var finalHost = upstream.RequestMessage?.RequestUri?.Host ?? uri.Host;
            if (!upstream.IsSuccessStatusCode)
                throw new CloudException(L.F("CloudSong.HttpStatus", (int)upstream.StatusCode));
            if (!AllowedHost(finalHost))
                throw new CloudException(L.T("CloudSong.RedirectBad"));

            await using (var src = await upstream.Content.ReadAsStreamAsync(watchdogCts.Token))
            {
                await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920))
                {
                    var buffer = new byte[81920];
                    while (true)
                    {
                        watchdogCts.CancelAfter(TimeSpan.FromSeconds(60));
                        var read = await src.ReadAsync(buffer, watchdogCts.Token);
                        if (read == 0) break;
                        await dst.WriteAsync(buffer.AsMemory(0, read), watchdogCts.Token);
                    }
                }
            }

            if (new FileInfo(tmp).Length == 0) throw new CloudException(L.T("CloudSong.EmptyContent"));
            File.Move(tmp, path, true); // 覆盖同名旧缓存
            return path;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new CloudException(L.T("CloudSong.DownloadTimeout"));
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch { /* 并发装载同名歌时的竞态，无害 */ }
        }
    }
}
