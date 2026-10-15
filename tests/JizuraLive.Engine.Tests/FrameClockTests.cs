using System.Diagnostics;
using JizuraLive.Render.Gpu;

namespace JizuraLive.Engine.Tests;

/// <summary>帧节奏（<see cref="FrameClock"/>）的纯逻辑测试——不需要 GPU/窗口。</summary>
public class FrameClockTests
{
    [Fact]
    public void NoWaitable_FallsBackToStopwatchPacing()
    {
        using var clock = new FrameClock(IntPtr.Zero);
        Assert.False(clock.Waitable);
        Assert.Equal(0, clock.Frame);
    }

    [Fact]
    public void WaitNext_AdvancesFrameAndReportsNonNegativeDt()
    {
        using var clock = new FrameClock(IntPtr.Zero);
        for (var i = 0; i < 50; i++) clock.WaitNext();

        Assert.Equal(50, clock.Frame);
        Assert.True(clock.Dt >= 0, $"Dt 不应为负：{clock.Dt}");
        Assert.True(clock.Fps >= 0, $"Fps 不应为负：{clock.Fps}");
    }

    [Fact]
    public void WaitNext_FirstFrameDtIsZero()
    {
        // 首帧没有「上一帧」，dt 必须是 0——否则下游按 dt 做的动画会在第一帧跳一下
        using var clock = new FrameClock(IntPtr.Zero);
        clock.WaitNext();
        Assert.Equal(0, clock.Dt);
    }

    [Fact]
    public void RequestStop_WakesWaitingWaitNext()
    {
        // 停机握手的契约：帧等待被切成 64ms 切片，RequestStop 后 WaitNext 必须立刻返回，
        // 而不是把渲染线程按在帧等待对象的 1s 超时上（那会让 StopRender 的 Join 预算被白等）
        using var clock = new FrameClock(IntPtr.Zero);
        var entered = new ManualResetEventSlim(false);

        var worker = new Thread(() =>
        {
            entered.Set();
            clock.WaitNext();
        }) { IsBackground = true };
        worker.Start();

        Assert.True(entered.Wait(1000), "等待线程未启动");
        Thread.Sleep(50);
        clock.RequestStop();

        var sw = Stopwatch.StartNew();
        Assert.True(worker.Join(200), $"RequestStop 未在 200ms 内唤醒 WaitNext（实测 {sw.ElapsedMilliseconds}ms）");
    }

    [Fact]
    public void WaitNext_AfterRequestStop_StillAdvancesStats()
    {
        // 停止只该影响等待时长，不该让统计停下来：停后仍要能算出 dt 并推进帧号，
        // 否则收尾那一两帧的 dt 会是 0/NaN，下游动画在关门前最后一帧跳一下
        using var clock = new FrameClock(IntPtr.Zero);
        clock.WaitNext();
        var frameBefore = clock.Frame;

        clock.RequestStop();
        clock.WaitNext();

        Assert.Equal(frameBefore + 1, clock.Frame);
        Assert.True(clock.Dt > 0, $"停后 WaitNext 仍应更新 Dt：{clock.Dt}");
    }
}
