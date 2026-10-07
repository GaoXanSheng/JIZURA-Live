using JizuraLive.Engine;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>decor/decorB 表现包冒烟（注册契约 + 每条目直调 Draw 不抛）。</summary>
[Collection("EngineRegistry")]
public class PackDecorTests
{
    [Fact]
    public void Decor包注册契约与Draw冒烟()
    {
        EngineBoot.InstallPacks();
        P11Decor.Install();
        P11DecorB.Install();

        var packs = new[] { "decor", "decorB" };
        foreach (var pack in packs)
        {
            var keys = Reg.DecorOrder.Where(k => Reg.Decor[k].Pack == pack).ToList();
            Assert.True(keys.Count > 0, $"{pack} 无条目");
            foreach (var k in keys)
            {
                Assert.False(string.IsNullOrEmpty(Reg.Decor[k].Name), $"decor.{k} 缺 name");
                Assert.NotNull(Reg.Decor[k].Draw);
            }
        }

        // 每条目直调 Draw 不抛（DecorSpec 用代表性取值）
        var proj = Project.NewDefault();
        proj.Lyrics = "夜明けの色を覚えてる";
        var plan = Planner.Plan(proj, null);
        var cut = plan.Cuts.FirstOrDefault();
        Assert.NotNull(cut);
        using var surf = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(320, 180, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul));
        foreach (var kv in Reg.Decor.ToList())
        {
            var env = Env.Make(surf.Canvas, plan, cut, plan.Style.Schemes.FirstOrDefault(), 0.5, 1.0 / 24, 0.5, 0.5, null, "main", null, 1.0);
            env.It = new Newtonsoft.Json.Linq.JObject();
            var ex = Record.Exception(() => kv.Value.Draw?.Invoke(env, new DecorSpec { Id = kv.Key, Seed = 7, N = 2, Mode = "count" }));
            Assert.Null(ex);
        }
    }

    [Fact]
    public void TreatTrans包注册契约()
    {
        EngineBoot.InstallPacks();
        P11TreatTrans.Install();

        Assert.True(Reg.TreatOrder.Count >= 1);
        Assert.True(Reg.TransOrder.Count >= 1);
        foreach (var k in Reg.TreatOrder)
            Assert.False(string.IsNullOrEmpty(Reg.Treat[k].Name));
        foreach (var k in Reg.TransOrder)
            Assert.False(string.IsNullOrEmpty(Reg.Trans[k].Name));
    }
}
