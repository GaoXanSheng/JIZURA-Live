using JizuraLive.Engine;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Text;

namespace JizuraLive.Pv;

public sealed class AutoOptions
{
    public string Style = "dynamic"; /* soft|dynamic|epic */
    public double Intensity = 0.6; /* 0..1 */
}

public sealed class AutoReport
{
    public int CameraKeys;
    public int EffectInstances;
    public int CurveKeys;
    public List<string> Notes = new();
}

public static class AutoDirector
{
    static AutoDirector()
    {
        EnsureParamUnits();
    }

    public static void EnsureParamUnits()
    {
        EnsureUnit("glitch", "amount", ParamUnit.Dimensionless);
        EnsureUnit("zoomPunch", "scale", ParamUnit.Dimensionless);
    }

    private static void EnsureUnit(string defId, string paramKey, ParamUnit unit)
    {
        var def = EffectCatalog.Get(defId);
        if (def == null) return;
        for (int i = 0; i < def.Params.Length; i++)
        {
            if (def.Params[i].Key == paramKey && def.Params[i].Unit == ParamUnit.Unspecified)
            {
                def.Params[i] = def.Params[i] with { Unit = unit };
            }
        }
    }

    private const string AutoPrefix = "fx_autodir_";
    private const string CameraScaleAddress = "camera.scale";
    private const string CameraRotationAddress = "camera.rotation";

    public static AutoReport Generate(DirectorProject project, BeatMap map, AutoOptions options)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(map);
        options ??= new AutoOptions();

        project.Scene ??= PvScene.CreateBlank();
        project.Effects ??= new EffectGraph();
        project.Curves ??= new List<CurveSave>();
        project.SignalGraph ??= new SignalGraph();

        var report = new AutoReport();
        double intensity = double.IsFinite(options.Intensity) ? Math.Clamp(options.Intensity, 0.0, 1.0) : 0.6;
        string style = (options.Style ?? "dynamic").Trim().ToLowerInvariant();
        if (style is not ("soft" or "dynamic" or "epic")) style = "dynamic";

        // 幂等清理旧生成物
        CleanupPreviousGeneration(project);

        var beats = map.Beats.Where(double.IsFinite).OrderBy(b => b).ToList();
        var peaks = map.Peaks.Where(double.IsFinite).OrderBy(p => p).ToList();
        var sections = map.Sections.Where(s => s != null && double.IsFinite(s.Start) && double.IsFinite(s.End)).ToList();

        // 自动运镜曲线
        GenerateCameraCurves(project, beats, peaks, sections, intensity, report);

        // 自动特效实例与节拍脉冲曲线
        GenerateEffects(project, beats, peaks, style, intensity, report);

        // 自动歌词样式段落切换
        ApplyLyricStyle(project, sections, report);

        report.Notes.Add($"AutoDirector: Style={style}, Intensity={intensity:F2}, CameraKeys={report.CameraKeys}, EffectInstances={report.EffectInstances}, CurveKeys={report.CurveKeys}");
        return report;
    }

    private static void CleanupPreviousGeneration(DirectorProject project)
    {
        var autoInstanceIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = project.Effects.Nodes.Count - 1; i >= 0; i--)
        {
            var node = project.Effects.Nodes[i];
            if (node.InstanceId.StartsWith(AutoPrefix, StringComparison.Ordinal))
            {
                autoInstanceIds.Add(node.InstanceId);
                project.Effects.Nodes.RemoveAt(i);
            }
        }

        project.Curves.RemoveAll(c =>
            c.Address == CameraScaleAddress ||
            c.Address == CameraRotationAddress ||
            c.Address.StartsWith($"effect:{AutoPrefix}", StringComparison.Ordinal) ||
            autoInstanceIds.Any(id => c.Address.StartsWith($"effect:{id}.", StringComparison.Ordinal)));

        project.SignalGraph.Bindings.RemoveAll(b =>
            b.Id.StartsWith("bind_autodir_", StringComparison.Ordinal) ||
            b.NodeId.StartsWith("node_autodir_", StringComparison.Ordinal) ||
            autoInstanceIds.Any(id => b.Address.StartsWith($"effect:{id}.", StringComparison.Ordinal)));

        project.SignalGraph.Nodes.RemoveAll(n => n.Id.StartsWith("node_autodir_", StringComparison.Ordinal));
        project.SignalGraph.Edges.RemoveAll(e => e.Id.StartsWith("edge_autodir_", StringComparison.Ordinal));
    }

    private static void GenerateCameraCurves(DirectorProject project, List<double> beats, List<double> peaks,
        List<Section> sections, double intensity, AutoReport report)
    {
        var scaleCurve = new FloatCurve();
        var rotationCurve = new FloatCurve();

        if (beats.Count == 0)
        {
            scaleCurve.TryAdd(0.0, 1.0, CurveInterp.Linear);
            project.Curves.Add(new CurveSave { Address = CameraScaleAddress, Curve = scaleCurve });
            report.CameraKeys = 1;
            report.CurveKeys += 1;
            return;
        }

        // 1. 段落级相机曲线
        foreach (var sec in sections)
        {
            double tStart = SnapToNearestBeat(sec.Start, beats);
            double tEnd = SnapToNearestBeat(sec.End, beats);
            string kind = (sec.Kind ?? "").Trim().ToLowerInvariant();

            switch (kind)
            {
                case "verse":
                    // verse 缓推
                    scaleCurve.Remove(tStart);
                    scaleCurve.TryAdd(tStart, 1.0, CurveInterp.Linear);
                    scaleCurve.Remove(tEnd);
                    scaleCurve.TryAdd(tEnd, 1.0 + 0.05 * intensity, CurveInterp.Linear);
                    break;

                case "chorus":
                    // chorus 拉开 / 俯仰
                    scaleCurve.Remove(tStart);
                    scaleCurve.TryAdd(tStart, 1.10 + 0.05 * intensity, CurveInterp.SmoothStep);
                    scaleCurve.Remove(tEnd);
                    scaleCurve.TryAdd(tEnd, 1.0, CurveInterp.SmoothStep);

                    rotationCurve.Remove(tStart);
                    rotationCurve.TryAdd(tStart, 2.0 * intensity, CurveInterp.SmoothStep);
                    rotationCurve.Remove(tEnd);
                    rotationCurve.TryAdd(tEnd, 0.0, CurveInterp.SmoothStep);
                    break;

                case "break":
                    // break 回中
                    scaleCurve.Remove(tStart);
                    scaleCurve.TryAdd(tStart, 1.0, CurveInterp.SmoothStep);
                    scaleCurve.Remove(tEnd);
                    scaleCurve.TryAdd(tEnd, 1.0, CurveInterp.SmoothStep);

                    rotationCurve.Remove(tStart);
                    rotationCurve.TryAdd(tStart, 0.0, CurveInterp.SmoothStep);
                    rotationCurve.Remove(tEnd);
                    rotationCurve.TryAdd(tEnd, 0.0, CurveInterp.SmoothStep);
                    break;

                default:
                    scaleCurve.Remove(tStart);
                    scaleCurve.TryAdd(tStart, 1.0, CurveInterp.Linear);
                    scaleCurve.Remove(tEnd);
                    scaleCurve.TryAdd(tEnd, 1.0, CurveInterp.Linear);
                    break;
            }
        }

        // 2. 节拍 scale punch：beat 时刻打 1.0→1.02→1.0 的快键
        var punchBeats = new HashSet<double>();
        if (peaks.Count > 0)
        {
            foreach (var p in peaks) punchBeats.Add(SnapToNearestBeat(p, beats));
        }
        else
        {
            for (int i = 0; i < beats.Count; i += 2) punchBeats.Add(beats[i]);
        }

        double punchVal = 1.0 + 0.02 * intensity;
        for (int i = 0; i < beats.Count; i++)
        {
            double b = beats[i];
            if (punchBeats.Contains(b))
            {
                if (i > 0)
                {
                    double prev = beats[i - 1];
                    if (!punchBeats.Contains(prev))
                    {
                        scaleCurve.Remove(prev);
                        scaleCurve.TryAdd(prev, 1.0, CurveInterp.Linear);
                    }
                }

                scaleCurve.Remove(b);
                scaleCurve.TryAdd(b, punchVal, CurveInterp.Linear);

                if (i < beats.Count - 1)
                {
                    double next = beats[i + 1];
                    if (!punchBeats.Contains(next))
                    {
                        scaleCurve.Remove(next);
                        scaleCurve.TryAdd(next, 1.0, CurveInterp.Linear);
                    }
                }
            }
        }

        scaleCurve.NormalizeKeys();
        rotationCurve.NormalizeKeys();

        project.Curves.Add(new CurveSave { Address = CameraScaleAddress, Curve = scaleCurve });
        int cameraKeys = scaleCurve.Count;

        if (rotationCurve.Count > 0)
        {
            project.Curves.Add(new CurveSave { Address = CameraRotationAddress, Curve = rotationCurve });
            cameraKeys += rotationCurve.Count;
        }

        report.CameraKeys = cameraKeys;
        report.CurveKeys += cameraKeys;
    }

    private static void GenerateEffects(DirectorProject project, List<double> beats, List<double> peaks,
        string style, double intensity, AutoReport report)
    {
        switch (style)
        {
            case "soft":
                AddEffect(project, "bloom", n => SetParam(n, "intensity", 1.1f * (float)intensity));
                AddEffect(project, "colorGrade", n =>
                {
                    SetParam(n, "strength", (float)intensity);
                    SetParam(n, "saturation", 1.0f + 0.2f * (float)intensity);
                    SetParam(n, "contrast", 1.0f + 0.1f * (float)intensity);
                });
                AddEffect(project, "vignette", n => SetParam(n, "intensity", 0.5f * (float)intensity));
                report.EffectInstances = 3;
                break;

            case "epic":
                AddEffect(project, "bloom", n => SetParam(n, "intensity", 1.4f * (float)intensity));
                AddEffect(project, "lightRays", n => SetParam(n, "intensity", 0.9f * (float)intensity));
                var zoomNode = AddEffect(project, "zoomPunch", n => SetParam(n, "scale", 1.0f));
                report.EffectInstances = 3;

                // zoomPunch 节拍脉冲
                if (beats.Count > 0)
                {
                    var zoomCurve = new FloatCurve();
                    AddPulseKeys(zoomCurve, beats, peaks, 1.0, 1.0 + 0.12 * intensity);
                    if (zoomCurve.Count > 0)
                    {
                        project.Curves.Add(new CurveSave { Address = $"effect:{zoomNode.InstanceId}.scale", Curve = zoomCurve });
                        report.CurveKeys += zoomCurve.Count;
                        BindEffectPulseCurve(project, zoomNode, "scale");
                    }
                }
                break;

            default: // dynamic
                AddEffect(project, "bloom", n => SetParam(n, "intensity", 1.1f * (float)intensity));
                AddEffect(project, "motionBlur", n =>
                {
                    SetParam(n, "length", 18f * (float)intensity);
                    SetParam(n, "strength", 0.8f * (float)intensity);
                });
                var glitchNode = AddEffect(project, "glitch", n => SetParam(n, "amount", 0.0f));
                report.EffectInstances = 3;

                // glitch 节拍脉冲：强度 0→峰值→回落
                if (beats.Count > 0)
                {
                    var glitchCurve = new FloatCurve();
                    AddPulseKeys(glitchCurve, beats, peaks, 0.0, 24.0 * intensity);
                    if (glitchCurve.Count > 0)
                    {
                        project.Curves.Add(new CurveSave { Address = $"effect:{glitchNode.InstanceId}.amount", Curve = glitchCurve });
                        report.CurveKeys += glitchCurve.Count;
                        BindEffectPulseCurve(project, glitchNode, "amount");
                    }
                }
                break;
        }
    }

    private static void BindEffectPulseCurve(DirectorProject project, EffectNode node, string paramKey)
    {
        EnsureParamUnits();

        string address = $"effect:{node.InstanceId}.{paramKey}";

        var baseNode = new SignalNode
        {
            Id = $"node_autodir_base_{node.InstanceId}_{paramKey}",
            Kind = SignalNodeKind.TimelineBaseValue,
            Texts = new Dictionary<string, string> { ["address"] = address },
        };

        var outNode = new SignalNode
        {
            Id = $"node_autodir_out_{node.InstanceId}_{paramKey}",
            Kind = SignalNodeKind.ParameterOutput,
        };

        var edge = new SignalEdge
        {
            Id = $"edge_autodir_{node.InstanceId}_{paramKey}",
            FromNode = baseNode.Id,
            FromPort = "out",
            ToNode = outNode.Id,
            ToPort = "in",
        };

        var binding = new SignalBinding
        {
            Id = $"bind_autodir_{node.InstanceId}_{paramKey}",
            NodeId = outNode.Id,
            Address = address,
        };

        project.SignalGraph.TryAddNode(baseNode, out _);
        project.SignalGraph.TryAddNode(outNode, out _);
        project.SignalGraph.TryAddEdge(edge, out _);
        project.SignalGraph.TryAddBinding(binding, out _);
    }

    private static void AddPulseKeys(FloatCurve curve, List<double> beats, List<double> peaks, double baseVal, double peakVal)
    {
        var targetBeats = new HashSet<double>();
        if (peaks.Count > 0)
        {
            foreach (var p in peaks) targetBeats.Add(SnapToNearestBeat(p, beats));
        }
        else
        {
            for (int i = 0; i < beats.Count; i += 2) targetBeats.Add(beats[i]);
        }

        for (int i = 0; i < beats.Count; i++)
        {
            double b = beats[i];
            if (targetBeats.Contains(b))
            {
                if (i > 0)
                {
                    double prev = beats[i - 1];
                    if (!targetBeats.Contains(prev))
                    {
                        curve.Remove(prev);
                        curve.TryAdd(prev, baseVal, CurveInterp.Linear);
                    }
                }

                curve.Remove(b);
                curve.TryAdd(b, peakVal, CurveInterp.Linear);

                if (i < beats.Count - 1)
                {
                    double next = beats[i + 1];
                    if (!targetBeats.Contains(next))
                    {
                        curve.Remove(next);
                        curve.TryAdd(next, baseVal, CurveInterp.Linear);
                    }
                }
            }
        }
        curve.NormalizeKeys();
    }

    private static EffectNode AddEffect(DirectorProject project, string defId, Action<EffectNode>? configure = null)
    {
        var node = new EffectNode
        {
            DefId = defId,
            InstanceId = $"{AutoPrefix}{defId}_{Guid.NewGuid().ToString("N")[..6]}",
            Enabled = true,
        };
        configure?.Invoke(node);
        project.Effects.Nodes.Add(node);
        return node;
    }

    private static void SetParam(EffectNode node, string key, float value)
    {
        var def = EffectCatalog.Get(node.DefId);
        if (def != null)
        {
            var p = def.Params.FirstOrDefault(x => x.Key == key);
            if (p != null)
            {
                node.Set(p, value);
                return;
            }
        }
        node.Values[key] = value;
    }

    private static void ApplyLyricStyle(DirectorProject project, List<Section> sections, AutoReport report)
    {
        bool hasChorus = sections.Any(s => s.Kind.Equals("chorus", StringComparison.OrdinalIgnoreCase));
        bool hasVerse = sections.Any(s => s.Kind.Equals("verse", StringComparison.OrdinalIgnoreCase));

        string chosen = hasChorus ? "Neon" : (hasVerse ? "Minimal" : LyricStyle.ClassicName);
        project.Scene.LyricStyleName = chosen;

        foreach (var sec in sections)
        {
            string secStyle = sec.Kind.Equals("chorus", StringComparison.OrdinalIgnoreCase) ? "Neon" : "Minimal";
            report.Notes.Add($"LyricStyle [{sec.Start:F2}s-{sec.End:F2}s] ({sec.Kind}): {secStyle}");
        }
    }

    private static double SnapToNearestBeat(double time, List<double> beats)
    {
        if (beats.Count == 0) return time;
        int idx = beats.BinarySearch(time);
        if (idx >= 0) return beats[idx];
        int next = ~idx;
        if (next <= 0) return beats[0];
        if (next >= beats.Count) return beats[^1];
        return Math.Abs(beats[next] - time) < Math.Abs(beats[next - 1] - time)
            ? beats[next]
            : beats[next - 1];
    }
}
