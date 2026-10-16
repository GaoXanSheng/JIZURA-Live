using JizuraLive.Pv;
using JizuraLive.Render.Effects;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 效果参数的曲线直接求值（<see cref="PvPipeline.ApplyEffectCurves"/>）：地址
/// <c>effect:&lt;InstanceId&gt;.&lt;param&gt;</c> 上有曲线就写进效果实例，按 schema 的 min/max 夹取，
/// 非有限值整条跳过。纯函数用例：无 GPU、无文件（管线本体要 D3D 设备，构造不出来）。
/// </summary>
public class EffectCurveEvalTests
{
    private static EffectNode BloomNode(EffectGraph effects) => effects.Add("bloom").Nodes[0];

    private static EffectParam ParamOf(EffectNode node, string key) =>
        EffectCatalog.Get(node.DefId)!.Params.First(p => p.Key == key);

    /// <summary>单 key 曲线：这一条覆盖曲线求值路径的本意——值来自曲线，不是实例常量。</summary>
    private static DirectorEvaluator CurvesWith(string address, double value)
    {
        var curves = new DirectorEvaluator();
        Assert.True(ParamAddress.TryParse(address, out var addr), $"测试前置地址应能解析：{address}");
        var curve = new FloatCurve();
        curve.TryAdd(0.0, value);
        curves.SetCurve(addr, curve);
        return curves;
    }

    [Fact]
    public void CurveValueIsWrittenIntoTheEffectParam()
    {
        var effects = new EffectGraph();
        var node = BloomNode(effects);
        var intensity = ParamOf(node, "intensity");
        var baseline = node.Value(intensity);

        PvPipeline.ApplyEffectCurves(effects, CurvesWith($"effect:{node.InstanceId}.intensity", 2.5), 0.0);

        Assert.NotEqual(baseline, node.Value(intensity));
        Assert.Equal(2.5f, node.Value(intensity), precision: 4);
    }

    [Fact]
    public void ParamWithoutCurveKeepsItsDocumentValue()
    {
        var effects = new EffectGraph();
        var node = BloomNode(effects);
        var intensity = ParamOf(node, "intensity");
        node.Set(intensity, 0.25f);

        // 曲线只挂在 radius 上：没被曲线覆盖的参数必须一字不动
        var radius = ParamOf(node, "radius");
        PvPipeline.ApplyEffectCurves(effects, CurvesWith($"effect:{node.InstanceId}.radius", 42.0), 0.0);

        Assert.Equal(0.25f, node.Value(intensity), precision: 4);
        Assert.Equal(42f, node.Value(radius), precision: 4);
    }

    [Fact]
    public void OutOfRangeCurveValuesAreClampedToTheParamSchema()
    {
        var effects = new EffectGraph();
        var node = BloomNode(effects);
        var intensity = ParamOf(node, "intensity");

        PvPipeline.ApplyEffectCurves(effects, CurvesWith($"effect:{node.InstanceId}.intensity", 99.0), 0.0);
        Assert.Equal(intensity.Max, node.Value(intensity), precision: 4);

        PvPipeline.ApplyEffectCurves(effects, CurvesWith($"effect:{node.InstanceId}.intensity", -5.0), 0.0);
        Assert.Equal(intensity.Min, node.Value(intensity), precision: 4);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void NonFiniteCurveValueIsSkipped(double bad)
    {
        var effects = new EffectGraph();
        var node = BloomNode(effects);
        var intensity = ParamOf(node, "intensity");
        node.Set(intensity, 0.75f);

        // NaN 会被 Math.Clamp 原样放行、一路流进常量缓冲，所以必须在写入之前挡掉
        PvPipeline.ApplyEffectCurves(effects, CurvesWith($"effect:{node.InstanceId}.intensity", bad), 0.0);

        Assert.Equal(0.75f, node.Value(intensity), precision: 4);
    }

    [Fact]
    public void NullCurvesOrUnknownDefIsANoOp()
    {
        var effects = new EffectGraph();
        var node = BloomNode(effects);
        var intensity = ParamOf(node, "intensity");

        PvPipeline.ApplyEffectCurves(effects, null, 0.0);
        Assert.Equal(intensity.Default, node.Value(intensity), precision: 4);

        // 目录里没有的定义（旧存档）：跳过而不是抛
        var ghost = new EffectNode { DefId = "no-such-effect", InstanceId = "fx_ghost_1" };
        ghost.Values["amount"] = 0.5f;
        effects.Nodes.Add(ghost);

        PvPipeline.ApplyEffectCurves(effects, CurvesWith("effect:fx_ghost_1.amount", 3.0), 0.0);
        Assert.Equal(0.5f, ghost.Values["amount"], precision: 4);
    }

    [Fact]
    public void CurvesForOtherDomainsDoNotTouchEffectParams()
    {
        var effects = new EffectGraph();
        var node = BloomNode(effects);
        var intensity = ParamOf(node, "intensity");

        var curves = new DirectorEvaluator();
        var cameraScale = new FloatCurve();
        cameraScale.TryAdd(0.0, 1.7);
        curves.SetCurve(new ParamAddress(ParamAddressKind.Camera, "", "scale"), cameraScale);
        var layerOpacity = new FloatCurve();
        layerOpacity.TryAdd(0.0, 0.2);
        curves.SetCurve(new ParamAddress(ParamAddressKind.Layer, "character", "opacity"), layerOpacity);

        PvPipeline.ApplyEffectCurves(effects, curves, 0.0);

        Assert.Equal(intensity.Default, node.Value(intensity), precision: 4);
    }
}
