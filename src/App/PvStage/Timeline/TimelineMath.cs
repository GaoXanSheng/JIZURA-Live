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
    public static readonly string[] Interps = ["Step", "Linear", "SmoothStep"];

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
            _ => a.Value + (b.Value - a.Value) * u,
        };
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
