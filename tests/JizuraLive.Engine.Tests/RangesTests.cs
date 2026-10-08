using JizuraLive.Engine;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JizuraLive.Engine.Tests;

public class RangesTests
{
    private static Plan MakeTestPlan()
    {
        var plan = new Plan
        {
            Duration = 10.0,
            Cuts = new List<Cut>
            {
                new() { Index = 0, Line = -1, Layout = "title", Start = 0.0, End = 1.0 },
                new() { Index = 1, Line = 0, Layout = "center", Start = 1.0, End = 3.0 },
                new() { Index = 2, Line = 0, Layout = "center", Start = 3.0, End = 5.0 },
                new() { Index = 3, Line = 1, Layout = "interlude", Start = 5.0, End = 6.0 },
                new() { Index = 4, Line = 2, Layout = "center", Start = 6.0, End = 8.0 },
                new() { Index = 5, Line = 2, Layout = "center", Start = 8.0, End = 10.0 },
            }
        };
        return plan;
    }

    [Fact]
    public void CutAround_FindsCorrectCut()
    {
        var plan = MakeTestPlan();

        // 落在切分 1 内部
        var c1 = Ranges.CutAround(plan, 2.0);
        Assert.NotNull(c1);
        Assert.Equal(1, c1.Index);

        // 落在切分 1 起点
        var cStart = Ranges.CutAround(plan, 1.0);
        Assert.NotNull(cStart);
        Assert.Equal(1, cStart.Index);

        // 超过末尾
        var cEnd = Ranges.CutAround(plan, 15.0);
        Assert.NotNull(cEnd);
        Assert.Equal(5, cEnd.Index);

        // 空 plan
        Assert.Null(Ranges.CutAround(null, 1.0));
    }

    [Fact]
    public void LoopRange_AllAndOff_ReturnFullDuration()
    {
        var plan = MakeTestPlan();
        var (s1, e1) = Ranges.LoopRange(plan, LoopMode.All, null, 2.5);
        Assert.Equal(0, s1);
        Assert.Equal(10.0, e1);

        var (s2, e2) = Ranges.LoopRange(plan, LoopMode.Off, null, 2.5);
        Assert.Equal(0, s2);
        Assert.Equal(10.0, e2);
    }

    [Fact]
    public void LoopRange_Cut_ReturnsSingleCutRange()
    {
        var plan = MakeTestPlan();
        // t=2.0 落在 Index=1 (1.0..3.0)
        var (s, e) = Ranges.LoopRange(plan, LoopMode.Cut, null, 2.0);
        Assert.Equal(1.0, s);
        Assert.Equal(3.0, e);
    }

    [Fact]
    public void LoopRange_Line_ReturnsEntireLineCuts()
    {
        var plan = MakeTestPlan();
        // t=2.0 落在 Line=0 (包含 Index=1 1.0..3.0 与 Index=2 3.0..5.0)
        var (s, e) = Ranges.LoopRange(plan, LoopMode.Line, null, 2.0);
        Assert.Equal(1.0, s);
        Assert.Equal(5.0, e);

        // t=5.5 落在 Line=1 (interlude，Line=-1 或跳过特殊)，返回其单切分
        var (sInter, eInter) = Ranges.LoopRange(plan, LoopMode.Line, null, 5.5);
        Assert.Equal(5.0, sInter);
        Assert.Equal(6.0, eInter);
    }

    [Fact]
    public void ExportRangeCuts_ClampsAndValidates()
    {
        Assert.Null(Ranges.ExportRangeCuts(null, 6));
        Assert.Null(Ranges.ExportRangeCuts(new JObject { ["from"] = -1 }, 6));
        Assert.Null(Ranges.ExportRangeCuts(new JObject { ["from"] = "bad" }, 6));

        // 正常范围
        var r = Ranges.ExportRangeCuts(new JObject { ["from"] = 1, ["to"] = 3 }, 6);
        Assert.NotNull(r);
        Assert.Equal((1, 3), r.Value);

        // from > to 交换 clamp
        var rInverted = Ranges.ExportRangeCuts(new JObject { ["from"] = 4, ["to"] = 1 }, 6);
        Assert.NotNull(rInverted);
        Assert.Equal((4, 4), rInverted.Value);

        // 越界夹住
        var rOut = Ranges.ExportRangeCuts(new JObject { ["from"] = 2, ["to"] = 10 }, 6);
        Assert.NotNull(rOut);
        Assert.Equal((2, 5), rOut.Value);
    }

    [Fact]
    public void ExportSpanSeconds_CalculatesPaddings()
    {
        var plan = MakeTestPlan();

        // 选 Index=1 到 Index=2 (1.0..5.0)
        // t0 = max(0, 1.0 - 0.25) = 0.75
        // t1 = min(Cuts[3].Start=5.0, 5.0 + 0.35) = 5.0
        var span = Ranges.ExportSpanSeconds(plan, new JObject { ["from"] = 1, ["to"] = 2 });
        Assert.NotNull(span);
        Assert.Equal(0.75, span.Value.T0, 2);
        Assert.Equal(5.0, span.Value.T1, 2);

        // 包含最后一切分 Index=5 (8.0..10.0) -> t1 必须是 plan.Duration (10.0)
        var spanLast = Ranges.ExportSpanSeconds(plan, new JObject { ["from"] = 4, ["to"] = 5 });
        Assert.NotNull(spanLast);
        Assert.Equal(5.75, spanLast.Value.T0, 2);
        Assert.Equal(10.0, spanLast.Value.T1, 2);
    }

    [Fact]
    public void RangeSuffix_FormatsCorrectly()
    {
        Assert.Equal("", Ranges.RangeSuffix(null, 6));
        Assert.Equal("_C02", Ranges.RangeSuffix(new JObject { ["from"] = 1, ["to"] = 1 }, 6));
        Assert.Equal("_C02-04", Ranges.RangeSuffix(new JObject { ["from"] = 1, ["to"] = 3 }, 6));
    }

    [Fact]
    public void ApplyLineRange_TogglesAndExtends()
    {
        var plan = MakeTestPlan();
        var proj = new Project();

        // 第一次点 Line=0 (Index=1..2)
        var ok = Ranges.ApplyLineRange(proj, plan, 0, extend: false);
        Assert.True(ok);
        Assert.NotNull(proj.ExportRange);
        Assert.Equal(1, proj.ExportRange["from"]);
        Assert.Equal(2, proj.ExportRange["to"]);

        // 再次点同段 -> 清空 (null)
        Ranges.ApplyLineRange(proj, plan, 0, extend: false);
        Assert.Null(proj.ExportRange);

        // 先点 Line=0，再 Shift 点击 Line=2 (Index=4..5) -> 并集 (1..5)
        Ranges.ApplyLineRange(proj, plan, 0, extend: false);
        Ranges.ApplyLineRange(proj, plan, 2, extend: true);
        Assert.NotNull(proj.ExportRange);
        Assert.Equal(1, proj.ExportRange["from"]);
        Assert.Equal(5, proj.ExportRange["to"]);
    }
}
