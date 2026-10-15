namespace JizuraLive.App.PvStage.Timeline;

/// <summary>时间轴的纯换算（时间/值 ↔ 像素、网格吸附、缩放锚点、命中测试），无 WPF 依赖。</summary>
public static class TimelineMath
{
    /// <summary>判定「同一时刻」的窗口，与 <see cref="JizuraLive.Pv.FloatCurve"/> 同口径（1µs）。</summary>
    public const double TimeEpsilon = 1e-6;

    /// <summary>key 命中半径（像素）。</summary>
    public const double KeyHitRadiusPx = 5.0;

    /// <summary>缩放上限：最窄一屏 2 秒。</summary>
    public const double MinSecondsPerScreen = 2.0;

    /// <summary>网格步长至少要占这么多像素，否则细到看不清。</summary>
    private const double MinGridPixels = 10.0;

    /// <summary>标尺刻度候选（秒），取第一个宽度够的。</summary>
    private static readonly double[] RulerSteps = [1, 5, 10, 30, 60, 300];

    /// <summary>Interp 取值，顺序 = 右键菜单顺序。</summary>
    public static readonly string[] Interps = ["Step", "Linear", "SmoothStep", "Bezier", "Spring"];

    /// <summary>Bezier 反查参数的二分次数：1/2²⁰ ≈ 1e-6，与契约的容差同量级。</summary>
    private const int BezierIterations = 20;

    /// <summary>Spring 归一化式的分母 1−q(1)，q(u)=e^(−5u)cos(12u)。</summary>
    private static readonly double SpringQ1 = Math.Exp(-5) * Math.Cos(12);

    /// <summary>吸附网格：取仍占 ≥10px 的最细步长（1s / 0.5s / 0.1s / 1 帧）。</summary>
    public static double GridStep(double pixelsPerSecond, double fps)
    {
        double frame = fps > 0 ? 1.0 / fps : 1.0;
        if (pixelsPerSecond >= MinGridPixels / frame) return frame;
        if (pixelsPerSecond >= MinGridPixels * 10) return 0.1;
        if (pixelsPerSecond >= MinGridPixels * 2) return 0.5;
        return 1.0;
    }

    /// <summary>吸附到网格；<paramref name="enabled"/> = false（Alt 关吸附）时原样返回。</summary>
    public static double Snap(double seconds, double step, bool enabled) =>
        enabled && step > 0 ? Math.Round(seconds / step, MidpointRounding.AwayFromZero) * step : seconds;

    /// <summary>标尺刻度步长（秒）：第一个宽度 ≥70px 的候选。</summary>
    public static double RulerStep(double pixelsPerSecond)
    {
        foreach (double step in RulerSteps)
            if (step * pixelsPerSecond >= 70) return step;
        return RulerSteps[^1];
    }

    public static double TimeToX(double seconds, double viewStartSeconds, double pixelsPerSecond) =>
        (seconds - viewStartSeconds) * pixelsPerSecond;

    public static double XToTime(double x, double viewStartSeconds, double pixelsPerSecond) =>
        pixelsPerSecond > 0 ? viewStartSeconds + x / pixelsPerSecond : viewStartSeconds;

    /// <summary>值 → y。域退化（Max ≤ Min）时居中。</summary>
    public static double ValueToY(double value, double min, double max, double top, double height)
    {
        if (max <= min) return top + height / 2;
        return top + height * (1 - (value - min) / (max - min));
    }

    public static double YToValue(double y, double min, double max, double top, double height)
    {
        if (max <= min || height <= 0) return (min + max) / 2;
        return min + (max - min) * (1 - (y - top) / height);
    }

    /// <summary>缩放钳制：整曲一屏（下界）↔ 2 秒一屏（上界）。</summary>
    public static double ClampPixelsPerSecond(double pixelsPerSecond, double viewportWidth, double durationSeconds)
    {
        if (viewportWidth <= 0) return pixelsPerSecond;
        double max = viewportWidth / MinSecondsPerScreen;
        double min = viewportWidth / Math.Max(durationSeconds, MinSecondsPerScreen);
        return Math.Clamp(pixelsPerSecond, min, max);
    }

    /// <summary>平移钳制：视图不越出 [0, duration]。</summary>
    public static double ClampViewStart(double viewStart, double pixelsPerSecond, double viewportWidth, double durationSeconds)
    {
        if (pixelsPerSecond <= 0 || viewportWidth <= 0) return viewStart;
        double maxStart = Math.Max(0, durationSeconds - viewportWidth / pixelsPerSecond);
        return Math.Clamp(viewStart, 0, maxStart);
    }

    /// <summary>缩放后让锚点时间仍落在锚点像素上：新起点 = 锚点时间 − 锚点像素 / 新比例。</summary>
    public static double ZoomViewStart(double anchorTime, double anchorX, double pixelsPerSecond) =>
        pixelsPerSecond > 0 ? anchorTime - anchorX / pixelsPerSecond : 0;

    /// <summary>该时刻上的 key 下标；没有返回 −1（同刻 = 替换语义的判据）。</summary>
    public static int IndexAtTime(IReadOnlyList<TKey> keys, double time)
    {
        for (int i = 0; i < keys.Count; i++)
            if (Math.Abs(keys[i].TimeSeconds - time) <= TimeEpsilon) return i;
        return -1;
    }

    /// <summary>命中半径内的 key；同刻优先（该 tick 上已有 key 时点击语义是替换这一个，而不是命中旁边那个）。</summary>
    public static int HitKey(IReadOnlyList<TKey> keys, double x, double viewStartSeconds, double pixelsPerSecond,
        double radius = KeyHitRadiusPx)
    {
        if (pixelsPerSecond <= 0) return -1;
        double clickTime = XToTime(x, viewStartSeconds, pixelsPerSecond);
        int sameTick = -1, nearest = -1;
        double best = double.MaxValue;
        for (int i = 0; i < keys.Count; i++)
        {
            double dx = Math.Abs(TimeToX(keys[i].TimeSeconds, viewStartSeconds, pixelsPerSecond) - x);
            if (Math.Abs(keys[i].TimeSeconds - clickTime) <= TimeEpsilon) sameTick = i;
            if (dx <= radius && dx < best)
            {
                best = dx;
                nearest = i;
            }
        }
        return sameTick >= 0 ? sameTick : nearest;
    }

    /// <summary>曲线在 time 处的显示值（左 key 的 Interp 决定区间；空轨取 fallback）。</summary>
    public static double Evaluate(IReadOnlyList<TKey> keys, double time, double fallback)
    {
        if (keys.Count == 0) return fallback;
        if (time <= keys[0].TimeSeconds) return keys[0].Value;
        if (time >= keys[^1].TimeSeconds) return keys[^1].Value;

        int lo = 0, hi = keys.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (keys[mid].TimeSeconds <= time) lo = mid;
            else hi = mid;
        }

        var a = keys[lo];
        var b = keys[lo + 1];
        double span = b.TimeSeconds - a.TimeSeconds;
        if (span <= 0) return b.Value;
        double u = (time - a.TimeSeconds) / span;
        return a.Interp switch
        {
            "Step" => a.Value,
            "SmoothStep" => a.Value + (b.Value - a.Value) * (u * u * (3 - 2 * u)),
            "Bezier" => BezierValue(a, b, u),
            "Spring" => a.Value + (b.Value - a.Value) * SpringValue(u),
            _ => a.Value + (b.Value - a.Value) * u,
        };
    }

    /// <summary>
    /// Bezier 段：控制柄是归一化 u/v，x 句柄 clamp 0..1 保证时间单调；
    /// 值 = y(w)，w 由 x(w)=u 二分反查（<see cref="BezierIterations"/>）。
    /// </summary>
    private static double BezierValue(in TKey a, in TKey b, double u)
    {
        double w = SolveBezierX(u, Math.Clamp(a.OutX, 0, 1), 1 - Math.Clamp(b.InX, 0, 1));
        return a.Value + (b.Value - a.Value) * Cubic(w, 0, a.OutY, 1 - b.InY, 1);
    }

    /// <summary>契约 §7.1 的 Spring 归一化式 f(u)=(1−q(u))/(1−q(1))；端点由 <see cref="Evaluate"/> 的钳制分支精确取 V0/V1。</summary>
    private static double SpringValue(double u) =>
        (1 - Math.Exp(-5 * u) * Math.Cos(12 * u)) / (1 - SpringQ1);

    /// <summary>二分反查 x(w)=u 的 w：x 控制点都在 0..1 内，x(w) 单调不减。</summary>
    private static double SolveBezierX(double u, double x1, double x2)
    {
        if (u <= 0) return 0;
        if (u >= 1) return 1;

        double lo = 0, hi = 1;
        for (int i = 0; i < BezierIterations; i++)
        {
            double mid = (lo + hi) / 2;
            if (Cubic(mid, 0, x1, x2, 1) < u) lo = mid;
            else hi = mid;
        }
        return (lo + hi) / 2;
    }

    /// <summary>三次贝塞尔在参数 w 处的分量（段端点是 0 与 1）。</summary>
    private static double Cubic(double w, double p0, double p1, double p2, double p3)
    {
        double s = 1 - w;
        return s * s * s * p0 + 3 * s * s * w * p1 + 3 * s * w * w * p2 + w * w * w * p3;
    }

    /// <summary>Bezier 出柄 P1 的时间/值（段 [a,b)，<see cref="TKey.OutX"/> 等是归一化坐标）。</summary>
    public static (double Time, double Value) OutHandlePoint(in TKey a, in TKey b) =>
        (a.TimeSeconds + a.OutX * (b.TimeSeconds - a.TimeSeconds), a.Value + a.OutY * (b.Value - a.Value));

    /// <summary>Bezier 入柄 P2 的时间/值（取段右 key 的入柄）。</summary>
    public static (double Time, double Value) InHandlePoint(in TKey a, in TKey b) =>
        (b.TimeSeconds - b.InX * (b.TimeSeconds - a.TimeSeconds), b.Value - b.InY * (b.Value - a.Value));

    /// <summary>拖动点 → 出柄归一化坐标：X clamp 0..1；Δv=0 时 Y 不影响形状，保持 <paramref name="outY"/>。</summary>
    public static (double X, double Y) OutHandleAt(double time, double value, in TKey a, in TKey b, double outY) =>
        (HandleU(time, a, b), HandleV(value, a, b, outY));

    /// <summary>拖动点 → 入柄归一化坐标：P2 从段右端反向量，故 X/Y 都是 1 减去正向比例。</summary>
    public static (double X, double Y) InHandleAt(double time, double value, in TKey a, in TKey b, double inY)
    {
        double delta = b.Value - a.Value;
        return (1 - HandleU(time, a, b), delta != 0 ? 1 - (value - a.Value) / delta : inY);
    }

    /// <summary>段内归一化时间，clamp 0..1（零长段取 0）。</summary>
    private static double HandleU(double time, in TKey a, in TKey b)
    {
        double span = b.TimeSeconds - a.TimeSeconds;
        return span > 0 ? Math.Clamp((time - a.TimeSeconds) / span, 0, 1) : 0;
    }

    /// <summary>段内归一化值；Δv=0 时形状与 Y 无关，保持 <paramref name="fallback"/>。</summary>
    private static double HandleV(double value, in TKey a, in TKey b, double fallback)
    {
        double delta = b.Value - a.Value;
        return delta != 0 ? (value - a.Value) / delta : fallback;
    }

    /// <summary>时刻 → 波形样本下标：波形覆盖整段音频，按时长线性摊到 SampleCount 上。</summary>
    public static long TimeToSample(double seconds, double durationSeconds, long sampleCount)
    {
        if (durationSeconds <= 0 || sampleCount <= 0) return 0;
        return Math.Clamp((long)(seconds / durationSeconds * sampleCount), 0, sampleCount - 1);
    }

    public static void Sort(List<TKey> keys) =>
        keys.Sort(static (a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
}
