using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using Vortice.Mathematics;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// <see cref="Immediate2D"/> 的稳态托管分配闸：每个绘制原语都是「BeginDraw → 画 → EndDraw」，
/// 不再为每笔分配闭包 + 委托（旧回调写法实测 104 字节/笔，一帧几十笔就是每帧几 KB 垃圾）。
/// 这类退化功能测试抓不到——画面照样对，只是帧循环每帧漏垃圾——只有分配计数能钉住。
///
/// 口径：几何原语 + 默认线型（<see cref="Paint2D"/> 的 join/cap/miter 都是默认值时走 D2D 内建样式）。
/// 两处已知的、本用例不覆盖的开销：非默认线型每笔会格式化一个缓存键字符串（StrokeStyle）、
/// DrawBitmap/DrawText 每次调用自建 Paint2D（或临时文本格式）。
/// </summary>
public class Immediate2DAllocationTests
{
    private const int W = 256;
    private const int H = 144;
    private const int WarmupFrames = 4;
    private const int MeasuredFrames = 20;

    /// <summary>一帧的绘制内容：覆盖各条原语路径（填充/描边/路径/圆/圆角/变换栈）。</summary>
    private static void DrawOneFrame(Immediate2D d, Paint2D fill, Paint2D stroke, Rect rect, Path2D path)
    {
        d.Begin();
        d.Clear(new Color4(0f, 0f, 0f, 1f));

        d.FillRect(rect, fill);
        d.StrokeRect(rect, stroke);
        d.DrawRect(rect, fill);
        d.FillRoundRect(rect, 6f, 6f, fill);
        d.StrokeRoundRect(rect, 6f, 6f, stroke);
        d.DrawRoundRect(rect, 6f, 6f, fill);
        d.FillCircle(40f, 80f, 18f, fill);
        d.StrokeCircle(40f, 80f, 18f, stroke);
        d.DrawCircle(40f, 80f, 18f, stroke);
        d.DrawLine(0f, 0f, W, H, stroke);
        d.FillPath(path, fill);
        d.StrokePath(path, stroke);
        d.DrawPath(path, fill);

        d.Save();
        d.Translate(8f, 6f);
        d.Rotate(12f);
        d.FillRect(rect, fill);
        d.Restore();

        d.End();
    }

    [GpuFact]
    public void SteadyStateFrameDoesNotAllocate()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();

        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var target = OffscreenTarget.Create(gpu, host.Ctx, W, H))
        using (var path = new Path2D(host.Draw.Factory))
        {
            host.Draw.SetTarget(target.Bitmap, W, H);

            // 画笔/路径几何/变换栈容量都在预热里建好（它们是一次性开销，不属于稳态）
            var fill = new Paint2D { Brush = new SolidBrush2D(new Color4(0.2f, 0.4f, 0.8f, 1f)) };
            var stroke = new Paint2D
            {
                IsStroke = true,
                StrokeWidth = 2f,
                Brush = new SolidBrush2D(new Color4(1f, 1f, 1f, 0.8f)),
            };
            var rect = new Rect(10, 10, 80, 40);
            path.MoveTo(4, 4);
            path.LineTo(60, 8);
            path.LineTo(32, 52);
            path.Close();

            for (var i = 0; i < WarmupFrames; i++) DrawOneFrame(host.Draw, fill, stroke, rect, path);

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < MeasuredFrames; i++) DrawOneFrame(host.Draw, fill, stroke, rect, path);
            var after = GC.GetAllocatedBytesForCurrentThread();

            Assert.Equal(0L, after - before);
        }
    }
}
