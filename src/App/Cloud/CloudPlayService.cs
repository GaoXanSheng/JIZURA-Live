using System.Windows;
using System.Windows.Threading;
using JizuraLive.App.I18n;
using JizuraLive.Logging;
using JizuraLive.Server.Models;

namespace JizuraLive.App.Cloud;

/// <summary>
/// 云试听播放会话（全局单例，挂在 MainWindow）：队列 + 播放链 + 进度轮询 + 与工程预览的互斥仲裁。
///
/// 为什么独立成服务：播放生命周期不该跟着面板窗口走（面板关掉还在放、主界面底部条要能控制），
/// 面板与主界面条只是它的两个视图。会话 token 管竞态——面板关闭、换队列、停播都换代，
/// 在途的"取音频/建链"回来发现自己过期就整批作废。
///
/// 三条纪律沿用上一版（都在真机上踩过）：
///  · 起播先 Pause() 工程预览；tick 里反向仲裁（工程预览在播 → 试听让位）——面板关了也要继续跑；
///  · "一首放完"只能看解码位置（链上补零包装让设备永远不会自己停，见 CloudPreview.AtEnd）；
///  · 取音频失败要拆链，否则旧链停在片尾会被连判成"放完了"而连跳整张歌单。
///
/// 播放器模式下再加两条（同样真机踩过）：
///  · 跟随态里引擎的"在播/暂停"完全由本服务的 tick 锚定（PreviewEngine.Pause/Play 在跟随态是空操作），
///    所以**每一条停 tick 的出口都必须先把画面钉住**（HoldEngine）——漏一步就留下"换歌失败后画面
///    还在循环播放、按空格也停不住"的死状态；
///  · 取不到音频的那首直接跳过（"平台上没有标识"的歌立即失败，不走接口重试链；其余失败连跳有上限），
///    推进队列只由 tick 做，起播在途期间不许重复推进（否则会把整张歌单跑空）。
/// </summary>
public sealed class CloudPlayService : IDisposable
{
    private readonly CloudPreview _player = new();
    private readonly DispatcherTimer _timer;

    private List<KgSong> _queue = [];
    private int _index = -1;
    private string _quality = "128";
    private int _session;   // 换代：面板关闭/换队列/停播都作废在途任务
    private int _loading;   // 起播链在途计数（跳过会嵌套触发新的起播，所以用计数不用布尔）
    private bool _skip;     // 这首取不到，等 tick 跳过它（推进只由 tick 做）
    private int _skipStreak; // 连续跳过次数（到上限停手报错，见 SkipUnplayable）
    private const int SkipStreakMax = 3;

    /// <summary>引擎（跟随时钟的写入端，MainWindow 构造时接上）。为空则一切跟随调用静默跳过
    /// ——无窗口的纯逻辑测试、或引擎还没建好的场景。</summary>
    public PreviewEngine? Engine { get; set; }

    /// <summary>试听 tick 是否在跑。有曲就必须在跑：跟随态的画面锚定、以及"取不到的那首跳过"
    /// 都由它驱动（tick 一停，画面就再没人管）。</summary>
    public bool Ticking => _timer.IsEnabled;

    /// <summary>手动跑一拍（对位 DispatcherTimer 的 Tick）：测试里不依赖消息泵也能驱动
    /// 跳过/收场/锚定这些逐拍逻辑。</summary>
    public void TickOnce() => OnTick();

    /// <summary>状态变化（换曲、播放态、队列、状态文案、音量）——视图据此整体刷新。</summary>
    public event Action? Changed;

    /// <summary>进度推进（200ms）——只有进度条/时间需要，不重刷列表。</summary>
    public event Action? Tick;

    public CloudPlayService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => OnTick();
        _player.Failed += OnDeviceFailed;
        ApplyVolume();
    }

    // ---------------- 对外状态 ----------------

    public bool HasTrack => _index >= 0 && _index < _queue.Count;
    public bool IsPlaying => _player.IsPlaying;
    public KgSong? Current => HasTrack ? _queue[_index] : null;
    public int QueueIndex => _index;
    public int QueueCount => _queue.Count;

    /// <summary>加载中/试听片段/失败等状态文案（空串＝正常播放或未在播放）。</summary>
    public string Status { get; private set; } = "";

    public double Time => _player.Position;
    public double Duration => _player.Duration;
    public double Frac => Duration > 0 ? Math.Clamp(Time / Duration, 0, 1) : 0;

    public double Volume => App.SettingsOf()?.CloudVolume ?? 0.85;
    public bool Muted => App.SettingsOf()?.CloudMuted ?? false;

    /// <summary>播放器模式：tick 把云链位置喂给引擎的跟随时钟（PV 跟着云歌走），并关掉反向仲裁
    /// （引擎此时在跟随态，它的 IsPlaying 是跟随位，不代表工程音频在播）。</summary>
    public bool PlayerMode { get; set; }

    /// <summary>起播前的钩子（播放器模式用它先把 PV 换成这首歌，再出声：画面与声音同时开始）。</summary>
    public Func<KgSong, Task>? BeforePlay { get; set; }

    /// <summary>当前装载音质（播放器模式预取要用同一档，和试听保持一致）。</summary>
    public string Quality => _quality;

    /// <summary>队列里的下一首（预取用）；没有则 null。</summary>
    public KgSong? PeekNext()
    {
        var next = StepIndex(_index, 1, _queue.Count);
        return next < 0 ? null : _queue[next];
    }

    /// <summary>外部（播放器会话）写状态行，例如「准备中…」「已就绪」。</summary>
    public void SetStatus(string text)
    {
        Status = text;
        Changed?.Invoke();
    }

    /// <summary>这首歌是不是正在（或被暂停的）那一首——面板行内 glyph 靠它认领。</summary>
    public bool IsCurrent(KgSong song) => Current is { } cur && SameSong(cur, song);

    /// <summary>同歌判定：平台 + 平台内身份（酷狗 Hash / 网易云 Id）。队列与面板列表是两批对象。</summary>
    public static bool SameSong(KgSong a, KgSong b)
    {
        var (sa, sb) = (a.Source ?? "kugou", b.Source ?? "kugou");
        if (!string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase)) return false;
        var (ia, ib) = (IdOf(a), IdOf(b));
        return ia.Length > 0 && ia == ib;
    }

    private static string IdOf(KgSong s) =>
        s.Source == "netease" ? s.Id : s.Hash.Length > 0 ? s.Hash : s.Id;

    /// <summary>上一首/下一首的下标；越界返回 -1（列表放完就收场，不循环）。</summary>
    public static int StepIndex(int index, int step, int count)
    {
        if (count == 0) return -1;
        var next = index + step;
        if (next < 0) next = 0;
        return next >= count ? -1 : next;
    }

    // ---------------- 动作 ----------------

    /// <summary>从列表的某一首开始顺序播（队列与音质都在这时快照——面板控件会随窗销毁）。</summary>
    public void Play(IReadOnlyList<KgSong> queue, int index, string quality)
    {
        if (index < 0 || index >= queue.Count) return;
        foreach (var s in queue) s.Source = string.IsNullOrEmpty(s.Source) ? "kugou" : s.Source;
        _queue = [.. queue];
        _quality = quality;
        _skipStreak = 0; // 换队列＝重新数：用户新点的一首不该背上一队列的连跳计数
        // 有曲就得有 tick：跟随态的画面完全由 tick 锚定，tick 一停就再没人管它（见 HoldEngine）
        if (!_timer.IsEnabled) _timer.Start();
        _ = PlayAtAsync(index);
    }

    public void Toggle()
    {
        if (!HasTrack) return;
        if (!_player.HasTrack)
        {
            // 链已拆（取音频失败/收场/设备故障）：这一下当"暂停画面"用——跟随态里引擎自己的 Pause
            // 是空操作，不在这里接住的话用户没有任何办法让画面停下来
            HoldEngine();
            Changed?.Invoke();
            return;
        }
        _player.Toggle();
        Changed?.Invoke();
    }

    public void Pause()
    {
        if (!_player.IsPlaying) return;
        _player.Pause();
        Changed?.Invoke();
    }

    public void Next() => _ = StepAsync(1);

    public void Prev() => _ = StepAsync(-1);

    public void SeekTo(double frac)
    {
        var dur = Duration;
        if (dur <= 0) return;
        _player.Seek(Math.Clamp(frac, 0, 1) * dur);
        Tick?.Invoke();
    }

    /// <summary>停播并清空队列（列表放完、用户主动停）。</summary>
    public void Stop()
    {
        _session++;
        _player.Teardown();
        _timer.Stop();
        _queue = [];
        _index = -1;
        _skip = false;
        Status = "";
        HoldEngine(); // 收场也要钉住画面：tick 停了就再没人锚定，画面会一直空转
        Changed?.Invoke();
        Log.Info("[Cloud] 试听会话收场（队列放完/主动停），画面已钉住");
    }

    // ---------------- 播放链 ----------------

    private async Task PlayAtAsync(int index)
    {
        if (index < 0 || index >= _queue.Count) return;
        var token = ++_session;
        _index = index;
        _skip = false; // 新的一首开始准备：作废上一首留下的"待跳过"
        var song = _queue[index];
        Status = L.T("Cloud.PreviewLoading");
        Changed?.Invoke();

        Engine?.Pause(); // 单一音频焦点：试听出声，工程预览先停

        _loading++; // 起播在途：tick 在此期间不推进队列（否则旧链停在片尾会被反复判"放完"而连跳）
        try
        {
            string path;
            var trial = false;
            try
            {
                (path, _, trial, _) = await SongLoader.EnsureAudioFileAsync(song.Source, song, _quality,
                    s => { if (token == _session) { Status = s; Changed?.Invoke(); } });
            }
            catch (Exception ex)
            {
                if (token != _session) return;
                SkipUnplayable(ex); // 这首取不到：拆链钉画面，交给 tick 跳过（不在这里直接 Next，推进只有一个入口）
                return;
            }
            if (token != _session) return; // 期间停播/换曲/面板换代

            // 起播前先把 PV 换成这首歌（播放器模式）：钩子返回后画面已就绪，再出声
            if (BeforePlay is { } hook)
            {
                try
                {
                    await hook(song);
                }
                catch (Exception ex)
                {
                    Log.Warn("[Cloud] 起播前钩子失败（继续出声）：" + ex.Message);
                }
                if (token != _session) return;
            }

            if (!await _player.PlayAsync(path))
            {
                if (token != _session) return;
                Status = L.T("Cloud.PreviewDeviceFailed");
                Changed?.Invoke();
                return;
            }
            if (token != _session)
            {
                _player.Teardown();
                return;
            }

            _skipStreak = 0; // 起播成功：连跳计数归零
            Status = trial ? L.T("Cloud.PreviewTrial") : "";
            if (!_timer.IsEnabled) _timer.Start();
            Changed?.Invoke();
            Log.Info($"[Cloud] 试听：{song.Name}（{_index + 1}/{_queue.Count}）");
        }
        finally
        {
            _loading--;
        }
    }

    /// <summary>
    /// 这首取不到音频（歌不存在/无版权/登录失效/断网）：拆链、把画面钉住，然后交给 tick 跳过它。
    ///
    /// "平台上没有标识"（歌单里的空条目）是**歌本身**的问题：立即跳过，且不计数——这类条目点几次都播不了，
    /// 卡在这里报错毫无意义（而且旧口径要走完整条接口重试链，一首能卡近一分钟）。其余失败（登录失效/
    /// 断网）是**整片**的问题：连跳整张歌单同样没意义，连跳到上限就停手，把原因报出来。
    /// </summary>
    private void SkipUnplayable(Exception ex)
    {
        _player.Teardown(); // 旧链停在片尾不拆会被连判成"放完了"而连跳整张歌单
        HoldEngine();       // 画面钉在当前位置：tick 停摆时也停得住
        var songScoped = ex is SongLoader.CloudException { SongScoped: true };
        if (!songScoped) _skipStreak++;
        if (!songScoped && _skipStreak > SkipStreakMax)
        {
            _skip = false;
            Status = L.F("Cloud.PreviewFailed", ex.Message);
            Log.Warn($"[Cloud] 连续 {_skipStreak} 首取不到音频，停止跳过：{ex.Message}");
        }
        else
        {
            _skip = true;
            Status = songScoped
                ? L.F("Cloud.PreviewSkippedNoId", _index + 1)
                : L.F("Cloud.PreviewSkipped", _index + 1);
            Log.Info($"[Cloud] 第 {_index + 1} 首取不到音频，跳过（{_skipStreak}/{SkipStreakMax}）：{ex.Message}");
        }
        if (!_timer.IsEnabled) _timer.Start(); // 推进由 tick 做：tick 必须在跑
        Changed?.Invoke();
    }

    private async Task StepAsync(int step)
    {
        if (!HasTrack) return;
        var next = StepIndex(_index, step, _queue.Count);
        if (next < 0)
        {
            Stop(); // 列表放完：收场（不循环）
            return;
        }
        await PlayAtAsync(next);
    }

    private void OnTick()
    {
        if (!HasTrack)
        {
            _timer.Stop();
            HoldEngine(); // 没曲可播：先把画面钉住再停 tick（tick 一停就再没人锚定它）
            Log.Info("[Cloud] 无曲可播，试听 tick 停摆（画面已钉住）");
            return;
        }
        // 取不到的那首：跳过（队列推进只在这里做，起播在途期间不重复推进）
        if (_skip && _loading == 0)
        {
            _skip = false;
            Next();
            return;
        }
        // 顺序播放：一首放完自动下一首（判据见 CloudPreview.AtEnd 的注释）。
        // 先钉住画面再换歌：旧链还停在片尾时，之后每一拍都还会判一次 AtEnd，不钉的话画面会顺着
        // 跟随钟越过片尾、按整首取模回卷——看起来就是"这首放完了画面还在循环播"。
        if (_player.AtEnd)
        {
            HoldEngine();
            if (_loading == 0) Next();
            return;
        }
        Tick?.Invoke();
        // 播放器模式：把云链的进度喂给引擎的跟随时钟，PV 就跟着这首歌走（引擎不自己出声）
        if (PlayerMode)
        {
            AnchorEngine();
            return;
        }
        // 反向仲裁：用户去播工程预览了，试听让位（面板关了这条也要跑）
        if (_player.IsPlaying && Engine?.IsPlaying == true)
        {
            _player.Pause();
            Changed?.Invoke();
        }
    }

    // ---------------- 引擎跟随时钟（播放器模式） ----------------

    /// <summary>正常锚定：位置与播放态照抄云链（每拍一次）。</summary>
    private void AnchorEngine() => Engine?.FollowAnchor(_player.Position, _player.IsPlaying);

    /// <summary>
    /// 把画面钉在当前位置并暂停——**所有"云链不出声了"的出口都要走它**（取音频失败、队列放完、
    /// 设备故障、用户按暂停）。跟随态里引擎自己的 Pause/Play 是空操作（PreviewEngine 直接 return），
    /// 画面停不停完全取决于有没有人重新锚定：漏掉这一步，跟随钟就会带着上一次的 run=true 一直按墙钟
    /// 外推、CurrentTime 又按整首取模回卷——表现为「换歌失败后画面还在循环播放、按空格也停不住」。
    /// 位置取"画面现在在哪"：这首放完就钉在最后一帧，链已拆就钉在当前位置（都不跳到 0、不回卷）。
    /// </summary>
    private void HoldEngine()
    {
        if (Engine is not { } e || !PlayerMode) return;
        var t = _player.AtEnd ? Math.Max(0, _player.Duration - 0.05) : Math.Max(0, e.CurrentTime);
        e.FollowAnchor(t, false);
    }

    private void OnDeviceFailed()
    {
        _player.Teardown();
        _timer.Stop();
        HoldEngine(); // 设备没了画面也得停住（tick 停了，这里不锚就永远在播）
        Status = L.T("Cloud.PreviewDeviceFailed");
        Changed?.Invoke();
        Log.Warn("[Cloud] 试听输出故障停机，画面已钉住");
    }

    // ---------------- 音量（独立于工程预览，落盘记忆） ----------------

    /// <summary>音量/消音（persist=false 用于拖动中：只改播放链，松手才落盘）。</summary>
    public void SetVolume(double? volume, bool? muted, bool persist = true)
    {
        var s = App.SettingsOf();
        if (s != null)
        {
            if (volume != null) s.CloudVolume = Math.Clamp(volume.Value, 0, 1);
            if (muted != null) s.CloudMuted = muted.Value;
            if (persist) s.SaveDebounced();
        }
        ApplyVolume();
        Changed?.Invoke();
    }

    private void ApplyVolume() => _player.Volume = Muted ? 0 : Volume;

    public void Dispose()
    {
        _session++;
        _timer.Stop();
        _player.Failed -= OnDeviceFailed;
        _player.Dispose();
    }
}
