using JizuraLive.Engine;

namespace JizuraLive.Pv;

public sealed class AutoOptions
{
    public string Style = "dynamic";
    public double Intensity = 0.6;

    public int Seed = 1;
}

public sealed class AutoReport
{
    public int CameraShots;
    public int EffectInstances;

    public int EffectWindows;

    public int CurveKeys;
    public int CharacterKeys;
    public int DecorLayers;
    public int TransSegments;
    public List<string> Notes = new();
}

public static partial class AutoDirector
{
    public const string AutoPrefix = "fx_autodir_";
    public const string LayerPrefix = "auto.";
    public const string AutoLayerPrefix = "autodir_";

    private static readonly string[] CameraParams = ["scale", "rotation", "offsetX", "offsetY", "blur", "brighten"];

    private static bool IsUser(string? source) => string.Equals(source, "user", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 原版 PV 引擎的舞台叠加层：沿用引擎 Plan 的 cut/运镜/转场，
    /// 只把角色层和整体转场缝上去。布局、文字处理仍由原版引擎负责。
    /// </summary>
    public static AutoReport GenerateLegacyStage(DirectorProject project, LegacyPvPlanInfo plan,
        BeatMap? map, AutoOptions options)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan);
        options ??= new AutoOptions();

        project.Scene ??= PvScene.CreateBlank();
        project.Curves ??= new List<CurveSave>();

        var report = new AutoReport();
        double intensity = double.IsFinite(options.Intensity) ? Math.Clamp(options.Intensity, 0.0, 1.0) : 0.6;
        string style = (options.Style ?? "dynamic").Trim().ToLowerInvariant();
        if (style is not ("soft" or "dynamic" or "epic")) style = "dynamic";

        var preservedShots = CleanupPreviousGeneration(project);

        if (project.LegacyStageCamera)
            BuildAutoCamera(project, plan, options, report, preservedShots);

        if (project.LegacyStageTransitions)
            BuildLegacyTransitions(project, plan, report);

        report.Notes.Add($"原版引擎舞台层: Style={style}, Seed={options.Seed}, "
                         + $"CameraShots={report.CameraShots}, CharacterKeys={report.CharacterKeys}, "
                         + $"TransSegments={report.TransSegments}");
        return report;
    }

    private static void BuildLegacyTransitions(DirectorProject project, LegacyPvPlanInfo plan, AutoReport report)
    {
        var seen = new HashSet<double>();
        foreach (var seg in plan.ToStageTransitions())
        {
            if (!seen.Add(Math.Round(seg.Start, 3))) continue;
            double dur = seg.End - seg.Start;
            if (TransitionsLockedNear(project, seg.Start, dur)) continue;
            project.DesignSegments.Add(seg);
        }

        foreach (var start in SwitchTimes(project, plan))
        {
            if (start <= 0.02 || !seen.Add(Math.Round(start, 3))) continue;
            double dur = Math.Clamp(plan.BeatLen * 0.5, 0.2, 0.6);
            if (TransitionsLockedNear(project, start, dur)) continue;

            var cut = plan.Cuts.FirstOrDefault(c => Math.Abs(c.Start - start) < 0.08);
            project.DesignSegments.Add(new DesignSegment
            {
                Track = DesignTrackKind.Trans,
                Start = start,
                End = start + dur,
                Technique = cut is { Emph: true } ? "whipPan" : "diagonalWipe",
                Params = cut is null
                    ? new Dictionary<string, double>(StringComparer.Ordinal)
                    : new Dictionary<string, double>(cut.TransParams, StringComparer.Ordinal),
                Seed = cut?.Seed ?? 0,
                Source = "auto",
                RefId = $"legacy_switch_{start:F2}",
            });
        }

        project.DesignSegments.Sort((a, b) => a.Start.CompareTo(b.Start));
        report.TransSegments = project.DesignSegments.Count(seg => seg.Track == DesignTrackKind.Trans
            && !(IsUser(seg.Source) || seg.Locked));
    }

    private static IEnumerable<double> SwitchTimes(DirectorProject project, LegacyPvPlanInfo plan)
    {
        if (project.LegacyStageCamera && project.CameraTrack is { Shots.Count: > 0 } track)
            return track.Shots.Where(shot => shot is not null && shot.Start > 0.02).Select(shot => shot.Start);
        return plan.Cuts.Where(cut => cut.Start > 0.02).Select(cut => cut.Start);
    }

    private static List<CameraShot> CleanupPreviousGeneration(DirectorProject project)
    {
        var autoInstanceIds = new HashSet<string>(StringComparer.Ordinal);
        var keptInstanceIds = new HashSet<string>(StringComparer.Ordinal);
        for (int i = project.Effects.Nodes.Count - 1; i >= 0; i--)
        {
            var node = project.Effects.Nodes[i];
            if (string.IsNullOrEmpty(node.Source)) node.Source = "auto";
            if (IsUser(node.Source) || node.Locked)
            {
                keptInstanceIds.Add(node.InstanceId);
                continue;
            }

            autoInstanceIds.Add(node.InstanceId);
            project.Effects.Nodes.RemoveAt(i);
        }

        project.Curves.RemoveAll(c =>
        {
            if (c is null) return true;
            if (IsUser(c.Source) || c.Locked) return false;
            if (string.IsNullOrEmpty(c.Source)) c.Source = "auto";
            bool legacyAuto =
                (c.Layer?.StartsWith(LayerPrefix, StringComparison.Ordinal) ?? false) ||
                (string.IsNullOrEmpty(c.Layer) && CameraParams.Any(p => c.Address == "camera." + p)) ||
                c.Address.StartsWith($"effect:{AutoPrefix}", StringComparison.Ordinal) ||
                c.Address.StartsWith($"layer:{AutoLayerPrefix}", StringComparison.Ordinal);
            if (keptInstanceIds.Count > 0 && c.Address.StartsWith("effect:", StringComparison.Ordinal))
                foreach (var id in keptInstanceIds)
                    if (c.Address.StartsWith($"effect:{id}.", StringComparison.Ordinal))
                        return false;
            return legacyAuto
                || autoInstanceIds.Any(id => c.Address.StartsWith($"effect:{id}.", StringComparison.Ordinal));
        });

        var cameraTrack = project.CameraTrack;
        var keptShots = new List<CameraShot>();
        if (cameraTrack is not null)
        {
            keptShots = cameraTrack.Shots.Where(shot => shot is not null
                                                        && (IsUser(shot.Source) || shot.Locked)).ToList();
            cameraTrack.Shots.RemoveAll(shot =>
            {
                if (shot is null) return true;
                if (IsUser(shot.Source) || shot.Locked) return false;
                return true;
            });
        }

        if (project.Scene.Layers.Any(l => l.Id.StartsWith(AutoLayerPrefix, StringComparison.Ordinal)))
            project.Scene.Layers = project.Scene.Layers
                .Where(l => !l.Id.StartsWith(AutoLayerPrefix, StringComparison.Ordinal)).ToList();

        project.DesignSegments.RemoveAll(seg => seg is null
            || (seg.Track == DesignTrackKind.Trans && !(IsUser(seg.Source) || seg.Locked))
            || (seg.RefId?.StartsWith(AutoPrefix, StringComparison.Ordinal) == true
                && !(IsUser(seg.Source) || seg.Locked)));

        return keptShots;
    }

    private static void Spike(FloatCurve c, double t, double baseVal, double peak, double decay)
    {
        Key(c, t - 0.03, baseVal, CurveInterp.Linear);
        Key(c, t, peak, CurveInterp.SmoothStep);
        Key(c, t + decay, baseVal, CurveInterp.Linear);
    }

    private static bool TransitionsLockedNear(DirectorProject project, double time, double dur)
    {
        foreach (var seg in project.DesignSegments)
        {
            if (seg is null || seg.Track != DesignTrackKind.Trans) continue;
            if (!seg.Locked && !IsUser(seg.Source)) continue;
            if (time >= seg.Start - dur * 0.5 && time <= seg.End + dur * 0.5) return true;
        }
        return false;
    }

    private static void Key(FloatCurve c, double t, double v, CurveInterp mode)
    {
        if (!double.IsFinite(t) || !double.IsFinite(v)) return;
        c.TryAdd(Math.Max(0, t), v, mode);
    }

    private static int Save(DirectorProject project, string address, string? layer, CurveBlend blend, FloatCurve curve, int minKeys = 1)
    {
        curve.NormalizeKeys();
        if (curve.Count < minKeys) return 0;
        if (project.Curves.Any(c => string.Equals(c.Address, address, StringComparison.Ordinal)
                                    && string.Equals(c.Layer, layer, StringComparison.Ordinal)))
            return 0;
        project.Curves.Add(new CurveSave { Address = address, Curve = curve, Layer = layer, Blend = blend, Source = "auto" });
        return curve.Count;
    }
}