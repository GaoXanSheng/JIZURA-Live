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
}
