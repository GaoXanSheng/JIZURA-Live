using System.Globalization;
using JizuraLive.Engine;
using JizuraLive.Render.Effects;

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

    public int TransSegments;

    public int EffectNodes;

    public int EffectWindows;

    public List<string> Notes = new();
}

public static partial class AutoDirector
{
    public const string AutoPrefix = "fx_autodir_";
    public const string LayerPrefix = "auto.";
    public const string AutoLayerPrefix = "autodir_";

    private static readonly string[] CameraParams = ["scale", "rotation", "offsetX", "offsetY", "blur", "brighten"];

    private static bool IsUser(string? source) => string.Equals(source, "user", StringComparison.OrdinalIgnoreCase);

    public static string AutoCamKey(double start) => "cam:" + start.ToString("F3", CultureInfo.InvariantCulture);

    public static string AutoTransKey(double start) => "trans:at:" + start.ToString("F3", CultureInfo.InvariantCulture);

    public static string AutoTransKey(DesignSegment seg)
    {
        var id = string.IsNullOrWhiteSpace(seg.RefId)
            ? seg.Start.ToString("F3", CultureInfo.InvariantCulture)
            : seg.RefId!;
        return "trans:" + id;
    }

    private static bool IsDeleted(DirectorProject project, string key) =>
        project.EditorLayout?.DeletedAutoIds?.Contains(key) == true;

        public static AutoReport GenerateLegacyStage(DirectorProject project, LegacyPvPlanInfo plan,
        BeatMap? map, AutoOptions options, bool legacyComposite)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(plan);
        options ??= new AutoOptions();

        project.Scene ??= PvScene.CreateBlank();
        project.Curves ??= new List<CurveSave>();

        var report = new AutoReport();
        options.Intensity = double.IsFinite(options.Intensity) ? Math.Clamp(options.Intensity, 0.0, 1.0) : 0.6;
        options.Style = (options.Style ?? "dynamic").Trim().ToLowerInvariant();
        if (options.Style is not ("soft" or "dynamic" or "epic")) options.Style = "dynamic";

        var preservedShots = CleanupPreviousGeneration(project);

        if (project.LegacyStageCamera)
            BuildAutoCamera(project, plan, options, report, preservedShots);

        project.SyncCharacterCameraTrack();

        if (project.LegacyStageTransitions)
            BuildLegacyTransitions(project, plan, report);

        BuildAutoEffects(project, plan, report, legacyComposite);

        report.Notes.Add($"原版引擎舞台层: Style={options.Style}, Seed={options.Seed}, "
                         + $"CameraShots={report.CameraShots}, TransSegments={report.TransSegments}, "
                         + $"EffectNodes={report.EffectNodes}, EffectWindows={report.EffectWindows}");
        return report;
    }

    private static readonly Dictionary<string, string> AutoEffectMap = new(StringComparer.Ordinal)
    {
        ["chroma"] = "chromatic",
        ["radialChroma"] = "chromatic",
        ["shake"] = "shake",
        ["slice"] = "slice",
        ["block"] = "glitch",
        ["macroBlock"] = "glitch",
        ["pixelSort"] = "glitch",
        ["colorBars"] = "glitch",
        ["tvStatic"] = "grain",
        ["flash"] = "flash",
        ["bloomFlash"] = "bloom",
        ["snapshot"] = "flash",
        ["zoom"] = "zoomPunch",
        ["zoomStutter"] = "zoomPunch",
        ["heartbeat"] = "zoomPunch",
        ["squash"] = "zoomPunch",
        ["rotateSnap"] = "zoomPunch",
        ["mosaic"] = "posterize",
        ["ditherBit"] = "posterize",
        ["edgeDetect"] = "posterize",
        ["negativeRing"] = "duotone",
        ["duotone"] = "duotone",
        ["bulge"] = "liquid",
        ["ripple"] = "liquid",
        ["defocus"] = "motionBlur",
        ["echoFrames"] = "motionBlur",
        ["loopScroll"] = "motionBlur",
        ["interlace"] = "scanline",
        ["scanBar"] = "scanline",
        ["filmAdvance"] = "grain",
        ["dustScratches"] = "grain",
        ["focusLines"] = "speedLines",
        ["speedLines"] = "speedLines",
        ["lightRays"] = "lightRays",
        ["anamorphic"] = "lightRays",
        ["starGlint"] = "lightRays",
        ["shatter"] = "glassShatter",
        ["halftone"] = "halftone",
    };

    private static void BuildAutoEffects(DirectorProject project, LegacyPvPlanInfo plan, AutoReport report,
        bool legacyComposite)
    {
        if (legacyComposite)
        {
            report.Notes.Add("原版合成层开启：事件特效由原版引擎绘制，不复制到 GPU 效果图");
            return;
        }

        var groups = new Dictionary<string, List<LegacyPvEventInfo>>(StringComparer.Ordinal);
        foreach (var ev in plan.Events)
        {
            if (ev is null || !AutoEffectMap.TryGetValue(ev.Type, out var defId)) continue;
            if (!groups.TryGetValue(defId, out var list))
                groups[defId] = list = new List<LegacyPvEventInfo>();
            list.Add(ev);
        }

        foreach (var (defId, events) in groups)
        {
            var autoId = AutoPrefix + defId;
            if (IsDeleted(project, "fx:" + autoId)) continue;

            var node = project.Effects.Nodes.FirstOrDefault(n =>
                n is not null && string.Equals(n.InstanceId, autoId, StringComparison.Ordinal));
            if (node is not null && (IsUser(node.Source) || node.Locked)) continue;

            if (node is null)
            {
                node = new EffectNode
                {
                    DefId = defId,
                    InstanceId = autoId,
                    Target = "",
                    Source = "auto",
                };
                project.Effects.Nodes.Add(node);
            }
            else
            {
                node.Windows.Clear();
                node.Values.Clear();
                node.Colors.Clear();
            }

            var windows = new List<EffectWindow>();
            foreach (var ev in events)
            {
                var duration = ev.Dur > 0.01 ? ev.Dur : 0.25;
                var start = Math.Max(0, ev.T - Math.Min(0.05, duration * 0.2));
                var end = ev.T + duration;
                if (end - start < 0.05) continue;

                var fadeIn = Math.Min(0.08, (end - start) * 0.35);
                var fadeOut = Math.Min(0.12, (end - start) * 0.45);
                var gain = Math.Clamp(0.6 + 0.35 * ev.Amp, 0.6, 1.8);
                var window = new EffectWindow(start, end, fadeIn, fadeOut) { Gain = gain };
                if (window.IsValid) windows.Add(window);
            }

            windows.Sort((a, b) => a.Start.CompareTo(b.Start));
            var merged = new List<EffectWindow>(windows.Count);
            foreach (var window in windows)
            {
                if (merged.Count > 0 && window.Start <= merged[^1].End + 0.02)
                {
                    var last = merged[^1];
                    merged[^1] = new EffectWindow(last.Start, Math.Max(last.End, window.End),
                        last.FadeIn, window.FadeOut)
                    {
                        Gain = Math.Max(last.Gain, window.Gain),
                    };
                }
                else
                {
                    merged.Add(window);
                }
            }

            foreach (var window in merged) node.Windows.Add(window);
            report.EffectNodes++;
            report.EffectWindows += merged.Count;
        }
    }

    private static void BuildLegacyTransitions(DirectorProject project, LegacyPvPlanInfo plan, AutoReport report)
    {
        var seen = new HashSet<double>();
        foreach (var seg in plan.ToStageTransitions())
        {
            if (!seen.Add(Math.Round(seg.Start, 3))) continue;
            if (IsDeleted(project, AutoTransKey(seg.Start)) || IsDeleted(project, AutoTransKey(seg))) continue;
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
            var segment = new DesignSegment
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
            };
            if (IsDeleted(project, AutoTransKey(start)) || IsDeleted(project, AutoTransKey(segment))) continue;
            project.DesignSegments.Add(segment);
        }

        project.DesignSegments.Sort((a, b) => a.Start.CompareTo(b.Start));
        report.TransSegments = project.DesignSegments.Count(seg => seg.Track == DesignTrackKind.Trans);
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

}