using JizuraLive.Pv;
using JizuraLive.Render.Effects;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 参数注册表（S06）：相机 / 图层 / 效果三域地址的单位、范围、默认值与标签。
/// 纯数据用例：无 GPU、无文件。注册表**不**夹取值（限幅是写目标时的调用方职责），所以这里只断言注册项本身。
/// </summary>
public class ParamRegistryTests
{
    // ---------- 相机域 ----------

    [Fact]
    public void CameraParamsAreRegisteredWithCanvasSizedOffsetRange()
    {
        var scene = new PvScene { Width = 1280, Height = 720 };

        // 位移是设计像素，范围 ±1 个设计画布：范围随工程画布变，单位不变
        AssertSpec("camera.offsetX", ParamUnit.Pixels, -1280, 1280, 0, "Param.Camera.OffsetX", scene: scene);
        AssertSpec("camera.offsetY", ParamUnit.Pixels, -720, 720, 0, "Param.Camera.OffsetY", scene: scene);
        AssertSpec("camera.scale", ParamUnit.Dimensionless, 0.5, 2, 1, "Param.Camera.Scale", scene: scene);
        AssertSpec("camera.rotation", ParamUnit.Degrees, -180, 180, 0, "Param.Camera.Rotation", scene: scene);

        // 换画布只动位移范围
        var wide = new PvScene { Width = 3840, Height = 2160 };
        AssertSpec("camera.offsetX", ParamUnit.Pixels, -3840, 3840, 0, "Param.Camera.OffsetX", scene: wide);
        AssertSpec("camera.scale", ParamUnit.Dimensionless, 0.5, 2, 1, "Param.Camera.Scale", scene: wide);
    }

    [Fact]
    public void CameraWithoutSceneFallsBackToTheDefaultCanvas()
    {
        // 编译期拿不到场景时（持久化往返、纯图用例）按默认画布解析，而不是把范围缩成 0
        var canvas = new PvScene();
        Assert.True(canvas.Width > 0 && canvas.Height > 0);

        AssertSpec("camera.offsetX", ParamUnit.Pixels, -canvas.Width, canvas.Width, 0, "Param.Camera.OffsetX");
        AssertSpec("camera.offsetY", ParamUnit.Pixels, -canvas.Height, canvas.Height, 0, "Param.Camera.OffsetY");
    }

    [Fact]
    public void CameraRejectsTargetIdAndUnknownParam()
    {
        // 唯一主镜头的地址形态是 camera.scale（Target 恒为空）：带 Target 的写法 ToString 吐不回原文本
        Assert.False(ParamRegistry.TryResolve(
            new ParamAddress(ParamAddressKind.Camera, "main", "scale"), new EffectGraph(), null, out _, out var targetError));
        Assert.Contains("Target 恒为空", targetError ?? "");

        Assert.False(ParamRegistry.TryResolve(Address("camera.focus"), new EffectGraph(), null, out _, out var paramError));
        Assert.StartsWith("target-missing-key", paramError);
    }

    // ---------- 图层域 ----------

    [Fact]
    public void LayerParamsAreRegisteredPerNameForAnyLayerId()
    {
        // 静态表：参数集合与具体图层无关，任意图层 Id 共用同一组几何/透明度参数
        foreach (var id in new[] { "character", "lyrics_pv", "src_7" })
        {
            AssertSpec($"layer:{id}.offsetX", ParamUnit.Ratio, -1, 1, 0, "Param.Layer.OffsetX");
            AssertSpec($"layer:{id}.offsetY", ParamUnit.Ratio, -1, 1, 0, "Param.Layer.OffsetY");
            AssertSpec($"layer:{id}.scale", ParamUnit.Dimensionless, 0.1, 5, 1, "Param.Layer.Scale");
            AssertSpec($"layer:{id}.opacity", ParamUnit.Ratio, 0, 1, 1, "Param.Layer.Opacity");
            AssertSpec($"layer:{id}.rotation", ParamUnit.Degrees, -180, 180, 0, "Param.Layer.Rotation");
        }
    }

    [Fact]
    public void LayerRejectsEmptyIdAndUnknownParam()
    {
        Assert.False(ParamRegistry.TryResolve(
            new ParamAddress(ParamAddressKind.Layer, "", "opacity"), new EffectGraph(), null, out _, out var idError));
        Assert.Contains("缺少图层 Id", idError ?? "");

        Assert.False(ParamRegistry.TryResolve(Address("layer:character.alpha"), new EffectGraph(), null, out _, out var keyError));
        Assert.StartsWith("target-missing-key", keyError);
    }

    // ---------- 效果域 ----------

    [Fact]
    public void EffectParamsComeFromTheInstanceSchema()
    {
        var effects = new EffectGraph();
        effects.Add("bloom");
        var instance = effects.Nodes[0].InstanceId;

        // 同一实例的三个 Number 参数：单位/范围/默认值都取自 schema，标签就是 schema 的 LabelKey
        AssertSpec($"effect:{instance}.threshold", ParamUnit.Ratio, 0, 1, 0.62, "Fx.Bloom.Threshold", effects: effects);
        AssertSpec($"effect:{instance}.intensity", ParamUnit.Dimensionless, 0, 3, 1.1, "Fx.Bloom.Intensity", effects: effects);
        AssertSpec($"effect:{instance}.radius", ParamUnit.Pixels, 1, 120, 26, "Fx.Bloom.Radius", effects: effects);

        // 同 DefId 的第二个实例独立寻址，各自拿自己那份 schema 事实
        effects.Add("bloom");
        var second = effects.Nodes[1].InstanceId;
        Assert.NotEqual(instance, second);
        AssertSpec($"effect:{second}.intensity", ParamUnit.Dimensionless, 0, 3, 1.1, "Fx.Bloom.Intensity", effects: effects);
    }

    [Fact]
    public void UnannotatedEffectUnitStaysUnspecified()
    {
        // 未标注单位的键照常注册出范围/默认值，但单位是 Unspecified：绑定校验据此 fail-closed 拒绝，
        // 把"这个参数还没登记量纲"的缺口露出来，而不是按错单位接上线
        var effects = new EffectGraph();
        effects.Add("chromatic");
        var instance = effects.Nodes[0].InstanceId;

        Assert.True(ParamRegistry.TryResolve(Address($"effect:{instance}.amount"), effects, null, out var spec, out var error), error);
        Assert.Equal(ParamUnit.Unspecified, spec.Unit);
        Assert.Equal(0, spec.Min);
        Assert.Equal(40, spec.Max);
    }

    [Fact]
    public void EffectAddressProblemsAreClassified()
    {
        var effects = new EffectGraph();
        effects.Add("bloom");
        effects.Add("grain");
        var bloom = effects.Nodes[0].InstanceId;
        var grain = effects.Nodes[1].InstanceId;

        Assert.False(ParamRegistry.TryResolve(Address($"effect:{bloom}.amount"), effects, null, out _, out var missingKey));
        Assert.StartsWith("target-missing-key", missingKey);

        // DefId 不能代替实例 Id：两个同种效果的实例各自独立寻址
        Assert.False(ParamRegistry.TryResolve(Address("effect:bloom.intensity"), effects, null, out _, out var defId));
        Assert.StartsWith("target-missing-instance", defId);
        Assert.Contains("DefId", defId ?? "");

        // Bool 参数不是数值引脚
        Assert.False(ParamRegistry.TryResolve(Address($"effect:{grain}.colored"), effects, null, out _, out var notNumber));
        Assert.StartsWith("target-not-number", notNumber);
    }

    // ---------- helpers ----------

    private static void AssertSpec(string text, ParamUnit unit, double min, double max, double def, string label,
        EffectGraph? effects = null, PvScene? scene = null)
    {
        Assert.True(ParamRegistry.TryResolve(Address(text), effects ?? new EffectGraph(), scene, out var spec, out var error),
            $"地址 {text} 应能解析：{error}");

        Assert.Equal(unit, spec.Unit);
        // 效果域的 schema 是 float：精度按 1e-6 比，只放过 float→double 的加宽噪声
        Assert.Equal(min, spec.Min, 6);
        Assert.Equal(max, spec.Max, 6);
        Assert.Equal(def, spec.Default, 6);
        Assert.Equal(label, spec.Label);
    }

    private static ParamAddress Address(string text)
    {
        Assert.True(ParamAddress.TryParse(text, out var addr), $"测试前置地址应能解析：{text}");
        return addr;
    }
}
