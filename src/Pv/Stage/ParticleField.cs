namespace JizuraLive.Pv;

/// <summary>一颗粒子在本帧的形态（设计像素坐标，Rotation 为角度制）。</summary>
public readonly record struct ParticleState(float X, float Y, float Rotation, float Scale, float Alpha);

/// <summary>
/// 粒子场求值：**纯函数、无状态、零分配**。每颗粒子的随机参数由 <c>hash(Seed, 序号)</c> 决定，
/// 位置是时间的解析函数——可以任意跳转/导出，同一时刻永远给出同一结果（导出对拍与 seek 都靠这条）。
///
/// 输出顺序：先环境粒子（索引 <c>[0, <see cref="AmbientCount"/>)</c>），再爆发星光；
/// 绘制方按这个分界选精灵（环境粒子按 Kind，爆发粒子一律星光）。
/// </summary>
public static class ParticleField
{
    public const int MaxParticles = 600;

    /// <summary>环境粒子数上界，即 <see cref="PvParticleConfig.Density"/> 的有效区间上端。</summary>
    public const int MaxAmbient = 400;

    /// <summary>每次爆发喷出的星光数。</summary>
    private const int BurstParticles = 40;

    /// <summary>爆发寿命（秒）：[b, b+1.2) 之外没有爆发粒子。</summary>
    private const double BurstLife = 1.2;

    private const int Sakura = 0;
    private const int Sparkle = 1;
    private const int Bokeh = 2;

    /// <summary>本帧的环境粒子数（0..<see cref="MaxAmbient"/>；非有限 Density 按 0 处理）。</summary>
    public static int AmbientCount(PvParticleConfig cfg)
    {
        if (cfg is null) return 0;
        var density = cfg.Density;
        return double.IsFinite(density) ? (int)Math.Clamp(Math.Round(density), 0, MaxAmbient) : 0;
    }

    /// <summary>
    /// 把 <paramref name="cfg"/> 在 <paramref name="time"/> 时刻的粒子写进 <paramref name="output"/>，
    /// 返回写入数量（≤ min(output.Length, <see cref="MaxParticles"/>)）。不分配、不改 <paramref name="cfg"/>。
    /// </summary>
    public static int Compute(PvParticleConfig cfg, double time, int width, int height,
        Span<ParticleState> output)
    {
        if (cfg is null) return 0;
        if (!double.IsFinite(time)) time = 0;

        var w = MathF.Max(1f, width);
        var h = MathF.Max(1f, height);
        var limit = Math.Min(output.Length, MaxParticles);
        var speed = Finite(cfg.Speed, 1.0, 0.2, 3.0);
        var size = Finite(cfg.Size, 1.0, 0.3, 3.0);
        var seed = cfg.Seed;

        var count = 0;
        var ambient = Math.Min(AmbientCount(cfg), limit);
        var kind = KindCode(cfg.Kind);
        for (var i = 0; i < ambient; i++)
        {
            output[count++] = kind switch
            {
                Sparkle => SparkleAt(seed, i, time, w, h, speed, size),
                Bokeh => BokehAt(seed, i, time, w, h, speed, size),
                _ => SakuraAt(seed, i, time, w, h, speed, size),
            };
        }

        var bursts = cfg.Bursts;
        if (bursts is not null)
        {
            for (var b = 0; b < bursts.Count && count < limit; b++)
            {
                var age = time - bursts[b];
                // 取反写法顺带把 NaN 的爆发时刻也挡在窗口外
                if (!(age >= 0) || age >= BurstLife) continue;
                for (var k = 0; k < BurstParticles && count < limit; k++)
                    output[count++] = BurstAt(seed, b, k, age, w, h, size);
            }
        }

        return count;
    }

    /// <summary>花瓣：自上而下飘落、正弦横摆 + 斜向漂移、自转，纵向与横向都循环。</summary>
    private static ParticleState SakuraAt(int seed, int i, double time, float w, float h,
        double speed, double size)
    {
        var fall = (0.05 + 0.07 * Unit(seed, i, 1)) * speed;          // 屏高/秒
        var progress = Frac(Unit(seed, i, 2) + time * fall / 1.2);
        var y = -0.1f * h + (float)progress * 1.2f * h;

        var swayAmp = w * (0.02f + 0.05f * Unit(seed, i, 3));
        var sway = Math.Sin(2 * Math.PI * (time * (0.5 + 1.5 * Unit(seed, i, 4)) + Unit(seed, i, 5)));
        var x = Wrap((float)(Unit(seed, i, 4) * w + time * (0.01 + 0.03 * Unit(seed, i, 3)) * w * speed
            + swayAmp * sway), w);

        var rotation = (float)(Unit(seed, i, 6) * 360 + time * (30 + 90 * Unit(seed, i, 3)) * speed);
        var scale = (float)(size * (0.7 + 0.6 * Unit(seed, i, 7)));
        var alpha = (float)Math.Clamp(Math.Min(progress, 1 - progress) / 0.08, 0, 1);
        return new ParticleState(x, y, rotation, scale, alpha);
    }

    /// <summary>星光：原地闪烁（相位随机的 alpha 脉动）并缓慢上升。</summary>
    private static ParticleState SparkleAt(int seed, int i, double time, float w, float h,
        double speed, double size)
    {
        var x = Unit(seed, i, 1) * w;
        var rise = (0.01 + 0.02 * Unit(seed, i, 2)) * speed;
        var progress = Frac(Unit(seed, i, 3) + time * rise);
        var y = h - (float)progress * h;

        var twinkle = 0.5 + 0.5 * Math.Sin(2 * Math.PI * (time * (0.6 + 1.4 * Unit(seed, i, 4)) + Unit(seed, i, 5)));
        var alpha = (0.15 + 0.85 * twinkle * twinkle)
            * Math.Clamp(Math.Min(progress, 1 - progress) / 0.05, 0, 1);

        var rotation = (float)(Unit(seed, i, 1) * 360 + time * 25 * speed);
        var scale = (float)(size * (0.6 + 0.7 * Unit(seed, i, 2)));
        return new ParticleState(x, y, rotation, scale, (float)alpha);
    }

    /// <summary>光斑：大而淡的圆，缓慢斜向漂移并循环。</summary>
    private static ParticleState BokehAt(int seed, int i, double time, float w, float h,
        double speed, double size)
    {
        var x = Wrap((float)(Unit(seed, i, 1) * w + time * (0.004 + 0.010 * Unit(seed, i, 2)) * w * speed), w);
        var y = Wrap((float)(Unit(seed, i, 3) * h - time * (0.003 + 0.008 * Unit(seed, i, 4)) * h * speed), h);

        var scale = (float)(size * (0.8 + 0.9 * Unit(seed, i, 5)));
        var alpha = (float)(0.06 + 0.12 * Unit(seed, i, 4));
        return new ParticleState(x, y, 0f, scale, alpha);
    }

    /// <summary>爆发星光：从画面中心按缓出速度向外辐射并淡出。</summary>
    private static ParticleState BurstAt(int seed, int b, int k, double age, float w, float h, double size)
    {
        var progress = age / BurstLife;
        var ease = 1 - Math.Pow(1 - progress, 3);

        var key = b * BurstParticles + k;
        var angle = (k / (double)BurstParticles + Unit(seed, key, 11) * 0.12) * 2 * Math.PI;
        var reach = 0.45 * Math.Sqrt((double)w * w + (double)h * h) * ease
            * (0.45 + 0.55 * Unit(seed, key, 12));

        var x = (float)(w * 0.5 + Math.Cos(angle) * reach);
        var y = (float)(h * 0.5 + Math.Sin(angle) * reach);
        var alpha = (float)Math.Pow(1 - progress, 1.5);
        var rotation = (float)(Unit(seed, key, 13) * 360 + age * 120);
        var scale = (float)(size * (0.7 + 0.6 * Unit(seed, key, 14)) * (1 - 0.35 * progress));
        return new ParticleState(x, y, rotation, scale, alpha);
    }

    private static int KindCode(string? kind) =>
        string.Equals(kind, "sparkle", StringComparison.Ordinal) ? Sparkle
        : string.Equals(kind, "bokeh", StringComparison.Ordinal) ? Bokeh
        : Sakura;

    private static double Finite(double value, double fallback, double min, double max) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;

    private static double Frac(double v) => v - Math.Floor(v);

    private static float Wrap(float v, float span) => v - MathF.Floor(v / span) * span;

    /// <summary>按 <c>(Seed, 序号, 用途)</c> 取一个 [0,1) 的确定性随机数（同一入参永远同一值）。</summary>
    private static float Unit(int seed, int index, uint salt) => (Hash(seed, index, salt) >> 8) * (1f / 16777216f);

    private static uint Hash(int seed, int index, uint salt)
    {
        var h = (ulong)(uint)seed * 0x9E3779B97F4A7C15UL
            ^ (ulong)(uint)index * 0xC2B2AE3D27D4EB4FUL
            ^ salt;
        h ^= h >> 30;
        h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 27;
        h *= 0x94D049BB133111EBUL;
        return (uint)(h ^ (h >> 31));
    }
}
