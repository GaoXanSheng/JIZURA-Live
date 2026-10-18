using System.IO;
using System.Windows.Media.Imaging;
using JizuraLive.LegacyEngine;
using JizuraLive.Pv;
using SkiaSharp;

namespace JizuraLive.App.PvStage;

/// <summary>用原版引擎的 Planner.PreviewPlan 渲染手法缩略图。</summary>
internal static class TechniquePreview
{
    private static readonly Dictionary<string, BitmapSource?> Cache = new(StringComparer.Ordinal);
    private static readonly object CacheGate = new();
    private static readonly object BootGate = new();
    private static readonly Renderer SharedRenderer = new();
    private static int _version;

    public static int Version => Volatile.Read(ref _version);

    public static void Invalidate()
    {
        lock (CacheGate)
        {
            Cache.Clear();
            _version++;
        }
    }

    public static BitmapSource? Render(LegacyPresetSettings preset, string? style, int seed,
        int sceneWidth, int sceneHeight, string group, string key, int thumbWidth)
    {
        if (string.IsNullOrWhiteSpace(group) || string.IsNullOrWhiteSpace(key)) return null;

        var cacheKey = $"{Version}|{style}|{seed}|{sceneWidth}x{sceneHeight}|{group}|{key}|{thumbWidth}";
        lock (CacheGate)
            if (Cache.TryGetValue(cacheKey, out var cached)) return cached;

        BitmapSource? result = null;
        try
        {
            lock (BootGate) EngineBoot.InstallPacks();
            if (!Supports(group, key)) return null;

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

            var plan = Planner.PreviewPlan(project, group, key);
            var width = Math.Max(24, thumbWidth);
            var scale = width / (double)Math.Max(1, plan.W);
            var height = Math.Max(1, (int)Math.Round(plan.H * scale));
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            using var surface = SKSurface.Create(info, bitmap.GetPixels(), bitmap.RowBytes);
            if (surface is null) return null;

            var time = group switch
            {
                "enter" => Math.Min(0.25, plan.Duration * 0.15),
                "exit" => Math.Max(0, plan.Duration - 0.25),
                "trans" => 1.2 + Math.Min(0.2, Math.Max(0, plan.Duration - 1.2) * 0.5),
                "fx" => 0.08,
                _ => plan.Duration * 0.5,
            };

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
            result = bmp;
        }
        catch
        {
            result = null;
        }

        lock (CacheGate) Cache[cacheKey] = result;
        return result;
    }

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
