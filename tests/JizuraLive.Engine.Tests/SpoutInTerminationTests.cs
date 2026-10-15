using System.Reflection;
using JizuraLive.Spout;
using Vortice.Direct3D11;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// M1 路线图第 6 步「SpoutIn 可终止性」的**托管侧**测试。
///
/// 覆盖范围与边界（重要，别在这里造假测试）：
///   · 覆盖：<see cref="SpoutIn.Pump"/> 的停机相关纯逻辑——disabled 短路、失败退避时序、
///     <c>_gaveUp</c> 终态语义、Dispose 幂等。这些都不碰 GPU。
///   · 不覆盖（只能靠 tools 层实测）：native <c>ReceiveTexturePtr</c> 的阻塞性、无人发送时的
///     返回耗时、源消失后第一帧的 ~67ms 确定性代价、MaxReceiveMs 量级。
///     证据见 tools/SpoutProbe（Q1–Q6）与 DOC/PV-ENGINE-ARCHITECTURE.md「验证证据」节。
///
/// 为什么不用真设备：构造 <see cref="SpoutIn"/> 只存设备指针，失败路径（OpenDirectX 抛异常）
/// 根本走不到 native 调用点，因此传 null 设备即可驱动完整的失败/退避/放弃状态机。
/// 传 null 是**故意的**：本文件测的是状态机，不是 D3D。
/// </summary>
public class SpoutInTerminationTests
{
    private readonly ITestOutputHelper _out;

    public SpoutInTerminationTests(ITestOutputHelper output) => _out = output;

    private const string Sender = "JizuraTestSpout";

    /// <summary>读私有字段（_fails/_gaveUp/_retryAfterTs 是纯托管状态机，没有 internal 出口）。</summary>
    private static T Field<T>(SpoutIn rx, string name)
    {
        var f = typeof(SpoutIn).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(f);
        return (T)f!.GetValue(rx)!;
    }

    /// <summary>
    /// enabled=false 必须短路返回：不建 receiver、不碰设备、不置 _gaveUp。
    /// 这是停机握手「先停泵再停线程」能成立的前提——上层把 enabled 翻成 false 就得立刻空转。
    /// </summary>
    [Fact]
    public void Pump_Disabled_ShortCircuitsWithoutTouchingDevice()
    {
        using var rx = new SpoutIn(null!);

        for (var i = 0; i < 10; i++)
            Assert.False(rx.Pump(false, Sender), "enabled=false 时必须返回 false");

        Assert.False(rx.Connected);
        Assert.Equal(0, Field<int>(rx, "_fails"));
        Assert.False(Field<bool>(rx, "_gaveUp"));
        Assert.False(Field<object?>(rx, "_rx") is not null, "enabled=false 且从未开过时不该建 receiver");
    }

    /// <summary>
    /// _gaveUp 语义：连续失败 <see cref="MaxFails"/>（5）次后 Pump 恒 false，且**不再尝试**。
    /// 失败路径在原生 OpenDirectX 上——这里用 null 设备驱动它必然抛异常，
    /// 走的是与真实设备失败完全相同的 Fail() 状态机。
    /// 注意退避：每次失败后 2s 内 Pump 被挡，所以五次失败必须跨过退避窗口逐次驱动。
    /// </summary>
    [Fact]
    public void Pump_GivesUpAfterFiveConsecutiveFailures_AndStaysGivenUp()
    {
        using var rx = new SpoutIn(null!);

        for (var i = 1; i <= 5; i++)
        {
            WaitPastRetry(rx);
            Assert.False(rx.Pump(true, Sender), $"第 {i} 次失败也必须返回 false");
            Assert.Equal(i, Field<int>(rx, "_fails"));
            // 前 4 次还不能放弃——重试机会要给够
            Assert.Equal(i >= 5, Field<bool>(rx, "_gaveUp"));
        }

        // 放弃之后再调用：连尝试都不做（_fails 不再增长），且与 enabled 无关
        WaitPastRetry(rx);
        Assert.False(rx.Pump(true, Sender));
        Assert.False(rx.Pump(false, Sender));
        Assert.Equal(5, Field<int>(rx, "_fails"));
        Assert.True(Field<bool>(rx, "_gaveUp"));
        Assert.False(string.IsNullOrWhiteSpace(rx.LastError), "放弃时必须留下原因，否则现场无法诊断");
        _out.WriteLine($"LastError = {rx.LastError}");
    }

    /// <summary>等到 <c>_retryAfterTs</c> 之后（并留 50ms 余量），让下一次 Pump 真的去尝试。</summary>
    private static void WaitPastRetry(SpoutIn rx)
    {
        var retryAfter = Field<long>(rx, "_retryAfterTs");
        var waitMs = retryAfter - Environment.TickCount64 + 50;
        if (waitMs > 0) Thread.Sleep((int)Math.Min(waitMs, 5000));
    }

    /// <summary>
    /// 重试退避：失败后 <see cref="RetryBackoffMs"/>（2s）内 Pump 直接返回、**不尝试**；
    /// 窗口过后才恢复尝试。这是「一台机器上源连不上时不会每帧猛戳 native」的保证。
    /// </summary>
    [Fact]
    public void Pump_BacksOffForTwoSecondsAfterFailure_ThenResumes()
    {
        using var rx = new SpoutIn(null!);

        // 第 1 次：真的尝试了（失败）
        Assert.False(rx.Pump(true, Sender));
        Assert.Equal(1, Field<int>(rx, "_fails"));
        var retryAfter = Field<long>(rx, "_retryAfterTs");
        Assert.True(retryAfter > Environment.TickCount64, "失败后必须排一个未来的重试时刻");

        // 退避窗口内连续调用：_fails 不许增长（证明是被退避挡下，不是又试了一次）
        for (var i = 0; i < 50; i++) Assert.False(rx.Pump(true, Sender));
        Assert.Equal(1, Field<int>(rx, "_fails"));

        // 等到窗口过去：尝试恢复（_fails 增长）
        WaitPastRetry(rx);
        Assert.False(rx.Pump(true, Sender));
        Assert.Equal(2, Field<int>(rx, "_fails"));
    }

    /// <summary>
    /// 关闭幂等：Dispose 可重复调用、可与 Pump(false) 交替调用，不抛、不卡。
    /// 停机握手里 UI 线程与渲染线程都可能碰到收尾，重复释放必须无害。
    /// </summary>
    [Fact]
    public void Dispose_IsIdempotent_AndSafeWithDisabledPump()
    {
        var rx = new SpoutIn(null!);

        rx.Pump(false, Sender);
        rx.Dispose();
        rx.Dispose(); // 二次 Dispose 不许抛

        Assert.False(rx.Connected);
        Assert.False(rx.Pump(false, Sender));
    }

    /// <summary>
    /// 文档钉死：<c>_gaveUp</c> 在本实例内永不清除，恢复靠删/加图层新建 provider
    /// （见 PvPipeline.ApplyLayerOps）。这条测试是防止有人「顺手」在 Pump 里清它——
    /// 清了就会变成无限重试，把失败状态从「可观测」变成「永远在抖」。
    /// </summary>
    [Fact]
    public void GaveUp_IsNeverCleared_ByPump_EvenAfterLongGap()
    {
        using var rx = new SpoutIn(null!);
        for (var i = 1; i <= 5; i++)
        {
            WaitPastRetry(rx);
            rx.Pump(true, Sender);
        }

        Assert.True(Field<bool>(rx, "_gaveUp"));

        // 后续 Pump 一律不动它：既不被时间冲掉，也不随 enabled 变化。
        // 不靠睡眠证明「时间过去也不自愈」——_gaveUp 只在 Fail() 里置位，没有任何清零点，
        // 终态是结构保证而非时序保证；用 Sleep(30) 断言只会把用例变成 flaky。
        Assert.False(rx.Pump(true, Sender));
        Assert.False(rx.Pump(false, Sender));
        Assert.True(Field<bool>(rx, "_gaveUp"));
    }
}
