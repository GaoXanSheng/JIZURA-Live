// M5 typo 三包（11p_typo1/2/3.js 移植）冒烟：
// 1) Install 后三包各自注册组含其全部 key（pack = typo1/typo2/typo3，次序 = 源文件出现序）、
//    二次 Install 幂等（order 不重复不漂移）；
// 2) 每包至少 3 条代表条目跑 Planner.PreviewPlan + 直调 Draw 不抛；再走真实管线（layout 直调
//    Draw / move+treat 走 MainDraw / decor 直调 / trans 直调 TransDrawFn）断言画面非全空。
//    离屏面按宿主契约绝不 Dispose（引擎整帧缓存会持有该画面的快照跨帧复用）。
// Name 断言只查非空（zh-labels 可能在全量引擎表面覆盖为简中）。

using System.Globalization;
using System.Linq;
using JizuraLive.Engine;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

[Collection("EngineRegistry")]
public class PackTypoTests
{
    private readonly ITestOutputHelper _out;

    public PackTypoTests(ITestOutputHelper output) => _out = output;

    /// <summary>11p_typo1.js 的 reg('layout', …) 出现序（18 条）。</summary>
    private static readonly string[] Typo1Keys =
    {
        "tyKeySplit", "tyCropGiant", "tyCross", "tyBandHide", "tyRuby", "tyBaseline",
        "tyScaleSteps", "tyJustify", "tyIndexTable", "tySplitType", "tyErode", "tyVRuler",
        "tyFullTrack", "tyStatCount", "tyMargin", "tyRotBlock", "tySquare", "tyLineFocus",
    };

    /// <summary>11p_typo2.js 的 reg(g, key, …) 出现序（8 enter + 8 exit + 4 hold）。</summary>
    private static readonly (string Group, string Key)[] Typo2Keys =
    {
        ("enter", "tyKeyFirst"), ("enter", "tyLineWipe"), ("enter", "tyZoomOne"), ("enter", "tyUnderLift"),
        ("enter", "tyDotGrow"), ("enter", "tyBracketOpen"), ("enter", "tyRetype"), ("enter", "tyRubyDrop"),
        ("exit", "tyStrike"), ("exit", "tyToDot"), ("exit", "tyLineFeed"), ("exit", "tyBracketClose"),
        ("exit", "tyToIndex"), ("exit", "tyKeyLast"), ("exit", "tyUnderSink"), ("exit", "tyFoldVert"),
        ("hold", "tyKeyPulse"), ("hold", "tyReadCursor"), ("hold", "tyOutlineBlink"), ("hold", "tyTrackStep"),
    };

    /// <summary>11p_typo3.js 的 reg/trReg 出现序（6 decor + 4 treat + 2 trans）。</summary>
    private static readonly (string Group, string Key)[] Typo3Keys =
    {
        ("decor", "tyColophon"), ("decor", "tyRunningHead"), ("decor", "tyGlyphBody"),
        ("decor", "tyTextRule"), ("decor", "tyTypeScale"), ("decor", "tyBigPunct"),
        ("treat", "tyHollowKey"), ("treat", "tyHeadRules"), ("treat", "tyHeadBig"), ("treat", "tyIndexSup"),
        ("trans", "tyRuleWipe"), ("trans", "tyGridCells"),
    };

    // ---------------- 注册表语义 ----------------

    [Fact]
    public void Install_后三包各注册组含全部key且幂等()
    {
        EngineBoot.Install();
        P11Typo1.Install();
        P11Typo2.Install();
        P11Typo3.Install();
        // 幂等：二次调用不改注册表
        P11Typo1.Install();
        P11Typo2.Install();
        P11Typo3.Install();

        foreach (var k in Typo1Keys)
        {
            Assert.True(Reg.Layouts.ContainsKey(k), $"layout.{k} 未注册");
            Assert.Equal("typo1", Reg.Layouts[k].Pack);
            Assert.False(string.IsNullOrEmpty(Reg.Layouts[k].Name), $"layout.{k} 缺 name");
            Assert.NotNull(Reg.Layouts[k].Plan);
            Assert.NotNull(Reg.Layouts[k].Draw);
        }
        foreach (var (g, k) in Typo2Keys)
        {
            var dict = g == "enter" ? Reg.Enter : g == "exit" ? Reg.Exit : Reg.Hold;
            Assert.True(dict.ContainsKey(k), $"{g}.{k} 未注册");
            Assert.Equal("typo2", dict[k].Pack);
            Assert.False(string.IsNullOrEmpty(dict[k].Name), $"{g}.{k} 缺 name");
            Assert.NotNull(dict[k].Draw);
        }
        foreach (var (g, k) in Typo3Keys)
        {
            if (g == "decor")
            {
                Assert.True(Reg.Decor.ContainsKey(k), $"decor.{k} 未注册");
                Assert.Equal("typo3", Reg.Decor[k].Pack);
                Assert.False(string.IsNullOrEmpty(Reg.Decor[k].Name), $"decor.{k} 缺 name");
                Assert.NotNull(Reg.Decor[k].Draw);
            }
            else if (g == "treat")
            {
                Assert.True(Reg.Treat.ContainsKey(k), $"treat.{k} 未注册");
                Assert.Equal("typo3", Reg.Treat[k].Pack);
                Assert.False(string.IsNullOrEmpty(Reg.Treat[k].Name), $"treat.{k} 缺 name");
                Assert.NotNull(Reg.Treat[k].Draw);
            }
            else
            {
                Assert.True(Reg.Trans.ContainsKey(k), $"trans.{k} 未注册");
                Assert.Equal("typo3", Reg.Trans[k].Pack);
                Assert.False(string.IsNullOrEmpty(Reg.Trans[k].Name), $"trans.{k} 缺 name");
                Assert.NotNull(Reg.Trans[k].Draw);
            }
        }

        // order 次序 = 源文件出现序，且幂等不会追加第二遍
        var o1 = Typo1Keys.Select(k => Reg.LayoutOrder.IndexOf(k)).ToList();
        Assert.DoesNotContain(-1, o1);
        for (var i = 1; i < o1.Count; i++)
            Assert.True(o1[i] > o1[i - 1], $"layout order 次序漂移：{Typo1Keys[i - 1]} → {Typo1Keys[i]}");
        var o2 = Typo2Keys.Select(t => Reg.OrderOf(t.Group).ToList().IndexOf(t.Key)).ToList();
        Assert.DoesNotContain(-1, o2);
        for (var i = 1; i < Typo2Keys.Length; i++)
            if (Typo2Keys[i].Group == Typo2Keys[i - 1].Group)
                Assert.True(o2[i] > o2[i - 1], $"{Typo2Keys[i].Group} order 次序漂移");
        var o3 = Typo3Keys.Select(t => Reg.OrderOf(t.Group).ToList().IndexOf(t.Key)).ToList();
        Assert.DoesNotContain(-1, o3);
        for (var i = 1; i < Typo3Keys.Length; i++)
            if (Typo3Keys[i].Group == Typo3Keys[i - 1].Group)
                Assert.True(o3[i] > o3[i - 1], $"{Typo3Keys[i].Group} order 次序漂移");

        // 元数据抽查（源文件字面量；Name 只查非空——zh-labels 可能覆盖）
        Assert.Equal(1.1, Reg.Layouts["tyKeySplit"].W);
        Assert.Equal(1.4, Reg.Layouts["tyKeySplit"].Emph);
        Assert.Equal(1.3, Reg.Layouts["tyIndexTable"].Portrait);
        Assert.Equal(0.6, Reg.Layouts["tyErode"].Portrait);
        Assert.Equal(0.4, Reg.Layouts["tyMargin"].Emph);
        Assert.False(Reg.Layouts["tyKeySplit"].Fits!(1));
        Assert.True(Reg.Layouts["tyKeySplit"].Fits!(8));
        Assert.Equal(1.0, Reg.Enter["tyZoomOne"].MinDur);
        Assert.Equal(1.0, Reg.Enter["tyRetype"].MinDur);
        Assert.Equal("front", Reg.Decor["tyColophon"].Layer);
        Assert.True(Reg.Decor["tyTextRule"].Subtle);
        Assert.True(Reg.Treat["tyHollowKey"].Safe);
        Assert.Equal(0.45, Reg.Trans["tyRuleWipe"].Dur);
        Assert.NotNull(Reg.Enter["tyZoomOne"].InDur);
        Assert.NotNull(Reg.Enter["tyRetype"].InDur);
    }

    // ---------------- 代表条目：PreviewPlan + 直调 Draw 不抛、非全空 ----------------

    [Fact]
    public void 代表条目_PreviewPlan与直调Draw不抛且画面非空()
    {
        EngineBoot.Install();
        P11Typo1.Install();
        P11Typo2.Install();
        P11Typo3.Install();
        SmokeFonts();

        var proj = Project.NewDefault();
        proj.Lyrics = "夜明けの色を/覚えてる";
        var font = "gothic_black"; // 字体目录常驻 key（SmokeFonts 已把它指到系统 CJK 字体）

        // 面绝不 Dispose：宿主契约（引擎整帧缓存持有该画面的快照跨帧复用）
        var surface = SKSurface.Create(new SKImageInfo(320, 180));
        var canvas = surface.Canvas;
        // 布局在设计空间（DesignSize）里画：直调 Draw 前把画布缩到设备像素（渲染器同约定，
        // env.Scale 与画布矩阵一致）
        var scale = 320.0 / Planner.DesignSize(proj.Aspect).W;
        var fails = new List<string>();

        void CheckNonEmpty(string tag, SKColor bg, double minRatio = 0.002)
        {
            canvas.Flush();
            double content = 0, total = 0;
            using var img = surface.Snapshot();
            using var px = img.PeekPixels();
            for (var y = 0; y < px.Height; y += 2)
                for (var x = 0; x < px.Width; x += 2)
                {
                    total++;
                    var c = px.GetPixelColor(x, y);
                    var d = Math.Max(Math.Max(Math.Abs(c.Red - bg.Red), Math.Abs(c.Green - bg.Green)),
                        Math.Abs(c.Blue - bg.Blue));
                    if (d > 40) content++;
                }
            var ratio = total > 0 ? content / total : 0;
            if (ratio <= minRatio)
                fails.Add($"{tag}: 画面近乎空白（contentRatio={ratio.ToString("P3", CultureInfo.InvariantCulture)}）");
        }

        // —— typo1：layout 直调 Draw（PreviewPlan 的 cut 带该 layout 与 params）——
        foreach (var key in new[] { "tyKeySplit", "tyBandHide", "tyVRuler", "tyLineFocus" })
        {
            Plan plan;
            try
            {
                plan = Planner.PreviewPlan(proj, "layout", key);
            }
            catch (Exception e)
            {
                fails.Add($"layout.{key}: PreviewPlan 抛 {e.GetType().Name}: {e.Message}");
                continue;
            }
            var cut = plan.Cuts[0];
            Assert.Equal(key, cut.Layout);
            var t = cut.Start + Math.Min(cut.Dur * 0.5, 0.9);
            var sc = plan.Style.Schemes.Count > 0 ? plan.Style.Schemes[0] : new Scheme();
            var bg = Env.SkColor(sc.Str("bg") ?? "#000000");
            try
            {
                canvas.Clear(bg);
                canvas.Save();
                canvas.Scale((float)scale);
                var env = Env.Make(canvas, plan, cut, sc, t - cut.Start, 0, 0.5, 0, null, "main", null, scale, t, t - cut.Start);
                var ex = Record.Exception(() => Reg.Layouts[key].Draw!(env));
                Assert.Null(ex);
                Assert.True(env.Body != null, $"layout.{key}: env.Body 为空");
                canvas.Restore();
                CheckNonEmpty($"layout.{key}", bg);
            }
            catch (Exception e)
            {
                fails.Add($"layout.{key}: Draw 抛 {e.GetType().Name}: {e.Message}");
            }
        }

        // —— typo2：enter/exit/hold 直调 MoveDef.Draw（不抛），再走 MainDraw 管线（非空）——
        foreach (var (g, key) in new[] { ("enter", "tyKeyFirst"), ("exit", "tyStrike"), ("hold", "tyKeyPulse") })
        {
            Plan plan;
            try
            {
                plan = Planner.PreviewPlan(proj, g, key);
            }
            catch (Exception e)
            {
                fails.Add($"{g}.{key}: PreviewPlan 抛 {e.GetType().Name}: {e.Message}");
                continue;
            }
            var cut = plan.Cuts[0];
            Assert.Equal(key, g == "enter" ? cut.Enter : g == "exit" ? cut.Exit : cut.Hold);
            var t = cut.Start + Math.Min(cut.Dur * 0.5, 0.9);
            var sc = plan.Style.Schemes.Count > 0 ? plan.Style.Schemes[0] : new Scheme();
            var bg = Env.SkColor(sc.Str("bg") ?? "#000000");
            try
            {
                canvas.Clear(bg);
                canvas.Save();
                canvas.Scale((float)scale);
                var env = Env.Make(canvas, plan, cut, sc, t - cut.Start, 1.0 / 24, 0.5, 0, null, "main", null, scale, t, t - cut.Start);
                var def = g == "enter" ? (MoveDef)Reg.Enter[key] : g == "exit" ? (MoveDef)Reg.Exit[key] : Reg.Hold[key];
                // 直调 Draw：真实管线里 it 是 item、p 是进度
                env.It = new JObject { ["text"] = "夜明けの色", ["font"] = font, ["size"] = 42, ["x"] = 160, ["y"] = 90 };
                env.Ap = 0.5;
                var ex = Record.Exception(() => def.Draw?.Invoke(env));
                Assert.Null(ex);
                // 非空：走 MainDraw 全管线（cut 的 enter/exit/hold 就是本条目）
                canvas.Clear(bg);
                var item = new JObject { ["text"] = "夜明けの色", ["font"] = font, ["size"] = 42, ["x"] = 160, ["y"] = 90 };
                var bb = JLayouts.MainDraw(env, item);
                canvas.Restore();
                Assert.True(bb != null, $"{g}.{key}: MainDraw 无 bbox");
                CheckNonEmpty($"{g}.{key}", bg);
            }
            catch (Exception e)
            {
                fails.Add($"{g}.{key}: Draw 抛 {e.GetType().Name}: {e.Message}");
            }
        }

        // —— typo3：decor 直调 Draw；treat 直调 + MainDraw；trans 直调 TransDrawFn ——
        foreach (var key in new[] { "tyColophon", "tyGlyphBody", "tyTextRule" })
        {
            Plan plan;
            try
            {
                plan = Planner.PreviewPlan(proj, "decor", key);
            }
            catch (Exception e)
            {
                fails.Add($"decor.{key}: PreviewPlan 抛 {e.GetType().Name}: {e.Message}");
                continue;
            }
            var cut = plan.Cuts[0];
            Assert.Contains(cut.Decor, d => d.Id == key);
            var t = cut.Start + Math.Min(cut.Dur * 0.5, 0.9);
            var sc = plan.Style.Schemes.Count > 0 ? plan.Style.Schemes[0] : new Scheme();
            var bg = Env.SkColor(sc.Str("bg") ?? "#000000");
            try
            {
                canvas.Clear(bg);
                canvas.Save();
                canvas.Scale((float)scale);
                var env = Env.Make(canvas, plan, cut, sc, t - cut.Start, 0, 0.5, 0, null, "main", null, scale, t, t - cut.Start);
                var spec = cut.Decor.First(d => d.Id == key);
                var ex = Record.Exception(() => Reg.Decor[key].Draw!(env, spec));
                Assert.Null(ex);
                canvas.Restore();
                // tyGlyphBody 只画 1 设计px 细引导线（1/6 设备缩放下是亚像素），阈值单独放行
                CheckNonEmpty($"decor.{key}", bg, key == "tyGlyphBody" ? 0.0002 : 0.002);
            }
            catch (Exception e)
            {
                fails.Add($"decor.{key}: Draw 抛 {e.GetType().Name}: {e.Message}");
            }
        }

        foreach (var key in new[] { "tyHollowKey", "tyHeadRules" })
        {
            Plan plan;
            try
            {
                plan = Planner.PreviewPlan(proj, "treat", key);
            }
            catch (Exception e)
            {
                fails.Add($"treat.{key}: PreviewPlan 抛 {e.GetType().Name}: {e.Message}");
                continue;
            }
            var cut = plan.Cuts[0];
            Assert.Equal(key, cut.Treat);
            var t = cut.Start + Math.Min(cut.Dur * 0.5, 0.9);
            var sc = plan.Style.Schemes.Count > 0 ? plan.Style.Schemes[0] : new Scheme();
            var bg = Env.SkColor(sc.Str("bg") ?? "#000000");
            try
            {
                canvas.Clear(bg);
                canvas.Save();
                canvas.Scale((float)scale);
                var env = Env.Make(canvas, plan, cut, sc, t - cut.Start, 0, 0.5, 0, null, "main", null, scale, t, t - cut.Start);
                env.It = new JObject { ["text"] = "夜明けの色", ["font"] = font, ["size"] = 42, ["x"] = 160, ["y"] = 90 };
                var ex = Record.Exception(() => Reg.Treat[key].Draw?.Invoke(env));
                Assert.Null(ex);
                // 非空：MainDraw 的 treat 调用点（cut.Treat = 本条目）
                canvas.Clear(bg);
                var item = new JObject { ["text"] = "夜明けの色", ["font"] = font, ["size"] = 42, ["x"] = 160, ["y"] = 90 };
                var bb = JLayouts.MainDraw(env, item);
                canvas.Restore();
                Assert.True(bb != null, $"treat.{key}: MainDraw 无 bbox");
                CheckNonEmpty($"treat.{key}", bg);
            }
            catch (Exception e)
            {
                fails.Add($"treat.{key}: Draw 抛 {e.GetType().Name}: {e.Message}");
            }
        }

        foreach (var key in new[] { "tyRuleWipe", "tyGridCells" })
        {
            Plan plan;
            try
            {
                plan = Planner.PreviewPlan(proj, "trans", key);
            }
            catch (Exception e)
            {
                fails.Add($"trans.{key}: PreviewPlan 抛 {e.GetType().Name}: {e.Message}");
                continue;
            }
            var cut = plan.Cuts.LastOrDefault(c => c.Trans == key);
            Assert.NotNull(cut);
            try
            {
                using var a = SolidImage(64, 48, new SKColor(200, 40, 40));
                using var b = SolidImage(64, 48, new SKColor(40, 40, 200));
                var sc = plan.Style.Schemes.Count > 0 ? plan.Style.Schemes[0] : new Scheme();
                var fn = (TransDrawFn)Reg.Trans[key].Draw!;
                var ctx = new TransCtx
                {
                    Cw = 64,
                    Ch = 48,
                    Sc = sc,
                    ScPrev = plan.Style.Schemes.Count > 1 ? plan.Style.Schemes[1] : sc,
                    St = plan.Style,
                    P = cut!.TransP,
                    Step = 0,
                    T = 0.2,
                    Scale = 1,
                    AllowFilter = true,
                    Seed = (int)cut.Seed,
                    Tmp = (w, h) => SKSurface.Create(new SKImageInfo(w, h)),
                };
                canvas.Clear(SKColors.Black);
                var ex = Record.Exception(() => fn(canvas, a, b, 0.5, ctx));
                Assert.Null(ex);
                CheckNonEmpty($"trans.{key}", SKColors.Black);
                // p<=0 精确输出 A、p>=1 精确输出 B（trReg 包装）
                canvas.Clear(SKColors.Black);
                fn(canvas, a, b, 0, ctx);
                Assert.True(MajorityColor(surface, 64, 48) is { Red: > 150, Blue: < 80 }, $"trans.{key}: p=0 未输出 A");
                fn(canvas, a, b, 1, ctx);
                Assert.True(MajorityColor(surface, 64, 48) is { Blue: > 150, Red: < 80 }, $"trans.{key}: p=1 未输出 B");
            }
            catch (Exception e)
            {
                fails.Add($"trans.{key}: Draw 抛 {e.GetType().Name}: {e.Message}");
            }
        }

        Assert.True(fails.Count == 0, "typo 三包冒烟失败：\n" + string.Join("\n", fails));
    }

    // ---------------- 工具 ----------------

    private static SKImage SolidImage(int w, int h, SKColor c)
    {
        var info = new SKImageInfo(w, h);
        using var surf = SKSurface.Create(info);
        surf.Canvas.Clear(c);
        return surf.Snapshot();
    }

    private static SKColor MajorityColor(SKSurface surface, int w, int h)
    {
        using var img = surface.Snapshot();
        using var px = img.PeekPixels();
        return px.GetPixelColor(w / 2, h / 2);
    }

    /// <summary>冒烟字体兜底（同 RenderSmoke）：字体目录条目改指系统 CJK 字体。仅本测试进程内生效。</summary>
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
            JFonts.AddUserFont(kv.Key, kv.Value.Label, fam, kv.Value.Weight, kv.Value.Kind ?? "custom");
    }
}
