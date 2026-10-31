using System.IO;
using JizuraLive.Engine;
using JizuraLive.LegacyEngine;
using JizuraLive.Logging;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Pv;

public sealed class LegacyPvOptions
{
    public bool Enabled { get; set; } = true;
    public string LrcText { get; set; } = "";
    public string? AudioPath { get; set; }
    public string Style { get; set; } = "noir";
    public int Seed { get; set; } = 20260922;
    public double RenderScale { get; set; } = 0.6667;
    public bool Synchronous { get; set; }

        public bool NoCutTrans { get; set; } = true;

    public bool StageCamera { get; set; } = true;
    public bool StageTransitions { get; set; } = true;

    public int Fps { get; set; }

    public bool Composite { get; set; }

    public bool NoPost { get; set; }

    public bool GpuRaster { get; set; }

    public List<LegacyCutOverride>? CutOverrides { get; set; }

    public LegacyPresetSettings? Preset { get; set; }
}

public sealed class LegacyPvPlanInfo
{
    public double Duration { get; set; }
    public double BeatLen { get; set; } = 0.5;
    public double Motion { get; set; } = 0.7;
    public float[] Energy { get; set; } = Array.Empty<float>();
    public double EnergyRate { get; set; }
    public int Width { get; set; } = 1920;
    public int Height { get; set; } = 1080;
    public List<double> Beats { get; set; } = new();
    public List<LegacyPvCutInfo> Cuts { get; set; } = new();
    public List<LegacyPvEventInfo> Events { get; set; } = new();

    public static LegacyPvPlanInfo From(Plan plan)
    {
        var info = new LegacyPvPlanInfo
        {
            Duration = plan.Duration,
            Motion = plan.Fx?.Motion ?? 0.7,
            Energy = plan.Energy ?? Array.Empty<float>(),
            EnergyRate = plan.EnergyRate,
            Width = plan.W > 0 ? plan.W : 1920,
            Height = plan.H > 0 ? plan.H : 1080,
            Beats = (plan.Beats ?? Array.Empty<double>())
                .Where(double.IsFinite).Where(b => b >= 0).Distinct().OrderBy(b => b).ToList(),
        };

        if (info.Beats.Count >= 2)
        {
            var diffs = info.Beats.Zip(info.Beats.Skip(1), (a, b) => b - a)
                .Where(d => d > 0).OrderBy(d => d).ToList();
            if (diffs.Count > 0) info.BeatLen = diffs[diffs.Count / 2];
        }

        foreach (var cut in plan.Cuts)
        {
            if (cut is null) continue;
            double start = cut.Start;
            double end = cut.End > start ? cut.End : start + Math.Max(0.05, cut.Dur);
            var item = new LegacyPvCutInfo
            {
                Start = start,
                End = end,
                Line = cut.Line,
                Dur = Math.Max(0.05, cut.Dur > 0 ? cut.Dur : end - start),
                Text = cut.Text ?? "",
                ZoneW = cut.Zone?.W ?? plan.W,
                ZoneH = cut.Zone?.H ?? plan.H,
                Cam = string.IsNullOrWhiteSpace(cut.Cam) ? "push" : cut.Cam,
                Trans = string.IsNullOrWhiteSpace(cut.Trans) ? null : cut.Trans,
                TransDur = cut.TransDur,
                Layout = cut.Layout,
                Enter = cut.Enter,
                Exit = cut.Exit,
                Treat = cut.Treat,
                Emph = cut.Emph,
                Seed = unchecked((int)cut.Seed),
            };

            CopyNumbers(cut.CamP, item.CamParams);
            CopyNumbers(cut.TransP, item.TransParams);
            CopyNumbers(cut.Params, item.LayoutParams);
            CopyNumbers(cut.TreatP, item.TreatParams);
            CopyNumbers(cut.BgP, item.BgParams);
            CopyStrings(cut.CamP, item.CamTexts);
            CopyStrings(cut.Params, item.LayoutTexts);
            CopyStrings(cut.TreatP, item.TreatTexts);
            CopyStrings(cut.BgP, item.BgTexts);
            info.Cuts.Add(item);
        }

        info.Cuts.Sort((a, b) => a.Start.CompareTo(b.Start));
        if (info.Duration <= 0 && info.Cuts.Count > 0) info.Duration = info.Cuts[^1].End;

        foreach (var ev in plan.Events)
        {
            if (ev is null || string.IsNullOrWhiteSpace(ev.Type)) continue;
            if (!double.IsFinite(ev.T) || !double.IsFinite(ev.Amp) || !double.IsFinite(ev.Dur)) continue;
            info.Events.Add(new LegacyPvEventInfo
            {
                T = Math.Max(0, ev.T),
                Type = ev.Type,
                Amp = ev.Amp,
                Dur = Math.Max(0, ev.Dur),
            });
        }

        info.Events.Sort((a, b) => a.T.CompareTo(b.T));
        return info;
    }

    private static void CopyNumbers(JObject? obj, Dictionary<string, double> target)
    {
        if (obj is null) return;
        foreach (var p in obj.Properties())
        {
            if (p.Value.Type == JTokenType.Boolean)
            {
                target[p.Name] = p.Value.Value<bool>() ? 1 : 0;
                continue;
            }

            if (p.Value.Type is not (JTokenType.Integer or JTokenType.Float)) continue;
            double v = p.Value.Value<double>();
            if (double.IsFinite(v)) target[p.Name] = v;
        }
    }

    internal static void ReplaceNumbers(JObject? obj, Dictionary<string, double> target)
    {
        target.Clear();
        CopyNumbers(obj, target);
    }

    private static void CopyStrings(JObject? obj, Dictionary<string, string> target)
    {
        if (obj is null) return;
        foreach (var p in obj.Properties())
        {
            if (p.Value.Type is not JTokenType.String) continue;
            target[p.Name] = p.Value.Value<string>() ?? "";
        }
    }

    internal static void ReplaceStrings(JObject? obj, Dictionary<string, string> target)
    {
        target.Clear();
        CopyStrings(obj, target);
    }

    internal static void ApplyStrings(JObject? target, Dictionary<string, string>? values)
    {
        if (target is null || values is null || values.Count == 0) return;
        foreach (var (key, value) in values)
            if (value is not null) target[key] = value;
    }

    internal static void ApplyNumbers(JObject? target, Dictionary<string, double>? values)
    {
        if (target is null || values is null || values.Count == 0) return;
        foreach (var (key, value) in values)
        {
            if (!double.IsFinite(value)) continue;
            var existing = target[key];
            target[key] = existing?.Type switch
            {
                JTokenType.Integer => (long)Math.Round(value),
                JTokenType.Boolean => value >= 0.5,
                _ => value,
            };
        }
    }

        public BeatMap ToBeatMap()
    {
        var map = new BeatMap
        {
            Bpm = BeatLen > 0.05 ? 60.0 / BeatLen : 120,
            Beats = new List<double>(Beats),
            Downbeats = Beats.Where((_, i) => i % 4 == 0).ToList(),
        };

        if (Duration <= 0.01) return map;
        var energy = Energy;
        if (energy.Length == 0 || EnergyRate <= 0)
        {
            map.Sections.Add(new Section { Start = 0, End = Duration, Kind = "verse", Energy = 0.5 });
            return map;
        }

        const double step = 1.0;
        int count = Math.Max(1, (int)Math.Ceiling(Duration / step));
        var values = new double[count];
        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            int idx = Math.Clamp((int)((i + 0.5) * step * EnergyRate), 0, energy.Length - 1);
            values[i] = energy[idx];
            sum += values[i];
        }

        var smooth = new double[count];
        for (int i = 0; i < count; i++)
        {
            int a = Math.Max(0, i - 1), b = Math.Min(count - 1, i + 1);
            double s = 0;
            for (int j = a; j <= b; j++) s += values[j];
            smooth[i] = s / (b - a + 1);
        }

        double threshold = Math.Max(0.15, sum / count * 1.05);
        bool high = smooth[0] >= threshold;
        double start = 0;
        var raw = new List<(double Start, double End, bool High)>();
        for (int i = 1; i < count; i++)
        {
            bool h = smooth[i] >= threshold;
            if (h == high) continue;
            raw.Add((start, i * step, high));
            start = i * step;
            high = h;
        }

        raw.Add((start, Duration, high));

        var merged = new List<(double Start, double End, bool High)>();
        foreach (var section in raw)
        {
            if (section.End - section.Start < 3.0 && merged.Count > 0)
                merged[^1] = (merged[^1].Start, section.End, merged[^1].High);
            else
                merged.Add(section);
        }

        if (merged.Count == 0) merged.Add((0, Duration, false));
        foreach (var section in merged)
        {
            double e = 0;
            int n = 0;
            for (double t = section.Start; t < section.End; t += step)
            {
                int idx = Math.Clamp((int)(t * EnergyRate), 0, energy.Length - 1);
                e += energy[idx];
                n++;
            }

            map.Sections.Add(new Section
            {
                Start = section.Start,
                End = section.End,
                Kind = section.High ? "chorus" : "verse",
                Energy = n > 0 ? e / n : 0.5,
            });
        }

        return map;
    }

    public List<DesignSegment> ToStageTransitions()
    {
        var list = new List<DesignSegment>();
        foreach (var cut in Cuts)
        {
            if (cut.Trans is null || cut.TransDur <= 0.01) continue;
            string tech = TransitionCatalog.Get(cut.Trans) is not null
                ? cut.Trans
                : cut.Emph ? "whipPan" : "diagonalWipe";

            var pars = new Dictionary<string, double>(cut.TransParams, StringComparer.Ordinal);
            list.Add(new DesignSegment
            {
                Track = DesignTrackKind.Trans,
                Start = cut.Start,
                End = cut.Start + Math.Clamp(cut.TransDur, 0.12, 0.9),
                Technique = tech,
                Params = pars,
                Seed = cut.Seed,
                Source = "auto",
                RefId = $"legacy_trans_{cut.Start:F2}",
            });
        }

        return list;
    }
}

public sealed class LegacyPvEventInfo
{
    public double T { get; set; }
    public string Type { get; set; } = "";
    public double Amp { get; set; }
    public double Dur { get; set; }
}

public sealed class LegacyPvCutInfo
{
    public double Start { get; set; }
    public double End { get; set; }
    public int Line { get; set; } = -1;
    public double Dur { get; set; }
    public string Text { get; set; } = "";
    public int ZoneW { get; set; }
    public int ZoneH { get; set; }
    public string Cam { get; set; } = "push";
    public string? Trans { get; set; }
    public double TransDur { get; set; }
    public string Layout { get; set; } = "center";
    public string Enter { get; set; } = "blur";
    public string Exit { get; set; } = "blur";
    public string Treat { get; set; } = "none";
    public bool Emph { get; set; }
    public int Seed { get; set; }
    public string? Source { get; set; }
    public Dictionary<string, double> CamParams { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> TransParams { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> LayoutParams { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> TreatParams { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, double> BgParams { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> LayoutTexts { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> TreatTexts { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> BgTexts { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> CamTexts { get; set; } = new(StringComparer.Ordinal);
}

public sealed class LegacyPvFrame
{
    public byte[] Bgra { get; init; } = Array.Empty<byte>();
    public int Width { get; init; }
    public int Height { get; init; }
}

public sealed class LegacyPvEngine : IDisposable
{
    private static readonly object BootGate = new();
    private static bool _booted;

    private readonly Renderer _renderer = new();
    private readonly Plan _plan;
    private readonly int _designWidth;
    private readonly int _designHeight;
    private SKBitmap? _bitmap;
    private SKSurface? _surface;
    private int _surfaceWidth;
    private int _surfaceHeight;
    private GpuRasterHost? _gpuHost;

    public string? Error { get; }
    public int CutCount => _plan.Cuts.Count;
    public LegacyPvPlanInfo PlanInfo { get; private set; } = new();

    public LegacyPvEngine(string lrcText, string audioPath, string? styleName, int seed,
        int designWidth, int designHeight, int fps, LegacyPresetSettings? preset = null)
    {
        _designWidth = Math.Max(16, designWidth);
        _designHeight = Math.Max(16, designHeight);
        try
        {
            lock (BootGate)
            {
                if (!_booted)
                {
                    EngineBoot.InstallPacks();
                    _booted = true;
                }
            }

            var audio = BuildAudio(audioPath);
            var project = new LegacyEngine.Project
            {
                Lyrics = lrcText ?? "",
                AudioPath = audioPath,
                AudioName = Path.GetFileName(audioPath),
                Style = MapStyle(styleName),
                Seed = (uint)Math.Max(1, seed),
                Aspect = AspectOf(_designWidth, _designHeight),
                Res = Math.Min(_designWidth, _designHeight),
                Fps = Math.Clamp(fps, 1, 240),
                Fx = new FxSettings(),
                Timing = new TimingSettings(),
                Lang = "auto",
                Title = Meta(lrcText, "ti"),
                Artist = Meta(lrcText, "ar"),
            };
            project.Enabled = Reg.DefaultEnabled();
            preset?.ApplyTo(project);
            _plan = Planner.Plan(project, audio);
            PlanInfo = LegacyPvPlanInfo.From(_plan);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _plan = new Plan
            {
                W = _designWidth,
                H = _designHeight,
                Fps = Math.Max(1, fps),
                Duration = 0,
                Style = new StyleDef(),
            };
            PlanInfo = new LegacyPvPlanInfo { Width = _designWidth, Height = _designHeight };
        }
    }

    public LegacyPvFrame? Render(double time, int? width = null, bool fast = false, bool noCutTrans = false,
        string? layer = null, bool transparent = false, bool noCamera = false, bool noPost = false)
    {
        if (Error is not null) return null;
        var w = Math.Max(16, width ?? _designWidth);
        var scale = w / (double)_plan.W;
        var h = Math.Max(1, (int)Math.Round(_plan.H * scale));
        if (_surface is null || _bitmap is null || _surfaceWidth != w || _surfaceHeight != h)
        {
            _renderer.ClearFrameCache();
            _surface?.Dispose();
            _bitmap?.Dispose();
            _bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            _surface = SKSurface.Create(_bitmap.Info, _bitmap.GetPixels(), _bitmap.RowBytes);
            _surfaceWidth = w;
            _surfaceHeight = h;
        }

        var opts = new RenderOpts
        {
            Scale = scale,
            Fast = fast,
            NoGhost = fast,
            NoCutTrans = noCutTrans,
            Layer = layer,
            Transparent = transparent,
            NoCamera = noCamera,
            NoPost = noPost,
        };

        if (_gpuHost is { } gpuHost)
        {
            try
            {
                gpuHost.Enter();
                try
                {
                    using var gpuSurface = gpuHost.CreateGpuSurface(w, h);
                    _renderer.Frame(gpuSurface.Canvas, _plan, Math.Max(0, time), opts);
                    gpuSurface.Canvas.Flush();
                    using var image = gpuSurface.Snapshot();
                    using var bitmap = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
                    if (!image.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0))
                        throw new InvalidOperationException("GPU 读回失败");
                    var gpuBytes = new byte[w * h * 4];
                    System.Runtime.InteropServices.Marshal.Copy(bitmap.GetPixels(), gpuBytes, 0, gpuBytes.Length);
                    return new LegacyPvFrame { Bgra = gpuBytes, Width = w, Height = h };
                }
                finally
                {
                    gpuHost.Exit();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("[PV/原版引擎] GPU 同步渲染失败，回退 CPU：" + ex.Message);
                UseCpu();
            }
        }

        lock (EngineRenderGate.Lock)
        {
            _renderer.Frame(_surface.Canvas, _plan, Math.Max(0, time), opts);
            _surface.Canvas.Flush();
        }

        var bytes = new byte[w * h * 4];
        System.Runtime.InteropServices.Marshal.Copy(_bitmap.GetPixels(), bytes, 0, bytes.Length);
        return new LegacyPvFrame { Bgra = bytes, Width = w, Height = h };
    }

    public void UseGpu(GpuRasterHost host)
    {
        _gpuHost = host;
        _renderer.SurfaceFactory = host.CreateGpuSurface;
    }

    public void UseCpu()
    {
        _gpuHost = null;
        _renderer.SurfaceFactory = null;
        _renderer.ClearFrameCache();
    }

    public void RenderTo(GpuSurface target, double time, bool noCutTrans, string? layer, bool transparent,
        bool noCamera, bool noPost = false)
    {
        if (Error is not null) return;
        var scale = target.Width / (double)_plan.W;
        lock (EngineRenderGate.Lock)
        {
            _renderer.Frame(target.Skia.Canvas, _plan, Math.Max(0, time),
                new RenderOpts
                {
                    Scale = scale,
                    Fast = false,
                    NoGhost = false,
                    NoCutTrans = noCutTrans,
                    Layer = layer,
                    Transparent = transparent,
                    NoCamera = noCamera,
                    NoPost = noPost,
                });
        }
    }

    private static readonly object AudioCacheGate = new();
    private static string? _audioCachePath;
    private static DateTime _audioCacheStamp;
    private static AudioData? _audioCache;

        public void ApplyCutOverrides(LegacyPvPlanInfo info)
    {
        if (info is null) return;
        var byStart = new Dictionary<double, LegacyPvCutInfo>();
        foreach (var cut in info.Cuts)
        {
            if (cut is null || !double.IsFinite(cut.Start)) continue;
            byStart[Math.Round(cut.Start, 3)] = cut;
        }

        if (byStart.Count == 0) return;
        lock (EngineRenderGate.Lock)
        {
            foreach (var cut in _plan.Cuts)
            {
                if (!byStart.TryGetValue(Math.Round(cut.Start, 3), out var item)) continue;

                if (!string.IsNullOrWhiteSpace(item.Layout))
                {
                    var changed = !string.Equals(cut.Layout, item.Layout, StringComparison.Ordinal);
                    cut.Layout = item.Layout;
                    if (changed && TryReplanLayout(cut, item))
                    {
                        LegacyPvPlanInfo.ReplaceNumbers(cut.Params, item.LayoutParams);
                        LegacyPvPlanInfo.ReplaceStrings(cut.Params, item.LayoutTexts);
                    }
                    else
                    {
                        if (item.LayoutParams.Count == 0)
                            LegacyPvPlanInfo.ReplaceNumbers(cut.Params, item.LayoutParams);
                        if (item.LayoutTexts.Count == 0)
                            LegacyPvPlanInfo.ReplaceStrings(cut.Params, item.LayoutTexts);
                    }
                }

                if (!string.IsNullOrWhiteSpace(item.Enter)) cut.Enter = item.Enter;
                if (!string.IsNullOrWhiteSpace(item.Exit)) cut.Exit = item.Exit;

                var treat = string.IsNullOrWhiteSpace(item.Treat) ? "none" : item.Treat;
                if (!string.Equals(cut.Treat, treat, StringComparison.Ordinal))
                {
                    cut.Treat = treat;
                    ReplanTreat(cut);
                    LegacyPvPlanInfo.ReplaceNumbers(cut.TreatP, item.TreatParams);
                    LegacyPvPlanInfo.ReplaceStrings(cut.TreatP, item.TreatTexts);
                }
                else
                {
                    if (item.TreatParams.Count == 0)
                        LegacyPvPlanInfo.ReplaceNumbers(cut.TreatP, item.TreatParams);
                    if (item.TreatTexts.Count == 0)
                        LegacyPvPlanInfo.ReplaceStrings(cut.TreatP, item.TreatTexts);
                }

                ApplyNumbers(cut.CamP, item.CamParams);
                ApplyNumbers(cut.Params, item.LayoutParams);
                ApplyNumbers(cut.TreatP, item.TreatParams);
                ApplyNumbers(cut.BgP, item.BgParams);
                LegacyPvPlanInfo.ApplyStrings(cut.CamP, item.CamTexts);
                LegacyPvPlanInfo.ApplyStrings(cut.Params, item.LayoutTexts);
                LegacyPvPlanInfo.ApplyStrings(cut.TreatP, item.TreatTexts);
                LegacyPvPlanInfo.ApplyStrings(cut.BgP, item.BgTexts);
            }

            _renderer.ClearFrameCache();
        }
    }

    private bool TryReplanLayout(Cut cut, LegacyPvCutInfo item)
    {
        if (!Reg.Layouts.TryGetValue(cut.Layout, out var def) || def.Plan is null) return false;

        var text = string.IsNullOrEmpty(item.Text) ? cut.Text : item.Text;
        var ctx = new LayoutCtx
        {
            Text = text,
            N = JLayouts.CpLen(text),
            W = item.ZoneW > 0 ? item.ZoneW : _plan.W,
            H = item.ZoneH > 0 ? item.ZoneH : _plan.H,
            Dur = item.Dur > 0 ? item.Dur : cut.Dur,
        };

        try
        {
            var rng = new Rng(unchecked(cut.Seed ^ 0x9E3779B9u));
            cut.Params = def.Plan(rng, ctx, _plan.Style ?? new StyleDef()) ?? new JObject();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ReplanTreat(Cut cut)
    {
        if (!Reg.Treat.TryGetValue(cut.Treat, out var def) || def.Plan is null)
        {
            cut.TreatP = new JObject();
            return;
        }

        try
        {
            var rng = new Rng(unchecked(cut.Seed ^ 0x85EBCA6Bu));
            cut.TreatP = def.Plan(rng, _plan.Style ?? new StyleDef()) ?? new JObject();
        }
        catch
        {
            cut.TreatP = new JObject();
        }
    }

    private static void ApplyNumbers(JObject? target, Dictionary<string, double>? values) =>
        LegacyPvPlanInfo.ApplyNumbers(target, values);

    private static AudioData BuildAudio(string audioPath)
    {
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(audioPath); }
        catch { stamp = default; }

        lock (AudioCacheGate)
        {
            if (_audioCache is not null
                && string.Equals(_audioCachePath, audioPath, StringComparison.OrdinalIgnoreCase)
                && _audioCacheStamp == stamp)
                return _audioCache;
        }

        var mono = LegacyEngine.AudioAnalyzer.DecodeMonoAnalyzeRate(audioPath, out var duration);
        var result = LegacyEngine.AudioAnalyzer.Analyze(mono, LegacyEngine.AudioAnalyzer.AnalyzeRate, duration);
        var data = new AudioData
        {
            Duration = duration,
            Bpm = result.Bpm,
            Beats = result.Beats,
            Energy = result.Energy,
            EnergyRate = result.EnergyRate,
            Peaks = result.Peaks,
            Pitch = result.Pitch,
            PitchRate = result.PitchRate,
        };

        lock (AudioCacheGate)
        {
            _audioCachePath = audioPath;
            _audioCacheStamp = stamp;
            _audioCache = data;
        }

        return data;
    }

    public static string MapStyle(string? style)
    {
        var key = (style ?? "").Trim().ToLowerInvariant();
        return key.Length == 0 ? "noir" : key;
    }

    public static string AspectOf(int width, int height)
    {
        var ratio = width / (double)Math.Max(1, height);
        if (Math.Abs(ratio - 16 / 9.0) < 0.02) return "16:9";
        if (Math.Abs(ratio - 9 / 16.0) < 0.02) return "9:16";
        if (Math.Abs(ratio - 4 / 3.0) < 0.02) return "4:3";
        if (Math.Abs(ratio - 3 / 4.0) < 0.02) return "3:4";
        if (Math.Abs(ratio - 1) < 0.02) return "1:1";
        if (Math.Abs(ratio - 21 / 9.0) < 0.02) return "21:9";
        return "16:9";
    }

    private static string Meta(string? lrc, string key)
    {
        if (string.IsNullOrWhiteSpace(lrc)) return "";
        var tag = "[" + key + ":";
        foreach (var raw in lrc.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith(tag, StringComparison.OrdinalIgnoreCase)) continue;
            var end = line.IndexOf(']');
            if (end <= tag.Length) continue;
            return line[tag.Length..end].Trim();
        }

        return "";
    }

    public void Dispose()
    {
        _renderer.Dispose();
        _surface?.Dispose();
        _surface = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
