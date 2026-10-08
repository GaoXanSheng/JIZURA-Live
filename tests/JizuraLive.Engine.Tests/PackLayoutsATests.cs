// P11LayoutsA（frontend/src/engine/11p_layoutsA.js 的 C# 移植）验收：
//   1) 注册：Install() 后 Reg.LayoutOrder 含本包全部 key，名称/pack 完整，注册顺序 = JS reg() 调用序；
//   2) plan：每个 layout 过 Planner.PreviewPlan 后 cut.params 可用，直接调 Plan 不抛且同种子可复现；
//   3) 渲染：每个 layout 各渲 1 帧到 320×180（面按宿主规矩绝不 Dispose），不抛异常且画面非全黑。

using JizuraLive.Engine;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

[Collection("EngineRegistry")]
public class PackLayoutsATests : IDisposable
{
    private readonly ITestOutputHelper _out;

    public PackLayoutsATests(ITestOutputHelper output) => _out = output;

    // 注册表是进程级静态状态：本包 Install() 会加 28 个 key，不能泄漏给别的测试
    // （ParityGoldenTests 的黄金值按「只装核心文件」的前提由 JS 侧生成，多装表现包会改变抽取结果）。
    // 每个测试方法各自 Install 一次、Dispose 时把本包新增的 key 移除，顺序表按 Remove 还原。
    private static readonly List<string> PackKeys = new();
    private static readonly object PackLock = new();

    private static void InstallPack()
    {
        lock (PackLock)
        {
            if (PackKeys.Count == 0)
            {
                var before = new HashSet<string>(Reg.Layouts.Keys);
                P11LayoutsA.Install();
                foreach (var k in Keys)
                    if (!before.Contains(k) && Reg.Layouts.ContainsKey(k))
                        PackKeys.Add(k);
            }
            else P11LayoutsA.Install();
        }
    }

    public void Dispose()
    {
        lock (PackLock)
        {
            foreach (var k in PackKeys)
            {
                Reg.Layouts.Remove(k);
                Reg.LayoutOrder.Remove(k);
            }
            PackKeys.Clear();
        }
    }

    /// <summary>本包 key（= JS 11p_layoutsA.js 的 reg() 调用序）。</summary>
    private static readonly string[] Keys =
    {
        "lowerThird", "corners", "staircase", "zigzag", "arcTop", "spiral", "gridCells", "dropCap",
        "justified", "frameBox", "bubble", "subtitleBar", "ticker", "splitScreen", "mirror", "sideways",
        "edgeFrame", "perspective", "hanko", "genkou", "panels", "filmstrip", "quote", "ruler",
        "searchBar", "chat", "notification", "ticket",
    };

    /// <summary>每帧新建的离屏面按尺寸保留、绝不 Dispose（引擎整帧缓存持有画面快照，面释放后缓存悬垂）。</summary>
    private static readonly List<SKSurface> KeepAlive = new();

    [Fact]
    public void Install_Registers_All_Keys_In_Js_Order()
    {
        EngineBoot.Install();
        InstallPack();

        foreach (var k in Keys)
        {
            Assert.True(Reg.Layouts.TryGetValue(k, out var def), $"layout.{k} 未注册");
            Assert.Equal("layoutsA", def!.Pack);
            Assert.False(string.IsNullOrWhiteSpace(def.Name), $"layout.{k} 缺 name");
            Assert.NotNull(def.Draw);
            Assert.NotNull(def.Plan);
            Assert.NotNull(def.Fits);
            Assert.Contains(k, Reg.LayoutOrder);
        }

        // 抽取候选顺序 = JS 源文件 reg() 调用序（同 pack 内严格递增）
        var idx = Keys.Select(k => Reg.LayoutOrder.IndexOf(k)).ToList();
        Assert.DoesNotContain(-1, idx);
        for (var i = 1; i < idx.Count; i++)
            Assert.True(idx[i] > idx[i - 1],
                $"注册顺序与 JS reg() 调用序不一致：{Keys[i - 1]}({idx[i - 1]}) → {Keys[i]}({idx[i]})");

        // 元数据抽查（JS 源逐字对拍的样本）
        Assert.Equal("下部テロップ", Reg.Layouts["lowerThird"].Name);
        Assert.Equal(1.1, Reg.Layouts["lowerThird"].W);
        Assert.Equal(0.9, Reg.Layouts["lowerThird"].Portrait);
        Assert.True(Reg.Layouts["lowerThird"].Fits!(20));
        Assert.False(Reg.Layouts["lowerThird"].Fits!(21));
        Assert.Equal("大きな頭文字", Reg.Layouts["dropCap"].Name);
        Assert.Equal(new[] { "editorial", "emotional", "calm" }, Reg.Layouts["dropCap"].Tags);
        Assert.Equal(1.4, Reg.Layouts["dropCap"].EnterBias!["blur"]!.Value<double>());
        Assert.Equal("safe", Reg.Layouts["justified"].Treat);
        Assert.True(Reg.Layouts["justified"].Busy);
        Assert.Equal(false, Reg.Layouts["splitScreen"].Treat);
        Assert.Equal(1.6, Reg.Layouts["splitScreen"].Emph);
        Assert.Equal(0.6, Reg.Layouts["ticket"].Portrait);
    }

    [Fact]
    public void Plan_For_Every_Layout_Is_NonNull_And_Deterministic()
    {
        EngineBoot.Install();
        InstallPack();
        var project = Project.NewDefault();

        foreach (var aspect in new[] { "16:9", "9:16" })
        {
            project.Aspect = aspect;
            foreach (var key in Keys)
            {
                var plan = Planner.PreviewPlan(project, "layout", key);
                Assert.NotEmpty(plan.Cuts);
                Assert.Equal(key, plan.Cuts[0].Layout);
                Assert.NotNull(plan.Cuts[0].Params);

                // 直接调 Plan（PreviewPlan 内部吞异常，这里要看到真异常）：同种子两跑必须逐字段相等
                var st = plan.Style;
                var text = plan.Cuts[0].Text;
                var n = text.Count(c => !char.IsWhiteSpace(c));
                var def = Reg.Layouts[key];
                JObject PlanOnce()
                {
                    var rng = J.Rng(J.H((int)J.Sid("layout:" + key), 11, 22));
                    return def.Plan!(rng, new LayoutCtx { Text = text, N = n, W = plan.W, H = plan.H, Dur = 2.4 }, st);
                }
                var p1 = PlanOnce();
                var p2 = PlanOnce();
                Assert.NotNull(p1);
                Assert.True(p1.Count > 0, $"{key}: plan 返回空对象（{aspect}）");
                Assert.True(JToken.DeepEquals(p1, p2), $"{key}: plan 两次结果不一致（RNG 顺序或非确定性源）");
                _out.WriteLine($"{aspect} layout.{key}: {p1.ToString(Newtonsoft.Json.Formatting.None)}");
            }
        }
    }

    [Fact]
    public void Every_Layout_Renders_One_Frame_NonEmpty()
    {
        EngineBoot.Install();
        InstallPack();

        var project = Project.NewDefault();
        project.Aspect = "16:9";
        using var renderer = new Renderer();
        var failures = new List<string>();
        var empty = new List<string>();

        foreach (var key in Keys)
        {
            var plan = Planner.PreviewPlan(project, "layout", key);
            var cut = plan.Cuts[0];
            var sc = plan.Style.Schemes.Count > 0 ? plan.Style.Schemes[0] : new Scheme();
            var bg = Env.SkColor(sc.Str("bg") ?? "#000000");

            // 320×180 离屏面：设计像素（plan.W×plan.H）经 opt.Scale 缩到测试面尺寸
            var surface = SKSurface.Create(new SKImageInfo(320, 180));
            KeepAlive.Add(surface);
            surface.Canvas.Clear(SKColors.Black);

            var t = 1.0; // 切分中段：pIn 已满、pOut 未起
            try
            {
                renderer.Frame(surface.Canvas, plan, t, new RenderOpts { Scale = 320.0 / plan.W });
                surface.Canvas.Flush();
            }
            catch (Exception ex)
            {
                failures.Add($"{key}: {ex.GetType().Name}: {ex.Message}");
                continue;
            }

            double content = 0, total = 0, nonBlack = 0;
            using (var px = surface.PeekPixels())
            {
                for (var y = 0; y < px.Height; y += 2)
                    for (var x = 0; x < px.Width; x += 2)
                    {
                        var c = px.GetPixelColor(x, y);
                        total++;
                        if (c.Red + c.Green + c.Blue > 24) nonBlack++;
                        var d = Math.Max(Math.Max(Math.Abs(c.Red - bg.Red), Math.Abs(c.Green - bg.Green)),
                            Math.Abs(c.Blue - bg.Blue));
                        if (d > 60) content++;
                    }
            }
            var ratio = total > 0 ? content / total : 0;
            _out.WriteLine($"layout.{key}: cut#{cut.Index} text=\"{cut.Text.Replace("\n", "/")}\" " +
                           $"bg={sc.Str("bg")} contentRatio={ratio:P2} nonBlack={nonBlack / total:P2}");
            if (nonBlack <= 0) empty.Add($"{key}: 整帧全黑");
            if (ratio <= 0.002) empty.Add($"{key}: 与背景差异像素占比 {ratio:P2} <= 0.2%（版式没画出东西？）");
        }

        Assert.True(failures.Count == 0, "渲染抛异常：\n" + string.Join("\n", failures));
        Assert.True(empty.Count == 0, string.Join("\n", empty));
    }

}
