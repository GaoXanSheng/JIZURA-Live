using System.IO;
using System.Windows.Media.Imaging;
using JizuraLive.LegacyEngine;
using JizuraLive.Pv;
using SkiaSharp;

namespace JizuraLive.App.PvStage;

internal static class TechniquePreview
{
    private static readonly Dictionary<string, BitmapSource[]?> FrameCache = new(StringComparer.Ordinal);
    private static readonly object CacheGate = new();
    private static readonly object BootGate = new();
    private static readonly Renderer SharedRenderer = new();
    private static int _version;

    public static int Version => Volatile.Read(ref _version);

    public static void Invalidate()
    {
        lock (CacheGate)
        {
            FrameCache.Clear();
            _version++;
        }
    }

    public static BitmapSource[]? RenderFrames(LegacyPresetSettings preset, string? style, int seed,
        int sceneWidth, int sceneHeight, string group, string key, int thumbWidth, int frameCount = 6)
    {
        if (string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(key)) return null;

        if (group == "trans" && TransitionCatalog.Get(key) is { } technique)
        {
            var one = PvTransitionPreview.Render(technique);
            return one is null ? null : [one];
        }

        var cacheKey = "frames|" + CacheKey(style, seed, sceneWidth, sceneHeight, group, key, thumbWidth)
                       + "|" + frameCount;
        lock (CacheGate)
            if (FrameCache.TryGetValue(cacheKey, out var cached)) return cached;

        BitmapSource[]? result = null;
        try
        {
            lock (BootGate) EngineBoot.InstallPacks();
            if (!Supports(group, key)) return null;

            var project = CreateProject(preset, style, seed, sceneWidth, sceneHeight);
            var plan = Planner.PreviewPlan(project, group, key);
            var width = Math.Max(24, thumbWidth);
            var scale = width / (double)Math.Max(1, plan.W);
            var height = Math.Max(1, (int)Math.Round(plan.H * scale));
            var times = FrameTimes(group, plan.Duration, Math.Max(1, frameCount));
            var frames = new List<BitmapSource>(times.Length);

            foreach (var time in times)
            {
                var frame = RenderAt(plan, width, height, scale, group, time);
                if (frame is not null) frames.Add(frame);
            }

            result = frames.Count > 0 ? frames.ToArray() : null;
        }
        catch
        {
            result = null;
        }

        lock (CacheGate) FrameCache[cacheKey] = result;
        return result;
    }

    private static Project CreateProject(LegacyPresetSettings preset, string? style, int seed,
        int sceneWidth, int sceneHeight)
    {
        var project = new Project
        {
            Style = LegacyPvEngine.MapStyle(style),
            Seed = (uint)Math.Max(1, seed),
            Aspect = LegacyPvEngine.AspectOf(sceneWidth, sceneHeight),
            Res = Math.Min(sceneWidth, sceneHeight),
            Fps = 24,
            Fx = new FxSettings(),
            Timing = new TimingSettings(),
            Lang = "auto",
        };
        project.Enabled = Reg.DefaultEnabled();
        preset.ApplyTo(project);
        return project;
    }

    private static double[] FrameTimes(string group, double duration, int count)
    {
        duration = Math.Max(0.2, duration);
        var times = new double[count];
        for (int i = 0; i < count; i++)
        {
            double t = count == 1 ? 0.5 : i / (double)(count - 1);
            double time = group switch
            {
                "enter" => 0.04 + (Math.Min(1.1, duration * 0.85) - 0.04) * t,
                "exit" => Math.Max(0.02, duration - 1.1) + Math.Min(1.1, duration * 0.85) * t,
                "fx" => 0.05 + 0.9 * t,
                _ => duration * (0.06 + 0.88 * t),
            };
            times[i] = Math.Clamp(time, 0.02, Math.Max(0.03, duration - 0.02));
        }

        return times;
    }

    private static BitmapSource? RenderAt(Plan plan, int width, int height, double scale,
        string group, double time)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        using var surface = SKSurface.Create(info, bitmap.GetPixels(), bitmap.RowBytes);
        if (surface is null) return null;

        surface.Canvas.Clear(SKColors.Black);
        lock (EngineRenderGate.Lock)
        {
            SharedRenderer.ClearFrameCache();
            SharedRenderer.Frame(surface.Canvas, plan, time, new RenderOpts
            {
                Scale = scale,
                NoGhost = true,
                NoHud = true,
                NoPost = group != "fx",
            });
            surface.Canvas.Flush();
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 85);
        if (data is null) return null;

        using var stream = new MemoryStream(data.ToArray());
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = stream;
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private static string CacheKey(string? style, int seed, int sceneWidth, int sceneHeight,
        string group, string key, int thumbWidth) =>
        $"{Version}|{style}|{seed}|{sceneWidth}x{sceneHeight}|{group}|{key}|{thumbWidth}";

    private static bool Supports(string group, string key) => group switch
    {
        "layout" => Reg.Layouts.ContainsKey(key),
        "enter" => Reg.Enter.ContainsKey(key),
        "hold" => Reg.Hold.ContainsKey(key),
        "exit" => Reg.Exit.ContainsKey(key),
        "decor" => Reg.Decor.ContainsKey(key),
        "treat" => Reg.Treat.ContainsKey(key),
        "bg" => Reg.Bg.ContainsKey(key),
        "cam" => Reg.Camera.ContainsKey(key),
        "trans" => Reg.Trans.ContainsKey(key),
        "fx" => Reg.Fxe.ContainsKey(key),
        _ => false,
    };
}
