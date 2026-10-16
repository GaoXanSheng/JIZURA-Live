using JizuraLive.Pv;
using JizuraLive.Render.Effects;
using Newtonsoft.Json;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>曲线调制层与效果出场区间：纯函数用例，无 GPU、无文件。</summary>
public class CurveLayerTests
{
    private static readonly ParamAddress Scale = new(ParamAddressKind.Camera, "", "scale");

    private static FloatCurve Const(double v)
    {
        var c = new FloatCurve();
        c.TryAdd(0, v);
        return c;
    }

    [Fact]
    public void Resolve_BaseThenAddThenMultiply_RegardlessOfInsertionOrder()
    {
        var a = new DirectorEvaluator();
        a.SetCurve(Scale, Const(2));
        a.SetLayer(Scale, "m", CurveBlend.Multiply, Const(3));
        a.SetLayer(Scale, "a", CurveBlend.Add, Const(1));

        var b = new DirectorEvaluator();
        b.SetLayer(Scale, "a", CurveBlend.Add, Const(1));
        b.SetCurve(Scale, Const(2));
        b.SetLayer(Scale, "m", CurveBlend.Multiply, Const(3));

        Assert.Equal(9, a.Resolve(Scale, 0, 1), precision: 9);
        Assert.Equal(9, b.Resolve(Scale, 0, 1), precision: 9);
    }

    [Fact]
    public void Resolve_WithoutBaseCurve_ModulatesTheRestValue()
    {
        var e = new DirectorEvaluator();
        e.SetLayer(Scale, "m", CurveBlend.Multiply, Const(1.5));

        Assert.False(e.TryEvaluate(Scale, 0, out _));
        Assert.True(e.Affects(Scale));
        Assert.Equal(1.8, e.Resolve(Scale, 0, 1.2), precision: 9);
    }

    [Fact]
    public void SetLayer_SameNameReplaces()
    {
        var e = new DirectorEvaluator();
        e.SetLayer(Scale, "x", CurveBlend.Add, Const(1));
        e.SetLayer(Scale, "x", CurveBlend.Add, Const(5));
        Assert.Single(e.LayersOf(Scale));
        Assert.Equal(5, e.Resolve(Scale, 0, 0), precision: 9);
    }

    [Fact]
    public void Snapshot_CopiesLayersIndependently()
    {
        var e = new DirectorEvaluator();
        var layer = Const(2);
        e.SetLayer(Scale, "m", CurveBlend.Multiply, layer);
        var snap = e.Snapshot();

        layer.Keys.Clear();
        layer.TryAdd(0, 10);

        Assert.Equal(2, snap.Resolve(Scale, 0, 1), precision: 9);
    }

    [Fact]
    public void SavesRoundTrip_KeepsLayersAndBaseJsonUnchanged()
    {
        var e = new DirectorEvaluator();
        e.SetCurve(Scale, Const(1.1));
        e.SetLayer(Scale, "auto.beat", CurveBlend.Multiply, Const(1.2));

        var saves = e.ToSaves();
        var json = JsonConvert.SerializeObject(saves);
        var back = DirectorEvaluator.FromSaves(JsonConvert.DeserializeObject<List<CurveSave>>(json));

        Assert.Equal(e.Resolve(Scale, 0, 1), back.Resolve(Scale, 0, 1), precision: 9);

        // 基础曲线的存档形态不带新字段：旧版本读得懂
        var baseJson = JsonConvert.SerializeObject(saves[0]);
        Assert.DoesNotContain("Layer", baseJson);
        Assert.DoesNotContain("Blend", baseJson);
        Assert.Contains("\"Multiply\"", JsonConvert.SerializeObject(saves[1]));
    }

    [Fact]
    public void RemoveCurvesForTarget_AlsoRemovesLayers()
    {
        var addr = new ParamAddress(ParamAddressKind.Effect, "fx_a", "amount");
        var e = new DirectorEvaluator();
        e.SetLayer(addr, "m", CurveBlend.Add, Const(1));
        e.RemoveCurvesForTarget("fx_a");
        Assert.False(e.Affects(addr));
    }

    [Fact]
    public void EffectMultiplyLayer_DoesNotCompoundAcrossFrames()
    {
        var graph = new EffectGraph().Add("bloom");
        var node = graph.Nodes[0];
        var intensity = EffectCatalog.Get("bloom")!.Params.First(p => p.Key == "intensity");
        node.Set(intensity, 1.0f);

        var e = new DirectorEvaluator();
        e.SetLayer(new ParamAddress(ParamAddressKind.Effect, node.InstanceId, "intensity"), "m", CurveBlend.Multiply, Const(1.5));

        PvPipeline.ApplyEffectCurves(graph, e, 0);
        PvPipeline.ApplyEffectCurves(graph, e, 0);
        PvPipeline.ApplyEffectCurves(graph, e, 0);

        Assert.Equal(1.5f, node.Value(intensity), precision: 5);
    }

    [Fact]
    public void Window_FadesAndCompressesOverlongFades()
    {
        var w = new EffectWindow(10, 12, FadeIn: 1, FadeOut: 0.5);
        Assert.Equal(0, w.WeightAt(9.99));
        Assert.Equal(0.5, w.WeightAt(10.5), precision: 9);
        Assert.Equal(1, w.WeightAt(11.0), precision: 9);
        Assert.Equal(0.5, w.WeightAt(11.75), precision: 9);
        Assert.Equal(0, w.WeightAt(12.0));

        // 淡入淡出之和 4s > 区间 2s：按比例压缩成 1s + 1s，中点恰好满权重
        var squeezed = new EffectWindow(0, 2, FadeIn: 2, FadeOut: 2);
        Assert.Equal(1, squeezed.WeightAt(1.0), precision: 9);
    }

    [Fact]
    public void Window_OutsideDeactivates_InsideScalesMixParam()
    {
        var graph = new EffectGraph().Add("glitch");
        var node = graph.Nodes[0];
        var amount = EffectCatalog.Get("glitch")!.Params.First(p => p.Key == "amount");
        node.Set(amount, 40f);
        node.Windows.Add(new EffectWindow(1, 2, FadeIn: 0.5));

        PvPipeline.ApplyEffectCurves(graph, null, 0.5);
        Assert.False(node.FrameActive);

        PvPipeline.ApplyEffectCurves(graph, null, 1.25);
        Assert.True(node.FrameActive);
        Assert.Equal(20f, node.Value(amount), precision: 4);

        PvPipeline.ApplyEffectCurves(graph, null, 1.75);
        Assert.Equal(40f, node.Value(amount), precision: 4);
    }

    [Fact]
    public void ZoomPunchWindow_FadesTowardIdentityOne()
    {
        var graph = new EffectGraph().Add("zoomPunch");
        var node = graph.Nodes[0];
        var scale = EffectCatalog.Get("zoomPunch")!.Params.First(p => p.Key == "scale");
        node.Set(scale, 1.2f);
        node.Windows.Add(new EffectWindow(0, 1, FadeIn: 1));

        PvPipeline.ApplyEffectCurves(graph, null, 0.5);
        Assert.Equal(1.1f, node.Value(scale), precision: 4);
    }

    [Fact]
    public void Windows_SurviveJsonAndClone_InvalidOnesDropped()
    {
        var graph = new EffectGraph().Add("flash");
        graph.Nodes[0].Windows.Add(new EffectWindow(3, 4, 0, 0.5));
        graph.Nodes[0].Windows.Add(new EffectWindow(1, 2));

        var json = EffectGraphJson.Serialize(graph);
        Assert.DoesNotContain("IsValid", json);
        var back = EffectGraphJson.Deserialize(json.Replace("\"End\": 2.0", "\"End\": 0.5"));
        Assert.Single(back.Nodes[0].Windows);
        Assert.Equal(new EffectWindow(3, 4, 0, 0.5), back.Nodes[0].Windows[0]);

        var clone = graph.Clone();
        Assert.Equal(2, clone.Nodes[0].Windows.Count);
        Assert.True(clone.Nodes[0].FrameActive);
    }
}
