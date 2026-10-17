using JizuraLive.Engine;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Text;

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
    public int DecorLayers;
    public int TransSegments;
    public List<string> Notes = new();
}

public static partial class AutoDirector
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

    public const string AutoPrefix = "fx_autodir_";
    public const string LayerPrefix = "auto.";
    public const string BeatLayer = "auto.beat";
    public const string PulseLayer = "auto.pulse";
    public const string SnapLayer = "auto.snap";
    public const string WhipLayer = "auto.whip";

    private static readonly string[] CameraParams = ["scale", "rotation", "offsetX", "offsetY", "blur", "brighten"];

    private static bool IsUser(string? source) => string.Equals(source, "user", StringComparison.OrdinalIgnoreCase);

    public static AutoReport Generate(DirectorProject project, BeatMap map, AutoOptions options)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(map);
        options ??= new AutoOptions();

        project.Scene ??= PvScene.CreateBlank();
        project.Effects ??= new EffectGraph();
        project.Curves ??= new List<CurveSave>();

        var report = new AutoReport();
        double intensity = double.IsFinite(options.Intensity) ? Math.Clamp(options.Intensity, 0.0, 1.0) : 0.6;
        string style = (options.Style ?? "dynamic").Trim().ToLowerInvariant();
        if (style is not ("soft" or "dynamic" or "epic")) style = "dynamic";

        var preservedShots = CleanupPreviousGeneration(project);

        var song = Song.Build(map, project.LrcText);
        var look = Look.For(style);

        project.Scene.Atmosphere = look.Atmosphere;
        project.Scene.LyricMotionSeed = options.Seed;
        var rng = new Random(options.Seed);

        BuildCamera(project, song, look, intensity, options.Seed, report, preservedShots);
        BuildEffects(project, song, look, intensity, rng, report);
        ApplyLyricStyle(project, song);

        BuildAnimeFx(project, song, look, style, intensity, new Random(unchecked(options.Seed * 7919 + 1)), report);
        BuildPerformance(project, song, style);
        BuildDecorLayers(project, song, look, intensity, new Random(unchecked(options.Seed * 104729 + 7)), report);
        BuildTransitions(project, song, look, intensity, new Random(unchecked(options.Seed * 15485863 + 3)), report);

        foreach (var sec in song.Sections)
            report.Notes.Add($"[{sec.Start:F2}s–{sec.End:F2}s] {sec.Kind}（能量 {sec.Energy:F2}）{(song.IsHigh(sec) ? " · 高潮段" : "")}");
        report.Notes.Add($"AutoDirector: Style={style}, Intensity={intensity:F2}, Seed={options.Seed}, "
                         + $"CameraShots={report.CameraShots}, EffectInstances={report.EffectInstances}, "
                         + $"EffectWindows={report.EffectWindows}, CurveKeys={report.CurveKeys}, "
                         + $"DecorLayers={report.DecorLayers}, TransSegments={report.TransSegments}");
        return report;
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

        project.LyricStyles.RemoveAll(span => span is null || !(IsUser(span.Source) || span.Locked));

        project.DesignSegments.RemoveAll(seg => seg is null
            || (seg.Track == DesignTrackKind.Trans && !(IsUser(seg.Source) || seg.Locked)));

        return keptShots;
    }

    public const string AutoLayerPrefix = "autodir_";

    private static void BuildWhips(Song song, Look look, double k, FloatCurve whip)
    {
        foreach (var sec in song.Sections)
        {
            if (!song.IsHigh(sec)) continue;
            int bar = 0;
            foreach (var d in song.DownbeatsIn(sec.Start, sec.End))
            {
                if (d - sec.Start < 0.05) continue;
                double w = (bar++ % 2 == 0 ? 1 : -1) * look.Whip * k;
                Key(whip, d, w, CurveInterp.SmoothStep);
                Key(whip, d + song.BarLen * 0.5, -w * 0.55, CurveInterp.SmoothStep);
            }
        }

        foreach (var sec in song.Sections)
        {
            if (sec.Start <= 0.01) continue;
            double w = look.Whip * 1.15 * k;
            Key(whip, Math.Max(0, sec.Start - 0.12), w, CurveInterp.SmoothStep);
            Key(whip, sec.Start + 0.02, -w * 0.6, CurveInterp.SmoothStep);
            Key(whip, sec.Start + 0.35, 0, CurveInterp.Linear);
        }
    }

    private static void BuildDefocus(Song song, Look look, double k, FloatCurve blur, FloatCurve brighten)
    {
        Key(blur, 0, 0.4 * k, CurveInterp.Linear);
        Key(brighten, 0, 0, CurveInterp.Linear);

        foreach (var d in Drops(song))
        {
            double rs = RiseStart(song, d);
            Key(blur, rs, 0.6 * k, CurveInterp.SmoothStep);
            Key(blur, Math.Max(rs, d - 0.06), look.DropBlur * k, CurveInterp.SmoothStep);
            Key(blur, d + 0.3, 0.8 * k, CurveInterp.Linear);
            Key(brighten, rs, 0, CurveInterp.SmoothStep);
            Key(brighten, d, look.DropBright * k, CurveInterp.SmoothStep);
            Key(brighten, d + 0.7, 0.02 * k, CurveInterp.Linear);
        }

        foreach (var sec in song.Sections)
        {
            if (!song.IsHigh(sec)) continue;
            foreach (var d in song.DownbeatsIn(sec.Start, sec.End))
            {
                if (d - sec.Start < 0.05) continue;
                Spike(blur, d, 0.8 * k, 2.4 * k, Math.Min(song.BarLen * 0.35, 0.5));
                Spike(brighten, d, 0.02 * k, 0.04 * k, Math.Min(song.BeatLen * 0.8, 0.35));
            }
        }

        foreach (var sec in song.Sections)
        {
            if (sec.Kind != "bridge") continue;
            double len = sec.End - sec.Start;
            Key(blur, sec.Start, 1.2 * k, CurveInterp.SmoothStep);
            Key(blur, sec.Start + len * 0.5, 3.2 * k, CurveInterp.SmoothStep);
            Key(blur, sec.End - Math.Min(0.08, len * 0.1), 1.2 * k, CurveInterp.Linear);
        }
    }

    private static void Spike(FloatCurve c, double t, double baseVal, double peak, double decay)
    {
        Key(c, t - 0.03, baseVal, CurveInterp.Linear);
        Key(c, t, peak, CurveInterp.SmoothStep);
        Key(c, t + decay, baseVal, CurveInterp.Linear);
    }

    private static List<double> Drops(Song song)
    {
        var drops = new List<double>();
        for (int i = 0; i < song.Sections.Count; i++)
        {
            var sec = song.Sections[i];
            if (!song.IsHigh(sec)) continue;
            if (i > 0 && song.IsHigh(song.Sections[i - 1])) continue;
            drops.Add(sec.Start);
        }

        return drops;
    }

    private static double RiseStart(Song song, double drop)
    {
        var prev = song.Sections.LastOrDefault(s => s.End <= drop + 1e-6 && s.Start < drop);
        double room = prev is null ? drop : (drop - prev.Start) * 0.5;
        return Math.Max(0, drop - Math.Min(song.BarLen, room));
    }

    private static void BuildEffects(DirectorProject project, Song song, Look look, double k, Random rng, AutoReport report)
    {
        var highs = song.Sections.Where(song.IsHigh).ToList();
        var bridges = song.Sections.Where(s => s.Kind == "bridge").ToList();
        double beat = song.BeatLen, bar = song.BarLen;

        var drops = Drops(song);

        string lyricFxTarget = song.Lines.Count > 0 ? PvStageCompositor.FixedZones[0] : "";

        if (look.Riser && drops.Count > 0)
        {
            var zoom = AddEffect(project, "zoomPunch", "riser", n => Set(n, "scale", 1.0f + 0.12f * (float)k));
            foreach (var d in drops)
            {
                double rs = RiseStart(song, d);
                if (d - rs > 0.2) Window(zoom, rs, d, fadeIn: d - rs, fadeOut: 0.03);
            }
        }

        if (look.Shake && drops.Count > 0)
        {
            var shake = AddEffect(project, "shake", "drop", n =>
            {
                Set(n, "amount", 20f * (float)k);
                Set(n, "rate", 1.8f);
            });
            foreach (var d in drops) Window(shake, d, d + 0.5, fadeOut: 0.45);
        }

        if (look.GlowBurst > 0 && drops.Count > 0)
        {
            var burst = AddEffect(project, "glow", "burst", n =>
            {
                Set(n, "threshold", 0.75f);
                Set(n, "intensity", look.GlowBurst * (float)k);
                Set(n, "radius", 40f);
            });
            foreach (var d in drops)
                Window(burst, d, d + Math.Min(0.8, beat * 2), fadeOut: Math.Min(0.7, beat * 1.6));
        }

        if (look.WhipBlur && highs.Count > 0)
        {
            var drag = AddEffect(project, "motionBlur", "whip", n =>
            {
                Set(n, "length", 52f * (float)k);
                Set(n, "angle", 0f);
            });
            foreach (var sec in highs)
                foreach (var d in song.DownbeatsIn(sec.Start, sec.End))
                {
                    if (d - sec.Start < 0.05) continue;
                    Window(drag, d, d + Math.Min(0.22, beat * 0.45), fadeOut: Math.Min(0.2, beat * 0.4));
                }
            foreach (var d in drops) Window(drag, d - 0.12, d + 0.1, fadeOut: 0.1);
        }

        if (look.Shatter && highs.Count > 0)
        {
            var target = drops.Count > 0 ? drops[^1] : highs[^1].Start;
            var glass = AddEffect(project, "glassShatter", "finale", n =>
            {
                Set(n, "amount", 20f * (float)k);
                Set(n, "cells", 10f);
                Set(n, "crack", 0.7f);
            });
            Window(glass, target, target + Math.Min(0.4, beat), fadeOut: Math.Min(0.4, beat));
        }

        if (look.Glitch && highs.Count > 0)
        {
            var glitch = AddEffect(project, "glitch", "burst", n =>
            {
                Set(n, "amount", 30f * (float)k);
                Set(n, "blockSize", 40f);
                Set(n, "seed", rng.Next(0, 999));
            }, lyricFxTarget);
            foreach (var d in drops)
                if (d > 0.15) Window(glitch, d - 0.12, d);
            foreach (var sec in highs)
            {
                int i = 0;
                foreach (var d in song.DownbeatsIn(sec.Start, sec.End))
                {
                    if (i++ % 2 == 1 && rng.NextDouble() < 0.6) Window(glitch, d, d + 0.1);
                }
            }
        }

        if (look.Chromatic && highs.Count > 0)
        {
            var chroma = AddEffect(project, "chromatic", "accent", n =>
            {
                Set(n, "amount", 18f * (float)k);
                Set(n, "radial", 1f);
            }, lyricFxTarget);
            foreach (var d in drops)
            {
                double rs = RiseStart(song, d);
                if (d - rs > 0.2) Window(chroma, rs, d, fadeIn: d - rs);
            }
            foreach (var sec in highs)
            foreach (var d in song.DownbeatsIn(sec.Start + 0.1, sec.End))
                Window(chroma, d, d + 0.2, fadeOut: 0.2);
        }

        if (bridges.Count > 0 && look.BridgeFx is { } bridgeFx)
        {
            var node = bridgeFx == "liquid"
                ? AddEffect(project, "liquid", "bridge", n =>
                {
                    Set(n, "amount", 18f * (float)k);
                    Set(n, "scale", 2.5f);
                    Set(n, "speed", 0.4f);
                })
                : AddEffect(project, "scanline", "bridge", n =>
                {
                    Set(n, "strength", 0.3f * (float)k);
                    Set(n, "density", 520f);
                });
            foreach (var b in bridges) Window(node, b.Start, b.End, fadeIn: bar, fadeOut: beat);
        }

        var grade = AddEffect(project, "colorGrade", "look", n =>
        {
            Set(n, "saturation", look.Saturation);
            Set(n, "contrast", look.Contrast);
            Set(n, "brightness", look.Brightness);
            Set(n, "strength", 0.5f + 0.5f * (float)k);
            SetColor(n, "tint", look.Tint);
            Set(n, "tintStrength", look.TintStrength);
        });

        if (drops.Count > 0)
        {
            var sat = new FloatCurve();
            Key(sat, 0, look.Saturation, CurveInterp.Linear);
            foreach (var d in drops) Spike(sat, d, look.Saturation, Math.Min(2f, look.Saturation + 0.4f * (float)k), 1.1);
            report.CurveKeys += Save(project, $"effect:{grade.InstanceId}.saturation", null, CurveBlend.Add, sat);
        }

        var bloom = AddEffect(project, "bloom", "glow", n =>
        {
            Set(n, "threshold", look.BloomThreshold);
            Set(n, "radius", look.BloomRadius);
            Set(n, "intensity", (float)look.BloomLo);
        });
        var bloomCurve = new FloatCurve();
        foreach (var sec in song.Sections)
        {
            double v = (look.BloomLo + (look.BloomHi - look.BloomLo) * sec.Energy) * (song.IsHigh(sec) ? 1.15 : 1.0);
            v *= 0.5 + 0.5 * k;
            double len = sec.End - sec.Start;
            Key(bloomCurve, sec.Start + Math.Min(beat, len / 3), v, CurveInterp.Linear);
            Key(bloomCurve, sec.End - Math.Min(0.08, len * 0.1), v, CurveInterp.SmoothStep);
        }
        report.CurveKeys += Save(project, $"effect:{bloom.InstanceId}.intensity", null, CurveBlend.Add, bloomCurve);

        if (look.Rays > 0 && highs.Count > 0)
        {
            var rays = AddEffect(project, "lightRays", "chorus", n =>
            {
                Set(n, "intensity", look.Rays * (float)k);
                Set(n, "length", 0.22f);
                Set(n, "cy", 0.3f);
            });
            foreach (var sec in highs) Window(rays, sec.Start, sec.End, fadeIn: bar, fadeOut: beat * 2);

            var raysCurve = new FloatCurve();
            Key(raysCurve, 0, look.Rays * k, CurveInterp.Linear);
            foreach (var d in drops) Spike(raysCurve, d, look.Rays * k, look.Rays * k * 2.4, beat * 2);
            foreach (var sec in highs)
                foreach (var d in song.DownbeatsIn(sec.Start, sec.End))
                    if (d - sec.Start >= 0.5) Spike(raysCurve, d, look.Rays * k, look.Rays * k * 1.5, beat);
            report.CurveKeys += Save(project, $"effect:{rays.InstanceId}.intensity", null, CurveBlend.Add, raysCurve);
        }

        var flash = AddEffect(project, "flash", "cut", n =>
        {
            Set(n, "amount", look.Flash * (float)(0.4 + 0.6 * k));
            SetColor(n, "color", look.FlashColor);
        });
        for (int i = 1; i < song.Sections.Count; i++)
        {
            var sec = song.Sections[i];
            double t = sec.Start;
            if (drops.Contains(t)) Window(flash, t, t + Math.Min(0.35, beat * 1.0), fadeOut: Math.Min(0.35, beat * 1.0));
            else if (look.SoftCuts) Window(flash, t, t + 0.22, fadeOut: 0.22);
        }

        if (look.BeatFlash && highs.Count > 0)
        {
            foreach (var sec in highs)
                foreach (var d in song.DownbeatsIn(sec.Start, sec.End))
                {
                    if (d - sec.Start < 0.5) continue;
                    Window(flash, d, d + 0.09, fadeOut: 0.09);
                }
        }

        AddEffect(project, "vignette", "look", n =>
        {
            Set(n, "intensity", look.Vignette * (float)(0.6 + 0.4 * k));
            Set(n, "smoothness", 0.5f);
        });

        if (look.Grain > 0)
            AddEffect(project, "grain", "look", n =>
            {
                Set(n, "amount", look.Grain);
                Set(n, "size", 1.4f);
            });

        project.Effects.Nodes.RemoveAll(n =>
            !n.Locked && !IsUser(n.Source)
            && n.InstanceId.StartsWith(AutoPrefix, StringComparison.Ordinal)
            && Transient.Contains(n.DefId) && n.Windows.Count == 0);

        foreach (var n in project.Effects.Nodes)
        {
            if (!n.InstanceId.StartsWith(AutoPrefix, StringComparison.Ordinal)) continue;
            n.Windows.Sort((a, b) => a.Start.CompareTo(b.Start));
            report.EffectInstances++;
            report.EffectWindows += n.Windows.Count;
        }
    }

    private static readonly HashSet<string> Transient =
        ["zoomPunch", "shake", "glassShatter", "glitch", "chromatic", "liquid", "scanline", "lightRays", "flash"];

    private static EffectNode AddEffect(DirectorProject project, string defId, string role,
        Action<EffectNode>? configure = null, string target = "")
    {
        string baseId = $"{AutoPrefix}{defId}_{role}";
        var existing = project.Effects.Nodes.FirstOrDefault(n =>
            string.Equals(n.InstanceId, baseId, StringComparison.Ordinal));
        if (existing is not null && (existing.Locked || IsUser(existing.Source)))
        {
            existing.Target = target;
            existing.Enabled = true;
            existing.Windows.Clear();
            configure?.Invoke(existing);
            return existing;
        }

        var node = new EffectNode
        {
            DefId = defId,
            InstanceId = baseId,
            Enabled = true,
            Target = target,
            Source = "auto",
        };
        configure?.Invoke(node);
        project.Effects.Nodes.Add(node);
        return node;
    }

    private static void Window(EffectNode node, double start, double end, double fadeIn = 0, double fadeOut = 0)
    {
        start = Math.Max(0, start);
        var w = new EffectWindow(start, end, Math.Max(0, fadeIn), Math.Max(0, fadeOut));
        if (w.IsValid) node.Windows.Add(w);
    }

    private static void Set(EffectNode node, string key, float value)
    {
        var p = EffectCatalog.Get(node.DefId)?.Params.FirstOrDefault(x => x.Key == key);
        if (p != null) node.Set(p, value);
        else node.Values[key] = value;
    }

    private static void SetColor(EffectNode node, string key, string hex)
    {
        var p = EffectCatalog.Get(node.DefId)?.Params.FirstOrDefault(x => x.Key == key && x.Kind == EffectParamKind.Color);
        if (p != null) node.SetColor(p, hex);
    }

    private static void BuildDecorLayers(DirectorProject project, Song song, Look look, double k, Random rng,
        AutoReport report)
    {
        if (!(song.Duration > 0.01) || song.Sections.Count == 0) return;

        string KindFor(Section sec, Look lk)
        {
            if (sec.Kind is "intro" or "outro") return lk.Style == "soft" ? DecorCatalog.Snow : DecorCatalog.Fireflies;
            if (song.IsHigh(sec)) return lk.Style == "epic" ? DecorCatalog.Hanabi : DecorCatalog.Confetti;
            return lk.Style == "soft" ? DecorCatalog.Petals : DecorCatalog.Fireflies;
        }

        var spanned = new List<(double Start, double End, string Kind, Section Sec)>();
        foreach (var sec in song.Sections)
        {
            if (sec.End - sec.Start < Math.Max(1.5, song.BarLen)) continue;
            spanned.Add((sec.Start, sec.End, KindFor(sec, look), sec));
        }
        if (spanned.Count == 0) return;

        int i = 0;
        foreach (var (start, end, kind, sec) in spanned)
        {
            bool merged = false;
            if (start > 0 && rng.NextDouble() > 0.25 + 0.5 * sec.Energy * k) continue;
            if (i > 0 && spanned[i - 1].Kind == kind && Math.Abs(spanned[i - 1].End - start) < 1e-6)
            {
                spanned[i - 1] = (spanned[i - 1].Start, end, kind, spanned[i - 1].Sec);
                merged = true;
            }
            i++;
            if (merged) continue;

            string id = $"{AutoLayerPrefix}decor_{kind}_{start:F2}";
            if (project.Scene.Layers.Any(l => string.Equals(l.Id, id, StringComparison.Ordinal))) continue;

            double density = Math.Round(Math.Clamp(20 + 110 * sec.Energy * k, 0, 400) / 5.0) * 5;
            project.Scene.Layers.Add(new PvLayerConfig
            {
                Id = id,
                Name = $"{DecorCatalog.Label(kind)}（自动）",
                Kind = PvLayerKind.Decor,
                Opacity = Math.Clamp(0.45 + 0.4 * sec.Energy * k, 0, 1),
                Decor = new DecorConfig
                {
                    Kinds = { kind },
                    Density = density,
                    Speed = Math.Clamp(0.6 + 0.7 * sec.Energy * k, 0.4, 1.8),
                    Size = Math.Clamp(0.8 + 0.35 * (1 - sec.Energy) * k, 0.6, 1.4),
                    Color = DecorCatalog.DefaultColor(kind),
                    AccentColor = DecorCatalog.DefaultAccent(kind),
                    Seed = 1 + (rng.Next() % 9999),
                    Bursts = song.IsHigh(sec) && look.Style != "soft"
                        ? song.DownbeatsIn(start + song.BarLen, end).Where(d => (int)Math.Round((d - start) / song.BarLen) % 4 == 0).ToList()
                        : new List<double>(),
                },
            });
            report.DecorLayers++;
        }
    }

    private static void BuildTransitions(DirectorProject project, Song song, Look look, double k, Random rng,
        AutoReport report)
    {
        if (!(song.Duration > 0.01)) return;
        if (look.Style == "soft" && !look.SoftCuts) return;

        double rate = Math.Clamp(0.2 + 0.55 * k, 0, 0.9);
        double transDur = Math.Clamp(song.BeatLen * 1.5, 0.22, 0.6);

        string PickFor(Section sec)
        {
            if (song.IsHigh(sec)) return rng.NextDouble() < 0.35 ? "whipPan" : "flashCross";
            if (sec.Kind is "intro" or "outro") return "hrBlink";
            return rng.NextDouble() < 0.3 ? "zoomThrough" : "diagonalWipe";
        }

        for (int s = 1; s < song.Sections.Count; s++)
        {
            var prev = song.Sections[s - 1];
            var sec = song.Sections[s];
            if (sec.Start <= 0.01 || sec.Start >= song.Duration - 0.01) continue;
            if (prev.Kind is "intro" && song.IsHigh(sec)) continue;
            double chance = rate * (song.IsHigh(sec) ? 1.5 : 1.0) * (0.4 + 0.6 * sec.Energy);
            if (rng.NextDouble() > chance) continue;
            if (TransitionsLockedNear(project, sec.Start, transDur)) continue;
            string tech = PickFor(sec);
            var pars = new Dictionary<string, double>(StringComparer.Ordinal);
            if (tech is "diagonalWipe" or "whipPan") pars["dir"] = rng.NextDouble() < 0.5 ? 1 : 3;
            project.DesignSegments.Add(new DesignSegment
            {
                Track = DesignTrackKind.Trans,
                Start = Math.Max(0, sec.Start - transDur * 0.35),
                End = sec.Start + transDur * 0.65,
                Technique = tech,
                Params = pars,
                Source = "auto",
                RefId = $"{AutoPrefix}trans_{sec.Start:F2}",
            });
            report.TransSegments++;
        }

        project.DesignSegments.Sort((a, b) => a.Start.CompareTo(b.Start));
    }

    private static bool TransitionsLockedNear(DirectorProject project, double time, double dur)
    {
        foreach (var seg in project.DesignSegments)
        {
            if (seg is null || seg.Track != DesignTrackKind.Trans || !seg.Locked) continue;
            if (time >= seg.Start - dur * 0.5 && time <= seg.End + dur * 0.5) return true;
        }
        return false;
    }

    private static void ApplyLyricStyle(DirectorProject project, Song song)
    {
        bool hasHigh = song.Sections.Any(song.IsHigh);
        bool hasVerse = song.Sections.Any(s => s.Kind == "verse");
        project.Scene.LyricStyleName = hasHigh ? "Neon" : (hasVerse ? "Minimal" : LyricStyle.ClassicName);
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

    private sealed record Look(
        double ChorusPunch, double VersePunch, double Kick, double BeatPulse,
        double Snap, double Whip, double Dutch,
        double DropBlur, double DropBright, float GlowBurst, bool WhipBlur, bool BeatFlash, double Atmosphere,
        float Saturation, float Contrast, float Brightness, string Tint, float TintStrength,
        float BloomThreshold, float BloomRadius, double BloomLo, double BloomHi,
        float Vignette, float Grain, float Flash, string FlashColor, bool SoftCuts,
        bool Riser, bool Shake, bool Shatter, bool Glitch, bool Chromatic, float Rays, string? BridgeFx,
        string Style, double CamGain, double CamCutBars)
    {
        public static Look For(string style) => style switch
        {
            "soft" => new Look(
                ChorusPunch: 0.025, VersePunch: 0, Kick: 0, BeatPulse: 0,
                Snap: 0.02, Whip: 24, Dutch: 0.8,
                DropBlur: 1.5, DropBright: 0.05, GlowBurst: 0.3f, WhipBlur: false, BeatFlash: false, Atmosphere: 0.45,
                Saturation: 1.05f, Contrast: 1.0f, Brightness: 0f, Tint: "#ffe6d0", TintStrength: 0.12f,
                BloomThreshold: 0.95f, BloomRadius: 30f, BloomLo: 0.08, BloomHi: 0.16,
                Vignette: 0.32f, Grain: 0.04f, Flash: 0.28f, FlashColor: "#fff4e0", SoftCuts: false,
                Riser: false, Shake: false, Shatter: false, Glitch: false, Chromatic: false, Rays: 0.18f, BridgeFx: "liquid",
                Style: "soft", CamGain: 0.55, CamCutBars: 4.0),
            "epic" => new Look(
                ChorusPunch: 0.10, VersePunch: 0.03, Kick: 0.6, BeatPulse: 0,
                Snap: 0.07, Whip: 140, Dutch: 4.2,
                DropBlur: 5, DropBright: 0.09, GlowBurst: 0.5f, WhipBlur: true, BeatFlash: true, Atmosphere: 0.7,
                Saturation: 0.95f, Contrast: 1.12f, Brightness: -0.02f, Tint: "#cfe8ff", TintStrength: 0.18f,
                BloomThreshold: 0.95f, BloomRadius: 36f, BloomLo: 0.08, BloomHi: 0.16,
                Vignette: 0.52f, Grain: 0.05f, Flash: 0.6f, FlashColor: "#ffe9f4", SoftCuts: true,
                Riser: true, Shake: true, Shatter: true, Glitch: false, Chromatic: false, Rays: 0.28f, BridgeFx: null,
                Style: "epic", CamGain: 1.15, CamCutBars: 1.25),
            _ => new Look(
                ChorusPunch: 0.08, VersePunch: 0.02, Kick: 0.8, BeatPulse: 0.012,
                Snap: 0.055, Whip: 110, Dutch: 3.2,
                DropBlur: 4, DropBright: 0.07, GlowBurst: 0.35f, WhipBlur: true, BeatFlash: true, Atmosphere: 0.6,
                Saturation: 1.12f, Contrast: 1.12f, Brightness: -0.02f, Tint: "#d8e4ff", TintStrength: 0.08f,
                BloomThreshold: 0.95f, BloomRadius: 24f, BloomLo: 0.08, BloomHi: 0.18,
                Vignette: 0.40f, Grain: 0f, Flash: 0.5f, FlashColor: "#ffd9ec", SoftCuts: true,
                Riser: true, Shake: true, Shatter: false, Glitch: true, Chromatic: true, Rays: 0f, BridgeFx: "scanline",
                Style: "dynamic", CamGain: 1.0, CamCutBars: 2.0),
        };
    }

    private sealed class Song
    {
        public List<double> Beats = new();
        public List<double> Downbeats = new();
        public List<Section> Sections = new();
        public List<(double Start, double End)> Lines = new();
        public double BeatLen = 0.5;
        public double BarLen = 2.0;
        public double Duration;
        private bool _hasChorus;

        public bool IsHigh(Section s) =>
            s.Kind is not ("intro" or "outro")
            && (s.Kind == "chorus" || s.Energy >= (_hasChorus ? 0.8 : 0.66));

        public Section? SectionAt(double t) => Sections.FirstOrDefault(s => t >= s.Start && t < s.End);

        public IEnumerable<double> DownbeatsIn(double a, double b) => Downbeats.Where(d => d >= a && d < b);

        public IEnumerable<double> BeatsIn(double a, double b) => Beats.Where(d => d >= a && d < b);

        public static Song Build(BeatMap map, string? lrcText)
        {
            var song = new Song();
            song.Beats = map.Beats.Where(double.IsFinite).Where(b => b >= 0).Distinct().OrderBy(b => b).ToList();

            if (song.Beats.Count >= 2)
            {
                var diffs = song.Beats.Zip(song.Beats.Skip(1), (a, b) => b - a).Where(d => d > 0).OrderBy(d => d).ToList();
                if (diffs.Count > 0) song.BeatLen = diffs[diffs.Count / 2];
            }
            else if (double.IsFinite(map.Bpm) && map.Bpm > 0)
            {
                song.BeatLen = 60.0 / map.Bpm;
            }

            song.Downbeats = map.Downbeats.Where(double.IsFinite).Where(b => b >= 0).Distinct().OrderBy(b => b).ToList();
            if (song.Downbeats.Count == 0) song.Downbeats = song.Beats.Where((_, i) => i % 4 == 0).ToList();
            song.BarLen = song.BeatLen * 4;

            try
            {
                foreach (var line in LrcParser.Parse(lrcText))
                    if (!string.IsNullOrWhiteSpace(line.Text))
                        song.Lines.Add((line.StartMs / 1000.0, (line.StartMs + line.DurMs) / 1000.0));
            }
            catch (Exception)
            {
            }

            var raw = map.Sections
                .Where(s => s != null && double.IsFinite(s.Start) && double.IsFinite(s.End) && s.End > s.Start)
                .OrderBy(s => s.Start)
                .ToList();

            double end = Math.Max(raw.Count > 0 ? raw.Max(s => s.End) : 0,
                song.Beats.Count > 0 ? song.Beats[^1] + song.BeatLen : 0);
            if (song.Lines.Count > 0) end = Math.Max(end, song.Lines.Max(l => l.End));
            song.Duration = end;

            if (raw.Count == 0 && end > 0)
                raw.Add(new Section { Start = 0, End = end, Kind = "verse", Label = "A", Energy = 0.5 });

            double lo = raw.Count > 0 ? raw.Min(s => s.Energy) : 0;
            double hi = raw.Count > 0 ? raw.Max(s => s.Energy) : 0;
            foreach (var s in raw)
            {
                double en = double.IsFinite(s.Energy) && hi - lo > 1e-9 ? (s.Energy - lo) / (hi - lo) : 0.5;
                song.Sections.Add(new Section
                {
                    Start = s.Start,
                    End = s.End,
                    Kind = (s.Kind ?? "verse").Trim().ToLowerInvariant(),
                    Label = s.Label,
                    Energy = en,
                });
            }

            song._hasChorus = song.Sections.Any(s => s.Kind == "chorus");
            return song;
        }
    }
}
