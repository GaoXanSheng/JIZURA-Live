// M5 表现包 B（layoutsB）冒烟：注册表语义 + planner/renderer 全链路。
// 1) Install 后 Reg.LayoutOrder 含全部 27 个 key（本包次序 = 源文件 J.register 出现序）；
// 2) 每个 layout 跑 Planner.PreviewPlan + Renderer 一帧 320×180：断言不抛且非全黑
//    （与 scheme bg 差异 > 40 的像素占比 > 0.4%）；布局 Draw 另在直连 Env 下再跑一次，
//    因为 Renderer.DrawCut 会吞掉 layout 异常（只留日志），不能只靠整帧渲染判定。
// 离屏面按宿主契约绝不 Dispose（Renderer 的帧缓存跨帧持有该画面的 SKImage 快照）。

using System.Globalization;
using System.Linq;
using JizuraLive.Engine;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 手法注册表是进程级全局状态：本类的 Install 会往 Reg 写 27 个条目，若与别的测试类并行
/// （xunit 默认按类并行），会改变并行类的 layout 抽取池。DisableParallelization 让本集合
/// 独占运行——代价是多花 ~0.5s，换来 planner/parity 类测试不被本包 Install 干扰。
/// </summary>
[Collection("EngineRegistry")]
public class PackLayoutsBTests
{
    private readonly ITestOutputHelper _out;

    public PackLayoutsBTests(ITestOutputHelper output) => _out = output;

    /// <summary>11p_layoutsB.js 的 J.register('layout', …) 出现序（27 条）。</summary>
    private static readonly string[] Keys =
    {
        "rain", "hanging", "orbit", "tunnel", "wordCloud", "bounceLine", "elastic", "crossBands",
        "stickerBomb", "neon", "keycaps", "bubbles", "slotMachine", "flipBoard", "credits",
        "zoomRepeat", "splitHalves", "columnsBig", "circleWords", "dotMatrix", "depthStack",
        "typeSpecimen", "kanjiFocus", "halfVertical", "curtain", "equalizer", "tape",
    };

    [Fact]
    public void Install_Registers_All_27_Keys_In_Source_Order()
    {
        EngineBoot.Install();
        P11LayoutsB.Install();
        P11LayoutsB.Install(); // 幂等：二次调用不改注册表

        foreach (var k in Keys)
        {
            Assert.True(Reg.Layouts.ContainsKey(k), $"layout.{k} 未注册");
            Assert.Equal("layoutsB", Reg.Layouts[k].Pack);
            Assert.False(string.IsNullOrEmpty(Reg.Layouts[k].Name), $"layout.{k} 缺 name");
            Assert.NotNull(Reg.Layouts[k].Plan);
            Assert.NotNull(Reg.Layouts[k].Draw);
        }
        Assert.Equal(Keys.Length, Keys.Distinct().Count());

        var order = Reg.LayoutOrder;
        var idx = Keys.Select(k => order.IndexOf(k)).ToList();
        Assert.DoesNotContain(-1, idx);
        for (var i = 1; i < idx.Count; i++)
            Assert.True(idx[i] > idx[i - 1], $"order 次序漂移：{Keys[i - 1]} → {Keys[i]}（{idx[i - 1]} → {idx[i]}）");
        // 幂等不会把 key 追加第二遍
        Assert.Equal(order.Count, order.Distinct().Count());

        // 元数据抽查（源文件字面量）；Name 在全量引擎表面可能已被 zh-labels 覆盖为简中
        var rainName = Reg.Layouts["rain"].Name;
        Assert.True(rainName is "文字の雨" or "文字雨", $"rain name 意外：{rainName}");
        Assert.True(Reg.Layouts["rain"].Busy);
        Assert.Equal(0.9, Reg.Layouts["rain"].W);
        Assert.Equal("safe", Reg.Layouts["hanging"].Treat);
        Assert.Equal(0.8, Reg.Layouts["hanging"].Portrait);
        Assert.Equal(false, Reg.Layouts["neon"].Treat);
        Assert.Equal(1.5, Reg.Layouts["zoomRepeat"].Emph);
        Assert.Equal(1.4, Reg.Layouts["columnsBig"].Portrait);
        Assert.Equal(1.6, Reg.Layouts["kanjiFocus"].Emph);
        Assert.False(Reg.Layouts["rain"].Fits!(0));
        Assert.True(Reg.Layouts["rain"].Fits!(8));
        Assert.True(Reg.Layouts["equalizer"].EnterBias!["pop"] != null);
    }

    [Fact]
    public void Every_Layout_PreviewPlan_And_320x180_Frame_Is_NonEmpty()
    {
        EngineBoot.Install();
        P11LayoutsB.Install();
        SmokeFonts();

        var project = Project.NewDefault();
        project.Lyrics = "夜明けの色を/覚えてる\nほどけた声が遠くで鳴った";
        using var renderer = new Renderer();

        // 面绝不 Dispose：宿主契约（引擎整帧缓存持有该画面的快照跨帧复用）
        var surface = SKSurface.Create(new SKImageInfo(320, 180));
        var canvas = surface.Canvas;
        var scale = 320.0 / Planner.DesignSize(project.Aspect).W;

        var fails = new List<string>();
        foreach (var key in Keys)
        {
            Plan plan;
            try
            {
                plan = Planner.PreviewPlan(project, "layout", key);
            }
            catch (Exception e)
            {
                fails.Add($"{key}: PreviewPlan 抛 {e.GetType().Name}: {e.Message}");
                continue;
            }
            var cut = plan.Cuts[0];
            Assert.Equal(key, cut.Layout);
            var t = cut.Start + Math.Min(cut.Dur * 0.5, 0.9);
            var schemes = plan.Style.Schemes;
            var sc = schemes.Count > 0 ? schemes[0] : new Scheme();
            var bg = Env.SkColor(sc.Str("bg") ?? "#000000");

            try
            {
                canvas.Clear(SKColors.Transparent);
                renderer.Frame(canvas, plan, t, new RenderOpts { Scale = scale });
                canvas.Flush();
            }
            catch (Exception e)
            {
                fails.Add($"{key}: Frame 抛 {e.GetType().Name}: {e.Message}");
                continue;
            }

            // 直连 Env 再跑一次布局：Renderer.DrawCut 吞异常，只有这里能确认 Draw 本体不抛
            try
            {
                var env = Env.Make(canvas, plan, cut, sc, t - cut.Start, 0,
                    0.5, 0, null, "main", null, scale, t, t - cut.Start);
                Reg.Layouts[key].Draw!(env);
                canvas.Flush();
            }
            catch (Exception e)
            {
                fails.Add($"{key}: layout.Draw 抛 {e.GetType().Name}: {e.Message}");
                continue;
            }

            // 非全黑：与 scheme bg 有实质差异的像素占比
            double content = 0, total = 0;
            using (var img = surface.Snapshot())
            using (var px = img.PeekPixels())
            {
                for (var y = 0; y < px.Height; y += 2)
                    for (var x = 0; x < px.Width; x += 2)
                    {
                        var c = px.GetPixelColor(x, y);
                        total++;
                        var d = Math.Max(Math.Max(Math.Abs(c.Red - bg.Red), Math.Abs(c.Green - bg.Green)),
                            Math.Abs(c.Blue - bg.Blue));
                        if (d > 40) content++;
                    }
            }
            var ratio = total > 0 ? content / total : 0;
            if (ratio <= 0.004)
                fails.Add($"{key}: 画面近乎空白（contentRatio={ratio.ToString("P2", CultureInfo.InvariantCulture)}）");
            else
                _out.WriteLine($"{key}: t={t.ToString("F2", CultureInfo.InvariantCulture)}s " +
                               $"contentRatio={ratio.ToString("P2", CultureInfo.InvariantCulture)} text=\"{(cut.Text ?? "").Replace("\n", "/")}\"");
        }

        Assert.True(fails.Count == 0, "layoutsB 冒烟失败：\n" + string.Join("\n", fails));
    }

    /// <summary>
    /// 冒烟字体兜底（同 RenderSmoke）：仓库自托管 woff2 分片未接，字体目录全部条目改指系统 CJK
    /// 字体，避免 .notdef 方块与 Default typeface 渲染 CJK 的原生崩溃。仅本测试进程内生效。
    /// </summary>
    private static void SmokeFonts()
    {
        var fam = new[] { "Yu Gothic", "Meiryo", "MS Gothic", "SimSun", "Arial" }
            .FirstOrDefault(f =>
            {
                try
                {
                    using var tf = SKTypeface.FromFamilyName(f);
                    return tf != null && string.Equals(tf.FamilyName, f, StringComparison.OrdinalIgnoreCase);
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
