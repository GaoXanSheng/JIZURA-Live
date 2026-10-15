using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using Vortice.Mathematics;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 新渲染核心（D2D + Immediate2D）的离屏回归闸：把一帧渲到离屏纹理再读回内存位图，
/// 既能做「非空白」健全性检查，也能和 golden 做容差比对——这是 GPU 重写期唯一能自动抓观感回归的手段。
///
/// 无 GPU 环境（CI 容器）整类跳过：D3D11 设备建不出来就没有可比的东西。
/// </summary>
public class Immediate2DTests
{
    private const int W = 640;
    private const int H = 360;
    private const string GoldenName = "render-core-scene";

    private const double MeanAbsTolerance = 2.0;
    private const double PctOverTolerance = 0.02;

    /// <summary>画一个覆盖主要原语的参考场景：渐变底、变换矩形、路径、圆、虚线、文字。</summary>
    private static void RenderScene(Immediate2D d, int w, int h)
    {
        d.Begin();
        d.Clear(new Color4(0.047f, 0.047f, 0.055f, 1f));

        // 1) 全屏线性渐变（含 alpha 叠加）
        var grad = new Paint2D
        {
            Brush = new LinearGradient2D(
                new System.Numerics.Vector2(0, 0),
                new System.Numerics.Vector2(w, h),
                [
                    new GradientStop2D(0f, new Color4(0.05f, 0.05f, 0.09f, 1f)),
                    new GradientStop2D(0.55f, new Color4(0.30f, 0.12f, 0.45f, 1f)),
                    new GradientStop2D(1f, new Color4(0.96f, 0.65f, 0.05f, 1f)),
                ]),
        };
        d.FillRect(new Rect(0, 0, w, h), grad);

        // 2) 变换栈：平移 + 旋转 + 描边矩形
        d.Save();
        d.Translate(120, 110);
        d.Rotate(18f);
        var stroke = new Paint2D
        {
            IsStroke = true,
            StrokeWidth = 4f,
            StrokeJoin = StrokeJoin2D.Round,
            Brush = new SolidBrush2D(new Color4(1f, 1f, 1f, 0.85f)),
        };
        d.StrokeRect(new Rect(-70, -45, 140, 90), stroke);
        d.Restore();

        // 3) 路径：五角星填充
        using (var star = new Path2D(d.Factory))
        {
            star.AddRect(0, 0, 0, 0); // 占位后 Reset，验证重置路径可用
            star.Reset();
            for (var i = 0; i < 10; i++)
            {
                var r = i % 2 == 0 ? 58f : 24f;
                var a = -MathF.PI / 2f + i * MathF.PI / 5f;
                var px = 470f + r * MathF.Cos(a);
                var py = 110f + r * MathF.Sin(a);
                if (i == 0) star.MoveTo(px, py);
                else star.LineTo(px, py);
            }

            star.Close();
            d.FillPath(star, new Paint2D { Brush = new SolidBrush2D(new Color4(1f, 0.35f, 0.55f, 1f)) });
        }

        // 4) 径向渐变圆
        d.FillCircle(150, 270, 52,
            new Paint2D
            {
                Brush = new RadialGradient2D(new System.Numerics.Vector2(150, 270), 52, 52,
                [
                    new GradientStop2D(0f, new Color4(0.09f, 0.96f, 0.83f, 1f)),
                    new GradientStop2D(1f, new Color4(0.09f, 0.96f, 0.83f, 0f)),
                ]),
            });

        // 5) 虚线
        d.DrawLine(230, 300, 610, 300,
            new Paint2D
            {
                IsStroke = true,
                StrokeWidth = 3f,
                DashPattern = [14f, 9f],
                Brush = new SolidBrush2D(new Color4(1f, 1f, 1f, 0.7f)),
            });

        // 6) 文本：标题居中 + 一行说明（逐字路径的对齐语义）
        var titleFont = d.Fonts.Get("Microsoft YaHei UI", 44);
        d.DrawText("JIZURA 渲染核心", w / 2f, 210, TextAlign2D.Center, titleFont,
            new Paint2D { Brush = new SolidBrush2D(new Color4(0.96f, 0.91f, 0.88f, 1f)) });

        var smallFont = d.Fonts.Get("Consolas", 20);
        d.DrawText("D3D11 + Direct2D + DirectWrite", w / 2f, 248, TextAlign2D.Center, smallFont,
            new Paint2D { Brush = new SolidBrush2D(new Color4(0.56f, 0.54f, 0.58f, 1f)) });

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
    public void RendersNonBlankFrameWithExpectedPrimitives()
    {
        if (!TrySetup(out var gpu, out var host, out var target)) return; // 无 GPU：跳过
        using (gpu)
        using (host)
        using (target)
        {
            using var bmp = RenderOnce(gpu!, host!, target!);

            Assert.Equal(W, bmp.Width);
            Assert.Equal(H, bmp.Height);

            // 非空白：统计不同颜色数，太少说明什么都没画上
            var colors = new HashSet<uint>();
            for (var y = 0; y < H; y += 3)
            for (var x = 0; x < W; x += 3)
            {
                var c = bmp.GetPixel(x, y);
                colors.Add(((uint)c.Red << 16) | ((uint)c.Green << 8) | c.Blue);
            }

            Assert.True(colors.Count > 200, $"画面颜色过少（{colors.Count}），可能没画出内容");
        }
    }

    [Fact]
    public void SameSceneRendersIdenticallyTwice()
    {
        if (!TrySetup(out var gpu, out var host, out var target)) return;
        using (gpu)
        using (host)
        using (target)
        {
            using var a = RenderOnce(gpu!, host!, target!);
            using var b = RenderOnce(gpu!, host!, target!);
            Assert.True(GoldenImage.BitIdentical(a, b),
                "同一场景渲两次必须逐字节相同——差异说明引入了非确定性（时间/随机/未清理状态）");
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
                Console.WriteLine($"[golden] 缺基准 {GoldenImage.PathOf(GoldenName)}；"
                                  + "设 JIZURA_GOLDEN_UPDATE=1 运行即可生成");
                return;
            }

            var diff = GoldenImage.Compare(golden.Value, frame);
            Assert.True(diff.SameSize, "尺寸与基准不一致：" + diff);
            Assert.True(diff.MeanAbs <= MeanAbsTolerance, $"平均绝对差超容差：{diff}");
            Assert.True(diff.PctOver8 <= PctOverTolerance, $"超阈值像素占比超容差：{diff}");
        }
    }
}
