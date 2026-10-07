using Newtonsoft.Json.Linq;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// UI 回环回归（栈溢出排查副产物）：LineList 的切分数/布局下拉写 overrides[li]，
/// 走 Planner 的 SplitToCount 路径——此前 20 个 fuzz 用例全没设 overrides，该路径零覆盖。
/// 引擎链（CodePoints/SegType/Segments/ChunkText/SplitToCount）本身全是迭代实现；
/// 真正的栈溢出根因是 UI 层 Mutate→StateChanged→重建行→绑定再触发 的同步回环，
/// 已在 LineList（幂等闸）与 EditorState.Mutate（重入闸）修复。此处锁定引擎侧行为不回归。
/// </summary>
[Collection("EngineRegistry")]
public class OverrideCutsTests
{
    [Fact]
    public void 切分数覆盖走SplitToCount_正常出切分()
    {
        EngineBoot.Install();
        var p = Project.NewDefault();
        p.Lyrics = "夜明けの色を覚えてる";
        p.Overrides["0"] = new JObject { ["cuts"] = 6 }; // LineList.NcutCombo 写入的形态
        var plan = Planner.Plan(p, null);
        var line0 = plan.Cuts.Count(c => c.Line == 0);
        Assert.True(line0 >= 2, $"指定 6 切分实际只得 {line0}（SplitToCount 未生效）");
    }

    [Theory]
    [InlineData("", 0)]        // 空串
    [InlineData("あ", 1)]      // 单字符
    [InlineData("　 ", 1)]     // 全空白
    [InlineData("漢漢漢漢漢漢漢漢", 1)] // 全同字类（回环高发输入）
    public void Segments_病态输入有限终止(string text, int expected)
    {
        EngineBoot.Install();
        var segs = Planner.Segments(text);
        Assert.Equal(expected, segs.Count);
    }
}
