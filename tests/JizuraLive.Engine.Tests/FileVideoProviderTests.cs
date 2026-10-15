using System.IO;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 视频图层（L1）：时间→定位的纯函数、三源互斥判定、以及"没有片子时不许炸"。
///
/// 真解码一帧需要仓库里有一段视频当夹具（本仓库没有），所以这里不假装验证过画面：
/// 解出像素、循环播放的接帧、负步长/翻行这些只能真跑片子才能钉住的结论，本文件不作断言。
/// </summary>
public class FileVideoProviderTests
{
    private readonly ITestOutputHelper _out;

    public FileVideoProviderTests(ITestOutputHelper output) => _out = output;

    // ---------------- 时间 → 定位（纯函数） ----------------

    [Theory]
    [InlineData(0.0, 2.0, 0.0)]
    [InlineData(0.5, 2.0, 0.5)]
    [InlineData(1.999, 2.0, 1.999)]
    [InlineData(2.0, 2.0, 0.0)]      // 正好一圈：回到开头而不是停在片尾
    [InlineData(2.5, 2.0, 0.5)]      // 循环取模
    [InlineData(61.0, 2.0, 1.0)]
    [InlineData(-0.5, 2.0, 1.5)]     // 帧时钟可能回退（拖动/暂停复位）：折进正向区间
    [InlineData(-2.0, 2.0, 0.0)]
    [InlineData(-5.0, 2.0, 1.0)]
    public void LoopTimeWrapsIntoRange(double time, double duration, double expected) =>
        Assert.Equal(expected, FileVideoProvider.LoopTime(time, duration), 6);

    [Fact]
    public void LoopTimeClampsNonFiniteAndInvalidDuration()
    {
        // 时长非法（0/负/NaN）：没有可用的循环长度，落在片头（不是 NaN 传出去把定位搞崩）
        Assert.Equal(0.0, FileVideoProvider.LoopTime(3.0, 0));
        Assert.Equal(0.0, FileVideoProvider.LoopTime(3.0, -1));
        Assert.Equal(0.0, FileVideoProvider.LoopTime(3.0, double.NaN));

        // 时刻非有限：同样钳到片头
        Assert.Equal(0.0, FileVideoProvider.LoopTime(double.NaN, 2.0));
        Assert.Equal(0.0, FileVideoProvider.LoopTime(double.PositiveInfinity, 2.0));
        Assert.Equal(0.0, FileVideoProvider.LoopTime(double.NegativeInfinity, 2.0));

        // 越界钳制的上界：结果永远落在 [0, 时长)
        foreach (var t in new[] { 1e9, -1e9, 12345.678 })
        {
            var wrapped = FileVideoProvider.LoopTime(t, 3.0);
            Assert.InRange(wrapped, 0.0, 3.0 - 1e-9);
        }
    }

    // ---------------- 三源互斥（Spout > Video > Image） ----------------

    private static PvLayerConfig Spout() => new()
    {
        Id = "spout",
        Kind = PvLayerKind.VisualAsset,
        SpoutSenderName = "VTubeStudioSpout",
    };

    private static PvLayerConfig Video() => new()
    {
        Id = "video",
        Kind = PvLayerKind.VisualAsset,
        SpoutSenderName = null,
        VideoPath = @"C:\media\loop.mp4",
    };

    private static PvLayerConfig Image() => new()
    {
        Id = "image",
        Kind = PvLayerKind.VisualAsset,
        SpoutSenderName = null,
        ImagePath = @"C:\media\still.png",
    };

    private static (bool Spout, bool Video, bool Image) Wants(PvLayerConfig layer) =>
        (PvPipeline.WantsSpout(layer), PvPipeline.WantsVideo(layer), PvPipeline.WantsImage(layer));

    [Fact]
    public void EachSingleSourceClaimsTheLayer()
    {
        Assert.Equal((true, false, false), Wants(Spout()));
        Assert.Equal((false, true, false), Wants(Video()));
        Assert.Equal((false, false, true), Wants(Image()));

        // 空白路径一律不算（"挂着但没填"与"没挂"同义）
        Assert.Equal((false, false, false), Wants(new PvLayerConfig { SpoutSenderName = " " }));
        Assert.Equal((false, false, false),
            Wants(new PvLayerConfig { SpoutSenderName = null, VideoPath = "  " }));
        Assert.Equal((false, false, false),
            Wants(new PvLayerConfig { SpoutSenderName = null, ImagePath = "" }));
    }

    /// <summary>手改文件把多个源同时挂上时，只有优先级最高的那个生效（否则一层会被两个提供者抢）。</summary>
    [Fact]
    public void MultipleSourcesResolveToHighestPriority()
    {
        var both = Video();
        both.ImagePath = @"C:\media\still.png";
        Assert.Equal((false, true, false), Wants(both)); // Video > Image

        var all = Spout();
        all.VideoPath = @"C:\media\loop.mp4";
        all.ImagePath = @"C:\media\still.png";
        Assert.Equal((true, false, false), Wants(all));  // Spout > Video > Image

        var spoutAndImage = Spout();
        spoutAndImage.ImagePath = @"C:\media\still.png";
        Assert.Equal((true, false, false), Wants(spoutAndImage));
    }

    [Fact]
    public void NonVisualAssetLayersClaimNoSource()
    {
        var bg = Video();
        bg.Kind = PvLayerKind.Background;
        Assert.Equal((false, false, false), Wants(bg));

        var lyric = Video();
        lyric.Kind = PvLayerKind.LyricPv;
        Assert.Equal((false, false, false), Wants(lyric));
    }

    /// <summary>新建视频层的默认值不能留着 SpoutSenderName（默认值是 "VTubeStudio"），否则它永远是个 Spout 层。</summary>
    [Fact]
    public void NewVideoLayerClearsSpoutSender()
    {
        var layer = PvPipeline.NewVideoLayer("src_1", @"C:\media\loop.mp4");

        Assert.Equal("loop", layer.Name);
        Assert.Equal("C:\\media\\loop.mp4", layer.VideoPath);
        Assert.Null(layer.SpoutSenderName);
        Assert.Null(layer.ImagePath);
        Assert.Equal((false, true, false), Wants(layer));
    }

    // ---------------- 没有片子时不许炸（解码器侧，不需要 GPU） ----------------

    [Fact]
    public void DecoderOpenOnMissingFileReportsReadableError()
    {
        var missing = Path.Combine(Path.GetTempPath(), "jizura-missing-" + Guid.NewGuid().ToString("N") + ".mp4");

        var decoder = MfVideoDecoder.TryOpen(missing, out var error);

        Assert.Null(decoder);
        Assert.Contains(missing, error); // 可读：指出是哪个文件
        _out.WriteLine("[视频] 缺文件时的报错：" + error);
    }

    /// <summary>文件在、但不是视频：MF 会拒，报错必须可读且不为空（不许静默返回 null）。</summary>
    [Fact]
    public void DecoderOpenOnGarbageFileReportsReadableError()
    {
        var junk = Path.Combine(Path.GetTempPath(), "jizura-junk-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllBytes(junk, new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x11, 0x22, 0x33 });
        try
        {
            var decoder = MfVideoDecoder.TryOpen(junk, out var error);

            Assert.Null(decoder);
            Assert.False(string.IsNullOrWhiteSpace(error), "打不开时必须留下可读原因");
            _out.WriteLine("[视频] 坏文件时的报错：" + error);
        }
        finally
        {
            if (File.Exists(junk)) File.Delete(junk);
        }
    }

    // ---------------- 没有片子时不许炸（提供者侧，需要 GPU） ----------------

    [GpuFact]
    public void VideoProviderWithMissingFileStaysQuiet()
    {
        var gpu = D3DDevice.TryCreate(out var why);
        if (gpu is null) GpuRequire.Fail(why);
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        {
            var missing = Path.Combine(Path.GetTempPath(), "jizura-missing-" + Guid.NewGuid().ToString("N") + ".mp4");
            using var provider = new FileVideoProvider("vid_missing", gpu, host.Ctx, missing);

            // 连泵两帧：第一帧试开（失败），第二帧走"同一戳不重试"的分支；两次都不许抛
            for (var i = 0; i < 2; i++)
            {
                var image = provider.Pump(new PvFrameContext(i * 0.016, 0, 0, 320, 180));
                Assert.False(image.HasContent, "没有片子时本层必须缺席，不能给出内容");
            }

            Assert.False(provider.Ready);
            Assert.NotNull(provider.LastError);
            Assert.Equal(0.0, provider.Duration);
            _out.WriteLine("[视频] 提供者的报错：" + provider.LastError);
        }
    }
}
