using JizuraLive.Engine;
using JizuraLive.Engine.Packs;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>styles/bgcamB 表现包冒烟：注册契约 + ResolveStyle 可用 + 每条目 Plan/Get/Draw 直调不抛。</summary>
[Collection("EngineRegistry")]
public class PackStylesBgcamTests
{
    private static readonly string[] StyleKeys =
    {
        "sakura", "ocean", "sunset", "forest", "vapor", "newsprint",
        "synth80", "kraft", "candy", "acid", "sumi", "gold"
    };

    [Fact]
    public void Styles包注册契约与ResolveStyle()
    {
        EngineBoot.InstallPacks();
        P11Styles.Install();

        foreach (var k in StyleKeys)
        {
            Assert.True(Reg.Styles.TryGetValue(k, out var st), $"styles.{k} 未注册");
            Assert.False(string.IsNullOrWhiteSpace(st!.Name), $"styles.{k} name 为空");
            Assert.True(st.Schemes.Count > 0, $"styles.{k} schemes 为空");
            Assert.Contains(k, JStyles.StyleOrder);
            foreach (var s in st.Schemes)
            {
                Assert.False(string.IsNullOrEmpty(s.Str("bg")), $"styles.{k} scheme 缺 bg");
                Assert.False(string.IsNullOrEmpty(s.Str("fg")), $"styles.{k} scheme 缺 fg");
            }
            Assert.True(st.Extra!.ContainsKey("desc"));
            Assert.True(st.Extra!.ContainsKey("fonts"));
            Assert.True(st.Extra!.ContainsKey("texture"));
            Assert.True(st.Extra!.ContainsKey("ghost"));

            // ResolveStyle(project with Style=key) 可用且 schemes 非空
            var proj = Project.NewDefault();
            proj.Style = k;
            var resolved = JStyles.ResolveStyle(proj);
            Assert.NotNull(resolved);
            Assert.True(resolved.Schemes.Count > 0, $"styles.{k} ResolveStyle 后 schemes 为空");
        }
    }

    [Fact]
    public void BgcamB包注册契约与PlanGetDraw冒烟()
    {
        EngineBoot.InstallPacks();
        P11BgcamB.Install();

        var bgKeys = Reg.BgOrder.Where(k => Reg.Bg[k].Pack == "bgcamB").ToList();
        Assert.True(bgKeys.Count > 0, "bgcamB 无 bg 条目");
        var camKeys = Reg.CameraOrder.Where(k => Reg.Camera[k].Pack == "bgcamB").ToList();
        Assert.True(camKeys.Count > 0, "bgcamB 无 cam 条目");

        var proj = Project.NewDefault();
        proj.Lyrics = "夜明けの色を覚えてる";
        var plan = Planner.Plan(proj, null);
        var cut = plan.Cuts.FirstOrDefault();
        Assert.NotNull(cut);
        var scheme = plan.Style.Schemes.FirstOrDefault();
        Assert.NotNull(scheme);

        using var surf = SKSurface.Create(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        Env MakeEnv() => Env.Make(surf.Canvas, plan, cut, scheme, 0.5, 8, 0.5, 0.5, null, "main", null, 1.0,
            t: 0.6, ltb: 0.55);

        foreach (var k in bgKeys)
        {
            var def = Reg.Bg[k];
            Assert.False(string.IsNullOrWhiteSpace(def.Name), $"bg.{k} 缺 name");
            var rng = new Rng(123);
            JObject? p = null;
            var exPlan = Record.Exception(() => p = def.Plan?.Invoke(rng, plan.Style));
            Assert.Null(exPlan);
            Assert.NotNull(p);

            cut!.Bg = k;
            cut.BgP = p!;
            var env = MakeEnv();
            var exDraw = Record.Exception(() => def.Draw?.Invoke(env));
            Assert.Null(exDraw);
        }

        foreach (var k in camKeys)
        {
            var def = Reg.Camera[k];
            Assert.False(string.IsNullOrWhiteSpace(def.Name), $"cam.{k} 缺 name");
            var rng = new Rng(123);
            JObject? p = null;
            var exPlan = Record.Exception(() => p = def.Plan?.Invoke(rng, plan.Style));
            Assert.Null(exPlan);
            Assert.NotNull(p);

            cut!.CamP = p!;
            var env = MakeEnv();
            CamState? cs = null;
            var exGet = Record.Exception(() => cs = def.Get?.Invoke(env));
            Assert.Null(exGet);
            Assert.NotNull(cs);
        }
    }
}
