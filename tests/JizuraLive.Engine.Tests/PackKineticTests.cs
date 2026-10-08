// M5 表现包 kinetic1/2/3（11p_kinetic1.js / 11p_kinetic2.js / 11p_kinetic3.js 移植）冒烟：
// 1) Install 幂等后，三包各自注册组（layout / enter+exit+hold / cam+trans+treat+decor）
//    含其全部 key，pack 归属正确，元数据（name 非空）与委托非空，注册顺序 = 源文件出现序；
// 2) 代表性条目（每包至少 3 条）走 Planner.PreviewPlan + 直调 Draw（layout/enter/hold/exit/treat
//    经 JLayouts.MainDraw 真管线）不抛、画面非全空。离屏面按宿主契约绝不 Dispose
//    （引擎整帧缓存会持有该画面的 SKImage 快照跨帧复用）。

using System.Globalization;
using System.Linq;
using JizuraLive.Engine;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

// RenderSmoke 用固定时刻 × 内容阈值断言一帧随机 plan，对注册池敏感——kinetic 条目入池会
// 改写它的抽取（实测：t=4.0 恰好抽中 blinds 进场 0.15s 的稀疏帧）。xunit 同 collection 按
// 文件元数据序跑类，本文件名排在 RenderSmoke.cs 之前，故类结束时用 fixture 把本包条目
// 从注册表摘除（Uninstall），把抽取池还原到本类运行前的状态。文件名按交付约定保持。
[Collection("EngineRegistry")]
public class SmokeKineticTests : IClassFixture<SmokeKineticTests.Cleanup>
{
    private readonly ITestOutputHelper _out;

    /// <summary>类级 fixture：全部用例跑完后把本包条目从注册表摘除（每类一次；测试进程内，
    /// 包测试各自显式 Install，互不影响）。</summary>
    public sealed class Cleanup : IDisposable
    {
        public void Dispose() => Uninstall();
    }

    public SmokeKineticTests(ITestOutputHelper output, Cleanup _) => _out = output;

    internal static void Uninstall()
    {
        foreach (var k in K1Layouts)
        {
            Reg.Layouts.Remove(k);
            Reg.LayoutOrder.Remove(k);
        }
        foreach (var (g, k) in K2Moves)
        {
            if (g == "enter") { Reg.Enter.Remove(k); Reg.EnterOrder.Remove(k); }
            else if (g == "exit") { Reg.Exit.Remove(k); Reg.ExitOrder.Remove(k); }
            else { Reg.Hold.Remove(k); Reg.HoldOrder.Remove(k); }
        }
        foreach (var (g, k) in K3Entries)
        {
            switch (g)
            {
                case "cam": Reg.Camera.Remove(k); Reg.CameraOrder.Remove(k); break;
                case "trans": Reg.Trans.Remove(k); Reg.TransOrder.Remove(k); break;
                case "treat": Reg.Treat.Remove(k); Reg.TreatOrder.Remove(k); break;
                case "decor": Reg.Decor.Remove(k); Reg.DecorOrder.Remove(k); break;
            }
        }
    }

    /// <summary>11p_kinetic1.js 的 reg('layout', …) 出现序（14 条）。</summary>
    private static readonly string[] K1Layouts =
    {
        "knSlamStack", "knQuarterTurn", "knSwapCenter", "knZoomDive", "knFlowSnap", "knSeesaw",
        "knTypeSlam", "knRhythmCuts", "knPathRide", "knGearWords", "knCollide", "knTumble",
        "knReflow", "knPadGrid",
    };

    /// <summary>11p_kinetic2.js 的 reg(group, …) 出现序（10 enter + 8 exit + 6 hold）。</summary>
    private static readonly (string Group, string Key)[] K2Moves =
    {
        ("enter", "knWordSlam"), ("enter", "knTypeToSlam"), ("enter", "knReplaceIn"),
        ("enter", "knHingeDrop"), ("enter", "knLoopIn"), ("enter", "knPushIn"),
        ("enter", "knInertia"), ("enter", "knWordSpin"), ("enter", "knDiveIn"),
        ("enter", "knStretchOut"),
        ("exit", "knWordKick"), ("exit", "knPushOut"), ("exit", "knDiveGlyph"),
        ("exit", "knLaunch"), ("exit", "knWordBlink"), ("exit", "knCloseGap"),
        ("exit", "knJumpCutOut"), ("exit", "knStackAway"),
        ("hold", "knWordPulse"), ("hold", "knCounterRock"), ("hold", "knWordRide"),
        ("hold", "knTickShift"), ("hold", "knBeatLean"), ("hold", "knGapBreath"),
    };

    /// <summary>11p_kinetic3.js 的 reg/trReg(group, …) 出现序（6 cam + 3 trans + 2 treat + 2 decor）。</summary>
    private static readonly (string Group, string Key)[] K3Entries =
    {
        ("cam", "knReadPan"), ("cam", "knTiltKick"), ("cam", "knCardFlip"),
        ("cam", "knShearKick"), ("cam", "knJumpCut"), ("cam", "knRushIn"),
        ("trans", "knCornerSwing"), ("trans", "knStutterCut"), ("trans", "knStripSlam"),
        ("treat", "knWordScale"), ("treat", "knWordPlate"),
        ("decor", "knSpeedTrail"), ("decor", "knWordTicks"),
    };

    [Fact]
    public void Install_幂等_三包注册全部条目_元数据非空_顺序对位()
    {
        EngineBoot.InstallPacks();
        P11Kinetic1.Install();
        P11Kinetic2.Install();
        P11Kinetic3.Install();
        // 幂等：二次调用不改注册表
        P11Kinetic1.Install();
        P11Kinetic2.Install();
        P11Kinetic3.Install();

        // —— kinetic1：layout 组 ——
        foreach (var k in K1Layouts)
        {
            Assert.True(Reg.Layouts.ContainsKey(k), $"layout.{k} 未注册");
            var d = Reg.Layouts[k];
            Assert.Equal("kinetic1", d.Pack);
            Assert.False(string.IsNullOrEmpty(d.Name), $"layout.{k} 缺 name");
            Assert.NotNull(d.Plan);
            Assert.NotNull(d.Draw);
            Assert.NotNull(d.Fits);
        }

        // —— kinetic2：enter/exit/hold 组 ——
        foreach (var (g, k) in K2Moves)
        {
            var dict = g == "enter" ? Reg.Enter : g == "exit" ? Reg.Exit : Reg.Hold;
            Assert.True(dict.ContainsKey(k), $"{g}.{k} 未注册");
            var d = dict[k];
            Assert.Equal("kinetic2", d.Pack);
            Assert.False(string.IsNullOrEmpty(d.Name), $"{g}.{k} 缺 name");
            Assert.NotNull(d.Draw);
        }

        // —— kinetic3：cam/trans/treat/decor 组 ——
        foreach (var (g, k) in K3Entries)
        {
            switch (g)
            {
                case "cam":
                    Assert.True(Reg.Camera.ContainsKey(k), $"cam.{k} 未注册");
                    Assert.Equal("kinetic3", Reg.Camera[k].Pack);
                    Assert.NotNull(Reg.Camera[k].Plan);
                    Assert.NotNull(Reg.Camera[k].Get);
                    break;
                case "trans":
                    Assert.True(Reg.Trans.ContainsKey(k), $"trans.{k} 未注册");
                    Assert.Equal("kinetic3", Reg.Trans[k].Pack);
                    Assert.NotNull(Reg.Trans[k].Draw);
                    break;
                case "treat":
                    Assert.True(Reg.Treat.ContainsKey(k), $"treat.{k} 未注册");
                    Assert.Equal("kinetic3", Reg.Treat[k].Pack);
                    Assert.NotNull(Reg.Treat[k].Draw);
                    break;
                case "decor":
                    Assert.True(Reg.Decor.ContainsKey(k), $"decor.{k} 未注册");
                    Assert.Equal("kinetic3", Reg.Decor[k].Pack);
                    Assert.NotNull(Reg.Decor[k].Draw);
                    break;
            }
        }

        // 注册顺序 = 源文件出现序（order 列表单调）
        void AssertOrder(string g, IReadOnlyList<string> keys)
        {
            var order = Reg.OrderOf(g).ToList();
            var idx = keys.Select(k => order.IndexOf(k)).ToList();
            Assert.DoesNotContain(-1, idx);
            for (var i = 1; i < idx.Count; i++)
                Assert.True(idx[i] > idx[i - 1], $"order 次序漂移：{g} {keys[i - 1]} → {keys[i]}");
        }
        AssertOrder("layout", K1Layouts);
        AssertOrder("enter", K2Moves.Where(m => m.Group == "enter").Select(m => m.Key).ToList());
        AssertOrder("exit", K2Moves.Where(m => m.Group == "exit").Select(m => m.Key).ToList());
        AssertOrder("hold", K2Moves.Where(m => m.Group == "hold").Select(m => m.Key).ToList());
        AssertOrder("cam", K3Entries.Where(m => m.Group == "cam").Select(m => m.Key).ToList());
        AssertOrder("trans", K3Entries.Where(m => m.Group == "trans").Select(m => m.Key).ToList());
        AssertOrder("treat", K3Entries.Where(m => m.Group == "treat").Select(m => m.Key).ToList());
        AssertOrder("decor", K3Entries.Where(m => m.Group == "decor").Select(m => m.Key).ToList());
    }

    // ---- 代表性条目（每包至少 3 条）：PreviewPlan + 直调 Draw 不抛、非全空 ----

    private static readonly string[] K1Samples = { "knSlamStack", "knSwapCenter", "knPathRide", "knPadGrid" };
    private static readonly (string Group, string Key)[] K2Samples =
    {
        ("enter", "knWordSlam"), ("exit", "knWordKick"), ("hold", "knWordPulse"),
    };
    private static readonly (string Group, string Key)[] K3Samples =
    {
        ("cam", "knReadPan"), ("trans", "knCornerSwing"), ("treat", "knWordScale"), ("decor", "knSpeedTrail"),
    };

    [Fact]
    public void K1_代表条目_PreviewPlan_MainDraw_不抛且非全空()
    {
        EngineBoot.InstallPacks();
        P11Kinetic1.Install();
        var proj = Project.NewDefault();
        proj.Lyrics = "夜明けの色を覚えてる";
        // 面绝不 Dispose：宿主契约（引擎缓存持有该画面快照）。
        // 预览 plan 是设计像素（16:9 → 1920x1080）；直调 Draw 画的是设计坐标，
        // 画布按 scale 预缩放（与 Renderer.Frame 同口径），否则全部内容被裁出画布。
        var surface = SKSurface.Create(new SKImageInfo(640, 360));
        var fails = new List<string>();
        foreach (var key in K1Samples)
        {
            Plan plan;
            try
            {
                plan = Planner.PreviewPlan(proj, "layout", key);
            }
            catch (Exception e)
            {
                fails.Add($"{key}: PreviewPlan 抛 {e.GetType().Name}: {e.Message}");
                continue;
            }
            Assert.Equal(key, plan.Cuts[0].Layout);
            var cut = plan.Cuts[0];
            var sc = plan.Style.Schemes.FirstOrDefault() ?? new Scheme();
            var bg = Env.SkColor(sc.Str("bg") ?? "#000000");
            try
            {
                var scale = 640.0 / plan.W;
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.Save();
                surface.Canvas.Scale((float)scale);
                var env = Env.Make(surface.Canvas, plan, cut, sc, 1.0, 1.0 / 24, 0.5, 0, null, "main", null, scale, 1.0, 1.0);
                Reg.Layouts[key].Draw!(env);
                surface.Canvas.Restore();
                surface.Canvas.Flush();
            }
            catch (Exception e)
            {
                fails.Add($"{key}: Draw 抛 {e.GetType().Name}: {e.Message}");
                continue;
            }
            if (!ContentOk(surface, bg, out var ratio))
                fails.Add($"{key}: 画面近乎空白（contentRatio={ratio.ToString("P2", CultureInfo.InvariantCulture)}）");
        }
        Assert.True(fails.Count == 0, "kinetic1 冒烟失败：\n" + string.Join("\n", fails));
    }

    [Fact]
    public void K2_代表条目_PreviewPlan_MainDraw_不抛且非全空()
    {
        EngineBoot.InstallPacks();
        P11Kinetic2.Install();
        var proj = Project.NewDefault();
        proj.Lyrics = "夜明けの色を覚えてる";
        var surface = SKSurface.Create(new SKImageInfo(640, 360)); // 面绝不 Dispose
        var fails = new List<string>();
        foreach (var (g, key) in K2Samples)
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
            if (g == "enter") Assert.Equal(key, cut.Enter);
            if (g == "exit") Assert.Equal(key, cut.Exit);
            if (g == "hold") Assert.Equal(key, cut.Hold);
            var sc = plan.Style.Schemes.FirstOrDefault() ?? new Scheme();
            var bg = Env.SkColor(sc.Str("bg") ?? "#000000");
            try
            {
                var scale = 640.0 / plan.W;
                surface.Canvas.Clear(SKColors.Transparent);
                surface.Canvas.Save();
                surface.Canvas.Scale((float)scale);
                // enter 镜内中段 / exit 出场早期 / hold 全程——lt 挑在对应管线活跃处
                var lt = g == "exit" ? cut.Dur - cut.OutDur * 0.2 : Math.Max(cut.InDur, 0.8);
                var env = Env.Make(surface.Canvas, plan, cut, sc, lt, 1.0 / 24, 0.5, 0, null, "main", null, scale, lt, lt);
                // 真管线：MainDraw 会按 cut.enter/hold/exit 调 MoveDef 并画出文字
                JLayouts.MainDraw(env, Item(plan, cut, sc));
                surface.Canvas.Restore();
                surface.Canvas.Flush();
            }
            catch (Exception e)
            {
                fails.Add($"{g}.{key}: MainDraw 抛 {e.GetType().Name}: {e.Message}");
                continue;
            }
            if (!ContentOk(surface, bg, out var ratio))
                fails.Add($"{g}.{key}: 画面近乎空白（contentRatio={ratio.ToString("P2", CultureInfo.InvariantCulture)}）");
        }
        Assert.True(fails.Count == 0, "kinetic2 冒烟失败：\n" + string.Join("\n", fails));
    }

    [Fact]
    public void K3_代表条目_PreviewPlan_Draw_不抛且非全空()
    {
        EngineBoot.InstallPacks();
        P11Kinetic3.Install();
        var proj = Project.NewDefault();
        proj.Lyrics = "夜明けの色を覚えてる";
        var surface = SKSurface.Create(new SKImageInfo(640, 360)); // 面绝不 Dispose
        var fails = new List<string>();
        foreach (var (g, key) in K3Samples)
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
            var cut = plan.Cuts[g == "trans" ? 1 : 0];
            var sc = plan.Style.Schemes.FirstOrDefault() ?? new Scheme();
            var bg = Env.SkColor(sc.Str("bg") ?? "#000000");
            try
            {
                surface.Canvas.Clear(SKColors.Transparent);
                switch (g)
                {
                    case "cam":
                    {
                        Assert.Equal(key, cut.Cam);
                        var env = Env.Make(surface.Canvas, plan, cut, sc, 1.0, 1.0 / 24, 0.5, 0, null, "main", null, 1.0, 1.0, 1.0);
                        var cam = Reg.Camera[key].Get!(env);
                        Assert.True(double.IsFinite(cam.S) && double.IsFinite(cam.X) && double.IsFinite(cam.Y)
                                    && double.IsFinite(cam.Rot) && double.IsFinite(cam.Skx) && double.IsFinite(cam.Blur),
                            $"cam.{key} 状态非有限值");
                        break;
                    }
                    case "trans":
                    {
                        Assert.Equal(key, cut.Trans);
                        var draw = Reg.Trans[key].Draw as TransDrawFn;
                        Assert.NotNull(draw);
                        using var sa = SolidImage(640, 360, SKColors.IndianRed);
                        using var sb = SolidImage(640, 360, SKColors.SteelBlue);
                        var info = new TransCtx
                        {
                            Cw = 640,
                            Ch = 360,
                            Sc = sc,
                            ScPrev = plan.Style.Schemes.FirstOrDefault() ?? sc,
                            St = plan.Style,
                            P = cut.TransP,
                            Step = 1,
                            T = 1.5,
                            Scale = 1,
                            AllowFilter = true,
                            Seed = (int)cut.Seed,
                            Tmp = (w, h) => SKSurface.Create(new SKImageInfo(w, h)),
                        };
                        draw!(surface.Canvas, sa, sb, 0.5, info);
                        surface.Canvas.Flush();
                        break;
                    }
                    case "treat":
                    {
                        Assert.Equal(key, cut.Treat);
                        var scale = 640.0 / plan.W;
                        surface.Canvas.Save();
                        surface.Canvas.Scale((float)scale);
                        var env = Env.Make(surface.Canvas, plan, cut, sc, 1.0, 1.0 / 24, 0.5, 0, null, "main", null, scale, 1.0, 1.0);
                        JLayouts.MainDraw(env, Item(plan, cut, sc)); // MainDraw 的 treat 调用点执行 Draw
                        surface.Canvas.Restore();
                        surface.Canvas.Flush();
                        break;
                    }
                    case "decor":
                    {
                        var spec = cut.Decor.FirstOrDefault();
                        Assert.NotNull(spec);
                        var scale = 640.0 / plan.W;
                        surface.Canvas.Save();
                        surface.Canvas.Scale((float)scale);
                        var env = Env.Make(surface.Canvas, plan, cut, sc, 1.0, 1.0 / 24, 0.5, 0, null, "main", null, scale, 1.0, 1.0);
                        env.Body = new BBox { X0 = plan.W * 0.2, Y0 = plan.H * 0.2, X1 = plan.W * 0.8, Y1 = plan.H * 0.75, Cx = plan.W / 2.0, Cy = plan.H / 2.0 };
                        Reg.Decor[key].Draw!(env, spec!);
                        surface.Canvas.Restore();
                        surface.Canvas.Flush();
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                fails.Add($"{g}.{key}: Draw 抛 {e.GetType().Name}: {e.Message}");
                continue;
            }
            if (g != "cam" && !ContentOk(surface, bg, out var ratio)) // cam 只取状态，不画画
                fails.Add($"{g}.{key}: 画面近乎空白（contentRatio={ratio.ToString("P2", CultureInfo.InvariantCulture)}）");
        }
        Assert.True(fails.Count == 0, "kinetic3 冒烟失败：\n" + string.Join("\n", fails));
    }

    // ---- helpers ----

    /// <summary>最小 item（直调 MainDraw 用）：词几何依赖 cut.words，字体走冒烟映射。</summary>
    private static JObject Item(Plan plan, Cut cut, Scheme sc) => new()
    {
        // PreviewPlan 给 move/treat 组的文本只有「字面」2 字、像素占比过小——
        // item 用整句词（词几何走 item 自身文本的分词回退，cut.words 只在能对上时用）
        ["text"] = "夜明けの色を覚えてる",
        ["font"] = "gothic_black",
        ["size"] = Math.Max(48.0, plan.H * 0.09),
        ["x"] = plan.W / 2.0,
        ["y"] = plan.H / 2.0,
        ["track"] = 0.02,
        ["color"] = sc.Str("fg"),
        ["mi"] = 0.0,
    };

    private static SKImage SolidImage(int w, int h, SKColor color)
    {
        var info = new SKImageInfo(w, h);
        using var surf = SKSurface.Create(info);
        surf.Canvas.Clear(color);
        return surf.Snapshot();
    }

    /// <summary>与 scheme bg 差异 &gt; 40 的像素占比 &gt; 0.4%（非全空）。</summary>
    private static bool ContentOk(SKSurface surface, SKColor bg, out double ratio)
    {
        double content = 0, total = 0;
        using var img = surface.Snapshot();
        using var px = img.PeekPixels();
        for (var y = 0; y < px.Height; y += 2)
            for (var x = 0; x < px.Width; x += 2)
            {
                var c = px.GetPixelColor(x, y);
                total++;
                var d = Math.Max(Math.Max(Math.Abs(c.Red - bg.Red), Math.Abs(c.Green - bg.Green)),
                    Math.Abs(c.Blue - bg.Blue));
                if (d > 40) content++;
            }
        ratio = total > 0 ? content / total : 0;
        return ratio > 0.004;
    }

}
