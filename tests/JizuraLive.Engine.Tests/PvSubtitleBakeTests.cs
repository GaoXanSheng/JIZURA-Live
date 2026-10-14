// 字幕烤入主通道（PV「字幕压人物之上」）的端到端接线：
//   起一条真 SpoutOut（自己的发送端名，避免与别的测试/程序撞名），主通道按设置收字幕；
//   用 SpoutIn 把主通道收回来，量画面下部那条字幕带里有没有字 → 开/关 SubtitleOnTop 对比。
// 这条盯的是 SpoutOut 里「Frame() 之后把字幕层画到出图面最上层」的那一步（Renderer 管不到它）。
//
// 源用测试自带的最小 ISpoutSource（plan + 一条 KRC 行），不依赖云服务与音频。
// （类名带 Pv 前缀：SubtitleLayerTests 已覆盖纯字幕层渲染，这里专测烤进主通道的路径。）

using System.Threading;
using JizuraLive.App.Cloud;
using JizuraLive.Engine;
using JizuraLive.Spout;
using SharpDX.Direct3D11;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;
using Device = SharpDX.Direct3D11.Device;

namespace JizuraLive.Engine.Tests;

[Collection("EngineRegistry")]
public class PvSubtitleBakeTests
{
    private const string Sender = "JIZURA-TEST-BAKE";

    private readonly ITestOutputHelper _out;

    public PvSubtitleBakeTests(ITestOutputHelper o) => _out = o;

    [Fact]
    public void Main_Channel_Bakes_Subtitles_When_Pv_Asks_For_It()
    {
        EngineBoot.InstallPacks();
        var on = BottomTextRatio(subtitleOnTop: true);
        var off = BottomTextRatio(subtitleOnTop: false);
        _out.WriteLine($"下部字幕带亮像素占比：烤入 {on:P2}，不烤 {off:P2}");

        Assert.True(on > 0.002, $"烤字幕开着时主通道下部没有字幕像素（{on:P2}）");
        Assert.True(on > off * 2.5, $"开/关字幕烤入的差异太小（{on:P2} vs {off:P2}）：字幕没画上去？");
    }

    /// <summary>起管线 → 收主通道一帧 → 返回「下部字幕带」亮像素占比（0~1）。</summary>
    private double BottomTextRatio(bool subtitleOnTop)
    {
        using var device = new Device(SharpDX.Direct3D.DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        var src = new FakeSource();
        using var spout = new SpoutOut();
        var name = Sender + (subtitleOnTop ? "-ON" : "-OFF");
        Assert.True(spout.Start(src, new SpoutOutOptions
        {
            SenderName = name,
            Width = 960,
            Height = 540,
            Fps = 20,
            Pv = new PvOptions { Enabled = false, SubtitleOnTop = subtitleOnTop },
        }));

        using var sink = new SpoutIn(device);
        SKBitmap? copy = null;
        try
        {
            var got = false;
            for (var i = 0; i < 60 && !got; i++)
            {
                got = sink.Pump(true, name, premultiplied: false);
                if (!got) Thread.Sleep(100);
            }

            Assert.True(got, "主通道没收上来");
            Assert.True(sink.TryCopyLatest(ref copy, out _));

            // 字幕排版在底部 5.5% 起排：取底部 12% 那条带量「亮字」
            long bright = 0, n = 0;
            var y0 = (int)(copy!.Height * 0.88);
            for (var y = y0; y < copy.Height; y += 2)
                for (var x = 0; x < copy.Width; x += 2)
                {
                    var c = copy.GetPixel(x, y);
                    n++;
                    if (c.Red > 200 && c.Green > 200 && c.Blue > 200) bright++;
                }

            return n == 0 ? 0 : bright / (double)n;
        }
        finally
        {
            copy?.Dispose();
            spout.Stop();
        }
    }

    /// <summary>最小 Spout 源：一条覆盖当前时刻的 KRC 行（不需要音频/云）。</summary>
    private sealed class FakeSource : ISpoutSource
    {
        private static readonly KrcWord[] Words =
        [
            new(0, 600, "夜"),
            new(600, 600, "明"),
            new(1200, 600, "け"),
            new(1800, 600, "の"),
        ];

        public FakeSource()
        {
            var project = Project.NewDefault();
            project.Lyrics = "夜明けの色を/覚えてる";
            Plan = Planner.Plan(project, null);
            Subtitles = [new KrcLine(0, 2400, "夜明けの", Words)];
        }

        public Plan Plan { get; }
        public double CurrentTime => 0.8;
        public bool IsPlaying => true;
        public long Version => 1;
        public IReadOnlyList<KrcLine>? Subtitles { get; }
        public SubtitleConfig SubtitleConfig { get; } = new() { Content = SubContent.Both };
        public long SubtitleVersion => 1;
    }
}
