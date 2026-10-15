using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using Vortice.Mathematics;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 离屏中间面 + D2D 效果链（模糊 / 投影 / 色矩阵 / 层间合成）的回归闸。
/// 这是旧引擎 <c>ImageFilter.CreateBlur</c>（17 处）、<c>CreateDropShadow</c>（3 处）、
/// <c>ColorFilter.CreateColorMatrix</c>（6 处）在新核心里的对位能力，必须逐帧可比。
/// </summary>
public class LayerEffectsTests
{
    private const int W = 640;
    private const int H = 360;
    private const string GoldenName = "render-core-effects";

    private const double MeanAbsTolerance = 2.0;
    private const double PctOverTolerance = 0.02;

    /// <summary>反相色矩阵（对位 SKColorFilter.CreateColorMatrix 的 5×4 布局：5 行 = RGBA+偏移）。</summary>
    private static Matrix5x4 InvertMatrix() => new(
        -1, 0, 0, 0,
        0, -1, 0, 0,
        0, 0, -1, 0,
        0, 0, 0, 1,
        1, 1, 1, 0);

    private static void RenderScene(Immediate2D d, int w, int h)
    {
        d.Begin();
        d.Clear(new Color4(0.047f, 0.047f, 0.055f, 1f));

        d.FillRect(new Rect(0, 0, w, h), new Paint2D
        {
            Brush = new LinearGradient2D(new System.Numerics.Vector2(0, 0), new System.Numerics.Vector2(0, h),
            [
                new GradientStop2D(0f, new Color4(0.06f, 0.06f, 0.10f, 1f)),
                new GradientStop2D(1f, new Color4(0.16f, 0.07f, 0.24f, 1f)),
            ]),
        });

        // 1) 模糊：亮色圆 → 大 σ 模糊 = 泛光光斑
        d.PushSurface();
        d.FillCircle(w * 0.25f, h * 0.35f, 44f,
            new Paint2D { Brush = new SolidBrush2D(new Color4(0.09f, 0.96f, 0.83f, 1f)) });
        d.PopSurface(new Paint2D { BlurSigma = 14f, Blend = Blend2D.Plus });

        // 2) 投影：文字先画到面上，再带偏移阴影合成
        d.PushSurface();
        d.DrawText("SHADOW", w * 0.52f, h * 0.30f, TextAlign2D.Center,
            d.Fonts.Get("Microsoft YaHei UI", 56),
            new Paint2D { Brush = new SolidBrush2D(new Color4(1f, 0.92f, 0.75f, 1f)) });
        d.PopSurface(new Paint2D
        {
            Shadow = new Shadow2D(8f, 8f, 7f, new Color4(0f, 0f, 0f, 0.85f)),
        });

        // 3) 色矩阵：反相的一块内容
        d.PushSurface();
        d.FillRect(new Rect(w * 0.10f, h * 0.62f, 200f, 90f),
            new Paint2D { Brush = new SolidBrush2D(new Color4(0.95f, 0.35f, 0.55f, 1f)) });
        d.DrawText("INVERT", w * 0.10f + 100f, h * 0.62f + 62f, TextAlign2D.Center,
            d.Fonts.Get("Consolas", 30),
            new Paint2D { Brush = new SolidBrush2D(new Color4(1f, 1f, 1f, 1f)) });
        d.PopSurface(new Paint2D { ColorMatrix = InvertMatrix() });

        // 4) 直接层间合成（Plus）：加色叠加
        d.FillCircle(w * 0.80f, h * 0.72f, 40f,
            new Paint2D { Brush = new SolidBrush2D(new Color4(0.9f, 0.5f, 0.1f, 0.75f)) });

        d.End();
    }

    private static bool TrySetup(out D3DDevice? gpu, out D2DHost? host, out OffscreenTarget? target)
    {
        gpu = D3DDevice.TryCreate(out _);
        if (gpu is null)
        {
            host = null;
            target = null;
            return false;
        }

        host = D2DHost.Create(gpu);
        target = OffscreenTarget.Create(gpu, host.Ctx, W, H);
        return true;
    }

    private static ReadbackImage RenderOnce(D3DDevice gpu, D2DHost host, OffscreenTarget target)
    {
        host.Draw.SetTarget(target.Bitmap, W, H);
        RenderScene(host.Draw, W, H);
        return target.Readback();
    }

    [Fact]
    public void EffectsProduceVisibleOutput()
    {
        if (!TrySetup(out var gpu, out var host, out var target)) return;
        using (gpu)
        using (host)
        using (target)
        {
            using var bmp = RenderOnce(gpu!, host!, target!);
            var colors = new HashSet<uint>();
            for (var y = 0; y < H; y += 3)
            for (var x = 0; x < W; x += 3)
            {
                var c = bmp.GetPixel(x, y);
                colors.Add(((uint)c.Red << 16) | ((uint)c.Green << 8) | c.Blue);
            }

            Assert.True(colors.Count > 300, $"画面颜色过少（{colors.Count}），效果链可能没生效");
        }
    }

    [Fact]
    public void EffectsAreDeterministic()
    {
        if (!TrySetup(out var gpu, out var host, out var target)) return;
        using (gpu)
        using (host)
        using (target)
        {
            using var a = RenderOnce(gpu!, host!, target!);
            using var b = RenderOnce(gpu!, host!, target!);
            Assert.True(GoldenImage.BitIdentical(a, b), "含效果链的场景渲两次必须逐字节相同");
        }
    }

    [Fact]
    public void MatchesGoldenWithinTolerance()
    {
        if (!TrySetup(out var gpu, out var host, out var target)) return;
        using (gpu)
        using (host)
        using (target)
        {
            using var frame = RenderOnce(gpu!, host!, target!);

            if (GoldenImage.UpdateRequested)
            {
                GoldenImage.SaveOrUpdate(GoldenName, frame);
                return;
            }

            using var golden = GoldenImage.TryLoad(GoldenName);
            if (golden is null)
            {
                Console.WriteLine($"[golden] 缺基准 {GoldenImage.PathOf(GoldenName)}");
                return;
            }

            var diff = GoldenImage.Compare(golden.Value, frame);
            Assert.True(diff.SameSize, "尺寸与基准不一致：" + diff);
            Assert.True(diff.MeanAbs <= MeanAbsTolerance, $"平均绝对差超容差：{diff}");
            Assert.True(diff.PctOver8 <= PctOverTolerance, $"超阈值像素占比超容差：{diff}");
        }
    }
}
