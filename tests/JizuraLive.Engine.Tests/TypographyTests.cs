using JizuraLive.App.PvStage;
using JizuraLive.Engine;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using JizuraLive.Render.Text;

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

    [Fact]
    public void TypographyLayerRendersTextOntoSurface()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) return;

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

    [Fact]
    public void AudioClockDriverControlsVirtualTimeline()
    {
        using var driver = new AudioClockDriver();
        Assert.False(driver.IsPlaying);
        Assert.Equal(0, driver.Time);

        driver.Seek(15.5);
        Assert.Equal(15.5, driver.Time, 1);

        driver.Play();
        Assert.True(driver.IsPlaying);

        driver.Pause();
        Assert.False(driver.IsPlaying);
    }
}
