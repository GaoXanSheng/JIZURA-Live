using System.IO;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using Vortice.Mathematics;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 固定设计画布：图层与效果图一律按设计尺寸求值，窗口尺寸只决定预览怎么 letterbox 贴上去。
/// 缩放预览窗口不许改动内容坐标、效果半径或图层取景。
/// </summary>
public class DesignCanvasTests
{
    private const int DesignW = 1920;
    private const int DesignH = 1080;

    /// <summary>非 16:9 的预览矩形，letterbox 必须在左右留出黑边。</summary>
    private const int PreviewW = 1000;
    private const int PreviewH = 300;

    private sealed class SizeProbe : IPvLayerProvider
    {
        public string Id => "bg";
        public long Version => 1;
        public (int Width, int Height) Seen;

        public PvLayerImage Pump(in PvFrameContext ctx)
        {
            Seen = (ctx.Width, ctx.Height);
            return PvLayerImage.FromSolid(new Color4(1f, 0f, 0f, 1f), ctx.Width, ctx.Height);
        }

        public void Dispose()
        {
        }
    }

    private static PvScene RedCanvasScene()
    {
        var scene = new PvScene
        {
            Name = "canvas",
            Width = DesignW,
            Height = DesignH,
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

    private static (ReadbackImage Image, SizeProbe Probe) RenderPreview(int previewW, int previewH)
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) throw new InvalidOperationException("no gpu");

        using (gpu)
        {
            var dir = Path.Combine(Path.GetTempPath(), "jz-canvas-" + Guid.NewGuid());
            Directory.CreateDirectory(dir);
            try
            {
                using var pipe = new PvPipeline(gpu, Path.Combine(dir, "scene.json"), RedCanvasScene(),
                    new EffectGraph(), null, false, null);
                var probe = new SizeProbe();
                pipe.Renderer.Stage.Register(probe);

                using var target = OffscreenTarget.Create(gpu, pipe.D2DContext, previewW, previewH);
                pipe.Prepare(0.0);
                pipe.Composite(target.Bitmap, previewW, previewH);
                gpu.Context.Flush();
                return (target.Readback(), probe);
            }
            finally
            {
                Directory.Delete(dir, true);
            }
        }
    }

    private static Color4 PixelAt(ReadbackImage img, int x, int y)
    {
        var i = (y * img.Width + x) * 4;
        var p = img.BgraPixels;
        return new Color4(p[i + 2] / 255f, p[i + 1] / 255f, p[i] / 255f, p[i + 3] / 255f);
    }

    [Fact]
    public void LayersSeeDesignSizeNotPreviewSize()
    {
        var (image, probe) = RenderPreview(PreviewW, PreviewH);
        using (image)
        {
            Assert.Equal((DesignW, DesignH), probe.Seen);
        }
    }

    [Fact]
    public void PreviewLetterboxesTheDesignCanvas()
    {
        using var image = RenderPreview(PreviewW, PreviewH).Image;

        var midY = PreviewH / 2;
        Assert.True(PixelAt(image, 2, midY).R < 0.02f,
            $"左边缘应是 letterbox 黑边，实测 {PixelAt(image, 2, midY)}");
        Assert.True(PixelAt(image, PreviewW - 3, midY).R < 0.02f,
            $"右边缘应是 letterbox 黑边，实测 {PixelAt(image, PreviewW - 3, midY)}");
        Assert.True(PixelAt(image, PreviewW / 2, midY).R > 0.9f,
            $"画面中心应是设计画布内容（纯红），实测 {PixelAt(image, PreviewW / 2, midY)}");
    }

    /// <summary>贴法本身：横条留上下黑边以外的左右黑边，竖条反过来留上下。</summary>
    [Theory]
    [InlineData(DesignW, DesignH, PreviewW, PreviewH, 233.33f, 0f, 533.33f, 300f)]
    [InlineData(DesignW, DesignH, 800, 1200, 0f, 375f, 800f, 450f)]
    [InlineData(DesignW, DesignH, 960, 540, 0f, 0f, 960f, 540f)]
    public void LetterboxFitsDesignCanvasIntoAnyPreview(
        int designW, int designH, int targetW, int targetH,
        float x, float y, float w, float h)
    {
        var r = PvStageRenderer.Letterbox(designW, designH, targetW, targetH);
        Assert.Equal(x, r.X, 2);
        Assert.Equal(y, r.Y, 2);
        Assert.Equal(w, r.Width, 2);
        Assert.Equal(h, r.Height, 2);
    }
}
