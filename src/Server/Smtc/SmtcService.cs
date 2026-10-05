using System.Globalization;
using Windows.Media.Control;
using JizuraLive.Logging;
using Newtonsoft.Json;

namespace JizuraLive.Server.Smtc;

// SMTC（系统媒体传输控制）会话监控。外部播放器（酷狗/网易云/QQ音乐/Spotify…）注册到
// Windows 后，这里能读到歌名/歌手/播放状态/进度快照。音频完全由播放器自己出声，
// 本服务只读不写，跟随模式（前端 cloud/follow.ts）轮询 /api/smtc/state 消费这些数据。
// 用法与 JiaoLongControl 的 OSD 媒体监视一致：RequestAsync 放线程池即可，无需 DispatcherQueue。
// 首次 API 请求时惰性启动（镜像宿主进程不用到就零开销）。
public sealed class SmtcService : IDisposable
{
    public sealed class SessionSnapshot
    {
        [JsonProperty("appId")] public string AppId { get; set; } = "";
        [JsonProperty("appName")] public string AppName { get; set; } = "";
        [JsonProperty("title")] public string Title { get; set; } = "";
        [JsonProperty("artist")] public string Artist { get; set; } = "";
        [JsonProperty("album")] public string Album { get; set; } = "";
        // Closed/Opened/Changing/Stopped/Playing/Paused
        [JsonProperty("status")] public string Status { get; set; } = "";
        // LastUpdatedAt 时刻的进度快照；PositionLiveMs 是按服务器时钟推算到「此刻」的进度，
        // 前端拿它直接锚定本地时钟，无需自己换算时钟差
        [JsonProperty("positionMs")] public long PositionMs { get; set; }
        [JsonProperty("positionLiveMs")] public long PositionLiveMs { get; set; }
        [JsonProperty("lastUpdatedAt")] public string LastUpdatedAt { get; set; } = "";
        [JsonProperty("durationMs")] public long DurationMs { get; set; }
        [JsonProperty("rate")] public double Rate { get; set; } = 1.0;
        // true = 该播放器不上报进度（如酷狗 20.x），进度是「切歌/恢复播放=从头」的墙钟估算
        [JsonProperty("estimated")] public bool Estimated { get; set; }
    }

    public sealed class StateSnapshot
    {
        [JsonProperty("ok")] public bool Ok { get; set; }
        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public string? Error { get; set; }
        [JsonProperty("selectedAppId", NullValueHandling = NullValueHandling.Ignore)]
        public string? SelectedAppId { get; set; }
        [JsonProperty("autoSelected")] public bool AutoSelected { get; set; }
        [JsonProperty("current", NullValueHandling = NullValueHandling.Ignore)]
        public SessionSnapshot? Current { get; set; }
        [JsonProperty("sessions")] public List<SessionSnapshot> Sessions { get; } = [];
    }

    private sealed class SessionState
    {
        public GlobalSystemMediaTransportControlsSession Session = null!;
        public string Source = ""; // AUMID，创建时取好缓存；WinRT 对象上的事件回调里可能拿到新实例，按 AUMID 对账
        public string Title = "";
        public string Artist = "";
        public string Album = "";
        public string Status = "";
        public TimeSpan Position;
        public DateTimeOffset LastUpdated;
        public TimeSpan EndTime;
        public double Rate = 1.0;
        public bool MetaFetched;
        // ===== 进度估算（无进度播放器兜底，如酷狗 20.x：SMTC 不报 timeline，UIA 无暴露）=====
        public bool TimelineOk;    // 播放器是否上报真实进度
        public bool HasEst;        // 估算锚点是否有效
        public DateTimeOffset EstAnchor; // 锚点：切歌/恢复播放的时刻，进度从 0（或冻结值）起算
        public double EstPos;      // 暂停时冻结的估算进度
        public string LastMetaKey = "";  // 估算用：检测切歌
        public string LastStatus = "";
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, SessionState> _sessions = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private Timer? _refreshTimer;
    private string? _preferredAppId; // 用户手动指定；null = 自动挑选
    private string? _error;
    private int _started;

    // 缓存变更通知（SSE 推送用）：锁外触发，订阅方自行容错
    public event Action? Changed;

    private void NotifyChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch
        {
            // 订阅方异常不影响服务
        }
    }

    public void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0) Start();
    }

    private void Start()
    {
        // 放线程池执行：不占 UI 线程，也规避 UI 上下文对 WinRT 异步的潜在干扰
        _ = Task.Run(async () =>
        {
            try
            {
                var manager = await GlobalSystemMediaTransportControlsSessionManager
                    .RequestAsync().AsTask();
                lock (_lock)
                {
                    _manager = manager;
                    _error = null;
                }

                manager.SessionsChanged += OnSessionsChanged;
                RefreshSessions();

                // 事件兜底轮询：个别播放器不发全事件，定期补刷进度/状态/缺失的元数据
                _refreshTimer = new Timer(
                    _ => RefreshSessions(), null, 3000, 3000);
                Log.Info("[Smtc] 会话管理器就绪");
            }
            catch (Exception ex)
            {
                lock (_lock) _error = ex.Message;
                Log.Info($"[Smtc] 系统媒体会话监视不可用: {ex.Message}");
            }
        });
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, object args)
    {
        RefreshSessions();
    }

    private void RefreshSessions()
    {
        GlobalSystemMediaTransportControlsSessionManager? manager;
        lock (_lock) manager = _manager;
        if (manager == null) return;

        try
        {
            var current = manager.GetSessions().ToList();
            var needMeta = new List<SessionState>();
            var changed = false;
            lock (_lock)
            {
                var alive = current.Select(s => s.SourceAppUserModelId).ToHashSet();
                var gone = _sessions.Values.Where(v => !alive.Contains(v.Source)).ToList();
                if (gone.Count > 0) changed = true;
                foreach (var st in gone)
                {
                    Unsubscribe(st.Session);
                    _sessions.Remove(st.Source);
                }

                foreach (var s in current)
                {
                    var id = s.SourceAppUserModelId;
                    if (_sessions.TryGetValue(id, out var st))
                    {
                        // 会话实例可能被系统重建，按 AUMID 对账换绑事件
                        if (!ReferenceEquals(st.Session, s))
                        {
                            Unsubscribe(st.Session);
                            st.Session = s;
                            Subscribe(s);
                            st.MetaFetched = false;
                            needMeta.Add(st);
                            changed = true;
                        }

                        continue;
                    }

                    st = new SessionState { Session = s, Source = id };
                    Subscribe(s);
                    _sessions[id] = st;
                    needMeta.Add(st);
                    changed = true;
                }
            }

            foreach (var s in current) UpdateTimeline(s.SourceAppUserModelId);

            // 已有会话缺元数据的补一次（事件丢失/播放器恢复后兜底）
            lock (_lock)
                foreach (var st in _sessions.Values)
                    if (!st.MetaFetched && !needMeta.Contains(st))
                        needMeta.Add(st);

            foreach (var st in needMeta) _ = RefreshMetaAsync(st);
            if (changed) NotifyChanged();
        }
        catch (Exception ex)
        {
            Log.Debug("[Smtc] RefreshSessions failed: " + ex.Message);
        }
    }

    private void Subscribe(GlobalSystemMediaTransportControlsSession s)
    {
        s.MediaPropertiesChanged += OnSessionMediaProps;
        s.TimelinePropertiesChanged += OnSessionTimeline;
        s.PlaybackInfoChanged += OnSessionTimeline;
    }

    private void Unsubscribe(GlobalSystemMediaTransportControlsSession s)
    {
        try
        {
            s.MediaPropertiesChanged -= OnSessionMediaProps;
            s.TimelinePropertiesChanged -= OnSessionTimeline;
            s.PlaybackInfoChanged -= OnSessionTimeline;
        }
        catch
        {
            // 会话已死时解绑失败可忽略
        }
    }

    private void OnSessionMediaProps(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        UpdateTimeline(sender.SourceAppUserModelId);
        _ = RefreshMetaBySourceAsync(sender.SourceAppUserModelId);
    }

    private void OnSessionTimeline(GlobalSystemMediaTransportControlsSession sender, object args)
    {
        UpdateTimeline(sender.SourceAppUserModelId);
    }

    private async Task RefreshMetaBySourceAsync(string source)
    {
        SessionState? st;
        lock (_lock) _sessions.TryGetValue(source, out st);
        if (st != null) await RefreshMetaAsync(st);
    }

    // 只刷进度/状态/时长，都是同步 WinRT 调用
    private void UpdateTimeline(string source)
    {
        SessionState? st;
        lock (_lock) _sessions.TryGetValue(source, out st);
        if (st == null) return;
        try
        {
            var tl = st.Session.GetTimelineProperties();
            var pi = st.Session.GetPlaybackInfo();
            var changed = false;
            lock (_lock)
            {
                changed = st.Position != tl.Position || st.EndTime != tl.EndTime
                                                       || !string.Equals(st.Status, pi.PlaybackStatus.ToString(), StringComparison.Ordinal);
                st.Position = tl.Position;
                st.LastUpdated = tl.LastUpdatedTime;
                st.EndTime = tl.EndTime;
                st.Status = pi.PlaybackStatus.ToString();
                var r = pi.PlaybackRate;
                st.Rate = r is > 0 and <= 4 ? r.Value : 1.0;
                // 酷狗 20.x 等不上报进度的播放器：三项全零/LastUpdatedTime 是 1601 最小值
                st.TimelineOk = st.Position > TimeSpan.Zero || st.EndTime > TimeSpan.Zero
                                || st.LastUpdated.Year > 2001;
                MaintainEstimate(st);
            }

            if (changed) NotifyChanged();
        }
        catch (Exception ex)
        {
            Log.Debug("[Smtc] UpdateTimeline failed for " + source + ": " + ex.Message);
        }
    }

    // 无进度播放器的墙钟估算：以「切歌 / 恢复播放 / 首次见到在播」为锚点从 0 起算，
    // 暂停时冻结。播到中途才开始跟随、播放器内 seek 会偏，由前端对齐微调修正
    private static void MaintainEstimate(SessionState st)
    {
        if (st.TimelineOk)
        {
            st.HasEst = false;
            return;
        }

        var metaKey = st.Title + "|" + st.Artist;
        var metaChanged = st.LastMetaKey.Length > 0 && metaKey != st.LastMetaKey;
        st.LastMetaKey = metaKey;
        var now = DateTimeOffset.UtcNow;
        if (st.Status == "Playing")
        {
            var resumed = st.LastStatus is "Paused" or "Stopped" or "Closed";
            if (metaChanged || !st.HasEst)
            {
                st.EstAnchor = now; // 切歌（或首次见到在播）= 从头
                st.HasEst = true;
            }
            else if (resumed && st.EstPos > 0)
            {
                // 暂停后恢复：从冻结的估算点继续，不从头
                st.EstAnchor = now - TimeSpan.FromSeconds(st.EstPos);
            }

            st.LastStatus = st.Status;
        }
        else
        {
            if (st.HasEst && st.LastStatus == "Playing")
                st.EstPos = (now - st.EstAnchor).TotalSeconds; // 暂停：冻结估算
            st.LastStatus = st.Status;
        }
    }

    private async Task RefreshMetaAsync(SessionState st)
    {
        try
        {
            var mp = await st.Session.TryGetMediaPropertiesAsync();
            var changed = false;
            lock (_lock)
            {
                changed = !string.Equals(st.Title, mp.Title, StringComparison.Ordinal)
                          || !string.Equals(st.Artist, mp.Artist, StringComparison.Ordinal);
                st.Title = mp.Title ?? "";
                st.Artist = mp.Artist ?? "";
                st.Album = mp.AlbumTitle ?? "";
                st.MetaFetched = true;
                MaintainEstimate(st); // 元数据常晚于播放状态到达：切歌检测到就重新锚定估算
            }

            if (changed) NotifyChanged();
        }
        catch (Exception ex)
        {
            Log.Debug("[Smtc] RefreshMeta failed: " + ex.Message);
        }
    }

    public StateSnapshot GetState()
    {
        EnsureStarted();
        var snap = new StateSnapshot();
        lock (_lock)
        {
            snap.Ok = _error == null;
            snap.Error = _error;

            SessionState? sel = null;
            if (!string.IsNullOrEmpty(_preferredAppId))
                _sessions.TryGetValue(_preferredAppId, out sel);
            if (sel == null)
                sel = _sessions.Values.OrderByDescending(Score).FirstOrDefault();

            snap.AutoSelected = string.IsNullOrEmpty(_preferredAppId);
            snap.SelectedAppId = sel?.Source;
            foreach (var kv in _sessions)
            {
                var s = ToSnapshot(kv.Value);
                snap.Sessions.Add(s);
                if (sel != null && kv.Key == sel.Source) snap.Current = s;
            }

            snap.Sessions.Sort((a, b) =>
                string.Equals(a.AppId, snap.SelectedAppId, StringComparison.Ordinal) ? -1 :
                string.Equals(b.AppId, snap.SelectedAppId, StringComparison.Ordinal) ? 1 : 0);
        }

        return snap;
    }

    // 已知音乐应用优先，其次正在播放，其次有元数据
    private static int Score(SessionState st)
    {
        var sc = 0;
        if (IsKnownMusic(st.Source)) sc += 100;
        if (st.Status == "Playing") sc += 50;
        if (st.Title.Length + st.Artist.Length > 0) sc += 10;
        return sc;
    }

    private static SessionSnapshot ToSnapshot(SessionState st)
    {
        var s = new SessionSnapshot
        {
            AppId = st.Source,
            AppName = FriendlyName(st.Source),
            Title = st.Title,
            Artist = st.Artist,
            Album = st.Album,
            Status = st.Status,
            DurationMs = (long)st.EndTime.TotalMilliseconds,
            Rate = st.Rate,
            PositionMs = (long)st.Position.TotalMilliseconds,
            LastUpdatedAt = st.LastUpdated.UtcDateTime.ToString("o", CultureInfo.InvariantCulture),
        };
        if (st.TimelineOk)
        {
            if (st.Status == "Playing")
            {
                var elapsed = DateTimeOffset.UtcNow - st.LastUpdated;
                if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
                if (elapsed > TimeSpan.FromMinutes(5)) elapsed = TimeSpan.FromMinutes(5); // 挂起恢复等异常时间保护
                s.PositionLiveMs = s.PositionMs + (long)(elapsed.TotalMilliseconds * st.Rate);
                if (s.DurationMs > 0 && s.PositionLiveMs > s.DurationMs) s.PositionLiveMs = s.DurationMs;
            }
            else
            {
                s.PositionLiveMs = s.PositionMs;
            }
        }
        else
        {
            // 墙钟估算：切歌/恢复播放 = 从头，暂停冻结
            s.Estimated = true;
            s.LastUpdatedAt = ""; // 无真实 timeline，别把 1601 最小值漏给前端
            s.PositionLiveMs = st.HasEst && st.Status == "Playing"
                ? Math.Max(0, (long)(DateTimeOffset.UtcNow - st.EstAnchor).TotalMilliseconds)
                : (long)(st.EstPos * 1000);
        }

        return s;
    }

    public void Select(string? appId)
    {
        lock (_lock) _preferredAppId = string.IsNullOrWhiteSpace(appId) ? null : appId.Trim();
        NotifyChanged();
    }

    private static readonly (string Key, string Name)[] KnownApps =
    [
        ("kugou", "酷狗音乐"),
        ("cloudmusic", "网易云音乐"),
        ("qqmusic", "QQ音乐"),
        ("kwmusic", "酷我音乐"),
        ("migu", "咪咕音乐"),
        ("spotify", "Spotify"),
        ("applemusic", "Apple Music"),
        ("appleinc.apple", "Apple Music"),
        ("foobar", "foobar2000"),
        ("musicbee", "MusicBee"),
        ("aimp", "AIMP"),
        // 非「音乐应用」（不计 KnownMusic 分），仅用于会话下拉的显示名
        ("msedge", "Edge 浏览器"),
        ("chrome", "Chrome 浏览器"),
        ("firefox", "Firefox 浏览器"),
        ("bilibili", "哔哩哔哩"),
    ];

    private static bool IsKnownMusic(string aumid)
    {
        var lower = aumid.ToLowerInvariant();
        return KnownApps.Any(k => lower.Contains(k.Key));
    }

    private static string FriendlyName(string aumid)
    {
        var lower = aumid.ToLowerInvariant();
        foreach (var (key, name) in KnownApps)
            if (lower.Contains(key))
                return name;
        var bang = aumid.IndexOf('!');
        return bang > 0 ? aumid[..bang] : aumid;
    }

    public void Dispose()
    {
        try
        {
            _refreshTimer?.Dispose();
        }
        catch
        {
            // 进程退出路径，失败可忽略
        }
    }
}
