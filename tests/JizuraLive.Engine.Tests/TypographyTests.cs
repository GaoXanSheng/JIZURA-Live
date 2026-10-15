using System.IO;
using JizuraLive.App.PvStage;
using JizuraLive.Engine;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using JizuraLive.Render.Text;
using NAudio.Wave;

namespace JizuraLive.Engine.Tests;

public class TypographyTests
{
    private const int W = 640;
    private const int H = 360;

    private const string SampleLrc = """
        [ti:Sample Song]
        [ar:Jizura Artist]
        [00:00.00]夜明けの色を覚えてる
        [00:03.50]ほどけた声が遠くで鳴った
        [00:07.20]ねえ、まだ間に合うかな
        [00:10.80]透明なままじゃ終われない
        """;

    [Fact]
    public void LrcParserParsesTimestampsAccurately()
    {
        var lines = LrcParser.Parse(SampleLrc);
        Assert.Equal(4, lines.Count);

        Assert.Equal(0, lines[0].StartMs);
        Assert.Equal("夜明けの色を覚えてる", lines[0].Text);

        Assert.Equal(3500, lines[1].StartMs);
        Assert.Equal(7200 - 3500, lines[1].DurMs);
        Assert.Equal("ほどけた声が遠くで鳴った", lines[1].Text);

        Assert.Equal(7200, lines[2].StartMs);
        Assert.Equal(10800, lines[3].StartMs);
    }

    [Fact]
    public void LrcParserLocatesCorrectLineByTime()
    {
        var lines = LrcParser.Parse(SampleLrc);

        Assert.Equal(0, LrcParser.FindLineIndex(lines, 1.0));
        Assert.Equal(1, LrcParser.FindLineIndex(lines, 4.0));
        Assert.Equal(2, LrcParser.FindLineIndex(lines, 8.0));
        Assert.Equal(3, LrcParser.FindLineIndex(lines, 12.0));
        Assert.Equal(-1, LrcParser.FindLineIndex(lines, 30.0));
    }

    [GpuFact]
    public void TypographyLayerRendersTextOntoSurface()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();

        using (gpu)
        using (var host = D2DHost.Create(gpu))
        {
            var lines = LrcParser.Parse(SampleLrc);
            using var provider = new TypographyLayerProvider("lyric_test", gpu, host, W, H);
            provider.SetLyrics(lines);

            provider.Render(1.5);
            var image = provider.Pump(new PvFrameContext(1.5, 0, 0.5, W, H));

            Assert.True(image.HasContent);
            Assert.Equal(W, image.Width);
            Assert.Equal(H, image.Height);
        }
    }

    /// <summary>
    /// 空档与 t==EndMs 必须真的清成透明：只查 HasContent 是假绿（目标面一直都在），
    /// 所以这里回读像素数 alpha——文字离开区间就不能留下上一帧（契约 §13.6 缓存与清屏）。
    /// </summary>
    [GpuFact]
    public void TypographyClearsDuringGapAndAtClipEnd()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();

        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var provider = new TypographyLayerProvider("lyric_clear", gpu, host, W, H))
        {
            // A=[1.0s,5.0s)、5.0s 的空标签清屏、B=[9.0s,12.0s)（无音频时长 → 末句 start+3s 兜底）
            provider.SetClips(LrcParser.ParseClips("[00:01]A\n[00:05]\n[00:09]B").Clips);

            provider.Render(2.0); // 句内：进度 1/4，进出场曲线都已走完（alpha=1）
            using (var lit = ReadbackOrFail(provider, "句内"))
                Assert.True(CountVisiblePixels(lit) > 0, "句内时刻应当画出文字");

            provider.Render(6.0); // 空档
            using (var gap = ReadbackOrFail(provider, "空档"))
                Assert.Equal(0, CountVisiblePixels(gap));

            provider.Render(5.0); // t == EndMs：半开区间终点
            using (var atEnd = ReadbackOrFail(provider, "t==EndMs"))
                Assert.Equal(0, CountVisiblePixels(atEnd));
        }
    }

    /// <summary>
    /// 同刻合并出的多行文本要**两行都画**：与同一 provider、其余不变、只留一行的像素数对比，
    /// 只画第一行（或把 '\n' 当缺字画）都会让这条失败。
    /// </summary>
    [GpuFact]
    public void TypographyDrawsMergedMultiLineText()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();

        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var provider = new TypographyLayerProvider("lyric_multiline", gpu, host, W, H))
        {
            var merged = Assert.Single(LrcParser.ParseClips("[00:01]上の行\n[00:01]下の行").Clips);
            Assert.Equal("上の行\n下の行", merged.Text); // 同刻两条先合并成多行

            provider.SetClips([merged]);
            provider.Render(2.0);
            int mergedPixels, singlePixels;
            using (var twoLines = ReadbackOrFail(provider, "合并多行"))
                mergedPixels = CountVisiblePixels(twoLines);

            provider.SetClips(LrcParser.ParseClips("[00:01]上の行").Clips); // 单行对照
            provider.Render(2.0);
            using (var oneLine = ReadbackOrFail(provider, "单行对照"))
                singlePixels = CountVisiblePixels(oneLine);

            Assert.True(mergedPixels > singlePixels,
                $"合并的两行都要画出来：多行 {mergedPixels} 像素，单行 {singlePixels} 像素");
        }
    }

    /// <summary>回读离屏面；还没 Render 过就没有目标面，要显式失败而不是静默跳过。</summary>
    private static ReadbackImage ReadbackOrFail(TypographyLayerProvider provider, string when) =>
        provider.ReadbackTarget()
        ?? throw new InvalidOperationException($"没有可回读的离屏面（{when}）：Render 之后才有目标面");

    /// <summary>数非透明像素（BGRA8888 预乘，alpha 在每像素第 4 字节）。</summary>
    private static int CountVisiblePixels(ReadbackImage image)
    {
        var pixels = image.BgraPixels;
        var count = 0;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) count++;

        return count;
    }

    /// <summary>
    /// 无音源时不跑虚拟时钟：时间只认用户定位，Play 不推进（旧实现在这里偷偷跑了一台 180s 墙钟）。
    /// </summary>
    [Fact]
    public void TransportStaysFrozenWithoutAudioSource()
    {
        using var driver = new AudioClockDriver();
        Assert.False(driver.IsPlaying);
        Assert.Equal(0, driver.Time);

        driver.Seek(15.5);
        Assert.Equal(15.5, driver.Time, 1);

        driver.Play();
        Assert.False(driver.IsPlaying);
        Assert.Equal(15.5, driver.Time, 1);
    }

    // ---- 音频驱动时钟 ----
    // 这一组只钉「加载 + 时钟读数语义」，绝不 Play()：WaveOut.Play 会真出声，
    // 且 CI 无音频设备时 waveOutOpen 直接失败。设备相关的行为（播放中 Seek 的停起冲刷、
    // OnStopped 三路分流）无法在没有真实播放的情况下构造，见文件末尾说明。

    /// <summary>合成测试音频：16bit/44.1kHz/双声道正弦波，独立临时目录，用完即删。</summary>
    private static string WriteToneWav(double seconds, int rate = 44100, int channels = 2)
    {
        var dir = Path.Combine(Path.GetTempPath(), "jz-audio-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "tone.wav");

        using var writer = new WaveFileWriter(path, new WaveFormat(rate, 16, channels));
        var frames = (int)(seconds * rate);
        var buffer = new float[channels * 4096];
        var written = 0;
        while (written < frames)
        {
            var n = Math.Min(4096, frames - written);
            for (var i = 0; i < n; i++)
            {
                var v = (float)(0.5 * Math.Sin(2 * Math.PI * 440.0 * (written + i) / rate));
                for (var c = 0; c < channels; c++) buffer[i * channels + c] = v;
            }
            writer.WriteSamples(buffer, 0, n * channels);
            written += n;
        }
        return path;
    }

    /// <summary>无音源时的 Seek 只改 _position：Time 立即变成定位值，且不随墙钟走。</summary>
    [Fact]
    public void SeekWithoutAudioSourceMovesTimeButClockStaysFrozen()
    {
        using var driver = new AudioClockDriver();

        driver.Seek(15.5);
        Assert.Equal(15.5, driver.Time, 1);

        Thread.Sleep(150);
        Assert.Equal(15.5, driver.Time, 1); // 没有源就不该自己爬
        Assert.False(driver.IsPlaying);

        // 无源时只做下限保护（项目时长未定），上界不截到 Duration
        driver.Seek(9999);
        Assert.Equal(9999, driver.Time, 1);
    }

    /// <summary>加载失败（文件不存在）时：返回 false、Error 保持 null、Duration 为 0。</summary>
    [Fact]
    public void LoadAudioMissingFileFailsQuietly()
    {
        using var driver = new AudioClockDriver();

        var ok = driver.LoadAudio(Path.Combine(Path.GetTempPath(), "jz-no-such-audio-" + Guid.NewGuid() + ".wav"));

        Assert.False(ok);
        // Error 的语义是「播放中设备故障」，加载失败只记日志，不占 Error
        Assert.Null(driver.Error);
        Assert.Equal(0, driver.Duration);
        Assert.Equal(0, driver.Time, 1);
    }

    /// <summary>加载合法音频后：Time 归零、Duration 与文件时长吻合、仍未播放。</summary>
    [Fact]
    public void LoadAudioResetsClockToZero()
    {
        if (WaveOut.DeviceCount == 0) return; // 无输出设备：Init 会失败，语义无意义

        var wav = WriteToneWav(2.0);
        try
        {
            using var driver = new AudioClockDriver();

            Assert.True(driver.LoadAudio(wav));
            Assert.Null(driver.Error);
            Assert.Equal(2.0, driver.Duration, 3);
            Assert.Equal(0, driver.Time, 3); // 加载成功必须归零，不是接着上次的位置
            Assert.False(driver.IsPlaying);

            // 有源后 Time 仍不自己爬（只有 Play 才起设备钟）
            Thread.Sleep(120);
            Assert.Equal(0, driver.Time, 3);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(wav)!, true);
        }
    }

    /// <summary>
    /// 加载后 Seek 落到解码器上：Time 与定位值一致、且停在未播放态（缓冲冲刷只有播放中才做）。
    /// 播放中 Seek 会把 _playBase 也搬过去并停起一次，那条路径需要真实设备播放，见末尾说明。
    /// </summary>
    [Fact]
    public void SeekAfterLoadPositionsReaderWithoutPlaying()
    {
        if (WaveOut.DeviceCount == 0) return;

        var wav = WriteToneWav(2.0);
        try
        {
            using var driver = new AudioClockDriver();
            Assert.True(driver.LoadAudio(wav));

            driver.Seek(0.7);
            Assert.Equal(0.7, driver.Time, 3);
            Assert.False(driver.IsPlaying);

            // 越界截到 [0, Duration]，不会把 _position 甩到文件外
            driver.Seek(99);
            Assert.Equal(driver.Duration, driver.Time, 3);
            driver.Seek(-5);
            Assert.Equal(0, driver.Time, 3);

            Thread.Sleep(120);
            Assert.Equal(0, driver.Time, 3); // 定位后依然不自己爬
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(wav)!, true);
        }
    }

    // 未覆盖的行为（不是漏测，是无法在不真实播放的前提下构造）：
    // 1. Play()/Pause()/播放中 Seek：WaveOut.Play 会启动播放线程并向真实设备出声，
    //    waveOutOpen 在没有输出设备的环境（CI）直接抛 MmException，Play 连起来跑会挂也会扰民。
    // 2. Time 在播放中的单调性、GetPosition 按 IEEE Float 格式换算（实测坑：按源 16bit 换算会快一倍）：
    //    两者都要等设备真的吐字节才有读数，造不出稳定的确定性断言。
    // 3. OnStopped 三路分流（fault / _stopRequested / 播完归位）：靠的是播放线程回调用例，
    //    同样需要真实播放，且天然带时序抖动。
    // 这些行为目前只由实机手验保证。
}
