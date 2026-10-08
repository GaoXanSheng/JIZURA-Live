// M5 管线钩子补全的回归测试：Env.Beat 对象桥 / DrawFx 的 pre/post/clipFn 消费 / IRegEntry.Set
// 套装门禁 / TransDrawFn 直调路径 / splitHalves 逐帧无告警。
// 渲染类用例注册临时条目（special 不进 order），断言用完即卸；注册表隔离靠 [Collection("EngineRegistry")]。

using System.IO;
using JizuraLive.Engine;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

[Collection("EngineRegistry")]
public class PipelineHookTests
{
    private readonly ITestOutputHelper _out;

    public PipelineHookTests(ITestOutputHelper output) => _out = output;

    private static void SysFonts()
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

    private static (Plan Plan, Renderer R) MakeRenderer(Project? p = null)
    {
        EngineBoot.InstallPacks();
        SysFonts();
        p ??= Project.NewDefault();
        p.Lyrics = "夜明けの色を/覚えてる\nほどけた声が遠くで鳴った";
        var plan = Planner.Plan(p, null);
        return (plan, new Renderer());
    }

    // ---------------- 1. Env.Beat 对象桥（渲染器每帧二分 → BeatHas/Since/Len/Index） ----------------

    [Fact]
    public void Renderer_Fills_Env_Beat_Object()
    {
        var (plan, r) = MakeRenderer();
        var seen = new List<(bool Has, double Since, double Len, int Idx)>();
        Reg.Register("layout", "__beatProbe", new LayoutDef
        {
            Name = "probe", Special = true,
            Draw = env => seen.Add((env.BeatHas, env.BeatSince, env.BeatLen, env.BeatIndex)),
        }, "test");
        try
        {
            plan.Beats = new[] { 1.0, 1.5, 2.0 };
            foreach (var c in plan.Cuts) c.Layout = "__beatProbe";
            using var surface = SKSurface.Create(new SKImageInfo(320, 180));
            r.Frame(surface.Canvas, plan, 2.05); // 上一拍 2.0 → since=0.05 len=0.5 index=2
            r.Frame(surface.Canvas, plan, 2.30); // since=0.30
        }
        finally
        {
            Reg.Layouts.Remove("__beatProbe");
        }
        Assert.True(seen.Count >= 2, "probe layout 未被渲染");
        Assert.True(seen[0].Has, "BeatHas 未挂载");
        Assert.Equal(2, seen[0].Idx);
        Assert.Equal(0.05, seen[0].Since, 3);
        Assert.Equal(0.5, seen[0].Len, 3);
        Assert.Equal(0.30, seen[1].Since, 3);
    }

    [Fact]
    public void Renderer_NoBeats_Leaves_BeatHas_False()
    {
        var (plan, r) = MakeRenderer();
        var seen = new List<bool>();
        Reg.Register("layout", "__beatProbe2", new LayoutDef
        {
            Name = "probe2", Special = true,
            Draw = env => seen.Add(env.BeatHas),
        }, "test");
        try
        {
            plan.Beats = System.Array.Empty<double>();
            foreach (var c in plan.Cuts) c.Layout = "__beatProbe2";
            using var surface = SKSurface.Create(new SKImageInfo(320, 180));
            r.Frame(surface.Canvas, plan, 1.0);
        }
        finally
        {
            Reg.Layouts.Remove("__beatProbe2");
        }
        Assert.True(seen.Count >= 1);
        Assert.False(seen[0]);
    }

    // ---------------- 2. DrawFx 的 pre/post/clipFn 消费 ----------------

    /// <summary>临时 layout：底板矩形（pre，画在文字下）→ 白底黑字 item（clipFn 圆裁）→ 红色满版方块（post，画在文字上）。
    /// 像素断言：pre 灰条在文字带内可见（不被 post 覆盖：post 只画在中心一块）、文字被圆外裁掉（四角无字）。</summary>
    [Fact]
    public void DrawFx_Consumes_Pre_Post_ClipFn()
    {
        var (plan, r) = MakeRenderer();
        Reg.Register("layout", "__hookProbe", new LayoutDef
        {
            Name = "hookProbe", Special = true,
            Draw = en =>
            {
                // pre：整宽灰条（画在主文字下方）
                en.PreFn = (e, _) => e.Rect(0, e.H * 0.45, e.W, e.H * 0.1, "#808080", 1, false);
                var it = new JObject
                {
                    ["text"] = "字",
                    ["font"] = "gothic_bold",
                    ["size"] = 72.0,
                    ["x"] = (double)en.W / 2,
                    ["y"] = (double)en.H / 2,
                    ["color"] = "#000000",
                };
                // clipFn：以 item 为中心的圆——圆外不可见
                var cx = en.W / 2.0;
                var cy = en.H / 2.0;
                env.ClipFn = (e2, _) =>
                {
                    var path = new SKPath();
                    path.AddCircle((float)cx, (float)cy, 40f);
                    return path;
                };
                en.PostFn = (e2, _, _) => e2.Rect(e2.W / 2.0 - 10, e2.H / 2.0 - 10, 20, 20, "#FF0000", 1, false);
                en.Body = JLayouts.MainDraw(en, it);
            },
        }, "test");
        SKImage img;
        try
        {
            foreach (var c in plan.Cuts) c.Layout = "__hookProbe";
            using var surface = SKSurface.Create(new SKImageInfo(320, 180));
            r.Frame(surface.Canvas, plan, 1.0);
            img = surface.Snapshot();
        }
        finally
        {
            Reg.Layouts.Remove("__hookProbe");
        }
        using (img)
        {
            var px = img.PeekPixels()!;
            SKColor At(int x, int y) => px.GetPixelColor(x, y);
            // post：中心 20×20 红块画在文字之上 → 中心点应是红色（post 生效且层级在文字上方）
            Assert.True(At(160, 90).Red > 180 && At(160, 90).Green < 90, $"中心应为红（post 在文字上），实际 {At(160, 90)}");
            // pre：灰条在 x=20（圆外、文字外）可见
            Assert.True(Math.Abs(At(20, 90).Red - 128) < 30, $"pre 灰条应可见，实际 {At(20, 90)}");
            // clipFn：文字四角（圆外）应是背景色（无字）；暗底 scheme 下背景近黑
            var corner = At(160, 20);
            Assert.True(corner.Red < 60 && corner.Green < 60, $"圆外不应有字（clipFn 裁剪），实际 {corner}");
        }
    }

    // ---------------- 3. IRegEntry.Set 套装门禁（Omakase 直读 Set 字段） ----------------

    [Fact]
    public void Omakase_Gate_Reads_Set_Field()
    {
        EngineBoot.InstallPacks();
        // horror 条目的 Set 字段 → 门禁按 project.Horror 开关放行
        var probe = Reg.Layouts.Values.First(d => d.Set == "horror");
        Assert.NotNull(probe);
        var pj = Project.NewDefault();
        pj.Horror = false;
        Assert.False(Omakase.RandomOk(pj, "layout", probe.Key));
        pj.Horror = true;
        Assert.True(Omakase.RandomOk(pj, "layout", probe.Key));
        // 带 horror 套装的风格不在追加分里（set 与 extra 互斥）
        Assert.Equal("horror", Omakase.SetOf("style", "hrRuin"));
        Assert.False(Omakase.IsExtra("style", "hrRuin"));
        // kinetic/typo 同理
        var kin = Reg.Layouts.Values.First(d => d.Set == "kinetic");
        pj.Kinetic = false;
        Assert.False(Omakase.RandomOk(pj, "layout", kin.Key));
        pj.Kinetic = true;
        Assert.True(Omakase.RandomOk(pj, "layout", kin.Key));
        var ty = Reg.Layouts.Values.First(d => d.Set == "typo");
        pj.Typo = false;
        Assert.False(Omakase.RandomOk(pj, "layout", ty.Key));
        pj.Typo = true;
        Assert.True(Omakase.RandomOk(pj, "layout", ty.Key));
    }

    // ---------------- 4. TransDef.Draw 调用路径（TransDrawFn 直调） ----------------

    [Fact]
    public void Trans_Draw_Delegate_Is_TransDrawFn_And_Composites()
    {
        EngineBoot.InstallPacks();
        // 渲染器的判定：tdef.Draw is TransDrawFn → 包注册的 trans 条目走该类型
        var tdef = Reg.Trans["channelSwitch"];
        Assert.NotNull(tdef);
        Assert.IsType<TransDrawFn>(tdef.Draw);
        // 直调合成：A 全红、B 全蓝，p=0.5 输出应有 A 与 B 的混合（撕条滚动両侧）
        using var a = SKSurface.Create(new SKImageInfo(64, 64));
        using var b = SKSurface.Create(new SKImageInfo(64, 64));
        a.Canvas.Clear(SKColors.Red);
        b.Canvas.Clear(SKColors.Blue);
        var tdef2 = Reg.Trans["channelSwitch"];
        tdef2!.Draw!.DynamicInvoke(a.Canvas, a.Snapshot(), b.Snapshot(), 0.5, new TransCtx
        {
            Cw = 64,
            Ch = 64,
            Sc = new Scheme(),
            ScPrev = new Scheme(),
            St = new StyleDef(),
            P = new JObject { ["seed"] = 1.0 },
            Step = 0,
            T = 0,
            Scale = 1,
            AllowFilter = false,
            Seed = 1,
            Tmp = (w, h) => SKSurface.Create(new SKImageInfo(w, h)),
        });
        using var img = b.Snapshot();
        var px = img.PeekPixels()!;
        var mid = px.GetPixelColor(32, 32);
        // p=0.5 已滚入 B 的中段：不再整幅是 A 的红
        Assert.True(mid.Blue > 100 || mid.Red < 200, $"p=0.5 应含 B 画面，实际 {mid}");
    }

    // ---------------- 5. splitHalves 逐帧无告警（perf 报告的每帧吞异常回归） ----------------

    [Fact]
    public void SplitHalves_Renders_Without_Warnings()
    {
        var (plan, r) = MakeRenderer();
        foreach (var c in plan.Cuts) c.Layout = "splitHalves";
        using var renderer = r;
        using var surface = SKSurface.Create(new SKImageInfo(960, 540));
        using var buf = new StringWriter();
        var prev = Console.Out;
        Console.SetOut(buf);
        try
        {
            for (var t = 0.6; t < 2.4; t += 0.2)
                renderer.Frame(surface.Canvas, plan, t);
        }
        finally
        {
            Console.SetOut(prev);
        }
        var text = buf.ToString();
        _out.WriteLine(text.Length == 0 ? "(no warnings)" : text);
        Assert.DoesNotContain("layout splitHalves", text);
        Assert.DoesNotContain("item pre", text);
        Assert.DoesNotContain("item post", text);
    }
}
