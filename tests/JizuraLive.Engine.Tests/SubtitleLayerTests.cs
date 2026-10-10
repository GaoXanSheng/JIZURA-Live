using JizuraLive.App.Cloud;
using JizuraLive.Engine;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 字幕层纯渲染测试（完全离线，无 Env/Plan）：3 行样例 KRC（含逐字时间与间奏）→
/// 640×360 渲染的非空/统计断言、行切换边界（空隙不画 vs HoldLastLine 保持上一句）、
/// 整行单色（同一行内输出与时刻无关）、配置项（内容模式/字号/底板/垂直偏移）生效。
/// 画布底统一清成中灰 #808080：白字、黑描边、半透明黑底板都与之拉开可测的差。
/// </summary>
public class SubtitleLayerTests
{
    private readonly ITestOutputHelper _out;

    public SubtitleLayerTests(ITestOutputHelper output) => _out = output;

    // ---------------- 样例：3 行 KRC（行0 首词留 0.5s 起唱空拍，行1 间奏，行2 两词） ----------------

    private static List<KrcLine> SampleLines() => new()
    {
        new KrcLine(2000, 3000, "你好世界", new List<KrcWord>
        {
            new(2500, 1000, "你"),
            new(3500, 1000, "好"),
            new(4500, 500, "世界")
        }),
        new KrcLine(6000, 4000, "", new List<KrcWord>()), // 间奏空行
        new KrcLine(10000, 3000, "再见", new List<KrcWord>
        {
            new(10000, 1500, "再"),
            new(11500, 1500, "见")
        })
    };

    /// <summary>JIZURA LRC 注音串（"roma　trans"，Krc.KrcToLrc 的产物形态）。</summary>
    private static readonly string[] Notes =
    {
        "nihao shijie　你好世界（翻）",
        "",
        "zaijian　再见（翻）"
    };

    private static SubtitleConfig Cfg() => new()
    {
        Content = SubContent.Both,
        Pos = SubPos.Bottom,
        Size = 4.5,
        Stroke = 4,
        Color = "#ffffff",
        StrokeColor = "#000000",
        Bar = true
        // 字体默认 "sans"（黑体无衬线）/ HoldLastLine=false 是默认值
    };

    private const int W = 640;
    private const int H = 360;

    // ---------------- 渲染与像素统计帮手 ----------------

    /// <summary>渲染一帧到中灰底上，返回 (ink, bbox, stats)。ink = 与灰底差 > 40 的像素数。</summary>
    private static (long Ink, int X0, int Y0, int X1, int Y1, SubtitleLayer.FrameStats Stats) Shot(
        SubtitleLayer layer, double t)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        surface.Canvas.Clear(new SKColor(128, 128, 128));
        var stats = layer.Render(surface.Canvas, t, W, H);
        surface.Canvas.Flush();
        using var img = surface.Snapshot();
        var (ink, x0, y0, x1, y1) = Scan(img);
        return (ink, x0, y0, x1, y1, stats);
    }

    private static (long Ink, int X0, int Y0, int X1, int Y1) Scan(SKImage img)
    {
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        long ink = 0;
        using var px = img.PeekPixels();
        for (var y = 0; y < px.Height; y++)
            for (var x = 0; x < px.Width; x++)
            {
                var c = px.GetPixelColor(x, y);
                if (Math.Abs(c.Red - 128) > 40 || Math.Abs(c.Green - 128) > 40 || Math.Abs(c.Blue - 128) > 40)
                {
                    ink++;
                    if (x < x0) x0 = x;
                    if (x > x1) x1 = x;
                    if (y < y0) y0 = y;
                    if (y > y1) y1 = y;
                }
            }

        return (ink, x0, y0, x1, y1);
    }

    /// <summary>两帧隔点采样的差异像素数（0 = 逐位一致）。</summary>
    private static long Diff(SubtitleLayer a, double ta, SubtitleLayer b, double tb)
    {
        static List<SKColor> Sample(SubtitleLayer l, double t)
        {
            using var surface = SKSurface.Create(new SKImageInfo(W, H));
            surface.Canvas.Clear(new SKColor(128, 128, 128));
            l.Render(surface.Canvas, t, W, H);
            surface.Canvas.Flush();
            using var img = surface.Snapshot();
            using var px = img.PeekPixels();
            var list = new List<SKColor>();
            for (var y = 0; y < px.Height; y += 2)
                for (var x = 0; x < px.Width; x += 2)
                    list.Add(px.GetPixelColor(x, y));
            return list;
        }

        var sa = Sample(a, ta);
        var sb = Sample(b, tb);
        long d = 0;
        for (var i = 0; i < sa.Count; i++)
            if (sa[i].Red != sb[i].Red || sa[i].Green != sb[i].Green || sa[i].Blue != sb[i].Blue)
                d++;
        return d;
    }

    // ---------------- 用例 ----------------

    [Fact]
    public void NotesFromPlan_配合_mainLineFalse_注音行可出画面()
    {
        EngineBoot.InstallPacks();
        var p = Project.NewDefault();
        p.Lyrics = "字幕测试ABC|zimu　测试翻译";
        p.Res = 360;
        p.Fps = 12;
        p.Timing.Snap = false;
        var plan = Planner.Plan(p, null);

        var lines = new List<JizuraLive.App.Cloud.KrcLine>
        {
            new(0, 5000, "字幕测试ABC",
                new[] { new JizuraLive.App.Cloud.KrcWord(0, 2000, "字幕测试"), new JizuraLive.App.Cloud.KrcWord(2000, 3000, "ABC") }),
        };

        var notes = SubtitleLayer.NotesFromPlan(lines, plan);
        Assert.NotNull(notes);
        Assert.Equal("zimu　测试翻译", notes![0]);

        var layer = new SubtitleLayer(lines, new SubtitleConfig { Content = SubContent.Both }, notes, mainLine: false);
        using var surface = SKSurface.Create(new SKImageInfo(640, 360));
        surface.Canvas.Clear(SKColors.Black);
        var stats = layer.Render(surface.Canvas, 0, 640, 360);
        Assert.True(stats.Drawn);
        Assert.Equal(0, stats.MainRows);
        Assert.Equal(2, stats.SubRows);
    }

    [Fact]
    public void 渲染_样例行_非空与统计()
    {
        var layer = new SubtitleLayer(SampleLines(), Cfg(), Notes);
        var (ink, x0, y0, x1, y1, st) = Shot(layer, 2.5);

        // t=2.5s：行0 [2000,5000) 活动，整行单色（词时间不进配色）
        Assert.True(st.Drawn);
        Assert.True(st.LineIndex == 0);
        Assert.Equal(3, st.Words);
        Assert.Equal(1, st.MainRows); // "你好世界" 4 字 × 16.2px 远小于 maxW=W*0.86 → 不折行
        Assert.Equal(2, st.SubRows); // Both → roma 行 + trans 行

        Assert.True(ink > 200, $"ink={ink}，画面近乎空白");
        Assert.InRange(y0, 0, H - 1);
        Assert.InRange(y1, 1, H);
        Assert.True(x1 > x0);
        _out.WriteLine($"t=2.5: ink={ink} box=({x0},{y0})-({x1},{y1}) rows={st.MainRows}+{st.SubRows}");
    }

    [Fact]
    public void 行间空隙_默认不画_Hold保持上一句()
    {
        var holdOff = new SubtitleLayer(SampleLines(), Cfg(), Notes);

        var gap = Shot(holdOff, 5.5); // 行0 已结束、行1（间奏）未开始
        Assert.True(gap.Stats.LineIndex == null);
        Assert.False(gap.Stats.Drawn);
        Assert.Equal(0, gap.Ink);

        var inter = Shot(holdOff, 7.0); // 间奏行 [6000,10000) 活动但无文本
        Assert.False(inter.Stats.Drawn);
        Assert.Equal(0, inter.Ink);

        var cfgHold = Cfg();
        cfgHold.HoldLastLine = true;
        var holdOn = new SubtitleLayer(SampleLines(), cfgHold, Notes);

        var kept1 = Shot(holdOn, 5.5); // 行间空隙 → 保持上一句
        Assert.True(kept1.Stats.LineIndex == 0);
        Assert.True(kept1.Stats.Drawn);
        Assert.True(kept1.Ink > 200);

        var kept2 = Shot(holdOn, 7.0); // 间奏也不吃掉上一句
        Assert.True(kept2.Stats.LineIndex == 0);
        Assert.True(kept2.Stats.Drawn);

        var before = Shot(holdOn, 1.0); // 首行之前：保持也无处可保
        Assert.True(before.Stats.LineIndex == null);
        Assert.Equal(0, before.Ink);
    }

    [Fact]
    public void 单色渲染_同一行内输出与时刻无关()
    {
        var layer = new SubtitleLayer(SampleLines(), Cfg(), Notes);

        // 字幕整行单色（不支持已唱/未唱逐字高亮）：t 只决定选哪一行，行内任意时刻逐位一致
        Assert.True(Diff(layer, 2.2, layer, 2.5) == 0, "行内不同时刻应逐位一致");
        Assert.True(Diff(layer, 2.5, layer, 4.8) == 0, "行内不同时刻应逐位一致");

        // 换行（行0 → 行2）画面必须有差
        Assert.True(Diff(layer, 2.5, layer, 10.5) > 0, "换行后画面应不同");
    }

    [Fact]
    public void 配置_内容模式与注音行数()
    {
        var lines = SampleLines();

        var off = new SubtitleLayer(lines, new SubtitleConfig { Content = SubContent.Off, Bar = true }, Notes);
        var rOff = Shot(off, 2.5);
        Assert.False(rOff.Stats.Drawn);
        Assert.Equal(0, rOff.Ink); // content=off 整层不画（TS SUB_DEFAULT，Bar 也不画）

        var cfgT = Cfg();
        cfgT.Content = SubContent.Trans;
        var rT = Shot(new SubtitleLayer(lines, cfgT, Notes), 2.5);
        Assert.True(rT.Stats.Drawn);
        Assert.Equal(1, rT.Stats.SubRows); // 只翻译
        Assert.Equal(1, rT.Stats.MainRows);

        var cfgR = Cfg();
        cfgR.Content = SubContent.Roma;
        var rR = Shot(new SubtitleLayer(lines, cfgR, Notes), 2.5);
        Assert.Equal(1, rR.Stats.SubRows); // 只音译

        var cfgB = Cfg();
        var rB = Shot(new SubtitleLayer(lines, cfgB, Notes), 2.5);
        Assert.Equal(2, rB.Stats.SubRows); // 双行（roma + trans 拆行）
    }

    [Fact]
    public void 配置_只画注音行_主行不出画面()
    {
        // 预览叠加与导出烤入用 mainLine:false：主歌词由引擎自己画（叠主行会重影），只画注音/翻译行
        var notesOnly = new SubtitleLayer(SampleLines(), Cfg(), Notes, mainLine: false);
        var r = Shot(notesOnly, 2.5);
        Assert.True(r.Stats.Drawn);
        Assert.Equal(0, r.Stats.MainRows); // 主行不参与排版
        Assert.Equal(2, r.Stats.SubRows);  // Both → roma + trans 两行照画
        Assert.True(r.Ink > 100, $"ink={r.Ink}，注音行应有笔画");

        // 与整块（mainLine=true）的帧确实不同（少一整行主行）
        var full = new SubtitleLayer(SampleLines(), Cfg(), Notes);
        Assert.True(Diff(notesOnly, 2.5, full, 2.5) > 0, "只画注音的行块应与整块不同");

        // 没有注音的工程（注音行来自 plan 的 cut 注音）：mainLine:false 时整层无可画 → 不出画面
        var noNotes = new SubtitleLayer(SampleLines(), Cfg(), null, mainLine: false);
        var rn = Shot(noNotes, 2.5);
        Assert.False(rn.Stats.Drawn);
        Assert.Equal(0, rn.Ink);
    }

    [Fact]
    public void 配置_字号_底板_垂直偏移生效()
    {
        var baseLayer = new SubtitleLayer(SampleLines(), Cfg(), Notes);
        var s1 = Shot(baseLayer, 4.8); // 白字 + 注音 + 底板

        // 字号翻倍 → 字块显著变高（不折行，行数不变）
        var cfgBig = Cfg();
        cfgBig.Size = 9;
        var s2 = Shot(new SubtitleLayer(SampleLines(), cfgBig, Notes), 4.8);
        Assert.Equal(1, s2.Stats.MainRows);
        Assert.Equal(2, s2.Stats.SubRows);
        var h1 = s1.Y1 - s1.Y0;
        var h2 = s2.Y1 - s2.Y0;
        Assert.True(h2 > h1 * 13 / 10, $"字号 9 的块高 {h2} 应明显大于字号 4.5 的 {h1}");

        // 底板条比纯文字框更宽（左右各 padX=fontPx*0.5）
        var cfgNoBar = Cfg();
        cfgNoBar.Bar = false;
        var s3 = Shot(new SubtitleLayer(SampleLines(), cfgNoBar, Notes), 4.8);
        Assert.True(s3.X1 - s3.X0 < s1.X1 - s1.X0,
            $"无边框宽 {s3.X1 - s3.X0} 应小于带底板宽 {s1.X1 - s1.X0}");

        // offY=-25（画面高的 -25% = -90px）→ 字块整体上移
        var cfgUp = Cfg();
        cfgUp.OffY = -25;
        var s4 = Shot(new SubtitleLayer(SampleLines(), cfgUp, Notes), 4.8);
        Assert.True(s4.Y0 < s1.Y0 - 50, $"上移后顶 {s4.Y0} 应比默认 {s1.Y0} 高出 50px 以上");
    }
}
