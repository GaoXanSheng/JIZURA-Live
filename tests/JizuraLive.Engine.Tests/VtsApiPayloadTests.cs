using JizuraLive.Vts;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// VTS 请求负载的形状闸（纯函数，不发任何东西给 VTS）：
/// 表情开关的字段名必须是官方 API 的 <c>expressionFile</c>（曾写过不存在的 <c>expression</c> 字段 +
/// 不存在的 <c>SetExpressionStateRequest</c>，两边都错、还自洽），注入的 mode 在 add/set 之间要分明。
/// </summary>
public class VtsApiPayloadTests
{
    [Fact]
    public void ExpressionPayloadUsesOfficialFieldsAndClampsFade()
    {
        var on = VtsApi.BuildExpressionPayload("smile.exp3.json", true, 5.0);

        Assert.Equal("smile.exp3.json", (string?)on["expressionFile"]);
        Assert.True((bool?)on["active"]);
        Assert.Equal(2.0, (double?)on["fadeTime"]);   // VTS 只接受 0~2 秒，超了要夹住
        Assert.Null(on["expression"]);                // 旧字段名在官方 API 里不存在
        Assert.Equal(new[] { "expressionFile", "active", "fadeTime" }, on.Properties().Select(p => p.Name).ToArray());

        var off = VtsApi.BuildExpressionPayload("smile.exp3.json", false, -1.0);
        Assert.False((bool?)off["active"]);
        Assert.Equal(0.0, (double?)off["fadeTime"]);
    }

    [Fact]
    public void InjectPayloadDefaultsToSetModeAndAddModeIsExplicit()
    {
        var values = new Dictionary<string, double> { ["FaceAngleY"] = 5 };
        var ranges = new Dictionary<string, (double Min, double Max)> { ["FaceAngleY"] = (-30, 30) };

        var injected = new HashSet<string>();
        var set = VtsApi.BuildInjectPayload(values, 1.0, ranges, injected)!;

        Assert.Equal("set", (string?)set["mode"]);
        Assert.Equal(30.0, (double?)set["parameterValues"]![0]!["value"]);   // set：0~1 归一化 → 参数范围上限
        Assert.Contains("FaceAngleY", injected);

        var addInjected = new HashSet<string>();
        var add = VtsApi.BuildInjectPayload(values, 1.0, ranges, addInjected, "add")!;

        Assert.Equal("add", (string?)add["mode"]);
        Assert.Contains("\"mode\":\"add\"", add.ToString(Formatting.None));
        // add：值是参数自身量纲的增量，原样叠加（5 度就是 5，不能被归一化夹成 1 再映射到 30）
        Assert.Equal(5.0, (double?)add["parameterValues"]![0]!["value"]);
        Assert.Equal(1.0, (double?)add["parameterValues"]![0]!["weight"]);
        Assert.Empty(addInjected);   // add 不记注入账，释放由调用方显式发全 0
    }
}
