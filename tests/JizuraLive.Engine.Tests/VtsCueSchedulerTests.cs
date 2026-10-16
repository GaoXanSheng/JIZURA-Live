using JizuraLive.Vts;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 演出 cue 调度器的回归闸：提前量、不重复触发、跳转（不补发 / 表情对齐 / 重新可触发）、
/// 表情到期自动关、角色映射、pulse 包络与限速、暂停期静默。
///
/// 全部用假 sink 记录调用——不连真实 VTS（用户可能正在直播，任何热键/表情都会出现在观众画面上）。
/// </summary>
public class VtsCueSchedulerTests
{
    private sealed class FakeSink : IVtsCueSink
    {
        public List<string> Order { get; } = new();
        public List<string> Hotkeys { get; } = new();
        public List<(string File, bool Active, double Fade)> Expressions { get; } = new();
        public List<(double X, double Y, double Rotation, double Size, double Seconds)> Moves { get; } = new();
        public List<Dictionary<string, double>> Injects { get; } = new();

        public int Calls => Order.Count;

        public void TriggerHotkey(string nameOrId)
        {
            Hotkeys.Add(nameOrId);
            Order.Add("hotkey:" + nameOrId);
        }

        public void SetExpression(string expressionFile, bool active, double fadeSec)
        {
            Expressions.Add((expressionFile, active, fadeSec));
            Order.Add($"expression:{expressionFile}:{(active ? "on" : "off")}");
        }

        public void MoveModelRelative(double x, double y, double rotation, double size, double seconds)
        {
            Moves.Add((x, y, rotation, size, seconds));
            Order.Add("move");
        }

        public void InjectAdd(IReadOnlyDictionary<string, double> values)
        {
            Injects.Add(new Dictionary<string, double>(values, StringComparer.Ordinal));
            Order.Add("inject");
        }
    }

    private static VtsCueTrack Track(double leadMs, params VtsCue[] cues) =>
        new() { LeadMs = leadMs, Cues = cues.ToList() };

    /// <summary>按 from→to 均匀推 steps+1 拍（步长必须远小于跳转判定线，这才是正常播放的样子）。</summary>
    private static void Play(VtsCueScheduler s, double from, double to, int steps)
    {
        for (var i = 0; i <= steps; i++) s.Tick(from + (to - from) * i / steps, true);
    }

    [Fact]
    public void LeadFiresOnceAndNotAgainAfterwards()
    {
        var sink = new FakeSink();
        var s = new VtsCueScheduler(sink);
        s.Load(Track(100, new VtsCue { Time = 1.0, Kind = VtsCueKind.Hotkey, Target = "Wave" }));

        Play(s, 0, 0.85, 17);            // 提前量 100ms：0.9 之前不许发
        Assert.Empty(sink.Hotkeys);

        Play(s, 0.85, 0.95, 10);
        Assert.Equal(new[] { "Wave" }, sink.Hotkeys);

        Play(s, 0.95, 3.0, 41);          // 过去之后不再重复
        Assert.Single(sink.Hotkeys);
    }

    [Fact]
    public void CuesFireInTimeOrderAndOutOfOrderInputIsSorted()
    {
        var sink = new FakeSink();
        var s = new VtsCueScheduler(sink);
        s.Load(Track(0,
            new VtsCue { Time = 1.2, Kind = VtsCueKind.Hotkey, Target = "B" },
            new VtsCue { Time = 1.0, Kind = VtsCueKind.Hotkey, Target = "A" },
            new VtsCue { Time = 1.05, Kind = VtsCueKind.Move, X = 5, Y = -3, Rotation = 7, Value = 0.2, Duration = 0.5 }));

        Play(s, 0, 1.3, 26);

        Assert.Equal(new[] { "hotkey:A", "move", "hotkey:B" }, sink.Order);
        Assert.Equal((5.0, -3.0, 7.0, 0.2, 0.5), Assert.Single(sink.Moves));
    }

    [Fact]
    public void ForwardJumpSkipsMissedCuesWithoutReplay()
    {
        var sink = new FakeSink();
        var s = new VtsCueScheduler(sink);
        s.Load(Track(0,
            new VtsCue { Time = 1, Kind = VtsCueKind.Hotkey, Target = "h1" },
            new VtsCue { Time = 2, Kind = VtsCueKind.Hotkey, Target = "h2" },
            new VtsCue { Time = 3, Kind = VtsCueKind.Hotkey, Target = "h3" }));

        Play(s, 0, 1.2, 12);
        Assert.Equal(new[] { "h1" }, sink.Hotkeys);

        // 往前 1.7s（> 0.5s）＝拖进度条：中间的 h2 直接跳过，不许补发
        Play(s, 2.9, 3.05, 3);
        Assert.Equal(new[] { "h1", "h3" }, sink.Hotkeys);
    }

    [Fact]
    public void FirstPlayingTickMidSongDoesNotReplayEarlierCues()
    {
        var sink = new FakeSink();
        var s = new VtsCueScheduler(sink);
        s.Load(Track(0,
            new VtsCue { Time = 1, Kind = VtsCueKind.Hotkey, Target = "h1" },
            new VtsCue { Time = 2, Kind = VtsCueKind.Hotkey, Target = "h2" },
            new VtsCue { Time = 11, Kind = VtsCueKind.Hotkey, Target = "h3" }));

        s.Tick(0, false);                // 暂停在开头，随后拖到中段再播放
        Play(s, 10, 11.05, 21);

        Assert.Equal(new[] { "h3" }, sink.Hotkeys);
    }

    [Fact]
    public void BackwardJumpMakesLaterCuesTriggerableAgain()
    {
        var sink = new FakeSink();
        var s = new VtsCueScheduler(sink);
        s.Load(Track(0,
            new VtsCue { Time = 1, Kind = VtsCueKind.Hotkey, Target = "h1" },
            new VtsCue { Time = 2, Kind = VtsCueKind.Hotkey, Target = "h2" }));

        Play(s, 0, 2.2, 22);
        Assert.Equal(new[] { "h1", "h2" }, sink.Hotkeys);

        // 往回拖：跳转本身不补发（h2 已过），但 h1/h2 重新可触发
        s.Tick(0.5, true);
        Assert.Equal(2, sink.Calls);

        Play(s, 0.5, 2.2, 17);
        Assert.Equal(new[] { "h1", "h2", "h1", "h2" }, sink.Hotkeys);
    }

    [Fact]
    public void JumpIntoChorusIntervalReappliesExpressionOn()
    {
        var sink = new FakeSink();
        var s = new VtsCueScheduler(sink);
        var track = Track(0,
            new VtsCue { Time = 10, Kind = VtsCueKind.Expression, Target = "role:chorus", Value = 1 },
            new VtsCue { Time = 20, Kind = VtsCueKind.Expression, Target = "role:chorus", Value = 0 });
        track.Roles[VtsRoles.Chorus] = "chorus.exp3.json";
        s.Load(track);

        Play(s, 0, 5, 10);
        Assert.Empty(sink.Expressions);

        // 跳进 [副歌开, 副歌关) 区间：开状态必须补发，否则观众看到的是没表情的副歌
        s.Tick(15, true);
        var on = Assert.Single(sink.Expressions);
        Assert.Equal(("chorus.exp3.json", true), (on.File, on.Active));

        Play(s, 15, 20.2, 52);
        Assert.Equal(new[] { true, false }, sink.Expressions.Select(e => e.Active));
    }

    [Fact]
    public void ExpressionWithDurationClosesItself()
    {
        var sink = new FakeSink();
        var s = new VtsCueScheduler(sink);
        s.Load(Track(0, new VtsCue
        {
            Time = 1, Kind = VtsCueKind.Expression, Target = "smile.exp3.json", Value = 1, Duration = 2,
        }));

        Play(s, 0, 1.05, 21);
        Assert.Equal(new[] { true }, sink.Expressions.Select(e => e.Active));

        Play(s, 1.05, 3.1, 41);   // 到期 1+2=3.0
        Assert.Equal(new[] { true, false }, sink.Expressions.Select(e => e.Active));
    }

    [Fact]
    public void UnmappedRoleIsSkippedAndMappedRoleResolves()
    {
        var sink = new FakeSink();
        var s = new VtsCueScheduler(sink);
        var track = Track(0,
            new VtsCue { Time = 1, Kind = VtsCueKind.Hotkey, Target = "role:chorus" },
            new VtsCue { Time = 1, Kind = VtsCueKind.Hotkey, Target = "role:outro" });
        track.Roles[VtsRoles.Outro] = "wave";
        s.Load(track);

        Play(s, 0, 1.5, 15);

        Assert.Equal(new[] { "wave" }, sink.Hotkeys);   // 未映射的角色整条跳过，不抛异常
    }

    [Fact]
    public void PulseEnvelopeRisesToHalfAtHalfAttackAndReleasesAtEnd()
    {
        var sink = new FakeSink();
        var s = new VtsCueScheduler(sink);
        s.Load(Track(0, new VtsCue
        {
            Time = 1.0, Kind = VtsCueKind.Pulse, Target = "FaceAngleY", Value = 10, Duration = 0.3,
        }));

        // τ=0.03 = 起音（60ms）的一半 → 0.5 × Value
        s.Tick(1.03, true);
        Assert.Equal(5.0, Assert.Single(sink.Injects)["FaceAngleY"], 6);

        // τ=Duration → 包络为 0（多推 10ms 避开浮点毛刺）：不再注入，并补发一次全 0 把参数放回追踪源
        s.Tick(1.31, true);
        Assert.Equal(2, sink.Injects.Count);
        Assert.Equal(0.0, sink.Injects[1]["FaceAngleY"]);

        s.Tick(1.5, true);   // 释放只发一次
        Assert.Equal(2, sink.Injects.Count);
    }

    [Fact]
    public void PulsesOnSameParameterAddUp()
    {
        var sink = new FakeSink();
        var s = new VtsCueScheduler(sink);
        s.Load(Track(0,
            new VtsCue { Time = 1.0, Kind = VtsCueKind.Pulse, Target = "FaceAngleY", Value = 2, Duration = 0.3 },
            new VtsCue { Time = 1.0, Kind = VtsCueKind.Pulse, Target = "FaceAngleY", Value = 3, Duration = 0.3 }));

        s.Tick(1.06, true);   // 两条都到峰值（env=1）
        Assert.Equal(5.0, Assert.Single(sink.Injects)["FaceAngleY"], 6);
    }

    [Fact]
    public void InjectRateDoesNotExceedThirtyHertz()
    {
        var sink = new FakeSink();
        var s = new VtsCueScheduler(sink);
        s.Load(Track(0, new VtsCue
        {
            Time = 0.9, Kind = VtsCueKind.Pulse, Target = "MouthSmile", Value = 1, Duration = 5,
        }));

        for (var k = 0; k <= 120; k++) s.Tick(1.0 + k / 120.0, true);   // 120Hz 推一拍，跑 1 秒

        // 1 秒内最多 31 次（首拍 + 每 1/30 秒一次）
        Assert.InRange(sink.Injects.Count, 29, 31);
    }

    [Fact]
    public void BackwardJumpDoesNotThrottlePulsesAtTheJumpTarget()
    {
        var sink = new FakeSink();
        var s = new VtsCueScheduler(sink);
        s.Load(Track(0, new VtsCue
        {
            Time = 1.0, Kind = VtsCueKind.Pulse, Target = "FaceAngleY", Value = 10, Duration = 5,
        }));

        Play(s, 1.0, 1.5, 5);                       // 正常播放：段内持续注入
        var before = sink.Injects.Count;
        Assert.True(before > 0);

        // 往回拖 0.45s（> 0.05s 判定线）＝跳转：跳转目标处的 pulse 必须按新位置立刻注入。
        // 限速用的是歌曲时间戳，往回跳后它是负数——不重置就会一直小于 30Hz 阈值，整段被静默。
        s.Tick(1.05, true);

        Assert.True(sink.Injects.Count > before, "跳转目标处的 pulse 被限速静默了");
        Assert.Equal(10 * (0.05 / 0.06), sink.Injects[^1]["FaceAngleY"], 6);
    }

    [Fact]
    public void BackwardJumpReleasesPulsesInjectedAtThePreviousPosition()
    {
        var sink = new FakeSink();
        var s = new VtsCueScheduler(sink);
        s.Load(Track(0,
            new VtsCue { Time = 1.0, Kind = VtsCueKind.Pulse, Target = "MouthOpen", Value = 8, Duration = 5 },
            new VtsCue { Time = 10.0, Kind = VtsCueKind.Pulse, Target = "FaceAngleY", Value = 10, Duration = 5 }));

        s.Tick(9.0, true);                          // 从 9s 起播（1s 那条 pulse 早被游标跳过，不注入）
        Play(s, 9.0, 10.5, 15);                     // 进入 10s 那条：注入 FaceAngleY
        Assert.Contains(sink.Injects, d => d.ContainsKey("FaceAngleY") && d["FaceAngleY"] > 0);

        s.Tick(1.5, true);                          // 往回拖进 1s 那条 pulse：换参数

        // add 模式的值会一直留在 VTS 侧：上一位置的 FaceAngleY 必须先补发全 0 放回追踪源
        var release = sink.Injects[^2];
        Assert.Equal(0.0, release["FaceAngleY"]);
        Assert.False(release.ContainsKey("MouthOpen"));

        // 然后是跳转目标处该有的新参数（τ=0.5 → 包络 1-(0.5-0.06)/(5-0.06)）
        var now = sink.Injects[^1];
        Assert.False(now.ContainsKey("FaceAngleY"));
        Assert.Equal(8 * (1 - (0.44 / 4.94)), now["MouthOpen"], 6);
    }

    [Fact]
    public void PausedTicksEmitNothingAndDoNotAdvanceTheCursor()
    {
        var sink = new FakeSink();
        var s = new VtsCueScheduler(sink);
        s.Load(Track(0,
            new VtsCue { Time = 0.1, Kind = VtsCueKind.Hotkey, Target = "A" },
            new VtsCue { Time = 0.0, Kind = VtsCueKind.Pulse, Target = "FaceAngleY", Value = 3, Duration = 0.3 }));

        s.Tick(5.0, false);
        s.Tick(8.0, false);
        Assert.Equal(0, sink.Calls);

        // 暂停期间不推进游标也不记时间：恢复时该发的照常发（τ=0.1 → 包络 5/6）
        s.Tick(0.1, true);
        Assert.Equal(new[] { "A" }, sink.Hotkeys);
        Assert.Equal(2.5, Assert.Single(sink.Injects)["FaceAngleY"], 6);
    }
}
