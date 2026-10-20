using JizuraLive.App.I18n;
using JizuraLive.Engine;

namespace JizuraLive.App.PvStage.Timeline;

public enum ClipEdge
{
    None,
    Start,
    End,
}

public readonly record struct ClipRange(double Start, double End);

public readonly record struct ClipHit(int Index, ClipEdge Edge);

public static class TimelineMath
{
    public const double TimeEpsilon = 1e-6;

    public const double KeyHitRadiusPx = 5.0;

    public const double ClipEdgeHitPx = 6.0;

    public const double MinClipSeconds = 1e-3;

    public const double MinSecondsPerScreen = 2.0;

    private const double MinGridPixels = 10.0;

    public const double KeyframeStep = 0.1;

    private static readonly double[] RulerSteps = [1, 5, 10, 30, 60, 300];

    public static readonly string[] Interps = ["Step", "Linear", "SmoothStep", "Bezier", "Spring"];

    private static readonly double SpringQ1 = Math.Exp(-5) * Math.Cos(12);

    public static double GridStep(double pixelsPerSecond, double fps)
    {
        double frame = fps > 0 ? 1.0 / fps : 1.0;
        if (pixelsPerSecond >= MinGridPixels / frame) return frame;
        if (pixelsPerSecond >= MinGridPixels * 10) return 0.1;
        if (pixelsPerSecond >= MinGridPixels * 2) return 0.5;
        return 1.0;
    }

    public static double Snap(double seconds, double step, bool enabled) =>
        enabled && step > 0 ? Math.Round(seconds / step, MidpointRounding.AwayFromZero) * step : seconds;

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

    public static double ValueToY(double value, double min, double max, double top, double height)
    {
        if (max <= min) return top + height / 2;
        return top + height * (1 - (value - min) / (max - min));
    }

    public static double ClampPixelsPerSecond(double pixelsPerSecond, double viewportWidth, double durationSeconds)
    {
        if (viewportWidth <= 0) return pixelsPerSecond;
        double max = viewportWidth / MinSecondsPerScreen;
        double min = viewportWidth / Math.Max(durationSeconds, MinSecondsPerScreen);
        return Math.Clamp(pixelsPerSecond, min, max);
    }

    public static double ClampViewStart(double viewStart, double pixelsPerSecond, double viewportWidth, double durationSeconds)
    {
        if (pixelsPerSecond <= 0 || viewportWidth <= 0) return viewStart;
        double maxStart = Math.Max(0, durationSeconds - viewportWidth / pixelsPerSecond);
        return Math.Clamp(viewStart, 0, maxStart);
    }

    public static double ZoomViewStart(double anchorTime, double anchorX, double pixelsPerSecond) =>
        pixelsPerSecond > 0 ? anchorTime - anchorX / pixelsPerSecond : 0;

    public static int IndexAtTime(IReadOnlyList<TKey> keys, double time)
    {
        for (int i = 0; i < keys.Count; i++)
            if (Math.Abs(keys[i].TimeSeconds - time) <= TimeEpsilon) return i;
        return -1;
    }

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

    public static ClipHit? HitClip(IReadOnlyList<TClip> clips, double time, double edgeToleranceSeconds)
    {
        if (HitClipEdge(clips, time, edgeToleranceSeconds) is { } edge) return edge;
        for (int i = 0; i < clips.Count; i++)
            if (time >= clips[i].Start && time < clips[i].End) return new ClipHit(i, ClipEdge.None);
        return null;
    }

    private static ClipHit? HitClipEdge(IReadOnlyList<TClip> clips, double time, double tolerance)
    {
        ClipHit? best = null;
        double bestDistance = tolerance;
        for (int i = 0; i < clips.Count; i++)
        {
            double toStart = Math.Abs(time - clips[i].Start);
            double toEnd = Math.Abs(time - clips[i].End);
            double distance = Math.Min(toStart, toEnd);
            if (distance > tolerance || (best is not null && distance >= bestDistance)) continue;
            best = new ClipHit(i, toStart <= toEnd ? ClipEdge.Start : ClipEdge.End);
            bestDistance = distance;
        }
        return best;
    }

    public static ClipRange DragClip(in ClipRange origin, ClipEdge edge, double deltaSeconds, double grid, bool snap)
    {
        if (edge == ClipEdge.None)
        {
            double start = Math.Max(0, Snap(origin.Start + deltaSeconds, grid, snap));
            return new(start, start + (origin.End - origin.Start));
        }

        double moved = Math.Max(0, Snap((edge == ClipEdge.Start ? origin.Start : origin.End) + deltaSeconds, grid, snap));
        return edge == ClipEdge.Start ? new(moved, origin.End) : new(origin.Start, moved);
    }

    public static string? ClipFault(IReadOnlyList<TClip> clips, int index, in ClipRange range)
    {
        if (range.End - range.Start <= MinClipSeconds) return L.T("歌词条太短：结束必须晚于开始");

        for (int i = 0; i < clips.Count; i++)
        {
            if (i == index) continue;
            if (range.Start < clips[i].End && clips[i].Start < range.End) return L.T("歌词条重叠：不能盖住相邻的句子");
        }

        return null;
    }

    public static List<LyricClip> WithClipRange(IReadOnlyList<LyricClip> clips, int index, long startMs, long endMs)
    {
        var next = new List<LyricClip>(clips);
        next[index] = clips[index] with { StartMs = startMs, EndMs = endMs };
        return next.OrderBy(static c => c.StartMs).ToList();
    }

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
            "Bezier" => a.Value + (b.Value - a.Value) * AutoBezierValue(u),
            "Spring" => a.Value + (b.Value - a.Value) * SpringValue(u),
            _ => a.Value + (b.Value - a.Value) * u,
        };
    }

    private static double AutoBezierValue(double u)
    {
        double lo = 0, hi = 1;
        for (int i = 0; i < 20; i++)
        {
            double mid = (lo + hi) / 2;
            if (Cubic(mid, 0, 0.333, 0.667, 1) < u) lo = mid;
            else hi = mid;
        }

        return Cubic((lo + hi) / 2, 0, 0, 1, 1);
    }

    private static double Cubic(double w, double p0, double p1, double p2, double p3)
    {
        double s = 1 - w;
        return s * s * s * p0 + 3 * s * s * w * p1 + 3 * s * w * w * p2 + w * w * w * p3;
    }

    private static double SpringValue(double u) =>
        (1 - Math.Exp(-5 * u) * Math.Cos(12 * u)) / (1 - SpringQ1);

    public static long TimeToSample(double seconds, double durationSeconds, long sampleCount)
    {
        if (durationSeconds <= 0 || sampleCount <= 0) return 0;
        return Math.Clamp((long)(seconds / durationSeconds * sampleCount), 0, sampleCount - 1);
    }

    public static void Sort(List<TKey> keys) =>
        keys.Sort(static (a, b) => a.TimeSeconds.CompareTo(b.TimeSeconds));
}
