using JizuraLive.Logging;

namespace JizuraLive.Vts;

public sealed class VtsCueScheduler
{
    private const double ExpressionFadeSec = 0.4;

    private const double MinInjectIntervalSec = 1.0 / 30.0;

    private const double PulseAttackSec = 0.06;

    private const double BackJumpSec = 0.05;
    private const double ForwardJumpSec = 0.5;

    private readonly IVtsCueSink _sink;
    private readonly HashSet<string> _loggedRoles = new(StringComparer.Ordinal);

    private List<VtsCue> _cues = new();
    private Dictionary<string, string> _roles = new(StringComparer.Ordinal);
    private double _leadSec = 0.08;

    private int _cursor;

    private bool _hasLastTime;
    private double _lastSongTime;

    private readonly Dictionary<string, bool> _expressionOn = new(StringComparer.Ordinal);

    private readonly Dictionary<string, double> _expressionCloseAt = new(StringComparer.Ordinal);

    private readonly HashSet<string> _pulsedParams = new(StringComparer.Ordinal);
    private double _lastInjectAt = double.NegativeInfinity;
    private bool _injecting;

    public VtsCueScheduler(IVtsCueSink sink) => _sink = sink;

    public void Load(VtsCueTrack track)
    {
        _cues = (track?.Cues ?? new List<VtsCue>())
            .Where(c => c is not null)
            .OrderBy(c => c.Time)
            .ToList();
        _roles = track?.Roles ?? new Dictionary<string, string>(StringComparer.Ordinal);

        var leadMs = track?.LeadMs ?? 0;
        _leadSec = double.IsFinite(leadMs) ? Math.Max(0, leadMs) / 1000.0 : 0;
        Reset();
    }

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

    public void Tick(double songTime, bool playing)
    {
        if (!playing) return;

        var jumped = _hasLastTime
            ? songTime < _lastSongTime - BackJumpSec || songTime > _lastSongTime + ForwardJumpSec
            : songTime > ForwardJumpSec;
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

            }
        }
    }

    private int FirstIndexAfter(double songTime)
    {
        for (var i = 0; i < _cues.Count; i++)
            if (_cues[i].Time - _leadSec > songTime) return i;
        return _cues.Count;
    }

    private void ApplyExpressionCue(VtsCue cue)
    {
        if (Resolve(cue.Target) is not { } file) return;

        var on = cue.Value > 0.5;
        SetExpressionState(file, on);

        if (on && cue.Duration > 0) _expressionCloseAt[file] = cue.Time + cue.Duration - _leadSec;
        else _expressionCloseAt.Remove(file);
    }

    private void SetExpressionState(string file, bool on)
    {
        if (_expressionOn.TryGetValue(file, out var current))
        {
            if (current == on) return;
        }
        else if (!on)
        {
            return;
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

    private void AlignExpressions(double songTime)
    {
        _expressionCloseAt.Clear();

        var desired = new Dictionary<string, (bool On, double CloseAt)>(StringComparer.Ordinal);
        foreach (var cue in _cues)
        {
            if (cue.Kind != VtsCueKind.Expression) continue;
            if (cue.Time - _leadSec > songTime) break;
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

        foreach (var file in _expressionOn.Keys.ToList())
            if (!desired.ContainsKey(file)) SetExpressionState(file, false);
    }

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
            sums[param] = sums.GetValueOrDefault(param) + cue.Value * env;
        }

        if (sums is { Count: > 0 })
        {
            _pulsedParams.UnionWith(sums.Keys);
            _injecting = true;
            if (songTime - _lastInjectAt < MinInjectIntervalSec - 1e-9) return;
            _lastInjectAt = songTime;
            _sink.InjectAdd(sums);
            return;
        }

        if (!_injecting) return;

        ReleasePulseInjections();
    }

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

    private static double Envelope(double tau, double duration)
    {
        if (tau < 0 || duration <= 0) return 0;
        var attack = Math.Min(PulseAttackSec, duration);
        if (tau < attack) return tau / attack;
        var decay = duration - attack;
        return decay <= 0 ? 0 : Math.Max(0, 1 - (tau - attack) / decay);
    }

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
