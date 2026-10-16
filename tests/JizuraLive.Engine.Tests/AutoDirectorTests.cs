using System.IO;
using JizuraLive.Engine;
using JizuraLive.Pv;
using Xunit;

namespace JizuraLive.Engine.Tests;

public class AutoDirectorTests
{
    private static BeatMap CreateSampleBeatMap()
    {
        return new BeatMap
        {
            Bpm = 120,
            Beats = new List<double> { 0.0, 0.5, 1.0, 1.5, 2.0, 2.5, 3.0, 3.5, 4.0 },
            Peaks = new List<double> { 1.5, 2.5 },
            Sections = new List<Section>
            {
                new Section { Start = 0.0, End = 1.5, Kind = "verse" },
                new Section { Start = 1.5, End = 3.0, Kind = "chorus" },
                new Section { Start = 3.0, End = 3.5, Kind = "break" },
                new Section { Start = 3.5, End = 4.0, Kind = "outro" },
            }
        };
    }

    private static string TempProjectPath() =>
        Path.Combine(Path.GetTempPath(), $"jzpv_autodir_{Guid.NewGuid():N}.jzpv");

    [Fact]
    public void Generate_SoftStyle_AssertStructure_Snapping_Effects_RoundTrip()
    {
        var project = new DirectorProject();
        var map = CreateSampleBeatMap();
        var options = new AutoOptions { Style = "soft", Intensity = 0.6 };

        var report = AutoDirector.Generate(project, map, options);

        // 结构断言
        Assert.True(report.CameraKeys > 0);
        Assert.Equal(3, report.EffectInstances);
        Assert.Equal(3, project.Effects.Nodes.Count);

        var defIds = project.Effects.Nodes.Select(n => n.DefId).ToList();
        Assert.Contains("bloom", defIds);
        Assert.Contains("colorGrade", defIds);
        Assert.Contains("vignette", defIds);

        // 吸附断言：时间全部吸附在 Beats 上
        var scaleCurveSave = project.Curves.FirstOrDefault(c => c.Address == "camera.scale");
        Assert.NotNull(scaleCurveSave);
        Assert.True(scaleCurveSave.Curve.Count > 0);
        Assert.All(scaleCurveSave.Curve.Keys, k => Assert.Contains(k.Time, map.Beats));

        var rotCurveSave = project.Curves.FirstOrDefault(c => c.Address == "camera.rotation");
        if (rotCurveSave != null)
        {
            Assert.All(rotCurveSave.Curve.Keys, k => Assert.Contains(k.Time, map.Beats));
        }

        // 歌词样式断言：有 chorus 段落切换到 Neon
        Assert.Equal("Neon", project.Scene.LyricStyleName);

        // 往返断言：序列化与反序列化
        string tempFile = TempProjectPath();
        try
        {
            Assert.Null(ProjectStore.Save(project, tempFile));
            var loadRes = ProjectStore.Load(tempFile);
            Assert.Null(loadRes.Fault);
            Assert.NotNull(loadRes.Project);
            Assert.Equal(project.Effects.Nodes.Count, loadRes.Project.Effects.Nodes.Count);
            Assert.Equal(project.Curves.Count, loadRes.Project.Curves.Count);
            Assert.Equal("Neon", loadRes.Project.Scene.LyricStyleName);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Generate_DynamicStyle_AssertStructure_Snapping_Effects_RoundTrip()
    {
        var project = new DirectorProject();
        var map = CreateSampleBeatMap();
        var options = new AutoOptions { Style = "dynamic", Intensity = 0.7 };

        var report = AutoDirector.Generate(project, map, options);

        Assert.True(report.CameraKeys > 0);
        Assert.Equal(3, report.EffectInstances);

        var defIds = project.Effects.Nodes.Select(n => n.DefId).ToList();
        Assert.Contains("bloom", defIds);
        Assert.Contains("motionBlur", defIds);
        Assert.Contains("glitch", defIds);

        // 节拍脉冲曲线：glitch.amount
        var glitchNode = project.Effects.Nodes.First(n => n.DefId == "glitch");
        var glitchCurve = project.Curves.FirstOrDefault(c => c.Address == $"effect:{glitchNode.InstanceId}.amount");
        Assert.NotNull(glitchCurve);
        Assert.True(glitchCurve.Curve.Count > 0);
        Assert.Contains(glitchCurve.Curve.Keys, k => k.Value > 0);

        var scaleCurve = project.Curves.First(c => c.Address == "camera.scale");
        Assert.All(scaleCurve.Curve.Keys, k => Assert.Contains(k.Time, map.Beats));

        string tempFile = TempProjectPath();
        try
        {
            Assert.Null(ProjectStore.Save(project, tempFile));
            var loadRes = ProjectStore.Load(tempFile);
            Assert.Null(loadRes.Fault);
            Assert.NotNull(loadRes.Project);
            Assert.Equal(3, loadRes.Project.Effects.Nodes.Count);
            Assert.Equal(project.SignalGraph.Bindings.Count, loadRes.Project.SignalGraph.Bindings.Count);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Generate_EpicStyle_AssertStructure_Snapping_Effects_RoundTrip()
    {
        var project = new DirectorProject();
        var map = CreateSampleBeatMap();
        var options = new AutoOptions { Style = "epic", Intensity = 0.8 };

        var report = AutoDirector.Generate(project, map, options);

        Assert.True(report.CameraKeys > 0);
        Assert.Equal(3, report.EffectInstances);

        var defIds = project.Effects.Nodes.Select(n => n.DefId).ToList();
        Assert.Contains("bloom", defIds);
        Assert.Contains("lightRays", defIds);
        Assert.Contains("zoomPunch", defIds);

        // zoomPunch.scale 节拍脉冲曲线
        var zoomNode = project.Effects.Nodes.First(n => n.DefId == "zoomPunch");
        var zoomCurve = project.Curves.FirstOrDefault(c => c.Address == $"effect:{zoomNode.InstanceId}.scale");
        Assert.NotNull(zoomCurve);
        Assert.True(zoomCurve.Curve.Count > 0);

        var scaleCurve = project.Curves.First(c => c.Address == "camera.scale");
        Assert.All(scaleCurve.Curve.Keys, k => Assert.Contains(k.Time, map.Beats));

        string tempFile = TempProjectPath();
        try
        {
            Assert.Null(ProjectStore.Save(project, tempFile));
            var loadRes = ProjectStore.Load(tempFile);
            Assert.Null(loadRes.Fault);
            Assert.NotNull(loadRes.Project);
            Assert.Equal(3, loadRes.Project.Effects.Nodes.Count);
            Assert.Equal(project.SignalGraph.Bindings.Count, loadRes.Project.SignalGraph.Bindings.Count);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void Generate_IntensityBoundaries_ZeroAndOne()
    {
        var map = CreateSampleBeatMap();

        // 边界 0
        var p0 = new DirectorProject();
        var rep0 = AutoDirector.Generate(p0, map, new AutoOptions { Style = "dynamic", Intensity = 0.0 });
        Assert.True(rep0.CameraKeys > 0);
        var scale0 = p0.Curves.First(c => c.Address == "camera.scale");
        Assert.All(scale0.Curve.Keys, k => Assert.True(k.Value <= 1.0 + 1e-9));

        // 边界 1
        var p1 = new DirectorProject();
        var rep1 = AutoDirector.Generate(p1, map, new AutoOptions { Style = "dynamic", Intensity = 1.0 });
        Assert.True(rep1.CameraKeys > 0);
        var scale1 = p1.Curves.First(c => c.Address == "camera.scale");
        Assert.Contains(scale1.Curve.Keys, k => Math.Abs(k.Value - 1.14) < 1e-6);
    }

    [Fact]
    public void Generate_RepeatedCalls_IsIdempotent()
    {
        var project = new DirectorProject();
        var map = CreateSampleBeatMap();
        var options = new AutoOptions { Style = "dynamic", Intensity = 0.6 };

        var rep1 = AutoDirector.Generate(project, map, options);
        int camKeys1 = rep1.CameraKeys;
        int fxInst1 = rep1.EffectInstances;
        int curveKeys1 = rep1.CurveKeys;
        int nodeCount1 = project.Effects.Nodes.Count;
        int curvesCount1 = project.Curves.Count;
        int sigNodes1 = project.SignalGraph.Nodes.Count;
        int sigEdges1 = project.SignalGraph.Edges.Count;
        int sigBindings1 = project.SignalGraph.Bindings.Count;

        // 第二次重复生成
        var rep2 = AutoDirector.Generate(project, map, options);
        Assert.Equal(camKeys1, rep2.CameraKeys);
        Assert.Equal(fxInst1, rep2.EffectInstances);
        Assert.Equal(curveKeys1, rep2.CurveKeys);
        Assert.Equal(nodeCount1, project.Effects.Nodes.Count);
        Assert.Equal(curvesCount1, project.Curves.Count);
        Assert.Equal(sigNodes1, project.SignalGraph.Nodes.Count);
        Assert.Equal(sigEdges1, project.SignalGraph.Edges.Count);
        Assert.Equal(sigBindings1, project.SignalGraph.Bindings.Count);

        // 第三次重复生成
        var rep3 = AutoDirector.Generate(project, map, options);
        Assert.Equal(camKeys1, rep3.CameraKeys);
        Assert.Equal(nodeCount1, project.Effects.Nodes.Count);
        Assert.Equal(curvesCount1, project.Curves.Count);
        Assert.Equal(sigNodes1, project.SignalGraph.Nodes.Count);
        Assert.Equal(sigEdges1, project.SignalGraph.Edges.Count);
        Assert.Equal(sigBindings1, project.SignalGraph.Bindings.Count);
    }

    [Fact]
    public void Generate_VerseOnly_UsesMinimalLyricStyle()
    {
        var project = new DirectorProject();
        var map = new BeatMap
        {
            Bpm = 100,
            Beats = new List<double> { 0.0, 1.0, 2.0 },
            Sections = new List<Section>
            {
                new Section { Start = 0.0, End = 2.0, Kind = "verse" }
            }
        };

        AutoDirector.Generate(project, map, new AutoOptions());
        Assert.Equal("Minimal", project.Scene.LyricStyleName);
    }

    [Fact]
    public void Generate_SignalCompilerPassesWithoutErrors()
    {
        var project = new DirectorProject();
        var map = CreateSampleBeatMap();
        AutoDirector.Generate(project, map, new AutoOptions());

        var result = SignalCompiler.Compile(project.SignalGraph, project.Effects, project.Scene);
        Assert.DoesNotContain(result.Issues, i => i.Severity == SignalIssueSeverity.Error);
    }

    [Fact]
    public void Generate_EffectPulseCurves_AreBoundInSignalGraph_AndEvaluatedCorrectly()
    {
        var map = CreateSampleBeatMap();

        // 1. dynamic style: glitch.amount 节拍脉冲曲线与信号图绑定
        var projectDyn = new DirectorProject();
        AutoDirector.Generate(projectDyn, map, new AutoOptions { Style = "dynamic", Intensity = 0.6 });

        var glitchNode = projectDyn.Effects.Nodes.First(n => n.DefId == "glitch");
        string glitchAddr = $"effect:{glitchNode.InstanceId}.amount";

        // 断言存在对应的 SignalBinding
        var glitchBinding = projectDyn.SignalGraph.Bindings.FirstOrDefault(b => b.Address == glitchAddr);
        Assert.NotNull(glitchBinding);

        // 断言信号图编译成功且产出可执行 Plan
        var compileDyn = SignalCompiler.Compile(projectDyn.SignalGraph, projectDyn.Effects, projectDyn.Scene);
        Assert.NotNull(compileDyn.Plan);
        Assert.DoesNotContain(compileDyn.Issues, i => i.Severity == SignalIssueSeverity.Error);

        var evaluatorDyn = new DirectorEvaluator();
        foreach (var save in projectDyn.Curves)
        {
            if (ParamAddress.TryParse(save.Address, out var addr))
                evaluatorDyn.SetCurve(addr, save.Curve);
        }

        var planDyn = compileDyn.Plan!;
        // 在非 peak 节拍点 (0.0s)，glitch 应为基准 0.0，而非停在最大强度
        planDyn.Evaluate(new SignalFrameInputs(0.0, 0.0, default, evaluatorDyn, default));
        var res0 = planDyn.Resolved.FirstOrDefault(r => r.Address.ToString() == glitchAddr);
        Assert.Equal(glitchAddr, res0.Address.ToString());
        Assert.Equal(ParamValueSource.Signal, res0.Source);
        Assert.Equal(0.0, res0.Value, precision: 3);

        // 在 peak 点 1.5s，glitch 应脉冲为 48.0 * 0.6 = 28.8
        planDyn.Evaluate(new SignalFrameInputs(1.5, 1.5, default, evaluatorDyn, default));
        var resPeak = planDyn.Resolved.FirstOrDefault(r => r.Address.ToString() == glitchAddr);
        Assert.Equal(48.0 * 0.6, resPeak.Value, precision: 3);

        // 2. epic style: zoomPunch.scale 节拍脉冲曲线与信号图绑定
        var projectEpic = new DirectorProject();
        AutoDirector.Generate(projectEpic, map, new AutoOptions { Style = "epic", Intensity = 0.8 });

        var zoomNode = projectEpic.Effects.Nodes.First(n => n.DefId == "zoomPunch");
        string zoomAddr = $"effect:{zoomNode.InstanceId}.scale";

        var zoomBinding = projectEpic.SignalGraph.Bindings.FirstOrDefault(b => b.Address == zoomAddr);
        Assert.NotNull(zoomBinding);

        var compileEpic = SignalCompiler.Compile(projectEpic.SignalGraph, projectEpic.Effects, projectEpic.Scene);
        Assert.NotNull(compileEpic.Plan);
        Assert.DoesNotContain(compileEpic.Issues, i => i.Severity == SignalIssueSeverity.Error);

        var evaluatorEpic = new DirectorEvaluator();
        foreach (var save in projectEpic.Curves)
        {
            if (ParamAddress.TryParse(save.Address, out var addr))
                evaluatorEpic.SetCurve(addr, save.Curve);
        }

        var planEpic = compileEpic.Plan!;
        // 在非 peak 节拍点 (0.0s)，zoom 应为基准 1.0，而非放大态
        planEpic.Evaluate(new SignalFrameInputs(0.0, 0.0, default, evaluatorEpic, default));
        var zoomRes0 = planEpic.Resolved.FirstOrDefault(r => r.Address.ToString() == zoomAddr);
        Assert.Equal(zoomAddr, zoomRes0.Address.ToString());
        Assert.Equal(ParamValueSource.Signal, zoomRes0.Source);
        Assert.Equal(1.0, zoomRes0.Value, precision: 3);

        // 在 peak 点 1.5s，zoom 应脉冲为 1.0 + 0.35 * 0.8 = 1.28
        planEpic.Evaluate(new SignalFrameInputs(1.5, 1.5, default, evaluatorEpic, default));
        var zoomResPeak = planEpic.Resolved.FirstOrDefault(r => r.Address.ToString() == zoomAddr);
        Assert.Equal(1.0 + 0.35 * 0.8, zoomResPeak.Value, precision: 3);
    }
}
