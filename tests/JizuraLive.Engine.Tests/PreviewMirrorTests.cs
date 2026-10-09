using System.Diagnostics;
using JizuraLive.App;
using SkiaSharp;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 预览镜像的新帧信号：预览泵靠它跟 Spout 的发布同拍（两套节拍器错拍时预览会「一顿一顿」，
/// 而 Spout 输出本身是满帧的）。这里只验证信号本身：发布唤醒等待方、无发布时有界超时、
/// 预览泵落后（许可累积）时不堵发布方。
/// </summary>
public class PreviewMirrorTests
{
    [Fact]
    public void 发布后等待方立刻被唤醒()
    {
        var m = new PreviewMirror();
        m.Request(64, 36);
        Assert.NotNull(m.BeginWrite(64, 36));
        var t0 = Stopwatch.GetTimestamp();
        m.EndWrite();
        Assert.True(m.WaitNewFrame(500), "发布之后应该等到新帧");
        var ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        Assert.True(ms < 100, $"等待 {ms:F0}ms 才醒，说明信号没起作用");
        Assert.Equal(1, m.Seq);
    }

    [Fact]
    public void 没有发布时有界超时()
    {
        var m = new PreviewMirror();
        m.Request(64, 36);
        var t0 = Stopwatch.GetTimestamp();
        Assert.False(m.WaitNewFrame(60), "没有新帧时不该等到");
        var ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        Assert.True(ms >= 40, $"只等了 {ms:F0}ms，说明提前返回了");
    }

    [Fact]
    public void 预览未取走上一帧时跳过读回()
    {
        var m = new PreviewMirror();
        m.Request(64, 36);
        Assert.NotNull(m.BeginWrite(64, 36));
        m.EndWrite();
        Assert.Equal(1, m.Seq);
        // 消费闸：上一帧还压在预览侧 → 本次读回（GPU 同步点）跳过，不白烧 GPU/PCIe
        Assert.Null(m.BeginWrite(64, 36));
        Assert.Equal(1, m.Seq);
        // 预览取走之后恢复读回
        m.WithLatest(_ => { });
        Assert.NotNull(m.BeginWrite(64, 36));
        m.EndWrite();
        Assert.Equal(2, m.Seq);
    }

    [Fact]
    public void 未请求镜像时不发布()
    {
        var m = new PreviewMirror();
        m.Request(0, 0); // ≤1 = 关闭镜像
        Assert.Null(m.BeginWrite(64, 36));
        Assert.False(m.Wanted);
    }
}
