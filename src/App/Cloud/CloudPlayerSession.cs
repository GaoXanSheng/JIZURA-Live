using System.IO;
using System.Windows;
using JizuraLive.App.I18n;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Server;
using JizuraLive.Server.Models;

namespace JizuraLive.App.Cloud;

/// <summary>
/// 播放器模式的会话协调器：让 PV 跟着云歌单当前播放的那首歌**真渲染**，同时**不动工程音频**。
///
/// 做法（比"逐首载入工程"干净一档）：
///   · 声音仍由云试听链（CloudPreview）出；
///   · 每首歌真下载 + 取词 + 解码分析，然后把**分析结果**交给引擎（`SetAudioSource(audio, path:null)`
///     ——只换规划用的节拍/时长/能量，顺手拆掉引擎自己的播放链，它不出声）；
///   · 引擎进入**跟随态**，时钟由 CloudPlayService 的 tick 用云链位置驱动（FollowController 同款）；
///   · 工程只被临时改写歌词/曲名/歌手（一次 Mutate，且不压撤销历史）；**AudioPath/AudioName 一个字节不动**。
///
/// 进模式前把整份工程快照写 `config\player_session.json`（防崩溃），退出时原样还原 + 把引擎音频还回去
/// （`EditorState.Audio` 全程没被改过，不需要重新解码分析）。启动时若发现遗留快照，说明上次是异常退出，
/// 直接还原并把文件删掉。
/// </summary>
public sealed class CloudPlayerSession
{
    private readonly EditorState _state;
    private readonly CloudPlayService _svc;

    private string _snapshot = "";
    private int _seq;                       // 换代：退出/重进都作废在途的预取
    private readonly Dictionary<string, SongLoader.PreparedSong> _prepared = new(); // 身份 → 已备好的素材
    private readonly List<string> _preparedOrder = [];   // 上限内保留最近几首（重进模式秒接，不无限占内存）
    private const int KeepPrepared = 6;
    private string _preparing = "";         // 正在预取的身份（防重复）

    public bool Active { get; private set; }

    /// <summary>会话状态变化（叠加层/播放条刷新）。</summary>
    public event Action? Changed;

    public static string SessionFile => Path.Combine(AppDirs.ConfigDir, "player_session.json");

    public CloudPlayerSession(EditorState state, CloudPlayService svc)
    {
        _state = state;
        _svc = svc;
    }

    // ---------------- 进出 ----------------

    /// <summary>进播放器模式：快照工程 → 接管引擎（跟随态）→ 让云链的每次起播先换 PV。</summary>
    public void Enter()
    {
        if (Active) return;
        Active = true;
        _seq++;
        _snapshot = _state.Snapshot();
        try
        {
            AtomicFile.Write(SessionFile, _snapshot);
        }
        catch (Exception ex)
        {
            Log.Warn("[Player] 会话快照落盘失败（崩溃后无法自动还原）：" + ex.Message);
        }

        _svc.PlayerMode = true;
        _svc.BeforePlay = AdoptForPlayAsync;
        // 引擎让位：本地音频链停掉（声音由云链出），并进入跟随态
        _state.Engine.Pause();
        _state.Engine.FollowAnchor(_state.Engine.CurrentTime, false);
        // 重进模式：正在播的那首立刻把 PV 接回来——否则画面停在你自己的工程上，
        // 看着就像"歌词被清空了"（只有切歌才会重新灌词）
        if (_svc.Current is { } cur) _ = AdoptForPlayAsync(cur);
        Log.Info("[Player] 进入播放器模式（工程已快照，退出时还原）");
        Changed?.Invoke();
    }

    /// <summary>退出播放器模式：停跟随 → 引擎音频还回去 → 工程按快照还原 → 删会话文件。
    /// restore=false：用户主动把当前这首"载入编辑器"（工程已经被这次装载改写），别再拿旧快照盖回去。</summary>
    public void Exit(bool restore = true)
    {
        if (!Active) return;
        Active = false;
        _seq++;
        _svc.PlayerMode = false;
        _svc.BeforePlay = null;
        _preparing = "";   // 素材缓存留着：队列还在放，重进模式可秒接

        _state.Engine.FollowEnd();
        if (restore)
        {
            _state.Engine.SetAudioSource(_state.Audio, _state.AudioPath, replan: true); // 拆跟随、把用户的音频接回引擎
            if (_snapshot.Length > 0 && !_state.RestoreSnapshotSilently(_snapshot))
                Log.Warn("[Player] 工程还原失败：快照仍留在 " + SessionFile);
        }
        else
        {
            // 留在工程里的就是刚载入的那首云歌：引擎自己建链放它，音量/循环按工程侧来
            _state.Engine.SetAudioSource(_state.Audio, _state.AudioPath, replan: true);
        }

        try
        {
            if (File.Exists(SessionFile)) File.Delete(SessionFile);
        }
        catch (Exception ex)
        {
            Log.Debug("[Player] 会话文件删除失败：" + ex.Message);
        }
        Log.Info("[Player] 已退出播放器模式（工程与音频已还原）");
        Changed?.Invoke();
    }

    /// <summary>启动时调用：发现遗留会话文件 = 上次异常退出，直接把工程还原回去。</summary>
    public bool RecoverLeftoverIfAny()
    {
        try
        {
            if (!File.Exists(SessionFile)) return false;
            var json = File.ReadAllText(SessionFile);
            File.Delete(SessionFile);
            if (json.Length == 0) return false;
            var ok = _state.RestoreSnapshotSilently(json);
            Log.Warn($"[Player] 检测到上次播放器模式异常退出，已{(ok ? "还原" : "尝试还原（失败）")}工程");
            return ok;
        }
        catch (Exception ex)
        {
            Log.Warn("[Player] 遗留会话恢复失败：" + ex.Message);
            return false;
        }
    }

    // ---------------- 到曲：把这首歌变成能渲染的 PV ----------------

    /// <summary>云链起播前的钩子：确保这首歌已备好 → 写歌词/曲名/歌手 → 把分析数据交给引擎。</summary>
    private async Task AdoptForPlayAsync(KgSong song)
    {
        var token = _seq;
        var prepared = await PrepareAsync(song);
        if (!Active || token != _seq) return;
        if (prepared == null)
        {
            // 备不出来（下载/分析失败）：跳过这首继续下一首；本次起播会被会话 token 作废
            _svc.SetStatus(L.T("Player.PrepareFailed"));
            _svc.Next();
            return;
        }

        // 歌词 + 曲名/歌手一次写完（不压撤销历史：几十首歌会把用户自己的历史挤光）
        _state.MutateNoHistory(p =>
        {
            if (prepared.Lrc.Length > 0) p.Lyrics = prepared.Lrc;
            p.Title = song.Name;
            p.Artist = song.Singer;
        }, L.T("Player.MutateSong"), replan: false);

        // 分析数据交给引擎（path=null：只换规划用的节拍/时长/能量，不建播放链——声音由云链出）
        _state.Engine.SetAudioSource(prepared.Audio, null, replan: true);

        _svc.SetStatus(prepared.Trial ? L.T("Cloud.PreviewTrial") : L.T("Player.NowPlaying"));
        Changed?.Invoke();
        Log.Info($"[Player] PV 已就绪：{song.Name} - {song.Singer}"
                 + $"（{J.FmtTime(prepared.Audio.Duration)}，{prepared.Audio.Bpm:0.0} BPM，试听片段={prepared.Trial}）");

        _ = PrefetchNextAsync(); // 边播边备下一首
    }

    /// <summary>取（或现做）这首歌的素材；命中预取就是零等待。</summary>
    private async Task<SongLoader.PreparedSong?> PrepareAsync(KgSong song)
    {
        var key = KeyOf(song);
        if (_prepared.TryGetValue(key, out var hit)) return hit;
        try
        {
            _svc.SetStatus(L.T("Player.Preparing"));
            var prepared = await SongLoader.PrepareAsync(_state, song.Source, song, _svc.Quality,
                s => _svc.SetStatus(s));
            Remember(key, prepared);
            return prepared;
        }
        catch (Exception ex)
        {
            Log.Warn($"[Player] 准备失败（这首跳过）：{song.Name} - {ex.Message}");
            _svc.SetStatus(L.F("Player.PrepareFailedMsg", song.Name));
            return null;
        }
    }

    /// <summary>后台预取队列里的下一首（串行一条：同一时刻只准备一首，别把 CPU 和网络铺满）。</summary>
    private async Task PrefetchNextAsync()
    {
        var token = _seq;
        var next = _svc.PeekNext();
        if (next == null) return;
        var key = KeyOf(next);
        if (_prepared.ContainsKey(key) || _preparing == key) return;
        _preparing = key;
        try
        {
            Log.Info($"[Player] 预取下一首：{next.Name} - {next.Singer}");
            var prepared = await SongLoader.PrepareAsync(_state, next.Source, next, _svc.Quality);
            if (Active && token == _seq) Remember(key, prepared);
        }
        catch (Exception ex)
        {
            Log.Info($"[Player] 预取失败（到曲时会再试一次）：{next.Name} - {ex.Message}");
        }
        finally
        {
            if (_preparing == key) _preparing = "";
        }
    }

    /// <summary>记住已备好的素材（上限内保留最近几首；超出丢最旧的）。</summary>
    private void Remember(string key, SongLoader.PreparedSong prepared)
    {
        if (!_prepared.ContainsKey(key)) _preparedOrder.Add(key);
        _prepared[key] = prepared;
        while (_preparedOrder.Count > KeepPrepared)
        {
            var oldest = _preparedOrder[0];
            _preparedOrder.RemoveAt(0);
            _prepared.Remove(oldest);
        }
    }

    /// <summary>队列键：平台 + 平台内身份（与 CloudPlayService.SameSong 同口径）。</summary>
    private static string KeyOf(KgSong s) =>
        (s.Source ?? "kugou") + ":" + (s.Source == "netease" ? s.Id : s.Hash.Length > 0 ? s.Hash : s.Id);
}
