using System.Diagnostics.CodeAnalysis;
using JizuraLive.Render.Gpu;
using Xunit;
using Xunit.Sdk;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 「需要 GPU 才能跑」的测试专用 Fact。
///
/// 背景纪律（DOC/IMPLEMENTATION-ROADMAP.md §10）：GPU 不可用必须**显式跳过并解释原因**，
/// 不许 <c>if (device is null) return;</c> 静默返回——那样测试报告里是一片绿，
/// 实际上一条断言都没执行（DOC/PV-ENGINE-ARCHITECTURE.md §5 明确点名了这个读法问题）。
///
/// 实现说明：xUnit 2.x 没有内置 Skip 机制（那是 3.x 的 <c>Assert.Skip</c>/特性）；
/// <see cref="FactAttribute.Skip"/> 在 2.9.3 是 virtual 的，所以这里覆写它，
/// 在发现阶段就求值，命中时测试跑成 Skipped 而不是 Passed——不引入任何新 NuGet 依赖。
/// </summary>
public sealed class GpuFactAttribute : FactAttribute
{
    public GpuFactAttribute() => Skip = GpuSkip.ReasonIfUnavailable();
}

/// <summary>
/// GPU 可用性判定：探测 D3D11 设备能否创建，并给出可读的跳过原因。
/// 探测结果按进程缓存——[GpuFact] 的 Skip 在**发现阶段**求值，同一次运行里可能问很多次。
/// </summary>
public static class GpuSkip
{
    private static string? _reason;

    /// <summary>无 GPU 时的跳过原因；有 GPU 时为空字符串。</summary>
    public static string ReasonIfUnavailable()
    {
        if (_reason is not null) return _reason;

        string reason;
        try
        {
            using var probe = D3DDevice.TryCreate(out var error);
            reason = probe is null
                ? $"需要 D3D11 设备（WARP 亦不可用）：{FirstLine(error)}"
                : "";
        }
        catch (Exception ex)
        {
            // 探测本身炸了（缺原生依赖等）也按不可用处理，原因照实写
            reason = "D3D11 设备探测异常：" + FirstLine(ex.Message);
        }

        _reason = reason;
        return reason;
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "(未给出原因)";
        var idx = text.IndexOfAny(['\r', '\n']);
        var line = idx < 0 ? text : text[..idx];
        line = line.Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}

/// <summary>
/// GPU 用例体内的兜底断言：走到这里说明「探测时说有 GPU、真建设备却失败」，
/// 或者设备在用例中途被移除。两种都是真问题，必须失败——不能 return 了事。
/// </summary>
public static class GpuRequire
{
    /// <summary>
    /// 断言设备已就绪；<paramref name="why"/> 是 <see cref="D3DDevice.TryCreate"/> 给出的失败原因（可空）。
    /// 标注 <see cref="DoesNotReturnAttribute"/>：调用方在 <c>if (gpu is null) GpuRequire.Fail();</c>
    /// 之后即可把 <c>gpu</c> 当非空用，不必到处写 <c>gpu!</c>。
    /// </summary>
    [DoesNotReturn]
    public static void Fail(string? why = null) =>
        Assert.Fail("本用例需要 D3D11 设备，但设备创建失败"
                    + (string.IsNullOrWhiteSpace(why) ? "" : $"：{FirstLine(why!)}")
                    + "。若这是无 GPU 的机器，本应由 [GpuFact] 判为跳过而不是失败——"
                    + "因此这里失败表示探测与实测不一致（设备中途被移除/驱动重置），需排查。");

    private static string FirstLine(string text)
    {
        var idx = text.IndexOfAny(['\r', '\n']);
        var line = idx < 0 ? text : text[..idx];
        line = line.Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}
