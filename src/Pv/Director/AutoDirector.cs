using JizuraLive.Engine;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Text;

namespace JizuraLive.Pv;

public sealed class AutoOptions
{
    public string Style = "dynamic"; /* soft|dynamic|epic */
    public double Intensity = 0.6; /* 0..1 */

    /// <summary>随机种子：同一首歌、同一种子，生成结果逐字节相同；换种子换一版点缀（故障/闪切的取舍）。</summary>
    public int Seed = 1;
}

public sealed class AutoReport
{
    public int CameraKeys;
    public int EffectInstances;

    /// <summary>全部效果出场区间数（一次闪白、一段副歌光束各算一段）。</summary>
    public int EffectWindows;

    public int CurveKeys;
    public List<string> Notes = new();
}

/// <summary>
/// 一键成片：音乐分析（节拍 / 下拍 / 段落 / 能量）+ 歌词行 → 运镜曲线、调制层与分段出场的特效。
///
/// 分三层写，彼此不覆盖：
/// <list type="bullet">
/// <item>基础曲线：每个段落一种镜头（主歌缓推、副歌切近后慢推加摇摆、桥段拉远回摆……），
/// 镜头位移跟着歌词行换落点，每句歌词镜头换一个位置。</item>
/// <item>调制层（<c>auto.*</c>）：下拍冲击、副歌每拍的轻脉冲，乘 / 加在基础曲线上，
/// 所以段落级的推拉不会被冲击打平。</item>
/// <item>效果出场区间：常驻的只有调色 / 柔光 / 暗角 / 颗粒这类"画面质感"；
/// 闪白、震屏、色散、故障、光束都只在转场与副歌里按区间出现，进副歌前有一小节蓄力。</item>
/// </list>
/// 生成物靠 <c>fx_autodir_</c> 实例前缀与 <c>auto.</c> 层名识别，重复生成先清掉上一版（幂等）。
/// </summary>
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

    private static readonly string[] CameraParams = ["scale", "rotation", "offsetX", "offsetY"];

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

        CleanupPreviousGeneration(project);

        var song = Song.Build(map, project.LrcText);
        var look = Look.For(style);
        var rng = new Random(options.Seed);

        BuildCamera(project, song, look, intensity, report);
        BuildEffects(project, song, look, intensity, rng, report);
        ApplyLyricStyle(project, song);

        // 各自独立的随机源：一条轨多抽一次随机数不改变别的轨，同种子整份工程逐字节相同
        BuildAnimeFx(project, song, look, style, intensity, new Random(unchecked(options.Seed * 7919 + 1)), report);
        BuildParticles(project, song, style, intensity, new Random(unchecked(options.Seed * 7919 + 2)), report);
        BuildPerformance(project, song, style, intensity, report);

        foreach (var sec in song.Sections)
            report.Notes.Add($"[{sec.Start:F2}s–{sec.End:F2}s] {sec.Kind}（能量 {sec.Energy:F2}）{(song.IsHigh(sec) ? " · 高潮段" : "")}");
        report.Notes.Add($"AutoDirector: Style={style}, Intensity={intensity:F2}, Seed={options.Seed}, "
                         + $"CameraKeys={report.CameraKeys}, EffectInstances={report.EffectInstances}, "
                         + $"EffectWindows={report.EffectWindows}, CurveKeys={report.CurveKeys}");
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
            (c.Layer?.StartsWith(LayerPrefix, StringComparison.Ordinal) ?? false) ||
            (string.IsNullOrEmpty(c.Layer) && CameraParams.Any(p => c.Address == "camera." + p)) ||
            c.Address.StartsWith($"effect:{AutoPrefix}", StringComparison.Ordinal) ||
            autoInstanceIds.Any(id => c.Address.StartsWith($"effect:{id}.", StringComparison.Ordinal)) ||
            c.Address.StartsWith($"layer:{AutoLayerPrefix}", StringComparison.Ordinal));

        // 换引用而不是原地删：渲染线程可能正在遍历这份图层列表
        if (project.Scene.Layers.Any(l => l.Id.StartsWith(AutoLayerPrefix, StringComparison.Ordinal)))
            project.Scene.Layers = project.Scene.Layers
                .Where(l => !l.Id.StartsWith(AutoLayerPrefix, StringComparison.Ordinal)).ToList();

        project.LyricStyles.Clear();
        project.VtsCues.Cues.Clear();
    }

    /// <summary>导演生成的图层（粒子层）Id 前缀。</summary>
    public const string AutoLayerPrefix = "autodir_";

    // ---------------------------------------------------------------- 运镜

    private static void BuildCamera(DirectorProject project, Song song, Look look, double k, AutoReport report)
    {
        var scale = new FloatCurve();
        var rotation = new FloatCurve();
        var offX = new FloatCurve();
        var offY = new FloatCurve();

        foreach (var sec in song.Sections)
        {
            double s = sec.Start, e = sec.End, len = e - s;
            if (len <= 0.05) continue;
            // 段尾留一小段给下一段的切入：段内用 SmoothStep 走完，段间 ~80ms 线性切换，读起来像剪辑点
            double hold = e - Math.Min(0.08, len * 0.1);
            double en = sec.Energy;

            switch (sec.Kind)
            {
                case "intro":
                    // 由近拉开，像开场揭幕
                    Key(scale, s, 1.0 + 0.07 * k, CurveInterp.SmoothStep);
                    Key(scale, hold, 1.0, CurveInterp.Linear);
                    Key(rotation, s, 0, CurveInterp.SmoothStep);
                    break;

                case "bridge":
                    // 拉远 + 慢慢回摆，给副歌留反差
                    Key(scale, s, 1.0 + 0.08 * k, CurveInterp.SmoothStep);
                    Key(scale, hold, 1.0, CurveInterp.Linear);
                    Key(rotation, s, -1.6 * k * look.Sway, CurveInterp.SmoothStep);
                    Key(rotation, hold, 1.6 * k * look.Sway, CurveInterp.Linear);
                    break;

                case "outro":
                    Key(scale, s, 1.0 + 0.05 * k, CurveInterp.SmoothStep);
                    Key(scale, hold, 1.0, CurveInterp.Linear);
                    Key(rotation, s, 0, CurveInterp.SmoothStep);
                    break;

                default:
                    if (song.IsHigh(sec))
                    {
                        // 副歌：一刀切近，段内继续慢推；每小节左右摇一次
                        Key(scale, s, 1.0 + 0.10 * k, CurveInterp.SmoothStep);
                        Key(scale, hold, 1.0 + 0.17 * k, CurveInterp.Linear);
                        Key(rotation, s, 0, CurveInterp.SmoothStep);
                        int bar = 0;
                        foreach (var d in song.DownbeatsIn(s, hold))
                        {
                            if (d - s < 0.05) continue;
                            Key(rotation, d, (bar++ % 2 == 0 ? 1 : -1) * 1.3 * k * look.Sway, CurveInterp.SmoothStep);
                        }
                        Key(rotation, hold, 0, CurveInterp.Linear);
                    }
                    else
                    {
                        // 主歌：缓推，能量越高推得越多
                        Key(scale, s, 1.0, CurveInterp.SmoothStep);
                        Key(scale, hold, 1.0 + 0.06 * k * (0.5 + en), CurveInterp.Linear);
                        Key(rotation, s, 0, CurveInterp.SmoothStep);
                    }
                    break;
            }
        }

        BuildDrift(song, look, k, offX, offY);

        int camKeys = 0;
        camKeys += Save(project, "camera.scale", null, CurveBlend.Add, scale);
        camKeys += Save(project, "camera.rotation", null, CurveBlend.Add, rotation);
        camKeys += Save(project, "camera.offsetX", null, CurveBlend.Add, offX);
        camKeys += Save(project, "camera.offsetY", null, CurveBlend.Add, offY);

        // 调制层：下拍冲击（乘在缩放上）+ 副歌下拍的转角踢（加在旋转上）+ 副歌每拍轻脉冲
        var punch = new FloatCurve();
        var kick = new FloatCurve();
        var pulse = new FloatCurve();
        Key(punch, 0, 1, CurveInterp.Linear);
        Key(pulse, 0, 1, CurveInterp.Linear);
        Key(kick, 0, 0, CurveInterp.Linear);
        double decay = Math.Min(song.BeatLen * 0.9, 0.42);

        foreach (var sec in song.Sections)
        {
            bool high = song.IsHigh(sec);
            double amp = high ? look.ChorusPunch : sec.Kind == "verse" ? look.VersePunch : 0;
            if (amp <= 0) continue;
            amp *= k;

            int bar = 0;
            foreach (var d in song.DownbeatsIn(sec.Start, sec.End))
            {
                bool accent = high || bar % 2 == 0;
                bar++;
                if (!accent) continue;
                Spike(punch, d, 1.0, 1.0 + amp, decay);
                if (high && look.Kick > 0)
                    Spike(kick, d, 0.0, (bar % 2 == 0 ? -1 : 1) * look.Kick * k, decay);
            }

            if (high && look.BeatPulse > 0)
            {
                var downs = song.DownbeatsIn(sec.Start, sec.End).ToHashSet();
                foreach (var b in song.BeatsIn(sec.Start, sec.End))
                    if (!downs.Contains(b))
                        Spike(pulse, b, 1.0, 1.0 + look.BeatPulse * k, Math.Min(song.BeatLen * 0.6, 0.25));
            }
        }

        camKeys += Save(project, "camera.scale", BeatLayer, CurveBlend.Multiply, punch, minKeys: 2);
        camKeys += Save(project, "camera.rotation", BeatLayer, CurveBlend.Add, kick, minKeys: 2);
        camKeys += Save(project, "camera.scale", PulseLayer, CurveBlend.Multiply, pulse, minKeys: 2);

        report.CameraKeys = camKeys;
        report.CurveKeys += camKeys;
    }

    /// <summary>
    /// 镜头落点：每句歌词换一个位置（没有歌词就每两小节换一次），按黄金角绕圈，相邻两句不会落在同一侧。
    /// 副歌开头回中，配合那一刀切近。
    /// </summary>
    private static void BuildDrift(Song song, Look look, double k, FloatCurve offX, FloatCurve offY)
    {
        var anchors = song.Lines.Count >= 2
            ? song.Lines.Select(l => l.Start).ToList()
            : song.Downbeats.Where((_, i) => i % 2 == 0).ToList();

        double amp = 42 * k * look.Drift;
        Key(offX, 0, 0, CurveInterp.SmoothStep);
        Key(offY, 0, 0, CurveInterp.SmoothStep);

        var chorusStarts = song.Sections.Where(song.IsHigh).Select(s => s.Start).ToList();
        double last = double.NegativeInfinity;
        int n = 0;
        foreach (var t in anchors.Where(double.IsFinite).OrderBy(t => t))
        {
            if (t - last < 0.6) continue;
            if (chorusStarts.Any(c => Math.Abs(c - t) < 0.4)) continue;
            last = t;

            var sec = song.SectionAt(t);
            double factor = sec is null ? 0.5 : song.IsHigh(sec) ? 0.55 : sec.Kind switch
            {
                "verse" => 1.0,
                "bridge" => 0.8,
                _ => 0.4,
            };
            double ang = n++ * 2.39996;
            Key(offX, t, Math.Cos(ang) * amp * factor, CurveInterp.SmoothStep);
            Key(offY, t, Math.Sin(ang) * amp * factor * 0.45, CurveInterp.SmoothStep);
        }

        foreach (var c in chorusStarts)
        {
            Key(offX, c, 0, CurveInterp.SmoothStep);
            Key(offY, c, 0, CurveInterp.SmoothStep);
        }

        Key(offX, song.Duration, 0, CurveInterp.Linear);
        Key(offY, song.Duration, 0, CurveInterp.Linear);
    }

    /// <summary>瞬时冲击：t 前 30ms 起势，t 时刻到峰，<paramref name="decay"/> 秒内缓落回基线。</summary>
    private static void Spike(FloatCurve c, double t, double baseVal, double peak, double decay)
    {
        Key(c, t - 0.03, baseVal, CurveInterp.Linear);
        Key(c, t, peak, CurveInterp.SmoothStep);
        Key(c, t + decay, baseVal, CurveInterp.Linear);
    }

    // ---------------------------------------------------------------- 特效

    private static void BuildEffects(DirectorProject project, Song song, Look look, double k, Random rng, AutoReport report)
    {
        var highs = song.Sections.Where(song.IsHigh).ToList();
        var bridges = song.Sections.Where(s => s.Kind == "bridge").ToList();
        double beat = song.BeatLen, bar = song.BarLen;

        // 进高潮段的那一刻（前一段不是高潮）：蓄力 + 落点冲击都挂在这里
        var drops = new List<double>();
        for (int i = 0; i < song.Sections.Count; i++)
        {
            var sec = song.Sections[i];
            if (!song.IsHigh(sec)) continue;
            if (i > 0 && song.IsHigh(song.Sections[i - 1])) continue;
            drops.Add(sec.Start);
        }

        // 蓄力长度：一小节，但不超过前一段的一半，也不早于 0
        double RiseStart(double drop)
        {
            var prev = song.Sections.LastOrDefault(s => s.End <= drop + 1e-6 && s.Start < drop);
            double room = prev is null ? drop : (drop - prev.Start) * 0.5;
            return Math.Max(0, drop - Math.Min(bar, room));
        }

        // 列表顺序 = 执行顺序：几何变形在前，调色 / 光效居中，闪白、暗角、颗粒最后压在整帧上

        if (look.Riser && drops.Count > 0)
        {
            var zoom = AddEffect(project, "zoomPunch", "riser", n => Set(n, "scale", 1.0f + 0.10f * (float)k));
            foreach (var d in drops)
            {
                double rs = RiseStart(d);
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

        if (look.Shatter && highs.Count > 0)
        {
            // 只砸一次：最后一个副歌入口（只有一段副歌时就是它）
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
            });
            foreach (var d in drops)
                if (d > 0.15) Window(glitch, d - 0.12, d);
            foreach (var sec in highs)
            {
                int i = 0;
                foreach (var d in song.DownbeatsIn(sec.Start, sec.End))
                {
                    // 隔小节、按种子取一半：故障是点缀，次次都来就成了噪音
                    if (i++ % 2 == 1 && rng.NextDouble() < 0.6) Window(glitch, d, d + 0.1);
                }
            }
        }

        if (look.Chromatic && highs.Count > 0)
        {
            var chroma = AddEffect(project, "chromatic", "accent", n =>
            {
                Set(n, "amount", 14f * (float)k);
                Set(n, "radial", 1f);
            });
            foreach (var d in drops)
            {
                double rs = RiseStart(d);
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

        AddEffect(project, "colorGrade", "look", n =>
        {
            Set(n, "saturation", look.Saturation);
            Set(n, "contrast", look.Contrast);
            Set(n, "brightness", look.Brightness);
            Set(n, "strength", 0.5f + 0.5f * (float)k);
            SetColor(n, "tint", look.Tint);
            Set(n, "tintStrength", look.TintStrength);
        });

        var bloom = AddEffect(project, "bloom", "glow", n =>
        {
            Set(n, "threshold", look.BloomThreshold);
            Set(n, "radius", look.BloomRadius);
            Set(n, "intensity", (float)look.BloomLo);
        });
        // 柔光强度跟着段落能量走：高潮段更亮，段间用一拍缓过去
        var bloomCurve = new FloatCurve();
        foreach (var sec in song.Sections)
        {
            double v = (look.BloomLo + (look.BloomHi - look.BloomLo) * sec.Energy) * (song.IsHigh(sec) ? 1.25 : 1.0);
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
                Set(n, "length", 0.18f);
                Set(n, "cy", 0.3f);
            });
            foreach (var sec in highs) Window(rays, sec.Start, sec.End, fadeIn: bar, fadeOut: beat * 2);
        }

        // 转场闪白：进高潮段重闪，其余段落切换轻闪（窗口越短，衰减越快，观感越轻）
        var flash = AddEffect(project, "flash", "cut", n =>
        {
            Set(n, "amount", look.Flash * (float)(0.4 + 0.6 * k));
            SetColor(n, "color", look.FlashColor);
        });
        for (int i = 1; i < song.Sections.Count; i++)
        {
            var sec = song.Sections[i];
            double t = sec.Start;
            if (drops.Contains(t)) Window(flash, t, t + Math.Min(0.55, beat * 1.5), fadeOut: Math.Min(0.55, beat * 1.5));
            else if (look.SoftCuts) Window(flash, t, t + 0.22, fadeOut: 0.22);
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

        // 一段区间都没拿到的点缀效果没有意义：留着就是全程常驻，正是要避免的
        project.Effects.Nodes.RemoveAll(n =>
            n.InstanceId.StartsWith(AutoPrefix, StringComparison.Ordinal)
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

    private static EffectNode AddEffect(DirectorProject project, string defId, string role, Action<EffectNode>? configure = null)
    {
        var node = new EffectNode
        {
            DefId = defId,
            InstanceId = $"{AutoPrefix}{defId}_{role}",
            Enabled = true,
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

    // ---------------------------------------------------------------- 歌词

    private static void ApplyLyricStyle(DirectorProject project, Song song)
    {
        bool hasHigh = song.Sections.Any(song.IsHigh);
        bool hasVerse = song.Sections.Any(s => s.Kind == "verse");
        project.Scene.LyricStyleName = hasHigh ? "Neon" : (hasVerse ? "Minimal" : LyricStyle.ClassicName);
    }

    // ---------------------------------------------------------------- 工具

    private static void Key(FloatCurve c, double t, double v, CurveInterp mode)
    {
        if (!double.IsFinite(t) || !double.IsFinite(v)) return;
        c.TryAdd(Math.Max(0, t), v, mode);
    }

    private static int Save(DirectorProject project, string address, string? layer, CurveBlend blend, FloatCurve curve, int minKeys = 1)
    {
        curve.NormalizeKeys();
        if (curve.Count < minKeys) return 0;
        project.Curves.Add(new CurveSave { Address = address, Curve = curve, Layer = layer, Blend = blend });
        return curve.Count;
    }

    /// <summary>一种风格的全部取值：镜头幅度、常驻质感、点缀开关。数值是 Intensity = 1 时的幅度。</summary>
    private sealed record Look(
        double Sway, double Drift, double ChorusPunch, double VersePunch, double Kick, double BeatPulse,
        float Saturation, float Contrast, float Brightness, string Tint, float TintStrength,
        float BloomThreshold, float BloomRadius, double BloomLo, double BloomHi,
        float Vignette, float Grain, float Flash, string FlashColor, bool SoftCuts,
        bool Riser, bool Shake, bool Shatter, bool Glitch, bool Chromatic, float Rays, string? BridgeFx)
    {
        public static Look For(string style) => style switch
        {
            "soft" => new Look(
                Sway: 0.7, Drift: 1.0, ChorusPunch: 0.025, VersePunch: 0, Kick: 0, BeatPulse: 0,
                Saturation: 1.05f, Contrast: 1.0f, Brightness: 0f, Tint: "#ffe6d0", TintStrength: 0.12f,
                BloomThreshold: 0.85f, BloomRadius: 30f, BloomLo: 0.12, BloomHi: 0.3,
                Vignette: 0.35f, Grain: 0.04f, Flash: 0.3f, FlashColor: "#fff4e0", SoftCuts: false,
                Riser: false, Shake: false, Shatter: false, Glitch: false, Chromatic: false, Rays: 0.18f, BridgeFx: "liquid"),
            "epic" => new Look(
                Sway: 1.0, Drift: 0.8, ChorusPunch: 0.06, VersePunch: 0.015, Kick: 0.6, BeatPulse: 0,
                Saturation: 0.92f, Contrast: 1.18f, Brightness: -0.02f, Tint: "#cfe8ff", TintStrength: 0.18f,
                BloomThreshold: 0.82f, BloomRadius: 36f, BloomLo: 0.15, BloomHi: 0.4,
                Vignette: 0.5f, Grain: 0.05f, Flash: 0.6f, FlashColor: "#fff8ee", SoftCuts: true,
                Riser: true, Shake: true, Shatter: true, Glitch: false, Chromatic: false, Rays: 0.28f, BridgeFx: null),
            _ => new Look(
                Sway: 1.0, Drift: 1.0, ChorusPunch: 0.05, VersePunch: 0.02, Kick: 0.8, BeatPulse: 0.012,
                Saturation: 1.15f, Contrast: 1.08f, Brightness: -0.02f, Tint: "#d8e4ff", TintStrength: 0.08f,
                BloomThreshold: 0.85f, BloomRadius: 24f, BloomLo: 0.1, BloomHi: 0.3,
                Vignette: 0.3f, Grain: 0f, Flash: 0.55f, FlashColor: "#ffffff", SoftCuts: true,
                Riser: true, Shake: true, Shatter: false, Glitch: true, Chromatic: true, Rays: 0f, BridgeFx: "scanline"),
        };
    }

    /// <summary>
    /// 生成用的歌曲视图：清洗过的节拍 / 下拍 / 段落（能量归一到 0..1）与歌词行。
    /// 分析结果缺什么就补什么——没有下拍按每 4 拍一小节，没有段落按整首一段主歌。
    /// </summary>
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

        /// <summary>
        /// 高潮段：标成副歌的段，或能量处在全曲前列（归一化 ≥ 0.8）的段。只信标签不够——
        /// 重复聚类常把 DJ 曲最炸的那一大段判成"主歌"，而真正的"副歌"只剩几秒的短段。
        /// 没有任何副歌标签时阈值放宽到 0.66。前奏 / 尾奏不算。
        /// </summary>
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
                // 歌词坏了只是少了落点依据，按小节落点照样能生成
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
