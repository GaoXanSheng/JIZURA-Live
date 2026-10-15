using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using Vortice.Mathematics;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>离屏中间面（PushSurface/PopSurface）的最小行为诊断：确认「画进面里」与「合成回来」两件事。</summary>
public class PushSurfaceDiagnostics
{
    private const int W = 200;
    private const int H = 200;
    private readonly ITestOutputHelper _out;

    public PushSurfaceDiagnostics(ITestOutputHelper output) => _out = output;

    [GpuFact]
    public void SurfaceIsolatesDrawingThenCompositesBack()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var target = OffscreenTarget.Create(gpu, host.Ctx, W, H))
        {
            var d = host.Draw;
            d.SetTarget(target.Bitmap, W, H);
            d.Begin();
            d.Clear(new Color4(1f, 0f, 0f, 1f)); // 全屏红

            // 面里画一个蓝方块（左上 0..100）
            d.PushSurface();
            d.FillRect(new Rect(0, 0, 100, 100), new Paint2D
            {
                Brush = new SolidBrush2D(new Color4(0f, 0f, 1f, 1f)),
            });
            d.PopSurface(new Paint2D());
            d.End();

            using var bmp = target.Readback();
            var corner = bmp.GetPixel(180, 180);   // 面外：应保持红
            var inside = bmp.GetPixel(50, 50);     // 面内：应为蓝
            _out.WriteLine($"[diag] 角落={corner} 面内={inside}");

            Assert.Equal(255, corner.Red);
            Assert.Equal(0, corner.Blue);
            Assert.Equal(255, inside.Blue);
        }
    }
}
