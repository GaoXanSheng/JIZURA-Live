// 渲染侧统计（诊断基建）：把「Frame 调用 / 整帧缓存命中 / 真渲染」的语义钉住。
// 背景：出帧侧（Spout 泵、预览泵）报的帧数只是泵拍数——命中整帧缓存那一帧只是一次贴图。
// 判断「引擎是否真画得过来」只能看真渲染次数与真渲染耗时，所以这层计数必须有测试看住。

using JizuraLive.Engine;
using SkiaSharp;
using Xunit;

namespace JizuraLive.Engine.Tests;

[Collection("EngineRegistry")]
public class RenderStatsTests
{
    [Fact]
    public void 统计_区分真渲染与整帧缓存命中()
    {
        EngineBoot.Install();

        var project = Project.NewDefault();
        project.Lyrics = "夜明けの色を/覚えてる\nほどけた声が遠くで鳴った";
        var plan = Planner.Plan(project, null);
        plan.Fx.Koma = 12; // 一拍二：同一逐步（1/12s）内反复出帧时才可能命中整帧缓存

        using var renderer = new Renderer();
        using var surface = SKSurface.Create(new SKImageInfo(960, 540));

        // 整帧缓存在事件窗口（chroma/shake 衰减 14 帧）与转场窗口内会被绕过，而 plan 内容由种子
        // 决定——所以不硬编码 t，改成在候选时刻里找一处缓存确实生效的，并在那里断言计数语义。
        // 断言对「键是否已被上一轮预热」鲁棒：冷键至多两次真画（记 hot → 落快照），其余必命中。
        Renderer.Stats? hit = null;
        var tried = new List<string>();
        foreach (var cand in Candidates(plan))
        {
            var tq = Math.Floor(cand * 12 + 1e-6) / 12; // 与渲染器同口径（koma=12 → stepDur=1/12）
            renderer.TakeWindowStats(); // 丢弃上一个候选的计数
            for (var k = 0; k < 6; k++)
                renderer.Frame(surface.Canvas, plan, tq + k * 0.001); // 同一 tq 连画 6 次
            var s = renderer.TakeWindowStats();
            if (s.Calls == 6 && s.Cached >= 4)
            {
                hit = s;
                tried.Add($"tq={tq:F4} → cached={s.Cached} real={s.Real}");
                break;
            }
            tried.Add($"tq={tq:F4} → cached={s.Cached} real={s.Real}");
        }
        Assert.True(hit != null, "候选时刻没有一处命中整帧缓存："
            + $"cuts=[{string.Join(", ", plan.Cuts.Select(c => $"{c.Start:F2}-{c.End:F2}"))}]"
            + $" events={plan.Events.Count}\n" + string.Join("\n", tried));
        var s0 = hit!.Value;
        Assert.Equal(s0.Calls, s0.Cached + s0.Real);
        Assert.True(s0.Real <= 2, $"冷键应至多两次真渲染，实际 {s0.Real} 次");
        if (s0.Real > 0)
        {
            Assert.True(s0.RealSumMs > 0, "真渲染耗时应 > 0");
            Assert.True(s0.MaxMs >= s0.RealSumMs / s0.Real - 1e-9, "峰值应不小于真渲染均值");
        }

        // 读取即清零（单读取方 = 日志线程）
        var empty = renderer.TakeWindowStats();
        Assert.Equal(0, empty.Calls);
        Assert.Equal(0, empty.Real);
    }

    [Fact]
    public void 统计_跨逐步不命中缓存()
    {
        EngineBoot.Install();

        var project = Project.NewDefault();
        project.Lyrics = "夜明けの色を/覚えてる\nほどけた声が遠くで鳴った";
        var plan = Planner.Plan(project, null);
        plan.Fx.Koma = 12;

        using var renderer = new Renderer();
        using var surface = SKSurface.Create(new SKImageInfo(960, 540));
        var t = Candidates(plan).First();

        renderer.Frame(surface.Canvas, plan, t);
        renderer.TakeWindowStats();
        renderer.Frame(surface.Canvas, plan, t + 1.0 / 12); // 下一个作画步：缓存键变了
        var s = renderer.TakeWindowStats();
        Assert.Equal(1, s.Calls);
        Assert.Equal(0, s.Cached);
        Assert.Equal(1, s.Real);
    }

    /// <summary>候选时刻：cut 内、离起点 ≥0.62s、离终点 ≥0.1s。
    /// 0.62s 的来历：事件（chroma/shake）衰减窗是 14 帧 ≈0.583s，窗内整帧缓存被绕过，
    /// 而计划器往往在每个 cut 开头打一个 chroma——贴着 cut 起点取样会永远测不到缓存。</summary>
    private static IEnumerable<double> Candidates(Plan plan)
    {
        foreach (var c in plan.Cuts)
            for (var t = c.Start + 0.62; t <= c.End - 0.10 + 1e-9; t += 0.05)
                yield return t;
    }
}
