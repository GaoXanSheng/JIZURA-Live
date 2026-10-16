using JizuraLive.Logging;

namespace JizuraLive.Vts;

/// <summary>
/// 演出 cue 的调度器：把一条 <see cref="VtsCueTrack"/> 按歌曲时间变成对 <see cref="IVtsCueSink"/> 的调用。
///
/// 纯逻辑（不建线程、不读时钟）：宿主按帧调 <see cref="Tick"/>，进度全由传入的 <c>songTime</c> 决定——
/// 播放/暂停/跳转都是宿主的播放器说了算，这里只负责"这个时刻该发什么"。
/// </summary>
public sealed class VtsCueScheduler
{
    /// <summary>表情开关的 VTS 侧过渡时长（VTS 只接受 0..2 秒）。</summary>
    private const double ExpressionFadeSec = 0.4;

    /// <summary>包络注入频率上限（30Hz）：起音只有 60ms，再稀就削峰了。</summary>
    private const double MinInjectIntervalSec = 1.0 / 30.0;

    /// <summary>包络起音时长。</summary>
    private const double PulseAttackSec = 0.06;

    // 跳转判定。宿主按帧 Tick（正常播放的步长远小于这两条线）：往回 0.05s 内算抖动不算跳转，
    // 往前超过 0.5s 一定是在拖进度条——跳过的 cue 不补发，否则会一口气喷一串动作。
    private const double BackJumpSec = 0.05;
    private const double ForwardJumpSec = 0.5;

    private readonly IVtsCueSink _sink;
    private readonly HashSet<string> _loggedRoles = new(StringComparer.Ordinal);

    private List<VtsCue> _cues = new();
    private Dictionary<string, string> _roles = new(StringComparer.Ordinal);
    private double _leadSec = 0.08;

    /// <summary>下一条还没处理过的 cue 的下标（离散 cue 的游标）。</summary>
    private int _cursor;

    private bool _hasLastTime;
    private double _lastSongTime;

    // 表情：目标 → 我们最后下发的开关。不在表里 = 从没下发过（VTS 侧视作关）。
    private readonly Dictionary<string, bool> _expressionOn = new(StringComparer.Ordinal);

    /// <summary>表情：目标 → 自动关的时刻（只有"开且带时长"的 cue 会登记）。</summary>
    private readonly Dictionary<string, double> _expressionCloseAt = new(StringComparer.Ordinal);

    // 参数脉冲：当前处于"已注入"状态时要记住注入过哪些参数，好在这一段结束时发全 0 放回。
    private readonly HashSet<string> _pulsedParams = new(StringComparer.Ordinal);
    private double _lastInjectAt = double.NegativeInfinity;
    private bool _injecting;

    public VtsCueScheduler(IVtsCueSink sink) => _sink = sink;

    /// <summary>装入一条轨（可换歌）；游标与已下发的状态一并复位。</summary>
    public void Load(VtsCueTrack track)
    {
        // 存盘往返 / 手改文件都可能塞进乱序 or 空条目：稳定排序保证同一时刻的 cue 按存档顺序触发
        _cues = (track?.Cues ?? new List<VtsCue>())
            .Where(c => c is not null)
            .OrderBy(c => c.Time)
            .ToList();
        _roles = track?.Roles ?? new Dictionary<string, string>(StringComparer.Ordinal);

        // 手改/坏写盘的 NaN 提前量会让所有比较恒假（整条轨静默不发），按"没有提前量"处理
        var leadMs = track?.LeadMs ?? 0;
        _leadSec = double.IsFinite(leadMs) ? Math.Max(0, leadMs) / 1000.0 : 0;
        Reset();
    }

    /// <summary>复位到"还没开始播放"：下一次 <see cref="Tick"/> 从当前位置重新对齐，不补发历史 cue。</summary>
    public void Reset()
    {
        _cursor = 0;
        _hasLastTime = false;
        _lastSongTime = 0;
        _expressionOn.Clear();
        _expressionCloseAt.Clear();
        _pulsedParams.Clear();
        _injecting = false;
        _lastInjectAt = double.NegativeInfinity;
        _loggedRoles.Clear();
    }

    /// <summary>按歌曲时间推进一步（宿主每帧调；<paramref name="playing"/> = false 时一个调用都不发）。</summary>
    public void Tick(double songTime, bool playing)
    {
        // 暂停期间不推进游标也不记时间：暂停中拖了进度，恢复时那次 Tick 会被当成跳转，正好重新对齐
        if (!playing) return;

        var jumped = _hasLastTime
                     && (songTime < _lastSongTime - BackJumpSec || songTime > _lastSongTime + ForwardJumpSec);
        _lastSongTime = songTime;
        _hasLastTime = true;

        if (jumped)
        {
            _cursor = FirstIndexAfter(songTime);
            AlignExpressions(songTime);
            ReleasePulseInjections();
        }
        else
        {
            FireDueCues(songTime);
        }

        CloseExpiredExpressions(songTime);
        TickPulses(songTime);
    }

    // ---------------- 离散 cue ----------------

    private void FireDueCues(double songTime)
    {
        while (_cursor < _cues.Count && _cues[_cursor].Time - _leadSec <= songTime)
        {
            var cue = _cues[_cursor++];
            switch (cue.Kind)
            {
                case VtsCueKind.Hotkey:
                    if (Resolve(cue.Target) is { } hotkey) _sink.TriggerHotkey(hotkey);
                    break;

                case VtsCueKind.Expression:
                    ApplyExpressionCue(cue);
                    break;

                case VtsCueKind.Move:
                    _sink.MoveModelRelative(cue.X, cue.Y, cue.Rotation, cue.Value, cue.Duration);
                    break;

                // Pulse 不在这里发：它是一段包络，由 TickPulses 每拍算
            }
        }
    }

    /// <summary>跳转后游标重定位：第一条 Time-lead &gt; songTime 的 cue（也就是跳过去的那一段不补发）。</summary>
    private int FirstIndexAfter(double songTime)
    {
        for (var i = 0; i < _cues.Count; i++)
            if (_cues[i].Time - _leadSec > songTime) return i;
        return _cues.Count;
    }

    // ---------------- 表情 ----------------

    private void ApplyExpressionCue(VtsCue cue)
    {
        if (Resolve(cue.Target) is not { } file) return;

        var on = cue.Value > 0.5;
        SetExpressionState(file, on);

        // "开且带时长"登记到期时刻；显式关 / 无时长的开要把旧的到期点撤掉（否则会把后来的开自动关掉）
        if (on && cue.Duration > 0) _expressionCloseAt[file] = cue.Time + cue.Duration - _leadSec;
        else _expressionCloseAt.Remove(file);
    }

    /// <summary>只在下发状态真的变了时才发（同一目标连续两条"开"不会重复发）。</summary>
    private void SetExpressionState(string file, bool on)
    {
        if (_expressionOn.TryGetValue(file, out var current))
        {
            if (current == on) return;
        }
        else if (!on)
        {
            return; // 从没开过：VTS 侧本来就是关的，不必发一条关
        }

        _expressionOn[file] = on;
        _sink.SetExpression(file, on, ExpressionFadeSec);
    }

    private void CloseExpiredExpressions(double songTime)
    {
        if (_expressionCloseAt.Count == 0) return;

        List<string>? due = null;
        foreach (var (file, at) in _expressionCloseAt)
            if (at <= songTime) (due ??= new List<string>()).Add(file);
        if (due is null) return;

        foreach (var file in due)
        {
            _expressionCloseAt.Remove(file);
            SetExpressionState(file, false);
        }
    }

    /// <summary>
    /// 跳转后把表情状态对齐到 songTime 应有的样子：每个目标取 Time-lead ≤ songTime 的最后一条
    /// Expression cue 的开关值（那一条是"开且带时长"但已过期的，此刻应为关），与已下发的不同就补发。
    /// </summary>
    private void AlignExpressions(double songTime)
    {
        _expressionCloseAt.Clear();

        var desired = new Dictionary<string, (bool On, double CloseAt)>(StringComparer.Ordinal);
        foreach (var cue in _cues)
        {
            if (cue.Kind != VtsCueKind.Expression) continue;
            if (cue.Time - _leadSec > songTime) break; // _cues 按 Time 升序
            if (Resolve(cue.Target) is not { } file) continue;
            var on = cue.Value > 0.5;
            var closeAt = on && cue.Duration > 0 ? cue.Time + cue.Duration - _leadSec : double.PositiveInfinity;
            desired[file] = (on && songTime < closeAt, closeAt);
        }

        foreach (var (file, state) in desired)
        {
            SetExpressionState(file, state.On);
            if (state.On) _expressionCloseAt[file] = state.CloseAt;
        }

        // 我们这个时刻不该开、但手上还开着（跳到了它开之前）的目标：关掉
        foreach (var file in _expressionOn.Keys.ToList())
            if (!desired.ContainsKey(file)) SetExpressionState(file, false);
    }

    // ---------------- 参数脉冲 ----------------

    private void TickPulses(double songTime)
    {
        Dictionary<string, double>? sums = null;
        foreach (var cue in _cues)
        {
            if (cue.Kind != VtsCueKind.Pulse) continue;
            var env = Envelope(songTime - (cue.Time - _leadSec), cue.Duration);
            if (env <= 0) continue;
            if (Resolve(cue.Target) is not { } param) continue;

            sums ??= new Dictionary<string, double>(StringComparer.Ordinal);
            sums[param] = sums.GetValueOrDefault(param) + cue.Value * env; // 同参数多条 pulse 相加
        }

        if (sums is { Count: > 0 })
        {
            _pulsedParams.UnionWith(sums.Keys);
            _injecting = true;
            if (songTime - _lastInjectAt < MinInjectIntervalSec - 1e-9) return; // 限速：这一拍丢给下一拍
            _lastInjectAt = songTime;
            _sink.InjectAdd(sums);
            return;
        }

        if (!_injecting) return;

        // 全部结束：补发一次全 0，把参数放回追踪源（add 模式的值会一直留着）
        ReleasePulseInjections();
    }

    /// <summary>
    /// 结束当前这段注入：补发一次全 0（add 模式的值会一直留在 VTS 侧），并把限速时间戳归零。
    ///
    /// 跳转时也必须走这里：限速时间戳记的是歌曲时间，往回拖之后 <c>songTime - _lastInjectAt</c> 是负数，
    /// 恒小于 30Hz 阈值，跳转目标处的整段 pulse 会被静默到播放进度追平跳转前的时刻为止；
    /// 上一位置注入的参数也得先放掉，否则它会一直挂在模型上。
    /// </summary>
    private void ReleasePulseInjections()
    {
        if (_injecting)
        {
            _injecting = false;
            var release = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var name in _pulsedParams) release[name] = 0;
            _pulsedParams.Clear();
            _sink.InjectAdd(release);
        }

        _lastInjectAt = double.NegativeInfinity;
    }

    /// <summary>包络：τ&lt;0 为 0，[0, 0.06) 线性起音，之后线性落到 τ=Duration 时为 0。</summary>
    private static double Envelope(double tau, double duration)
    {
        if (tau < 0 || duration <= 0) return 0;
        var attack = Math.Min(PulseAttackSec, duration);
        if (tau < attack) return tau / attack;
        var decay = duration - attack;
        return decay <= 0 ? 0 : Math.Max(0, 1 - (tau - attack) / decay);
    }

    // ---------------- 目标解析 ----------------

    /// <summary>"role:xxx" → <see cref="VtsCueTrack.Roles"/>；未映射返回 null（同一角色只记一条日志）。</summary>
    private string? Resolve(string target)
    {
        if (string.IsNullOrEmpty(target)) return null;
        if (!target.StartsWith("role:", StringComparison.Ordinal)) return target;

        var role = target[5..];
        if (_roles.TryGetValue(role, out var mapped) && !string.IsNullOrEmpty(mapped)) return mapped;

        if (_loggedRoles.Add(role)) Log.Warn($"[VTS] 演出 cue 的角色没有映射，已跳过：{role}");
        return null;
    }
}
