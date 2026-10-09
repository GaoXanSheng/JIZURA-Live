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

    // ---------------- 1b. 水平居中 ----------------

    /// <summary>
    /// 水平居中回归：字幕块（主行 + 注音行）应落在画面水平中线附近。
    /// 原有契约测试只验「顶部无内容/底部有内容」，横向偏移发现不了。
    /// 用与编辑器默认一致的配置（Content=Trans、Font=sans、OffX=0）复现用户的通道工况。
    /// </summary>
    [Fact]
    public void 字幕通道_水平居中()
    {
        EngineBoot.Install();
        var cfg = new SubtitleConfig
        {
            Content = SubContent.Trans, Pos = SubPos.Bottom, Size = 4.5, Stroke = 4, Font = "sans"
        };
        var lines = new List<KrcLine>
        {
            new(2000, 3000, "So she tries to cover up her pain",
                new List<KrcWord> { new(2000, 3000, "So she tries to cover up her pain") })
        };
        var layer = new SubtitleLayer(lines, cfg, new[] { "所以她在尝试掩饰痛苦" });

        const int W = 1920, H = 1080;
        using var surface = SKSurface.Create(new SKImageInfo(W, H, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        var stats = layer.Render(surface.Canvas, 2.5, W, H);
        surface.Canvas.Flush();
        Assert.True(stats.Drawn);

        using var img = surface.Snapshot();
        int x0 = int.MaxValue, x1 = -1, y0 = int.MaxValue, y1 = -1;
        using (var px = img.PeekPixels())
            for (var y = 0; y < H; y += 2)
                for (var x = 0; x < W; x += 2)
                    if (px.GetPixelColor(x, y).Alpha > 0)
                    {
                        if (x < x0) x0 = x;
                        if (x > x1) x1 = x;
                        if (y < y0) y0 = y;
                        if (y > y1) y1 = y;
                    }

        var center = (x0 + x1) / 2.0;
        _out.WriteLine($"ink bbox x=[{x0},{x1}] y=[{y0},{y1}] center={center}（期望 {W / 2}）");
        Assert.InRange(center, W / 2 - 40, W / 2 + 40);
    }

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

        // 字幕样式改动：NotifySubtitleCfgChanged 也要递增版本号，触发暂停态与 Spout 通道重绘
        var v2 = src.SubtitleVersion;
        preview.NotifySubtitleCfgChanged();
        Assert.True(src.SubtitleVersion > v2);
    }

    // ---------------- 4. 无 KRC 时从 plan 行合成（网易 LRC / 手动粘贴 / 重启恢复工程） ----------------

    /// <summary>
    /// 回归：Spout 字幕通道曾经只认 KRC 逐字数据，重启恢复工程（KRC 不落盘）后通道一直是空的，
    /// 而预览叠加与导出烤入各自有一份 plan 合成 fallback。合成逻辑现在收进 SubtitleLayer.FromPlan，
    /// 三条路共用。这里钉住合成结果的形状与「mainLine=true 时主行也能画」。
    /// </summary>
    [Fact]
    public void FromPlan_无KRC时合成字幕行_主行可画且注音随行()
    {
        EngineBoot.Install();
        var project = Project.NewDefault();
        project.Lyrics = "[00:02.00]夜明けの色を覚えてる|yoromeki　黎明之色\n"
                         + "[00:06.00]ほどけた声が／遠くで鳴った|hodoketa koe　解开的声音\n";
        var plan = Planner.Plan(project, null);

        var synth = SubtitleLayer.FromPlan(plan);
        Assert.NotNull(synth);
        Assert.Equal(2, synth!.Value.Lines.Count);
        // 整行合成一个「词」：mainLine=true 的 Spout 通道 / 纯字幕导出要能画出主行
        Assert.Single(synth.Value.Lines[0].Words);
        Assert.Equal("夜明けの色を覚えてる", synth.Value.Lines[0].Words[0].Text);
        Assert.Equal("yoromeki　黎明之色", synth.Value.Notes[0]);

        // 主行 + 注音行都应落笔（mainLine=true，KRC 通道的默认语义）
        var layer = new SubtitleLayer(synth.Value.Lines, new SubtitleConfig { Content = SubContent.Both }, synth.Value.Notes);
        using var surface = SKSurface.Create(new SKImageInfo(1920, 1080, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        var stats = layer.Render(surface.Canvas, 2.5, 1920, 1080);
        Assert.True(stats.MainRows >= 1, "无 KRC 逐字时主行也应画出来");
        Assert.True(stats.SubRows >= 1, "注音行也应画出来");

        // 工程歌词行没有注音内容 → null（调用方退化为不画）
        project.Lyrics = "[00:02.00]夜明けの色を覚えてる\n";
        Assert.Null(SubtitleLayer.FromPlan(Planner.Plan(project, null)));
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
