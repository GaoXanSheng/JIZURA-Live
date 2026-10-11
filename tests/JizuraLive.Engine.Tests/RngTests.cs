using JizuraLive.Engine;
using Xunit;

namespace JizuraLive.Engine.Tests;

public class RngTests
{
    [Fact]
    public void WPick_AllZeroWeights_TakesLast()
    {
        // JS wpick 契约：权重和为 0 时不中途命中，取最后（<= 会提前命中首个，曾与注释矛盾）
        var rng = new Rng(7331);
        for (var i = 0; i < 100; i++)
            Assert.Equal("last", rng.WPick(new[] { ("first", 0.0), ("mid", 0.0), ("last", 0.0) }));
    }

    [Fact]
    public void WPick_SinglePositiveWeight_WinsAlways()
    {
        var rng = new Rng(9127);
        for (var i = 0; i < 100; i++)
            Assert.Equal("yes", rng.WPick(new[] { ("no", 0.0), ("yes", 1.0) }));
    }

    [Fact]
    public void WPick_NormalWeights_StaysInRangeAndReproducible()
    {
        var a = new Rng(42);
        var b = new Rng(42);
        var list = new[] { ("a", 1.0), ("b", 2.0), ("c", 3.0) };
        for (var i = 0; i < 200; i++)
        {
            var va = a.WPick(list);
            Assert.Contains(va, new[] { "a", "b", "c" });
            Assert.Equal(va, b.WPick(list)); // 同种子同序列
        }
    }
}
