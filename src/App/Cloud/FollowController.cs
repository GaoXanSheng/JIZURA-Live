using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using JizuraLive.App.I18n;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Server;
using JizuraLive.Server.Models;
using JizuraLive.Server.Smtc;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.App.Cloud;

/// <summary>
/// SMTC 跟随外部播放器（对位 frontend/src/cloud/follow.ts）：
/// 每 300ms 轮询 SmtcService 快照 → PreviewEngine.FollowAnchor 重锚定本地时钟（外部播放器出声，
/// 本地只同步进度渲染歌词）；外部暂停 → 引擎暂停；切歌检测（元数据连续稳定 2s 去抖）→
/// 酷狗 KRC 优先 / 网易云 LRC 兜底自动匹配歌词写入工程，匹配结果落盘缓存。
/// 全部状态变更都在 UI 线程（Dispatcher），网络在后台；Changed 通知面板刷新。
/// </summary>
public sealed class FollowController : IDisposable
{
    private const int PollMs = 300;

    /// <summary>元数据要连续稳定 2 秒才匹配：播放器切歌时常分多次更新（先清空再填新值），
    /// 立刻搜会搜到上一首。</summary>
    private const int DebounceMs = 2000;

    // 状态字面量与旧版 FollowStatus 一致，面板按同款文案渲染
    public const string StOff = "off";
    public const string StNoSession = "no-session";
    public const string StNoMeta = "no-meta";
    public const string StMatching = "matching";
    public const string StOk = "ok";
    public const string StNoMatch = "no-match";
    public const string StError = "error";

    private sealed record CacheEntry(string Source, KgSong Song, long Ts);

    private readonly Dispatcher _dispatcher;
    private readonly EditorState _state;
    private readonly SmtcService _smtc;

    private int _on;
    private CancellationTokenSource? _cts;
    private PreviewEngine Engine => _state.Engine;

    // ===== 状态（UI 线程读写，面板经属性读）=====
    public bool On => Volatile.Read(ref _on) == 1;
    public string Status { get; private set; } = StOff;
    public string Detail { get; private set; } = "";
    public string AppName { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string Artist { get; private set; } = "";
    public string MatchedAs { get; private set; } = "";
    public string Fmt { get; private set; } = ""; // '' | krc | lrc | ne
    public bool Estimated { get; private set; }
    public bool AutoSelected { get; private set; } = true;
    public string SelectedAppId { get; private set; } = "";
    public IReadOnlyList<SmtcService.SessionSnapshot> Sessions { get; private set; } = [];

    /// <summary>手动对齐偏移（秒），正数 = 歌词时间轴向前赶。</summary>
    public double Offset { get; private set; }

    /// <summary>跟随状态变化（UI 线程触发；面板据此刷新）。</summary>
    public event Action? Changed;

    private LoopMode _savedLoop = LoopMode.All;
    private string _candidateKey = "";
    private DateTime _candidateAt;
    private string _matchedKey = "";
    private int _matchSeq;
    private string _lastSrtcStatus = "";

    // 跟随期间顶栏就是外部曲目：覆盖工程 Title/Artist 前先存一份原值，停止跟随时还回去
    private bool _titleSaved;
    private string _savedTitle = "";
    private string _savedArtist = "";

    private static readonly string CloudDir = Path.Combine(AppDirs.Root, "cloud");

    /// <summary>跟随匹配缓存 / 对齐偏移的文件名（清云缓存时按名保留：偏移是用户手调的）。</summary>
    public const string CacheFileName = "follow_cache.json";
    public const string StateFileName = "follow_state.json";

    private static readonly string CacheFile = Path.Combine(CloudDir, CacheFileName);
    private static readonly string StateFile = Path.Combine(CloudDir, StateFileName);
    private readonly object _cacheLock = new();
    private Dictionary<string, CacheEntry> _cache = new();

    public FollowController(Dispatcher dispatcher, EditorState state)
    {
        _dispatcher = dispatcher;
        _state = state;
        _smtc = ServerContext.Smtc;
        LoadPersisted();
    }

    private void Notify() => Changed?.Invoke();

    private void LoadPersisted()
    {
        try
        {
            if (File.Exists(StateFile))
            {
                var j = JObject.Parse(File.ReadAllText(StateFile));
                Offset = j.Value<double?>("offset") ?? 0;
            }

            if (File.Exists(CacheFile))
            {
                var j = JObject.Parse(File.ReadAllText(CacheFile));
                foreach (var p in j.Properties())
                {
                    var e = p.Value;
                    var song = e["song"]?.ToObject<KgSong>();
                    if (song != null)
                        _cache[p.Name] = new CacheEntry(e.Value<string?>("source") ?? "kugou", song,
                            (long)(e.Value<double?>("ts") ?? 0));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("[Follow] 跟随缓存读取失败（忽略）：" + ex.Message);
        }
    }

    private void PersistOffset()
    {
        try
        {
            Directory.CreateDirectory(CloudDir);
            AtomicFile.Write(StateFile, new JObject { ["offset"] = Offset }.ToString(Formatting.Indented));
        }
        catch (Exception ex)
        {
            Log.Debug("[Follow] 对齐偏移落盘失败：" + ex.Message);
        }
    }

    private void CachePut(string key, CacheEntry e)
    {
        lock (_cacheLock)
        {
            _cache[key] = e;
            if (_cache.Count > 300)
                foreach (var k in _cache.OrderBy(kv => kv.Value.Ts).Take(_cache.Count - 300).Select(kv => kv.Key)
                             .ToList())
                    _cache.Remove(k);
            try
            {
                var j = new JObject();
                foreach (var kv in _cache)
                    j[kv.Key] = new JObject
                    {
                        ["source"] = kv.Value.Source,
                        ["song"] = JObject.FromObject(kv.Value.Song),
                        ["ts"] = kv.Value.Ts
                    };
                Directory.CreateDirectory(CloudDir);
                AtomicFile.Write(CacheFile, j.ToString(Formatting.None));
            }
            catch (Exception ex)
            {
                Log.Debug("[Follow] 跟随缓存落盘失败：" + ex.Message);
            }
        }
    }

    private CacheEntry? CacheGet(string key)
    {
        lock (_cacheLock) return _cache.GetValueOrDefault(key);
    }

    // ---------------- 开关 ----------------

    public void Start()
    {
        if (On) return;
        Volatile.Write(ref _on, 1);
        Status = StNoSession;
        Detail = "";
        _matchedKey = "";
        _candidateKey = "";
        _lastSrtcStatus = "";
        _titleSaved = false;
        _matchSeq++;
        _dispatcher.Invoke(() =>
        {
            Engine.Pause(); // 跟随期间外部播放器出声：本地音频先停（不销毁链，退出跟随可手动续播）
            _savedLoop = Engine.Loop;
            Engine.SetLoop(LoopMode.Off); // 循环会和外部播放器的真实进度打架
        });
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => LoopAsync(_cts.Token));
        Log.Info("[Follow] 跟随已开启：正在检测播放器…");
        Notify();
    }

    public void Stop()
    {
        if (!On) return;
        Volatile.Write(ref _on, 0);
        _matchSeq++; // 进行中的匹配作废
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        _dispatcher.Invoke(() =>
        {
            Engine.FollowEnd(); // 时间轴冻结在当前位置
            Engine.SetLoop(_savedLoop);
            RestoreTitle();
        });
        Status = StOff;
        Detail = "";
        Log.Info("[Follow] 跟随已停止（当前歌词保留在工程里）");
        Notify();
    }

    public void Toggle()
    {
        if (On) Stop();
        else Start();
    }

    /// <summary>匹配失败/匹配错歌后重试自动匹配（清掉已匹配标记，下一轮轮询重新走去抖匹配）。</summary>
    public void Rematch()
    {
        if (!On) return;
        _matchedKey = "";
        _candidateKey = "";
        _candidateAt = default;
        Notify();
    }

    /// <summary>手动对齐微调（正数=歌词提前）。</summary>
    public void Nudge(double delta)
    {
        Offset = Math.Round(Offset + delta, 1);
        PersistOffset();
        Notify();
    }

    /// <summary>指定跟随的播放器（null/空 = 自动挑选）。</summary>
    public void SelectSession(string? appId) => _smtc.Select(string.IsNullOrWhiteSpace(appId) ? null : appId);

    // ---------------- 轮询 ----------------

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && On)
        {
            try
            {
                var snap = _smtc.GetState(); // 内部全锁保护，后台线程可调
                if (!On) return;
                await _dispatcher.InvokeAsync(() => Apply(snap), DispatcherPriority.Background);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Debug("[Follow] 轮询异常：" + ex.Message);
            }

            try
            {
                await Task.Delay(PollMs, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    // ---------------- 状态应用（UI 线程） ----------------

    private void Apply(SmtcService.StateSnapshot st)
    {
        if (!On) return;
        AutoSelected = st.AutoSelected;
        SelectedAppId = st.SelectedAppId ?? "";
        Sessions = st.Sessions;

        if (!st.Ok)
        {
            Status = StError;
            Detail = st.Error ?? L.T("CloudFollow.SmtcUnavailable");
            Notify();
            return;
        }

        var cur = st.Current;
        if (cur == null || (cur.Title.Length == 0 && cur.Artist.Length == 0))
        {
            // 没有会话/会话没有元数据：暂停渲染时间轴，歌词画面停在原地
            if (Engine.IsPlaying) Engine.FollowAnchor(Engine.CurrentTime, false);
            Status = cur != null ? StNoMeta : StNoSession;
            Detail = cur != null ? "" : L.T("CloudFollow.NoPlayer");
            Notify();
            return;
        }

        AppName = cur.AppName;
        Title = cur.Title;
        Artist = cur.Artist;
        PushExternalTitle(Title, Artist);

        // 切歌检测（去抖）：新歌一露头立刻冻结时间轴并清掉旧歌的匹配信息，
        // 元数据连续稳定 2 秒后才发起匹配
        var key = Norm(Title) + "|" + Norm(Artist);
        if (key != _matchedKey)
        {
            if (key != _candidateKey)
            {
                _candidateKey = key;
                _candidateAt = DateTime.UtcNow;
                if (Engine.IsPlaying) Engine.FollowAnchor(Engine.CurrentTime, false);
                Status = StMatching;
                Detail = "";
                MatchedAs = "";
                Fmt = "";
            }
            else if ((DateTime.UtcNow - _candidateAt).TotalMilliseconds >= DebounceMs && Status == StMatching)
            {
                _matchedKey = key;
                _ = MatchAndAdoptAsync(cur);
            }
        }

        // 匹配中冻结时间轴（旧歌词不能跟着新歌跑）；匹配失败也继续跟——
        // 用户手动在搜索页载入歌词后无缝接上
        if (Status == StMatching)
        {
            Notify();
            return;
        }

        Estimated = cur.Estimated;
        // 估算进度在歌播完到切歌之间会继续走，钳到 plan 结束附近防时间轴越跑越远
        var planDur = _state.Plan?.Duration ?? 0;
        var liveSec = Math.Min(cur.PositionLiveMs / 1000.0 + Offset, planDur + 1);
        // 估算模式兜底：单曲循环重播同一首时 SMTC 元数据不变，服务端无从感知；
        // 用「暂停→播放且时间轴已停在结尾」判定为重播/换曲，归零从头
        if (Estimated && cur.Status == "Playing" && _lastSrtcStatus.Length > 0 && _lastSrtcStatus != "Playing"
            && Engine.CurrentTime >= planDur - 0.5)
            liveSec = 0;
        _lastSrtcStatus = cur.Status;
        Engine.FollowAnchor(liveSec, cur.Status == "Playing");
        Notify();
    }

    // ---------------- 顶栏曲名/歌手 ----------------

    /// <summary>
    /// 跟随期间顶栏（以及 HUD / 导出用的工程 Title/Artist）显示外部曲目：与上一首不同的外部歌名
    /// 就写进工程；第一次覆盖前把原值存下来，停止跟随时还回去。写的是播放器给的原文，不做标题清洗
    /// （清洗只用于匹配搜索）。
    /// </summary>
    private void PushExternalTitle(string title, string artist)
    {
        var p = _state.Project;
        if (p.Title == title && p.Artist == artist) return;
        if (!_titleSaved)
        {
            _savedTitle = p.Title;
            _savedArtist = p.Artist;
            _titleSaved = true;
        }
        _state.Mutate(q =>
        {
            q.Title = title;
            q.Artist = artist;
        }, L.T("CloudFollow.MutateTitleArtist"), replan: true, look: true);
    }

    /// <summary>停止跟随：把被外部曲目顶掉的曲名/歌手还回工程。</summary>
    private void RestoreTitle()
    {
        if (!_titleSaved) return;
        _titleSaved = false;
        var (title, artist) = (_savedTitle, _savedArtist);
        var p = _state.Project;
        if (p.Title == title && p.Artist == artist) return;
        _state.Mutate(q =>
        {
            q.Title = title;
            q.Artist = artist;
        }, L.T("CloudFollow.MutateTitleArtist"), replan: true, look: true);
    }

    // ---------------- 匹配 ----------------

    private static readonly string[] BadWords =
    [
        "live", "伴奏", "inst", "karaoke", "cover", "remix", "翻唱", "现场", "concert", "纯音乐", "dj版",
        "instrumental"
    ];

    public static string Norm(string s) =>
        Regex.Replace((s ?? "").ToLowerInvariant(), @"[\s\p{P}\p{S}]+", "");

    /// <summary>SMTC 标题常带垃圾后缀（(Official Video)/【MV】…），匹配前剥掉；只剥含 junk 词的括段。</summary>
    public static string CleanTitle(string t) => Regex.Replace(t ?? "",
        @"[（(【\[][^）)】\]]*[）)】\]]",
        m => Regex.IsMatch(m.Value, @"official|mv|lyric|audio|video|歌词|高清|完整版|hq|4k", RegexOptions.IgnoreCase)
            ? ""
            : m.Value).Trim();

    /// <summary>播放器没给歌手时的兜底：标题「A - B」不知道哪边是歌手——两种顺序都交给匹配去试。</summary>
    public static (string Title, string Artist)[] TitleArtistCandidates(string title, string artist)
    {
        if (!string.IsNullOrEmpty(artist) || string.IsNullOrEmpty(title)) return [(title, artist)];
        var m = Regex.Match(title, @"^(.+?)\s*[-–—]\s*(.+)$");
        if (!m.Success) return [(title, artist)];
        var a = m.Groups[1].Value.Trim();
        var b = m.Groups[2].Value.Trim();
        return a.Length > 0 && b.Length > 0 ? [(b, a), (a, b)] : [(title, artist)];
    }

    /// <summary>多歌手拆分：「A/B」「A、B」「A&B」「A feat. B」拆成部件匹配，别拿整串互 include。</summary>
    public static string[] ArtistParts(string s) =>
        Regex.Split(s ?? "", @"[/、,，&]|feat\.?|featuring", RegexOptions.IgnoreCase)
            .Select(Norm).Where(x => x.Length > 0).ToArray();

    public static bool ArtistMatches(string queryArtist, string resultSinger)
    {
        var qs = ArtistParts(queryArtist);
        var ss = ArtistParts(resultSinger);
        return qs.Length > 0 && ss.Length > 0 &&
               qs.Any(a => ss.Any(s => s == a || s.Contains(a, StringComparison.Ordinal) ||
                                       a.Contains(s, StringComparison.Ordinal)));
    }

    /// <summary>
    /// 从搜索结果里挑最像的：标题/歌手相似度为主，曲目时长接近 SMTC EndTime 是强信号；
    /// Live/伴奏/翻唱等扣分。分数太低宁可返回 null 让用户手动搜，错词比没词糟糕。
    /// 歌手是硬把关：同名歌不同歌手（翻唱/DJ版/remix）是「歌词对不上」的头号来源。
    /// </summary>
    public static KgSong? PickSong(IReadOnlyList<KgSong> list, string title, string artist, double durSec)
    {
        var tn = Norm(title);
        var an = Norm(artist);
        KgSong? best = null;
        var bestScore = -1e9;
        for (var i = 0; i < list.Count; i++)
        {
            var it = list[i];
            var n = Norm(it.Name);
            var sg = Norm(it.Singer);
            var sc = i * -0.5;
            if (n == tn) sc += 60;
            else if (n.Length > 0 && (n.Contains(tn, StringComparison.Ordinal) || tn.Contains(n, StringComparison.Ordinal)))
                sc += 30;
            var artistHit = an.Length > 0 && ArtistMatches(artist, it.Singer);
            if (artistHit) sc += 30;
            else if (an.Length > 0 && sg.Length > 0) sc -= 45; // 沾不上边：同名不同歌手，大概率不是这首歌的词
            foreach (var bad in BadWords)
                if (n.Contains(bad, StringComparison.Ordinal))
                    sc -= 25;
            if (durSec > 0 && it.Duration > 0)
            {
                var d = Math.Abs(it.Duration - durSec);
                if (d <= 3) sc += 20;
                else if (d <= 10) sc += 8;
                else if (d > 30) sc -= 10;
            }

            if (sc > bestScore)
            {
                bestScore = sc;
                best = it;
            }
        }

        return bestScore >= 30 ? best : null;
    }

    private async Task<CacheEntry?> FindSongAsync(string title, string artist, double durSec)
    {
        var key = Norm(title) + "|" + Norm(artist);
        var hit = CacheGet(key);
        if (hit != null) return hit;
        foreach (var source in new[] { "kugou", "netease" })
        {
            try
            {
                var j = source == "kugou"
                    ? await ServerContext.KuGou.SearchAsync($"{title} {artist}".Trim(), 1)
                    : await ServerContext.Netease.SearchAsync($"{title} {artist}".Trim(), 1);
                if (!j.Ok) continue;
                var pick = PickSong(j.List, title, artist, durSec);
                if (pick != null)
                {
                    var e = new CacheEntry(source, pick, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                    CachePut(key, e);
                    return e;
                }
            }
            catch
            {
                // 这个源挂了试下一个
            }
        }

        return null;
    }

    private async Task MatchAndAdoptAsync(SmtcService.SessionSnapshot cur)
    {
        var seq = Interlocked.Increment(ref _matchSeq);
        string title = "", artist = "";
        Status = StMatching;
        Detail = "";
        MatchedAs = "";
        Fmt = "";
        Notify();
        try
        {
            // 「A - B」不知道哪边是歌手时两种顺序各试一轮（见 TitleArtistCandidates）
            CacheEntry? found = null;
            foreach (var (t, a) in TitleArtistCandidates(cur.Title, cur.Artist))
            {
                title = CleanTitle(t);
                artist = CleanTitle(a);
                found = await FindSongAsync(title, artist, cur.DurationMs / 1000.0);
                if (found != null || seq != Volatile.Read(ref _matchSeq)) break;
            }

            if (seq != Volatile.Read(ref _matchSeq)) return; // 期间又切歌了
            if (found == null)
            {
                Status = StNoMatch;
                Detail = L.T("CloudFollow.NoMatchDetail");
                Notify();
                return;
            }

            var (source, song) = (found.Source, found.Song);
            // 歌词走落盘缓存：同一首外部歌反复跟随/重生匹配不再请求接口
            var identity = SongLoader.IdentityOf(source, song);
            var ly = LyricCache.Get(source, identity);
            if (ly == null)
            {
                ly = source == "netease"
                    ? await ServerContext.Netease.GetLyricAsync(song.Id)
                    : await ServerContext.KuGou.GetLyricAsync(song.Hash, null, null, song.AlbumId);
                LyricCache.Put(source, identity, ly, song.AlbumId);
            }
            if (seq != Volatile.Read(ref _matchSeq)) return;
            if (!ly.Ok || string.IsNullOrEmpty(ly.Content))
            {
                Status = StNoMatch;
                Detail = L.T("CloudFollow.LyricEmpty");
                Notify();
                return;
            }

            var (lrc, _, _, _) = SongLoader.AdoptLyric(_state, source, ly, song.AlbumId);
            Fmt = source == "netease" ? "ne" : ly.Fmt == "krc" ? "krc" : "lrc";
            // 匹配结果带歌手：同名歌多版本（原版/翻唱/DJ版），只看歌名分不清词是谁的
            MatchedAs = (source == "kugou" ? L.T("Cloud.SourceKugou") : L.T("Cloud.SourceNetease")) + "·" +
                        song.Name +
                        (song.Singer.Length > 0 ? " - " + song.Singer : "");
            _state.Mutate(p => p.Lyrics = lrc, L.T("CloudFollow.MutateLyrics"), replan: true);
            Status = StOk;
            // 核对警告：歌手对不上＝多半串了翻唱/DJ版；时长差太多＝多半试听截断或不同版本
            var warns = new List<string>();
            if (artist.Length > 0 && song.Singer.Length > 0 && !ArtistMatches(artist, song.Singer))
                warns.Add(L.F("CloudFollow.ArtistMismatch", song.Singer, artist));
            var planDur = _state.Plan?.Duration ?? 0;
            var srtcDur = cur.DurationMs / 1000.0;
            if (planDur > 0 && srtcDur > 0 && Math.Abs(planDur - srtcDur) > 3)
                warns.Add(L.F("CloudFollow.DurationMismatch", J.FmtTime(planDur), J.FmtTime(srtcDur)));
            Detail = string.Join("；", warns);
            Log.Info($"[Follow] 歌词已匹配：{MatchedAs}（{Fmt}）{Detail}");
        }
        catch (Exception ex)
        {
            if (seq == Volatile.Read(ref _matchSeq))
            {
                Status = StError;
                Detail = L.F("CloudFollow.MatchFailed", ex.Message);
            }
        }

        Notify();
    }

    public void Dispose() => Stop();
}
