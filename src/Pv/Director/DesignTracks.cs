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
    public static DesignSegment FromEffectNode(EffectNode node, double duration = 0)
    {
        var w = node.Windows.Count > 0 ? node.Windows[0] : default;

        var seg = new DesignSegment
        {
            Track = DesignTrackKind.Fx,
            Start = w.IsValid ? w.Start : 0,
            End = w.IsValid ? w.End : Math.Max(0, duration),
            FadeIn = w.IsValid ? w.FadeIn : 0,
            FadeOut = w.IsValid ? w.FadeOut : 0,
            Technique = node.DefId,
            Source = node.Source,
            Locked = node.Locked,
            RefId = node.InstanceId,
        };

        return seg;
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
}
