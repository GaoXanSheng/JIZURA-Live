using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using Vortice.Mathematics;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 效果链的分项诊断：同一个「红底 + 面内蓝方块」场景分别套 无效果/模糊/投影/反相 四种 PopSurface，
/// 打印关键像素，用来定位是哪一环把画面弄坏的。
/// </summary>
public class EffectChainDiagnostics
{
    private const int W = 200;
    private const int H = 200;
    private readonly ITestOutputHelper _out;

    public EffectChainDiagnostics(ITestOutputHelper output) => _out = output;

    private static Matrix5x4 Invert() => new(
        -1, 0, 0, 0,
        0, -1, 0, 0,
        0, 0, -1, 0,
        0, 0, 0, 1,
        1, 1, 1, 0);

    [Fact]
    public void EachEffectBehaves()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) return;
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        {
            Report(host, gpu, "无效果", new Paint2D());
            Report(host, gpu, "模糊σ6", new Paint2D { BlurSigma = 6f });
            Report(host, gpu, "投影", new Paint2D { Shadow = new Shadow2D(6f, 6f, 4f, new Color4(0, 0, 0, 1)) });
            Report(host, gpu, "反相", new Paint2D { ColorMatrix = Invert() });
        }
    }

    private void Report(D2DHost host, D3DDevice gpu, string name, Paint2D pop)
    {
        using var target = OffscreenTarget.Create(gpu, host.Ctx, W, H);
        var d = host.Draw;
        d.SetTarget(target.Bitmap, W, H);
        d.Begin();
        d.Clear(new Color4(1f, 0f, 0f, 1f));

        d.PushSurface();
        d.FillRect(new Rect(0, 0, 100, 100), new Paint2D
        {
            Brush = new SolidBrush2D(new Color4(0f, 0f, 1f, 1f)),
        });
        d.PopSurface(pop);
        d.End();

        using var bmp = target.Readback();
        var inside = bmp.GetPixel(50, 50);     // 面内（蓝方块）
        var outside = bmp.GetPixel(180, 180);  // 面外（红底）
        var edge = bmp.GetPixel(103, 103);     // 方块外一点（投影会落在这）
        _out.WriteLine($"[{name}] 面内={Hex(inside)} 面外={Hex(outside)} 影位={Hex(edge)}");
    }

    private static string Hex(ReadbackPixel c) => $"#{c.Red:x2}{c.Green:x2}{c.Blue:x2}{c.Alpha:x2}";
}
