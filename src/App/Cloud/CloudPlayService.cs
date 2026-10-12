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
/// </summary>
public sealed class CloudPlayService : IDisposable
{
    private readonly CloudPreview _player = new();
    private readonly DispatcherTimer _timer;

    private List<KgSong> _queue = [];
    private int _index = -1;
    private string _quality = "128";
    private int _session;   // 换代：面板关闭/换队列/停播都作废在途任务

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
        _ = PlayAtAsync(index);
    }

    public void Toggle()
    {
        if (!HasTrack) return;
        _player.Toggle();
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
        Status = "";
        Changed?.Invoke();
    }

    // ---------------- 播放链 ----------------

    private async Task PlayAtAsync(int index)
    {
        if (index < 0 || index >= _queue.Count) return;
        var token = ++_session;
        _index = index;
        var song = _queue[index];
        Status = L.T("Cloud.PreviewLoading");
        Changed?.Invoke();

        App.MainInstance?.State.Engine.Pause(); // 单一音频焦点：试听出声，工程预览先停

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
            _player.Teardown(); // 旧链停在片尾，不拆会被连判成"放完了"而连跳整张歌单
            Status = L.F("Cloud.PreviewFailed", ex.Message);
            Log.Info("[Cloud] 试听取音频失败：" + ex.Message);
            Changed?.Invoke();
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

        Status = trial ? L.T("Cloud.PreviewTrial") : "";
        if (!_timer.IsEnabled) _timer.Start();
        Changed?.Invoke();
        Log.Info($"[Cloud] 试听：{song.Name}（{_index + 1}/{_queue.Count}）");
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
            return;
        }
        // 顺序播放：一首放完自动下一首（判据见 CloudPreview.AtEnd 的注释）
        if (_player.AtEnd)
        {
            Next();
            return;
        }
        Tick?.Invoke();
        // 播放器模式：把云链的进度喂给引擎的跟随时钟，PV 就跟着这首歌走（引擎不自己出声）
        if (PlayerMode)
        {
            App.MainInstance?.State.Engine.FollowAnchor(_player.Position, _player.IsPlaying);
            return;
        }
        // 反向仲裁：用户去播工程预览了，试听让位（面板关了这条也要跑）
        if (_player.IsPlaying && App.MainInstance?.State.Engine.IsPlaying == true)
        {
            _player.Pause();
            Changed?.Invoke();
        }
    }

    private void OnDeviceFailed()
    {
        _player.Teardown();
        _timer.Stop();
        Status = L.T("Cloud.PreviewDeviceFailed");
        Changed?.Invoke();
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
