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
    public class CloudException(string message) : Exception(message)
    {
        /// <summary>失败是"这首歌本身"造成的（平台上没有标识／无版权／链接为空），与"登录失效／断网"
        /// 这类整片失败区分开：前者直接跳过，后者连跳整张歌单没意义
        /// （见 CloudPlayService.SkipUnplayable）。</summary>
        public bool SongScoped { get; init; }
    }

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

    /// <summary>歌曲身份（下载记录与歌词缓存共用）：酷狗优先 Hash，拿不到退回 AlbumAudioId/Id；网易云用 Id。
    /// 空串＝这首歌没法标识，两边都退化成不记缓存。</summary>
    public static string IdentityOf(string source, KgSong song)
    {
        var id = source == "netease"
            ? song.Id
            : !string.IsNullOrWhiteSpace(song.Hash) ? song.Hash
            : !string.IsNullOrWhiteSpace(song.AlbumAudioId) ? song.AlbumAudioId
            : song.Id;
        return (id ?? "").Trim();
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
        var prepared = await PrepareAsync(state, source, song, quality, progress,
            // 歌词一拿到就先落进工程（老语义：音频装载失败时已写入的歌词也得排进 plan）
            lrc => state.Mutate(p => p.Lyrics = lrc, L.T("CloudSong.MutateLyrics"), replan: false), ct, applyToState: false);
        state.Mutate(p =>
        {
            p.Title = song.Name;
            p.Artist = song.Singer;
        }, L.T("CloudSong.MutateTitleArtist"), replan: false, look: true);

        state.SetSubtitles(prepared.Subtitles);
        if (prepared.LyricSource != null) state.SetLyricSource(prepared.LyricSource);
        state.SetAudio(prepared.Audio, prepared.Path, prepared.FileName); // 内部 ReplanAsync，歌词与音频一次排进 plan
        sw.Stop();
        Log.Info($"[Cloud] 已装载：{song.Name} - {song.Singer} → {prepared.Path}"
                 + $"（{J.FmtTime(prepared.Audio.Duration)}，{prepared.Audio.Bpm:0.0} BPM，{sw.ElapsedMilliseconds:#,0}ms{prepared.Note}）");
        return new CloudLoadResult(prepared.Path, prepared.FileName, prepared.Lrc, prepared.Note, prepared.Trial,
            prepared.Audio.Duration, prepared.Audio.Bpm, sw.ElapsedMilliseconds);
    }

    /// <summary>一首云歌"能渲染 PV"所需的全部素材。</summary>
    public sealed record PreparedSong(string Path, string FileName, bool Trial, string Lrc, string Note, AudioData Audio,
        List<KrcLine>? Subtitles = null, LyricSource? LyricSource = null);

    /// <summary>
    /// 把一首云歌准备成能渲染的 PV：下载（走下载记录/缓存）→ 取词（AdoptLyric 写字幕层与歌词来源，会话级）
    /// → 解码 + 节拍分析。**不写工程**——写不写歌词/曲名由调用方决定（装载要写、播放器模式预取不写）。
    /// onLyrics：歌词一就绪就回调（装载路径借它实现"音频失败也保住歌词"的老语义）。
    /// 分析失败抛 CloudException（此时歌词已通过 onLyrics/AdoptLyric 生效）。
    /// </summary>
    public static async Task<PreparedSong> PrepareAsync(EditorState state, string source, KgSong song,
        string quality, Action<string>? progress = null, Action<string>? onLyrics = null,
        CancellationToken ct = default, bool applyToState = false)
    {
        var identity = IdentityOf(source, song);
        var (path, fileName, trial, _) = await EnsureAudioFileAsync(source, song, quality, progress, ct);

        // 歌词：失败不拦音频（老版本同策略——有歌没词照样能做）
        string lrc = "";
        var lyricNote = "，没找到歌词";
        List<KrcLine>? subs = null;
        LyricSource? lyricSrc = null;
        try
        {
            progress?.Invoke(L.T("CloudSong.GetLyric"));
            ct.ThrowIfCancellationRequested();
            var ly = LyricCache.Get(source, identity);
            if (ly == null)
            {
                ly = source == "netease"
                    ? await ServerContext.Netease.GetLyricAsync(song.Id)
                    : await ServerContext.KuGou.GetLyricAsync(song.Hash, null, null, song.AlbumId);
                LyricCache.Put(source, identity, ly, song.AlbumId);
            }
            if (ly.Ok && !string.IsNullOrEmpty(ly.Content))
                (lrc, lyricNote, subs, lyricSrc) = AdoptLyric(applyToState ? state : null, source, ly, song.AlbumId, applyToState: applyToState);
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
        if (lrc.Length > 0) onLyrics?.Invoke(lrc);

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
            state.ReplanAsync(); // 歌词可能已写入：这里补一次重排
            throw new CloudException(L.F("CloudSong.LoadAudioFailed", ex.Message));
        }

        return new PreparedSong(path, fileName, trial, lrc, lyricNote, audio, subs, lyricSrc);
    }

    /// <summary>
    /// 装载/切换歌词：解析原始歌词 → 按当前音译/翻译勾选生成 JIZURA 语法的 LRC →
    /// 写入字幕层逐字数据 → 记下来源与候选版本（歌词面板的版本下拉读它）。
    /// 返回 (LRC 文本, 提示语, KRC 字幕行, 歌词来源)；调用方负责把 LRC 写进工程（装载是中间步，不能顺手 replan）。
    /// </summary>
    /// <param name="selected">当前选中的候选下标（-1 = 没走候选，直接用接口返回的那份）。</param>
    /// <param name="candidates">切换版本时沿用上一份候选（对位 Vue adoptKgLyric 的 keepCands）。</param>
    /// <param name="applyToState">是否直接更新 state 的字幕与歌词来源（预取时为 false 防污染当前正在播的歌）。</param>
    public static (string Lrc, string Note, List<KrcLine>? Subtitles, LyricSource Src) AdoptLyric(EditorState? state, string source, KgLyricResponse ly,
        string albumId, int selected = -1, List<KgLyricCandidate>? candidates = null, bool applyToState = true)
    {
        var wantRoma = App.SettingsOf()?.WantRoma ?? true;
        var wantTrans = App.SettingsOf()?.WantTrans ?? true;
        LyricSource src;
        string note;
        if (source == "netease")
        {
            var lrcRows = Krc.NeParseLrc(Krc.B64Text(ly.Content!));
            var trans = string.IsNullOrEmpty(ly.Trans) ? new List<Krc.NeRow>() : Krc.NeParseLrc(Krc.B64Text(ly.Trans));
            var roma = string.IsNullOrEmpty(ly.Roma) ? new List<Krc.NeRow>() : Krc.NeParseLrc(Krc.B64Text(ly.Roma));
            src = new LyricSource { Source = source, Fmt = "ne", NeRaw = (lrcRows, trans, roma) };
            note = trans.Count > 0 || roma.Count > 0 ? "，歌词已填入（含注音）" : "，歌词已填入（无音译/翻译）";
        }
        else
        {
            var cands = candidates ?? ly.Candidates ?? new List<KgLyricCandidate>();
            if (ly.Fmt == "krc")
            {
                src = new LyricSource
                {
                    Source = source, Fmt = "krc", Text = Krc.Decrypt(Convert.FromBase64String(ly.Content!)),
                    Candidates = cands, Selected = selected, AlbumId = albumId
                };
                note = "，歌词已填入";
            }
            else
            {
                src = new LyricSource
                {
                    Source = source, Fmt = "lrc", Text = Krc.B64Text(ly.Content!),
                    Candidates = cands, Selected = selected, AlbumId = albumId
                };
                note = "，歌词已填入（普通 LRC，无音译/翻译）";
            }
        }

        // KRC 逐字 / LRC 时间戳 → 字幕层数据（预览叠加/主通道烤字幕/Spout 字幕通道消费）
        List<KrcLine>? subs = null;
        if (source == "kugou")
        {
            try
            {
                subs = Krc.ParseKgLyric(ly.Fmt, ly.Content)
                       ?? (string.IsNullOrEmpty(ly.Content) ? null : Krc.ParseLrc(Krc.B64Text(ly.Content)));
            }
            catch (Exception ex)
            {
                Log.Debug("[Cloud] 酷狗歌词解析失败（字幕层跳过）：" + ex.Message);
            }
        }
        else if (source == "netease" && !string.IsNullOrEmpty(ly.Content))
        {
            try
            {
                subs = Krc.ParseLrc(Krc.B64Text(ly.Content!));
            }
            catch (Exception ex)
            {
                Log.Debug("[Cloud] 网易云歌词解析失败（字幕层跳过）：" + ex.Message);
            }
        }

        if (applyToState && state != null)
        {
            state.SetSubtitles(subs);
            state.SetLyricSource(src);
        }

        return (src.BuildLrc(wantRoma, wantTrans), note, subs, src);
    }

    /// <summary>
    /// 音译/翻译勾选变化后立即重生成歌词（对位 Vue applyLyricOptions）：用留存的原始歌词文本
    /// 重新生成，不必重新装载整首歌。
    /// </summary>
    public static void ReapplyLyricOptions(EditorState state)
    {
        var src = state.LyricSrc;
        if (src == null) return;
        var wantRoma = App.SettingsOf()?.WantRoma ?? true;
        var wantTrans = App.SettingsOf()?.WantTrans ?? true;
        state.Mutate(p => p.Lyrics = src.BuildLrc(wantRoma, wantTrans), L.T("CloudSong.MutateLyrics"), replan: true);
    }

    /// <summary>
    /// 切换歌词版本（对位 Vue switchVersion）：按候选 id 重新取词 → 重生成歌词与字幕。
    /// 返回给用户看的成功提示；失败抛 CloudException。必须在 UI 线程调用。
    /// </summary>
    public static async Task<string> SwitchVersionAsync(EditorState state, int idx)
    {
        var src = state.LyricSrc;
        if (src == null || idx < 0 || idx >= src.Candidates.Count) return "";
        var c = src.Candidates[idx];
        // 候选版本按「候选 id」单独缓存（跟歌曲身份不是一个键空间），切回切过的版本不再请求接口
        var key = LyricCache.CandidatePrefix + c.Id;
        var ly = LyricCache.Get("kugou", key);
        if (ly == null)
        {
            ly = await ServerContext.KuGou.GetLyricAsync(null, c.Id, c.AccessKey, src.AlbumId);
            LyricCache.Put("kugou", key, ly, src.AlbumId);
        }
        if (!ly.Ok || string.IsNullOrEmpty(ly.Content))
            throw new CloudException(ly.Error ?? L.T("CloudSong.NoLyric"));
        var (lrc, _, _, _) = AdoptLyric(state, src.Source, ly, src.AlbumId, idx, src.Candidates);
        state.Mutate(p => p.Lyrics = lrc, L.T("CloudSong.MutateLyrics"), replan: true);
        // KRC 解出来却没有时间轴 = 这份词不是逐字格式（解密成功但内容不是歌词）
        var bad = ly.Fmt == "krc" && !Regex.IsMatch(lrc, @"^\[\d\d:\d\d[.:]", RegexOptions.Multiline);
        if (bad) return L.T("Lyrics.Version.BadFmt");
        return L.F("Lyrics.Version.Switched", LyricSource.Label(c))
               + (ly.Fmt == "lrc" ? L.T("Lyrics.Version.PlainLrc") : "");
    }

    // ---------------- 音频下载 ----------------

    /// <summary>
    /// 装载与试听共用的第一步：拿到这首歌的本地音频文件（不碰工程、不解码分析）。
    /// 下载记录命中 → 连 URL 接口都不请求；否则 URL 接口 + 下载并记账。
    /// 返回 (文件路径, 缓存文件名, 是否试听片段, 是否来自缓存)。
    /// </summary>
    public static async Task<(string Path, string FileName, bool Trial, bool FromCache)> EnsureAudioFileAsync(
        string source, KgSong song, string quality, Action<string>? progress = null,
        CancellationToken ct = default)
    {
        var identity = IdentityOf(source, song);

        // 歌单里的空条目（名字/哈希皆空）连 URL 接口都没法问：立即失败，别走完整条接口重试链
        // （实测一首能卡近一分钟，最后报的还是"登录可能已失效"——既慢又误导）
        if (identity.Length == 0)
            throw new CloudException(L.T("CloudSong.NoIdentity")) { SongScoped = true };

        // 下载记录先行：同一首歌 + 同一音质已经下好，连 URL 接口都不请求（离线、VIP 状态变化都能重载）
        var rec = AudioIndex.Get(source, identity, quality);
        if (rec != null)
        {
            progress?.Invoke(L.T("CloudSong.CacheHit"));
            Log.Info($"[Cloud] 命中下载记录：{rec.File}（{source}:{identity}:{quality}）");
            return (AudioIndex.FilePath(rec.File)!, rec.File, false, true);
        }

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
        var fileName = AudioIndex.ResolveFileName(source, identity, song.Singer, song.Name, ext);
        var trial = u.Trial == true;
        // 同一首歌的旧记录：换了音质（128k/320k 都落 mp3、文件名相同）、或上次只下到试听片段而现在能拿
        // 完整音轨 → 同名文件不能沿用，重下覆盖；其余情况沿用「同名文件存在即复用」的老口径（兼容老缓存）
        var prev = AudioIndex.GetAny(source, identity);
        progress?.Invoke(L.T("CloudSong.Download"));
        var path = await DownloadAudioAsync(u.Url, fileName, ct, AudioIndex.CanReuseExisting(prev, quality, trial));
        AudioIndex.Put(source, identity, quality, fileName, new FileInfo(path).Length, ext, trial,
            song.Name, song.Singer);
        return (path, fileName, trial, false);
    }

    /// <summary>
    /// 下载音频到缓存目录（同名复用已有文件；大小&gt;0 视为有效）。域名白名单 + 重定向后复检 +
    /// 握手 30s / 读停滞 60s 看门狗（与旧版 AudioProxy 同套纪律，流落盘版）。
    /// reuseExisting=false 时忽略已存在的同名文件强制重下（换音质 / 试听片段升级为完整音轨）。
    /// </summary>
    public static async Task<string> DownloadAudioAsync(string url, string fileName, CancellationToken ct,
        bool reuseExisting = true)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !AllowedHost(uri.Host))
            throw new CloudException(L.T("CloudSong.BadUrl"));

        Directory.CreateDirectory(CacheDir);
        var path = Path.Combine(CacheDir, fileName);
        if (reuseExisting && File.Exists(path) && new FileInfo(path).Length > 0)
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
