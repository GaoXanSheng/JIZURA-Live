using JizuraLive.Pv;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 粒子场是纯函数：同输入必须逐位同结果、坐标有限、稳态零分配——
/// 这三条是导出对拍与 seek 的前提（位置是时间的解析函数，不靠逐帧累加）。
/// </summary>
public class ParticleFieldTests
{
    private const int W = 640;
    private const int H = 360;

    private static PvParticleConfig Cfg(string kind = "sakura", double density = 120, int seed = 1,
        List<double>? bursts = null) => new()
    {
        Kind = kind,
        Density = density,
        Seed = seed,
        Bursts = bursts ?? new List<double>(),
    };

    private static ParticleState[] Run(PvParticleConfig cfg, double time)
    {
        var buffer = new ParticleState[ParticleField.MaxParticles];
        var count = ParticleField.Compute(cfg, time, W, H, buffer);
        return buffer[..count];
    }

    private static double Dist(ParticleState s) =>
        Math.Sqrt(Math.Pow(s.X - W / 2.0, 2) + Math.Pow(s.Y - H / 2.0, 2));

    [Fact]
    public void SameInputGivesIdenticalResults()
    {
        var cfg = Cfg(bursts: new List<double> { 2.0 });
        for (var t = 0.0; t < 3.0; t += 0.25)
            Assert.Equal(Run(cfg, t), Run(cfg, t));
    }

    [Fact]
    public void CountFollowsDensityAndStaysWithinBudget()
    {
        Assert.Empty(Run(Cfg(density: 0), 0));
        Assert.Equal(40, Run(Cfg(density: 40), 0).Length);
        Assert.Equal(400, Run(Cfg(density: 400), 0).Length);
        Assert.Equal(ParticleField.MaxAmbient, Run(Cfg(density: 9999), 0).Length);
        Assert.True(ParticleField.MaxAmbient <= ParticleField.MaxParticles);
    }

    [Fact]
    public void OutputSpanLimitsCount()
    {
        var small = new ParticleState[10];
        Assert.Equal(10, ParticleField.Compute(Cfg(density: 300), 0, W, H, small));
    }

    [Fact]
    public void KindsProduceDifferentFields()
    {
        var sakura = Run(Cfg("sakura"), 1.0);
        var sparkle = Run(Cfg("sparkle"), 1.0);
        var bokeh = Run(Cfg("bokeh"), 1.0);

        Assert.NotEqual(sakura, sparkle);
        Assert.NotEqual(sakura, bokeh);
        Assert.NotEqual(sparkle, bokeh);
    }

    /// <summary>
    /// 爆发窗口 [b, b+1.2)：窗口外一颗都没有；窗口内从中心附近喷出，且越晚离中心越远（缓出辐射）。
    /// Density=0 让环境粒子全部缺席，留下的就是纯爆发粒子。
    /// </summary>
    [Fact]
    public void BurstAppearsOnlyInsideItsWindowAndExpandsOutward()
    {
        var cfg = Cfg(density: 0, bursts: new List<double> { 2.0 });

        Assert.Empty(Run(cfg, 1.9));

        var early = Run(cfg, 2.3);
        Assert.InRange(early.Length, 30, 50);
        Assert.True(early.Min(Dist) < 0.4 * Math.Max(W, H), "爆发粒子应从画面中心附近喷出");

        var late = Run(cfg, 2.6);
        Assert.Equal(early.Length, late.Length);
        Assert.True(late.Average(Dist) > early.Average(Dist), "缓出辐射：更晚的时刻粒子应离中心更远");
    }

    [Fact]
    public void AllStatesAreFinite()
    {
        var times = new[] { -1.0, 0.0, 0.5, 1.234, 3.3, 100.0 };
        foreach (var kind in new[] { "sakura", "sparkle", "bokeh" })
        {
            var cfg = Cfg(kind, density: 400, bursts: new List<double> { 0.5, 3.0 });
            foreach (var t in times)
            {
                foreach (var s in Run(cfg, t))
                {
                    Assert.True(float.IsFinite(s.X) && float.IsFinite(s.Y) && float.IsFinite(s.Rotation)
                        && float.IsFinite(s.Scale) && float.IsFinite(s.Alpha), $"{kind} t={t} 出现非有限值 {s}");
                }
            }
        }
    }

    [Fact]
    public void SteadyStateDoesNotAllocate()
    {
        var cfg = Cfg(density: 400, bursts: new List<double> { 1.0, 2.0, 3.0 });
        var buffer = new ParticleState[ParticleField.MaxParticles];
        for (var i = 0; i < 4; i++) ParticleField.Compute(cfg, i * 0.1, W, H, buffer);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) ParticleField.Compute(cfg, 0.5 + i * 0.01, W, H, buffer);
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
    }
}
