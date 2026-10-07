// layoutsC 表现包（P11LayoutsC）冒烟：注册表完整性 + 每条 layout 的 PreviewPlan + Renderer 一帧 +
// 直连 Env 的 Draw。
//
// 约定（README「宿主接入须知」）：交给 Renderer.Frame 的 SKSurface 按尺寸保留、绝不 Dispose——
// 引擎整帧缓存会持有该画面的 SKImage 快照跨帧复用，面被释放后缓存悬垂。这里把面留在列表里不销毁。
//
// 注册表是进程级全局状态：本类的 Install 会改变并行测试类的 layout 抽取池，故本集合 DisableParallelization
// 独占运行（同 PackLayoutsBTests 的既定做法）。注意 Install 是永久的——若在闸门测试（ParityGoldenTests
// 的黄金值由只加载 core 的 JS 侧产出）之前装上本包，抽取池变化会让对拍出现差异，那是 M5 表现包接入的
// 全局议题，不在本文件处置。

using System.Globalization;
using System.IO;
using System.Linq;
using JizuraLive.Engine;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>注册表是进程级全局状态：本包 Install 与并行测试类互斥（独占运行，代价 ~1s）。</summary>
[Collection("EngineRegistry")]
public class PackLayoutsCTests
{
    private readonly ITestOutputHelper _out;

    public PackLayoutsCTests(ITestOutputHelper output) => _out = output;

    /// <summary>源文件 frontend/src/engine/11p_layoutsC.js 的 reg(...) 键序（也是安装序）。</summary>
    private static readonly string[] Keys =
    {
        "magazine", "headlineDeck", "contents", "footnote", "proofread", "numbered", "poster", "swissGrid",
        "dictionary", "ema", "ransom", "newspaper", "vinyl", "cassette", "bookSpine", "polaroid", "stampSheet",
        "postcard", "letterPaper", "calendar", "chochin", "routeMap", "stationSign", "noren", "tanzaku",
        "omikuji", "kakejiku", "shoji", "clapper", "warningLabel", "priceTag", "nameTag", "stickyNotes", "karuta"
    };

    private const int W = 320;
    private const int H = 180;
    private const string Text = "夜明けの色を覚えてる";

    [Fact]
    public void Pack_Keys_Are_Registered_In_Order()
    {
        EngineBoot.Install();
        P11LayoutsC.Install();

        foreach (var k in Keys)
        {
            Assert.True(Reg.Layouts.ContainsKey(k), $"layout {k} 未注册");
            Assert.Equal("layoutsC", Reg.Layouts[k].Pack);
            Assert.True(Reg.LayoutOrder.Contains(k), $"layout {k} 不在 Reg.LayoutOrder");
            Assert.False(Reg.Layouts[k].Special, $"layout {k} 意外标记为 special");
        }

        // 安装按源文件顺序追加（幂等：重复 Install 不重复入 order）
        var n0 = Reg.LayoutOrder.Count;
        P11LayoutsC.Install();
        Assert.Equal(n0, Reg.LayoutOrder.Count);

        var last = Reg.LayoutOrder.SkipWhile(k => k != Keys[0]).Take(Keys.Length).ToArray();
        Assert.Equal(Keys, last);
        _out.WriteLine($"layoutsC: {Keys.Length} 条 layout 已注册；LayoutOrder 共 {n0} 条");
    }

    [Fact]
    public void Every_Layout_Plans_And_Renders_One_Frame()
    {
        var st = InstallAndStyle();
        using var renderer = new Renderer();
        var surfaces = new List<SKSurface>(); // 面不 Dispose（见文件头注释）
        var ratios = new Dictionary<string, double>();
        foreach (var key in Keys)
        {
            var plan = BuildPlan(key, st);
            var surface = SKSurface.Create(new SKImageInfo(W, H));
            Assert.NotNull(surface);
            surfaces.Add(surface);
            surface!.Canvas.Clear(new SKColor(0, 0, 0, 0));
            var t = 1.1;
            var ex = Record.Exception(() => renderer.Frame(surface.Canvas, plan, t));
            Assert.True(ex == null, $"{key}: Renderer.Frame 抛异常 {ex}");
            surface.Canvas.Flush();

            var sc = st.Schemes.Count > 0 ? st.Schemes[0] : new Scheme();
            var bg = Env.SkColor(sc.Str("bg") ?? "#000000");
            double content = 0, total = 0;
            using (var px = surface.PeekPixels()) // 不用 Snapshot（README：会触发 SkiaSharp COW 记账）
            {
                for (var y = 0; y < px.Height; y += 2)
                    for (var x = 0; x < px.Width; x += 2)
                    {
                        var c = px.GetPixelColor(x, y);
                        total++;
                        var d = Math.Max(Math.Max(Math.Abs(c.Red - bg.Red), Math.Abs(c.Green - bg.Green)),
                            Math.Abs(c.Blue - bg.Blue));
                        if (d > 24) content++;
                    }
            }
            var ratio = total > 0 ? content / total : 0;
            ratios[key] = ratio;

            // 进场/退场帧只查不抛（退场帧本就接近空，不做像素断言）
            foreach (var tx in new[] { 0.15, 3.85 })
            {
                surface.Canvas.Clear(new SKColor(0, 0, 0, 0));
                var exi = Record.Exception(() => renderer.Frame(surface.Canvas, plan, tx));
                Assert.True(exi == null, $"{key}: Renderer.Frame(t={tx}) 抛异常 {exi}");
            }
        }

        foreach (var kv in ratios)
            _out.WriteLine($"{kv.Key,-14} contentRatio={kv.Value:P2}");
        var dead = ratios.Where(kv => kv.Value <= 0.0005).Select(kv => kv.Key).ToArray();
        Assert.True(dead.Length == 0, "以下 layout 一帧全空（只有 bg）：" + string.Join(", ", dead));
    }

    /// <summary>
    /// 直连 Env 调 LayoutDef.Draw：Renderer 的 drawCut 会吞掉 layout 异常（只留日志），
    /// 只跑整帧渲染判不出 Draw 内部抛错。这里同时要求主体 bbox 非空（版式必须给出文字块）。
    /// </summary>
    [Fact]
    public void Every_Layout_Draw_Runs_Directly_On_Env()
    {
        var st = InstallAndStyle();
        var surfaces = new List<SKSurface>();
        var empty = new List<string>();
        foreach (var key in Keys)
        {
            var plan = BuildPlan(key, st);
            var cut = plan.Cuts[0];
            var surface = SKSurface.Create(new SKImageInfo(W, H));
            Assert.NotNull(surface);
            surfaces.Add(surface);
            var sc = st.Schemes.Count > 0 ? st.Schemes[0] : new Scheme();
            var env = Env.Make(surface!.Canvas, plan, cut, sc, 1.1, 1.0 / 24, 0.5, 0.0, null, "main", null, 1, 1.1, 1.1);
            var ex = Record.Exception(() => Reg.Layouts[key].Draw!(env));
            Assert.True(ex == null, $"{key}: Draw 直连抛异常 {ex}");
            if (env.Body == null) empty.Add(key);
        }
        Assert.True(empty.Count == 0, "以下 layout 的 Draw 未产出主体 bbox：" + string.Join(", ", empty));
        _out.WriteLine($"layoutsC: {Keys.Length} 条 Draw 直连通过（均产出 bbox）");
    }

    // ---------------- 共用装配 ----------------

    /// <summary>EngineBoot + 本包 Install + 冒烟字体；返回模板 plan 的风格（含 schemes）。</summary>
    private static StyleDef InstallAndStyle()
    {
        EngineBoot.Install();
        P11LayoutsC.Install();
        SmokeFonts();
        var project = Project.NewDefault();
        project.Lyrics = Text;
        project.Title = "テスト曲";
        project.Artist = "歌手";
        var template = Planner.Plan(project, null);
        Assert.NotNull(template.Style);
        return template.Style;
    }

    /// <summary>单条 layout 的最小可渲染 plan：Width/Height 就是 320×180（设计空间 = 设备空间）。</summary>
    private static Plan BuildPlan(string key, StyleDef st)
    {
        var def = Reg.Layouts[key];
        Assert.NotNull(def.Plan);
        Assert.NotNull(def.Draw);
        Assert.True(def.Fits!(8), $"{key}.fits(8) 为假");

        var rng = new Rng(J.Sid("layoutsC:" + key));
        var ctx = new LayoutCtx { Text = Text, N = 8, W = W, H = H, Dur = 3.2 };
        var p = def.Plan!(rng, ctx, st);
        Assert.NotNull(p);

        var plan = new Plan
        {
            W = W,
            H = H,
            Fps = 24,
            Duration = 4,
            StyleKey = st.Key,
            Style = st,
            Fx = new FxSettings(),
            Lang = "ja",
            Typeset = false,
        };
        plan.Cuts.Add(new Cut
        {
            Index = 0,
            Text = Text,
            LineText = Text,
            Line = 0,
            Start = 0,
            End = 4,
            Dur = 4,
            Layout = key,
            Enter = "cut",
            Exit = "cut",
            Hold = "still",
            InDur = 0.3,
            OutDur = 0.25,
            Stagger = 0.04,
            Params = p,
            Seed = 0x9E3779B9,
            Treat = "none",
            Words = new[] { "夜明け", "の", "色を", "覚えてる" },
        });
        return plan;
    }

    /// <summary>
    /// 冒烟字体兜底：仓库自托管字体是 woff2 分片（字体管线未接时 CJK 会变 .notdef 方块），
    /// 把字体目录全部条目改指系统 CJK 字体，仅本测试进程内生效（同 RenderSmoke.SmokeFonts）。
    /// </summary>
    private static void SmokeFonts()
    {
        var fam = new[] { "Yu Gothic", "Meiryo", "MS Gothic", "SimSun", "Arial" }
            .FirstOrDefault(f =>
            {
                try
                {
                    using var t = SKTypeface.FromFamilyName(f);
                    return t != null && string.Equals(t.FamilyName, f, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }) ?? "Arial";
        foreach (var kv in JFonts.Table.ToArray())
            JFonts.AddUserFont(kv.Key, kv.Value.Label, fam, kv.Value.Weight, kv.Value.Kind);
    }
}
