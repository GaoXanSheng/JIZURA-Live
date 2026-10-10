// 抽帧取值口径：对位前端 J.komaOf = fx.koma != null ? +fx.koma : (fx.onTwos === false ? 0 : 12)。
// 「默认全帧」（Koma=null + OnTwos=false）是本 fork 相对上游 JS 唯一的故意差异；除此之外的每一次
// 回退都必须与浏览器逐条相同，否则同一份工程在两边画面不一样（老工程 onTwos=false 曾被 C# 误判成
// 一拍二，就是因为 KomaOf 只看 Koma 字段、丢了 onTwos 那条回退）。

using JizuraLive.App;
using JizuraLive.Engine;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JizuraLive.Engine.Tests;

[Collection("EngineRegistry")]
public class KomaParityTests
{
    [Fact]
    public void KomaOf_与前端同口径()
    {
        Assert.Equal(0, Planner.KomaOf(new FxSettings())); // 缺省 = 全帧（本 fork 的默认）
        Assert.Equal(0, Planner.KomaOf(new FxSettings { Koma = null, OnTwos = false })); // 前端：onTwos===false → 0
        Assert.Equal(12, Planner.KomaOf(new FxSettings { Koma = null, OnTwos = true })); // 前端：无 koma 键 + onTwos → 12
        Assert.Equal(0, Planner.KomaOf(new FxSettings { Koma = 0, OnTwos = true }));      // 显式档位永远优先
        Assert.Equal(12, Planner.KomaOf(new FxSettings { Koma = 12, OnTwos = false }));
        Assert.Equal(8, Planner.KomaOf(new FxSettings { Koma = 8 }));
        Assert.Equal(24, Planner.KomaOf(new FxSettings { Koma = 24 }));

        Assert.Equal(1.0 / 24, Planner.StepDur(new FxSettings(), 24), 12);  // 全帧 = 同输出 fps
        Assert.Equal(1.0 / 60, Planner.StepDur(new FxSettings(), 60), 12);
        Assert.Equal(1.0 / 12, Planner.StepDur(new FxSettings { Koma = 12 }, 60), 12); // 抽帧与输出 fps 解耦
    }

    [Fact]
    public void 工程往返_保留抽帧字段()
    {
        // 不碰 L（语言是全局态，LocaleTests 并行时会互相干扰）：ProjectIO 只用到引擎注册表
        EngineBoot.Install();
        Omakase.Install();

        var p = Project.NewDefault();
        Assert.Null(p.Fx.Koma); // 新工程不写 koma 键（前端 komaOf 才走 onTwos 回退）
        Assert.False(p.Fx.OnTwos);
        Assert.Equal(0, Planner.KomaOf(p.Fx));
        Assert.Equal(0, Planner.KomaOf(Planner.Plan(p, null).Fx)); // 计划侧 CloneFx 也要原样带走（全帧）

        var back = ProjectIO.MergeProject(JObject.Parse(ProjectIO.SerializeProject(p)));
        Assert.Null(back.Fx.Koma);
        Assert.False(back.Fx.OnTwos);
        Assert.Equal(0, Planner.KomaOf(back.Fx));

        p.Fx.Koma = 12; // 显式选一拍二后再往返
        p.Fx.OnTwos = true;
        var back2 = ProjectIO.MergeProject(JObject.Parse(ProjectIO.SerializeProject(p)));
        Assert.Equal(12, back2.Fx.Koma);
        Assert.True(back2.Fx.OnTwos);
        Assert.Equal(12, Planner.KomaOf(back2.Fx));

        // 老工程：有 onTwos 没 koma —— 回退必须与前端一致（此前 C# 一律当成 12）
        Assert.Equal(0, Planner.KomaOf(ProjectIO.MergeProject(JObject.Parse("{\"fx\":{\"onTwos\":false}}")).Fx));
        Assert.Equal(12, Planner.KomaOf(ProjectIO.MergeProject(JObject.Parse("{\"fx\":{\"onTwos\":true}}")).Fx));
        Assert.Equal(8, Planner.KomaOf(ProjectIO.MergeProject(JObject.Parse("{\"fx\":{\"koma\":8}}")).Fx));
    }

    [Fact]
    public void 特效面板写入_与前端同语义()
    {
        // 面板/前端下拉写的是同一对：koma = 档位，onTwos = 档位 > 0（FxTab.CbKoma_SelectionChanged、
        // FxTab.vue onKoma、Omakase 随机三处一致）。这里只钉住「写进去怎么读回来」。
        var f = new FxSettings();
        foreach (var (k, want) in new[] { (0, 0), (12, 12), (8, 8), (24, 24) })
        {
            f.Koma = k;
            f.OnTwos = k > 0;
            Assert.Equal(want, Planner.KomaOf(f));
            Assert.Equal(want, Omakase.FxGet(f, "koma")); // 锁定参数读法 = 有效档位
        }
    }
}
