using System.Diagnostics;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using Vortice.Mathematics;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 新渲染核心（D2D + Immediate2D）在 1080p 下、接近真实 PV 负载的**性能基线**。
///
/// 为什么要量：旧链路 CPU 光栅 1920×1080 稳定帧是 47.6–49.5 ms（见 <c>EngineBaselineTests</c>，
/// 也就是 ~20fps 上限）。迁移值不值得做，取决于新核心在同样负载下是否明显更快——这条测试给出数字。
///
/// 计时口径：分开记「命令提交」与「含 GPU 落地」。提交耗时是渲染线程实际占用的时间；
/// 含落地（末尾一次回读强制同步）是每帧的端到端上限。
/// </summary>
public class RenderCoreBaselineTests
{
    private const int W = 1920;
    private const int H = 1080;
    private const int Frames = 30;

    /// <summary>贴近 PV 真实负载：渐变底 + 若干装饰图形 + 约 300 个字形 + 混合/效果层。</summary>
    private static void DrawScene(Immediate2D d, int w, int h, int frame)
    {
        d.Begin();
        d.Clear(new Color4(0.05f, 0.05f, 0.08f, 1f));

        d.FillRect(new Rect(0, 0, w, h), new Paint2D
        {
            Brush = new LinearGradient2D(new System.Numerics.Vector2(0, 0),
                new System.Numerics.Vector2(w, h),
            [
                new GradientStop2D(0f, new Color4(0.06f, 0.06f, 0.10f, 1f)),
                new GradientStop2D(0.6f, new Color4(0.28f, 0.11f, 0.42f, 1f)),
                new GradientStop2D(1f, new Color4(0.96f, 0.65f, 0.05f, 1f)),
            ]),
        });

        // 装饰：60 个旋转描边矩形
        for (var i = 0; i < 60; i++)
        {
            d.Save();
            d.Translate((i * 137 % w), (i * 89 % h));
            d.Rotate(i * 6f + frame);
            d.StrokeRect(new Rect(-40, -25, 80, 50), new Paint2D
            {
                IsStroke = true,
                StrokeWidth = 2f,
                StrokeJoin = StrokeJoin2D.Round,
                Brush = new SolidBrush2D(new Color4(1f, 1f, 1f, 0.25f)),
            });
            d.Restore();
        }

        // 装饰：20 条路径（星形）
        for (var k = 0; k < 20; k++)
        {
            using var star = new Path2D(d.Factory);
            var cx = (k * 211 % w);
            var cy = (k * 173 % h);
            for (var i = 0; i < 10; i++)
            {
                var r = i % 2 == 0 ? 60f : 24f;
                var a = -MathF.PI / 2f + i * MathF.PI / 5f;
                var px = cx + r * MathF.Cos(a);
                var py = cy + r * MathF.Sin(a);
                if (i == 0) star.MoveTo(px, py);
                else star.LineTo(px, py);
            }

            star.Close();
            d.FillPath(star, new Paint2D { Brush = new SolidBrush2D(new Color4(1f, 0.35f, 0.55f, 0.55f)) });
        }

        // 歌词主体：约 300 个字形（逐字绘制 = 旧引擎的排版路径）
        var font = d.Fonts.Get("Microsoft YaHei UI", 96);
        const string line = "私たちはいつか夢の先へと歩いていくのだろうか";
        var cps = line.EnumerateRunes().ToArray();
        var total = 0f;
        foreach (var r in cps) total += font.MeasureWidth(r.Value);
        var x = (w - total) / 2f;
        for (var rep = 0; rep < 3; rep++)
        {
            var cx = x;
            foreach (var r in cps)
            {
                d.DrawText(r.ToString(), cx, h * 0.5f + rep * 120f, TextAlign2D.Left, font,
                    new Paint2D { Brush = new SolidBrush2D(new Color4(0.96f, 0.91f, 0.88f, 1f)) });
                cx += font.MeasureWidth(r.Value);
            }
        }

        // 效果层：模糊 + 加色
        d.PushSurface();
        d.FillCircle(w * 0.3f, h * 0.7f, 160f,
            new Paint2D { Brush = new SolidBrush2D(new Color4(0.09f, 0.96f, 0.83f, 0.8f)) });
        d.PopSurface(new Paint2D { BlurSigma = 26f, Blend = Blend2D.Plus });

        d.End();
    }

    [GpuFact]
    public void Measures1080pFrameCost()
    {
        var gpu = D3DDevice.TryCreate(out var err);
        // 带 [GpuFact] 的设备用例：探测说可用、真建却失败，那是真问题，必须显式失败。
        // 之前这里打印一句「无 GPU，跳过」就 return——报告里一片绿，实际一帧没量。
        if (gpu is null) GpuRequire.Fail(err);

        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var target = OffscreenTarget.Create(gpu, host.Ctx, W, H))
        {
            var d = host.Draw;
            d.SetTarget(target.Bitmap, W, H);

            // 预热（字体/画刷/效果首次成型）
            DrawScene(d, W, H, 0);
            using (target.Readback()) { }

            var sw = Stopwatch.StartNew();
            for (var i = 0; i < Frames; i++)
            {
                d.SetTarget(target.Bitmap, W, H);
                DrawScene(d, W, H, i);
            }

            var submitMs = sw.Elapsed.TotalMilliseconds / Frames;

            using (target.Readback()) { } // 强制 GPU 落地
            var totalMs = sw.Elapsed.TotalMilliseconds / Frames;

            Console.WriteLine($"[render-core baseline] {W}x{H} 近真实负载："
                              + $"命令提交 {submitMs:F2} ms/帧，含 GPU 落地 {totalMs:F2} ms/帧"
                              + $"（旧引擎 CPU 光栅 ~47.6ms/帧）");

            // 宽松上限：只为抓灾难性回归（CI 机器差异大，不做严格断言）
            Assert.True(totalMs < 100, $"单帧耗时异常（{totalMs:F2}ms）");
        }
    }
}
