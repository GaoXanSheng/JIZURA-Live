// 独立 Spout 字幕通道（<SpoutID>-LYRICS）的回归测试：
//   1. 通道渲染契约——透明底只出底部字幕条（曾经错画成引擎 front 层整帧画面）；
//   2. 注音行从 plan 的 cut 注音回填（KrcToLrc 的全角斜杠归一化）；
//   3. PreviewEngine 的字幕数据接线（Spout 泵经 ISpoutSource 读）。
using System.IO;
using JizuraLive.App;
using JizuraLive.App.Cloud;
using JizuraLive.Engine;
using JizuraLive.Spout;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

[Collection("EngineRegistry")]
public class SubtitleChannelTests
{
    private readonly ITestOutputHelper _out;

    public SubtitleChannelTests(ITestOutputHelper output) => _out = output;

    private static List<KrcLine> SampleLines() => new()
    {
        new KrcLine(2000, 3000, "夜明けの色を覚えてる", new List<KrcWord>
        {
            new(2500, 1000, "夜明けの"),
            new(3500, 1500, "色を覚えてる")
        }),
        new KrcLine(6000, 4000, "ほどけた声が/遠くで鳴った", new List<KrcWord>
        {
            new(6000, 2000, "ほどけた"),
            new(8000, 2000, "声が/遠くで鳴った")
        })
    };

    // ---------------- 1. 通道渲染契约 ----------------

    [Fact]
    public void 字幕通道_透明底_顶部无内容_底部出字幕()
    {
        EngineBoot.Install();
        var cfg = new SubtitleConfig { Content = SubContent.Both, Pos = SubPos.Bottom, Size = 4.5, Stroke = 4 };
        var layer = new SubtitleLayer(SampleLines(), cfg, new[] { "yoromeki　黎明之色", "hodoketa koe　解开的声音" });

        const int W = 1920, H = 1080;
        using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        var stats = layer.Render(surface.Canvas, 2.6, W, H); // 第一行第一词已唱、第二词未唱
        surface.Canvas.Flush();
        Assert.True(stats.Drawn, "字幕层应出画面");
        Assert.True(stats.MainRows >= 1);
        Assert.True(stats.SubRows >= 2, "双语注音应有音译 + 翻译两行");

        using var img = surface.Snapshot();
        long topInk = 0, bottomInk = 0;
        using (var px = img.PeekPixels())
        {
            for (var y = 0; y < H; y += 4)
                for (var x = 0; x < W; x += 4)
                {
                    var a = px.GetPixelColor(x, y).Alpha;
                    if (a == 0) continue;
                    if (y < 600) topInk++;
                    if (y > 800) bottomInk++;
                }
        }

        _out.WriteLine($"topInk={topInk} bottomInk={bottomInk}");
        Assert.Equal(0, topInk);      // 引擎画面/背景不得出现在字幕通道
        Assert.True(bottomInk > 0, "底部字幕条应有内容");

        var path = Path.Combine(RepoObjDir(), "subtitle-channel-contract.png");
        using (var png = img.Encode(SKEncodedImageFormat.Png, 95))
        using (var fs = File.Create(path))
            png.SaveTo(fs);
        _out.WriteLine("png → " + path);
    }

    // ---------------- 2. 注音回填（斜杠归一化） ----------------

    [Fact]
    public void NotesFromPlan_整行对齐_斜杠归一化()
    {
        EngineBoot.Install();
        var project = Project.NewDefault();
        // 第一行注音齐全；第二行 KRC 是半角 '/'，工程 LRC 里是 KrcToLrc 换过的全角 '／'
        project.Lyrics = "[00:02.00]夜明けの色を覚えてる|yoromeki　黎明之色\n"
                         + "[00:06.00]ほどけた声が／遠くで鳴った|hodoketa koe　解开的声音\n";
        var plan = Planner.Plan(project, null);
        var lines = new List<KrcLine>
        {
            new(2000, 3000, "夜明けの色を覚えてる", Array.Empty<KrcWord>()),
            new(6000, 4000, "ほどけた声が/遠くで鳴った", Array.Empty<KrcWord>())
        };

        var notes = SubtitleLayer.NotesFromPlan(lines, plan);
        Assert.NotNull(notes);
        Assert.Equal("yoromeki　黎明之色", notes![0]);
        Assert.Equal("hodoketa koe　解开的声音", notes[1]);

        // 没有注音的工程 → null（退化为只画主行）
        project.Lyrics = "[00:02.00]夜明けの色を覚えてる\n[00:06.00]ほどけた声が／遠くで鳴った\n";
        var plan2 = Planner.Plan(project, null);
        Assert.Null(SubtitleLayer.NotesFromPlan(lines, plan2));
    }

    // ---------------- 3. PreviewEngine 字幕接线 ----------------

    [Fact]
    public void PreviewEngine_字幕接线_版本递增与清空()
    {
        using var preview = new PreviewEngine();
        ISpoutSource src = preview;
        Assert.Null(src.Subtitles);
        var v0 = src.SubtitleVersion;
        Assert.Equal(SubContent.Both, src.SubtitleConfig.Content); // 编辑器默认双语，有数据即出画面

        var lines = SampleLines();
        preview.SetSubtitles(lines);
        Assert.Same(lines, src.Subtitles);
        Assert.True(src.SubtitleVersion > v0);

        var v1 = src.SubtitleVersion;
        preview.SetSubtitles(new List<KrcLine>()); // 空 = 清空（网易 LRC 防残留语义）
        Assert.Null(src.Subtitles);
        Assert.True(src.SubtitleVersion > v1);
    }

    private static string RepoObjDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JizuraLive.csproj")))
            dir = dir.Parent!;
        var obj = Path.Combine(dir?.FullName ?? AppContext.BaseDirectory, "obj");
        Directory.CreateDirectory(obj);
        return obj;
    }
}
