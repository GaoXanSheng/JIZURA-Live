using JizuraLive.Engine;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 字幕图层（P3）：纯 GPU 字幕图层测试。
/// </summary>
public class SubtitleProviderTests
{
    private const int W = 640;
    private const int H = 360;

    private readonly ITestOutputHelper _out;

    public SubtitleProviderTests(ITestOutputHelper output) => _out = output;

    private sealed class FixedSource : IPvSubtitleSource
    {
        public IReadOnlyList<KrcLine>? Lines { get; init; }
        public long Version { get; init; } = 1;
    }

    private static List<KrcLine> SyntheticKrc()
    {
        var words = new List<KrcWord>
        {
            new(0, 400, "声"),
            new(400, 400, "が"),
            new(800, 500, "遠"),
            new(1300, 500, "く"),
            new(1800, 600, "で"),
        };
        return
        [
            new KrcLine(0, 2400, "声が遠くで", words),
            new KrcLine(3000, 2000, "まだ君を探してる",
                [new KrcWord(3000, 1000, "まだ"), new KrcWord(4000, 1000, "君を探してる")]),
        ];
    }

    [GpuFact]
    public void SubtitleLayerDrawsThroughProvider()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        {
            var source = new FixedSource { Lines = SyntheticKrc() };
            using var provider = new SubtitleProvider("subtitle_top", gpu, host.Ctx, source);

            provider.Render(1.0, W, H);
            var img = provider.Pump(new PvFrameContext(1.0, 0, 0, W, H));
            Assert.True(img.HasContent, "提供者应当给出可绘制内容");
            Assert.Equal(W, img.Width);
            Assert.Equal(H, img.Height);
        }
    }

    [GpuFact]
    public void NoSubtitleDataYieldsNoContent()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        {
            using var provider = new SubtitleProvider("subtitle_top", gpu, host.Ctx,
                new FixedSource { Lines = null });
            provider.Render(1.0, W, H);
            var img = provider.Pump(new PvFrameContext(1.0, 0, 0, W, H));
            Assert.True(img.HasContent);
        }
    }
}
