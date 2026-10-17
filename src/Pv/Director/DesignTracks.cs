using JizuraLive.Render.Effects;

namespace JizuraLive.Pv;

public enum DesignTrackKind
{
    Layout,

    Enter,

    Hold,

    Exit,

    Decor,

    Treat,

    Bg,

    Cam,

    Fx,

    Trans,
}

public static class DesignTrackNames
{
    public static string Label(DesignTrackKind kind) => kind switch
    {
        DesignTrackKind.Layout => "布局",
        DesignTrackKind.Enter => "入场",
        DesignTrackKind.Hold => "保持",
        DesignTrackKind.Exit => "退场",
        DesignTrackKind.Decor => "装饰",
        DesignTrackKind.Treat => "文字处理",
        DesignTrackKind.Bg => "背景",
        DesignTrackKind.Cam => "运镜",
        DesignTrackKind.Fx => "画面效果",
        DesignTrackKind.Trans => "转场",
        _ => kind.ToString(),
    };
}

public sealed class DesignSegment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public DesignTrackKind Track { get; set; }

    public double Start { get; set; }

    public double End { get; set; }

    public double FadeIn { get; set; }

    public double FadeOut { get; set; }

    public string Technique { get; set; } = "";

    public Dictionary<string, double> Params { get; set; } = new(StringComparer.Ordinal);

    public string Source { get; set; } = "user";

    public bool Locked { get; set; }

    public string? RefId { get; set; }
}

public static class DesignTracks
{
    public static List<DesignSegment> FromEffectNode(EffectNode node, double duration = 0)
    {
        var segments = new List<DesignSegment>();
        for (int i = 0; i < node.Windows.Count; i++)
        {
            var w = node.Windows[i];
            if (!w.IsValid) continue;
            segments.Add(new DesignSegment
            {
                Track = DesignTrackKind.Fx,
                Start = w.Start,
                End = w.End,
                FadeIn = w.FadeIn,
                FadeOut = w.FadeOut,
                Technique = node.DefId,
                Source = node.Source,
                Locked = node.Locked,
                RefId = $"{node.InstanceId}#{i}",
            });
        }

        if (segments.Count > 0) return segments;

        segments.Add(new DesignSegment
        {
            Track = DesignTrackKind.Fx,
            Start = 0,
            End = Math.Max(0, duration),
            Technique = node.DefId,
            Source = node.Source,
            Locked = node.Locked,
            RefId = node.InstanceId,
        });

        return segments;
    }

    public static DesignSegment FromCameraShot(CameraShot shot)
    {
        var seg = new DesignSegment
        {
            Track = DesignTrackKind.Cam,
            Start = shot.Start,
            End = shot.End,
            Technique = shot.Preset,
            Source = shot.Source,
            Locked = shot.Locked,
        };

        foreach (var (key, value) in shot.Params)
            seg.Params[key] = value;

        return seg;
    }

    public static DesignSegment FromLyricStyleSpan(LyricStyleSpan span)
    {
        return new DesignSegment
        {
            Track = DesignTrackKind.Treat,
            Start = span.Start,
            End = span.End,
            Technique = span.Style,
            Source = span.Source,
            Locked = span.Locked,
        };
    }

    public static DesignSegment FromTransition(string technique, double start, double duration,
        IReadOnlyDictionary<string, double>? pars = null)
    {
        var seg = new DesignSegment
        {
            Track = DesignTrackKind.Trans,
            Start = start,
            End = start + Math.Max(0, duration),
            Technique = technique ?? "",
        };

        if (pars is not null)
            foreach (var (key, value) in pars) seg.Params[key] = value;

        return seg;
    }
}

public sealed class TransitionTrack
{
    public List<DesignSegment> Segments { get; } = new();

    public void Add(string technique, double start, double duration,
        IReadOnlyDictionary<string, double>? pars = null)
    {
        if (string.IsNullOrWhiteSpace(technique) || !double.IsFinite(start) || !(duration > 0)) return;
        Segments.Add(DesignTracks.FromTransition(technique, start, duration, pars));
    }

    public void Clear() => Segments.Clear();

    public void LoadFrom(IEnumerable<DesignSegment>? segments)
    {
        Segments.Clear();
        if (segments is null) return;
        foreach (var seg in segments)
        {
            if (seg is null || seg.Track != DesignTrackKind.Trans) continue;
            Add(seg.Technique, seg.Start, seg.End - seg.Start, seg.Params);
        }
    }

    public DesignSegment? At(double time)
    {
        if (!double.IsFinite(time)) return null;
        foreach (var seg in Segments)
            if (time >= seg.Start && time < seg.End) return seg;
        return null;
    }

    public bool Covers(double time) => At(time) is not null;
}
