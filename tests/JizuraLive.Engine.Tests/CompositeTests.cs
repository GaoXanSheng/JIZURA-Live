using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using Vortice.Mathematics;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 两输入合成（<see cref="Immediate2D.DrawComposite"/>）的数学正确性：用两个纯色面按各混合模式合成，
/// 与手算结果比对。这条闸保证 14 项「需要背景输入」的混合模式不是「看起来差不多」而是**算对了**。
/// </summary>
public class CompositeTests
{
    private const int S = 64;

    private static (int R, int G, int B) Composite(D2DHost host, D3DDevice gpu, Blend2D mode,
        Color4 backdrop, Color4 layer)
    {
        using var target = OffscreenTarget.Create(gpu, host.Ctx, S, S);
        host.Draw.SetTarget(target.Bitmap, S, S);

        using var back = host.Draw.CreateSurface(S, S);
        using var lay = host.Draw.CreateSurface(S, S);
        var full = new Rect(0, 0, S, S);

        // D2D 的绘制必须全部落在 BeginDraw/EndDraw 之间（含往中间面里画）
        host.Draw.Begin();

        host.Draw.PushSurface(back);
        host.Draw.FillRect(full, new Paint2D { Brush = new SolidBrush2D(backdrop) });
        host.Draw.PopSurface();

        host.Draw.PushSurface(lay);
        host.Draw.FillRect(full, new Paint2D { Brush = new SolidBrush2D(layer) });
        host.Draw.PopSurface();

        host.Draw.Clear(new Color4(0f, 0f, 0f, 1f));
        host.Draw.DrawComposite(new Paint2D { Blend = mode }, back, lay);
        host.Draw.End();

        using var bmp = target.Readback();
        var c = bmp.GetPixel(S / 2, S / 2);
        return (c.Red, c.Green, c.Blue);
    }

    /// <summary>把 0..1 的期望值换成 0..255 并给 8 位舍入留余量。</summary>
    private static void AssertClose((int R, int G, int B) got, float r, float g, float b, string what)
    {
        const int tol = 4;
        var er = (int)MathF.Round(r * 255);
        var eg = (int)MathF.Round(g * 255);
        var eb = (int)MathF.Round(b * 255);
        Assert.True(Math.Abs(got.R - er) <= tol && Math.Abs(got.G - eg) <= tol && Math.Abs(got.B - eb) <= tol,
            $"{what} 不符：实际 ({got.R},{got.G},{got.B})，期望 ({er},{eg},{eb})（容差 {tol}）");
    }

    [GpuFact]
    public void BlendModesMatchHandComputedResults()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        {
            // 背景 (0.8,0.4,0.2)，前景 (0.5,0.5,1.0)
            var back = new Color4(0.8f, 0.4f, 0.2f, 1f);
            var lay = new Color4(0.5f, 0.5f, 1.0f, 1f);

            AssertClose(Composite(host, gpu, Blend2D.Multiply, back, lay), 0.40f, 0.20f, 0.20f, "Multiply");
            AssertClose(Composite(host, gpu, Blend2D.Screen, back, lay), 0.90f, 0.70f, 1.00f, "Screen");
            AssertClose(Composite(host, gpu, Blend2D.Darken, back, lay), 0.50f, 0.40f, 0.20f, "Darken");
            AssertClose(Composite(host, gpu, Blend2D.Lighten, back, lay), 0.80f, 0.50f, 1.00f, "Lighten");
            AssertClose(Composite(host, gpu, Blend2D.Difference, back, lay), 0.30f, 0.10f, 0.80f, "Difference");
        }
    }

    [GpuFact]
    public void CompositeRespectsOpacity()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        {
            using var target = OffscreenTarget.Create(gpu, host.Ctx, S, S);
            host.Draw.SetTarget(target.Bitmap, S, S);

            using var back = host.Draw.CreateSurface(S, S);
            using var lay = host.Draw.CreateSurface(S, S);
            var full = new Rect(0, 0, S, S);

            host.Draw.Begin();

            host.Draw.PushSurface(back);
            host.Draw.FillRect(full, new Paint2D { Brush = new SolidBrush2D(new Color4(0f, 0f, 0f, 1f)) });
            host.Draw.PopSurface();

            host.Draw.PushSurface(lay);
            host.Draw.FillRect(full, new Paint2D { Brush = new SolidBrush2D(new Color4(1f, 1f, 1f, 1f)) });
            host.Draw.PopSurface();

            host.Draw.Clear(new Color4(0f, 0f, 0f, 1f));
            // SourceOver + alpha 0.5：黑底上叠半透明白 = 中灰
            host.Draw.DrawComposite(new Paint2D { Blend = Blend2D.SrcOver, Alpha = 0.5f }, back, lay);
            host.Draw.End();

            using var bmp = target.Readback();
            var c = bmp.GetPixel(S / 2, S / 2);
            Assert.True(Math.Abs(c.Red - 128) <= 4 && Math.Abs(c.Green - 128) <= 4 && Math.Abs(c.Blue - 128) <= 4,
                $"半透明合成应为中灰，实际 ({c.Red},{c.Green},{c.Blue})");
        }
    }

    /// <summary>
    /// 合成链消费**帧状态**（S06 消费表）：layer.offsetX 是画布宽/高的比例，在设计尺寸处换算成像素。
    /// 帧状态里 0.25 / −0.5、画布 1920×1080 ⇒ 位移 (480, −540)px；文档常量（0）不再参与。
    /// </summary>
    [Fact]
    public void LayerFrameStateOffsetIsConvertedToDesignPixels()
    {
        const float W = 1920f, H = 1080f;
        var layer = new PvLayerConfig { Id = "character", Parallax = new PvParallaxConfig { Depth = 1.0 } };

        // 文档常量是 0；本帧值来自帧状态（曲线/信号算出来的）
        var state = PvLayerState.From(layer) with { OffsetX = 0.25, OffsetY = -0.5 };
        var tf = PvParallax.Compute(W, H, layer, state, PvCamState.Identity,
            globalParallax: false, antiBleed: false);

        // 单位缩放下矩阵的平移分量就是「图层偏移（比例×画布尺寸）」
        Assert.Equal(0.25f * W, tf.Matrix.M31, 3);
        Assert.Equal(-0.5f * H, tf.Matrix.M32, 3);

        // 对照：不带帧状态的重载只认文档常量（0 ⇒ 不动）
        var docOnly = PvParallax.Compute(W, H, layer, PvCamState.Identity, globalParallax: false, antiBleed: false);
        Assert.Equal(0f, docOnly.Matrix.M31, 3);
        Assert.Equal(0f, docOnly.Matrix.M32, 3);

        // 整条腿：曲线 → base(t)（帧状态）→ 设计像素。0.25 在 1920 画布上是 480px
        var curves = new DirectorEvaluator();
        var curve = new FloatCurve();
        Assert.True(curve.TryAdd(0.0, 0.0));
        Assert.True(curve.TryAdd(10.0, 0.5));
        curves.SetCurve(new ParamAddress(ParamAddressKind.Layer, "character", "offsetX"), curve);

        var fromCurve = PvPipeline.BaseLayer(layer, curves, 5.0);
        var tfFromCurve = PvParallax.Compute(W, H, layer, fromCurve, PvCamState.Identity,
            globalParallax: false, antiBleed: false);
        Assert.Equal(0.25f * W, tfFromCurve.Matrix.M31, 3);
    }

    /// <summary>
    /// 合成器读的是帧状态而不是文档常量：同一层、同一文档，只改帧状态里的 offsetX，
    /// 画面跟着挪——「layer.offsetX 打帧屏幕上可见」在合成链上的那条断言。
    /// </summary>
    [GpuFact]
    public void CompositorFollowsLayerFrameState()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();

        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var target = OffscreenTarget.Create(gpu, host.Ctx, S, S))
        {
            host.Draw.SetTarget(target.Bitmap, S, S);

            // 关掉视差、防露底与环境光面：让位移成为画面里唯一的变化量
            var scene = new PvScene
            {
                Width = S,
                Height = S,
                GlobalParallaxEnabled = false,
                AntiBleedGuardEnabled = false,
                AmbientBlendEnabled = false,
            };
            var layer = new PvLayerConfig { Id = "l", Parallax = new PvParallaxConfig { Depth = 1.0 } };
            scene.Layers.Add(layer);

            var images = new Dictionary<string, PvLayerImage>
            {
                ["l"] = PvLayerImage.FromSolid(new Color4(1f, 1f, 1f, 1f), S, S),
            };

            // 文档常量是 0；帧状态里 offsetX = 0.5 ⇒ 整层右移半幅
            scene.Frame.SetLayer("l", PvLayerState.From(layer) with { OffsetX = 0.5 });

            host.Draw.Begin();
            host.Draw.Clear(new Color4(0f, 0f, 0f, 1f));
            new PvStageCompositor(host.Draw).Composite(scene, PvCamState.Identity, images, S, S);
            host.Draw.End();

            using var bmp = target.Readback();
            Assert.True(bmp.GetPixel(S / 4, S / 2).Red < 40, "左半幅应已空出（层被帧状态移走）");
            Assert.True(bmp.GetPixel(S * 3 / 4, S / 2).Red > 200, "右半幅应被层盖住");

            // 帧状态不写回文档
            Assert.Equal(0.0, layer.OffsetX, 12);
        }
    }

    [Fact]
    public void CompositeIsDeterministic()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        {
            var a = Composite(host, gpu, Blend2D.Overlay, new Color4(0.8f, 0.4f, 0.2f, 1f),
                new Color4(0.5f, 0.5f, 1f, 1f));
            var b = Composite(host, gpu, Blend2D.Overlay, new Color4(0.8f, 0.4f, 0.2f, 1f),
                new Color4(0.5f, 0.5f, 1f, 1f));
            Assert.Equal(a, b);
        }
    }
}
