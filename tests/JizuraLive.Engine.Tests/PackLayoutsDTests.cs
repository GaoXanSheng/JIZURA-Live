// 表现包 layoutsD 冒烟：注册表语义（order 含全部 key / pack / plan+draw 齐备）
// + 每个 layout 走一遍 EngineBoot → PreviewPlan（320×180）→ Renderer 一帧，不抛且画面非全黑。
//
// 约定（src/Engine/README.md 宿主接入须知）：交给 Renderer.Frame 的 SKSurface 必须按尺寸保留、
// 绝不 Dispose（引擎整帧缓存持有该画面的 SKImage 快照跨帧复用），本测试因此不释放渲染面。

using JizuraLive.Engine;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

[Collection("EngineRegistry")]
public class PackLayoutsDTests
{
    private readonly ITestOutputHelper _out;

    public PackLayoutsDTests(ITestOutputHelper output) => _out = output;

    /// <summary>JS 11p_layoutsD.js 里 reg(...) 的 34 个 key，顺序即注册顺序。</summary>
    public static readonly string[] Keys =
    {
        "cube", "cylinder", "flipCards", "accordion", "flag", "ribbon", "pendulum", "pile", "blocks",
        "balloons", "magnets", "tiles", "bulbs", "ledScroll", "billboard", "crowdBubbles", "crossword",
        "wordSearch", "puzzle", "shadowPlay", "kaleido", "dominoes", "burst", "fisheye", "wall",
        "origami", "zipper", "sliceStack", "glitchGrid", "mosaicTiles", "maskReveal", "contour",
        "halftoneBig", "stencil",
    };

    public static IEnumerable<object[]> KeyRows() => Keys.Select(k => new object[] { k });

    [Fact]
    public void Order_含全部34个key且pack正确()
    {
        P11LayoutsD.Install();
        Assert.Equal(34, Keys.Length);
        foreach (var k in Keys)
        {
            Assert.True(Reg.Layouts.ContainsKey(k), $"{k} 未注册");
            var def = Reg.Layouts[k];
            Assert.Equal("layoutsD", def.Pack);
            Assert.Contains(k, Reg.LayoutOrder);
            Assert.False(string.IsNullOrEmpty(def.Name), $"{k} 缺 name");
            Assert.NotNull(def.Plan);
            Assert.NotNull(def.Draw);
            Assert.False(def.Special, $"{k} 不应是 special");
        }
        // 幂等：重复 Install 不追加。全套测试并行跑（其它表现包也往同一注册表写），
        // 因此只断言本包的相对顺序与条目数，不按全局 count/相邻性断言。
        P11LayoutsD.Install();
        var present = Reg.LayoutOrder.Where(Keys.Contains).ToList();
        Assert.Equal(Keys, present);
    }

    [Theory]
    [MemberData(nameof(KeyRows))]
    public void Layout_渲染一帧320x180_不抛且非全黑(string key)
    {
        EngineBoot.Install();
        P11LayoutsD.Install();

        var plan = PreviewPlan(key);
        // 面按尺寸保留、不 Dispose（README 的宿主契约）
        var surface = SKSurface.Create(new SKImageInfo(plan.W, plan.H));
        surface.Canvas.Clear(SKColors.White);

        using var renderer = new Renderer();
        var t = plan.Cuts[0].Start + plan.Cuts[0].Dur * 0.5;
        renderer.Frame(surface.Canvas, plan, t);
        surface.Canvas.Flush();

        using var img = surface.Snapshot();
        var sc = plan.Style.Schemes[0];
        var bg = Env.SkColor(sc.Str("bg") ?? "#000000");
        double content = 0, total = 0, nonBlack = 0;
        using (var px = img.PeekPixels())
            for (var y = 0; y < px.Height; y += 2)
                for (var x = 0; x < px.Width; x += 2)
                {
                    var c = px.GetPixelColor(x, y);
                    total++;
                    var d = Math.Max(Math.Max(Math.Abs(c.Red - bg.Red), Math.Abs(c.Green - bg.Green)), Math.Abs(c.Blue - bg.Blue));
                    if (d > 40) content++;
                    if (c.Red > 24 || c.Green > 24 || c.Blue > 24) nonBlack++;
                }
        var ratio = total > 0 ? content / total : 0;
        _out.WriteLine($"{key}: bg={sc.Str("bg")} contentRatio={ratio:P2} nonBlack={nonBlack / total:P2}");
        Assert.True(total > 0, $"{key}: 采样像素为 0");
        Assert.True(nonBlack > 0, $"{key}: 整帧全黑");
        Assert.True(ratio > 0.002, $"{key}: 与背景差异 >40 的像素占比 {ratio:P2} ≤ 0.2%（画面基本空白）");
    }

    /// <summary>
    /// UI 缩略图同款最小 plan：单刀、居中时段、Enter/Exit 都用 cut（不抢画面），
    /// params 走该 layout 自己的 Plan（键名与 planner 一致）。
    /// </summary>
    private static Plan PreviewPlan(string key)
    {
        var style = JStyles.ResolveStyle(Project.NewDefault());
        const string text = "夜明けの色を覚えてる";
        var n = Planner.Segments(text).Count;
        var plan = new Plan
        {
            W = 320,
            H = 180,
            Fps = 24,
            Duration = 4,
            Style = style,
            StyleKey = style.Key,
            Seed = 20260922,
            Fx = new FxSettings(),
            Lang = "ja",
        };
        var cut = new Cut
        {
            Index = 0,
            Text = text,
            LineText = text,
            Line = 0,
            Start = 0.5,
            End = 3.0,
            Dur = 2.5,
            Layout = key,
            Enter = "cut",
            Exit = "cut",
            Hold = "still",
            InDur = 0.3,
            OutDur = 0.25,
            Stagger = 0.04,
            Seed = 4242,
            Words = text.Split(' '),
        };
        var def = Reg.Layouts[key];
        cut.Params = def.Plan!(new Rng(4242), new LayoutCtx { Text = text, N = n, W = plan.W, H = plan.H, Dur = cut.Dur }, style)
                     ?? new JObject();
        plan.Cuts.Add(cut);
        plan.Lines.Add(new PlanLine { Index = 0, Src = 0, Text = text, Start = cut.Start, End = cut.End, VisEnd = cut.End });
        return plan;
    }

}
