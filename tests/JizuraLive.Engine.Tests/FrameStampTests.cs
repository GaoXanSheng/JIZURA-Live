using System.IO;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// L01：一帧只有一个时间源。宿主交给 Prepare 的时刻必须一路走到图层与 GPU 常量，
/// 中途不许任何环节回头自己读时钟——没有 Driver 时尤其不许退化成 t=0。
/// </summary>
public class FrameStampTests
{
    private const int W = 320;
    private const int H = 180;

    private readonly ITestOutputHelper _out;

    public FrameStampTests(ITestOutputHelper output) => _out = output;

    private static PvScene FlatScene()
    {
        var scene = new PvScene
        {
            Name = "stamp",
            Width = W,
            Height = H,
            AmbientBlendEnabled = false,
        };
        scene.Layers.Add(new PvLayerConfig
        {
            Id = "bg",
            Name = "bg",
            Kind = PvLayerKind.Background,
            Cover = true,
            Parallax = new PvParallaxConfig { Depth = 0, RotationFactor = 0, ScaleFactor = 0 },
            VisualBlend = new PvVisualBlendConfig
            {
                AmbientTintIntensity = 0, RimGlow = 0, DropShadow = 0, AntiBleedMargin = 0,
            },
        });
        return scene;
    }

    /// <summary>横向渐变：任何相位相关的位移都能在这里留下像素差。</summary>
    private static byte[] Gradient()
    {
        var px = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var i = (y * W + x) * 4;
            px[i] = (byte)(255 - x * 255 / W);
            px[i + 1] = (byte)(x * 255 / W);
            px[i + 2] = (byte)(y * 255 / H);
            px[i + 3] = 255;
        }

        return px;
    }

    private static ReadbackImage RenderAt(double time)
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();

        using (gpu)
        {
            var dir = Path.Combine(Path.GetTempPath(), "jz-framestamp-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                var effects = new EffectGraph();
                effects.Add("wave");

                using var pipe = new PvPipeline(gpu, Path.Combine(dir, "scene.json"), FlatScene(),
                    effects, null, false, null);
                using var img = Image2D.FromBgraPixels(pipe.D2DContext, Gradient(), W, H);
                pipe.Renderer.Stage.Register(new TextureProvider("bg", img.Bitmap, W, H));

                using var target = OffscreenTarget.Create(gpu, pipe.D2DContext, W, H);
                pipe.Prepare(time);
                pipe.Composite(target.Bitmap, W, H);
                gpu.Context.Flush();
                return target.Readback();
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [GpuFact]
    public void HostFrameTimeReachesGpuWithoutAnyDriver()
    {
        using var early = RenderAt(1.23);
        using var late = RenderAt(9.87);

        var diff = GoldenImage.Compare(early, late);
        _out.WriteLine($"[L01] wave 相位差 {diff}");

        Assert.True(diff.SameSize && diff.MeanAbs > 1.0,
            $"宿主给的帧时刻必须传到 GPU：t=1.23 与 t=9.87 应画出不同相位，实际 {diff}");
    }
}
