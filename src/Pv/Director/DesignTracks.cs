using JizuraLive.App.I18n;
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

    LayerCam,
}

public static class DesignTrackNames
{
    public static string Label(DesignTrackKind kind) => kind switch
    {
        DesignTrackKind.Layout => L.T("布局"),
        DesignTrackKind.Enter => L.T("入场"),
        DesignTrackKind.Hold => L.T("保持"),
        DesignTrackKind.Exit => L.T("退场"),
        DesignTrackKind.Decor => L.T("装饰"),
        DesignTrackKind.Treat => L.T("文字处理"),
        DesignTrackKind.Bg => L.T("背景"),
        DesignTrackKind.Cam => L.T("运镜"),
        DesignTrackKind.Fx => L.T("画面效果"),
        DesignTrackKind.Trans => L.T("转场"),
        DesignTrackKind.LayerCam => L.T("角色运镜"),
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

    public int Seed { get; set; }

    public Dictionary<string, string>? Colors { get; set; }

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

}

public sealed class TransitionTrack
{
    private readonly object _gate = new();
    private List<DesignSegment> _segments = new();

    public void LoadFrom(IEnumerable<DesignSegment>? segments)
    {
        var next = new List<DesignSegment>();
        if (segments is not null)
        {
            foreach (var seg in segments)
            {
                if (seg is null || seg.Track != DesignTrackKind.Trans) continue;
                if (string.IsNullOrWhiteSpace(seg.Technique) || !(seg.End - seg.Start > 0)) continue;
                next.Add(seg);
            }
        }

        lock (_gate) _segments = next;
    }

    public DesignSegment? At(double time)
    {
        if (!double.IsFinite(time)) return null;
        lock (_gate)
        {
            foreach (var seg in _segments)
                if (time >= seg.Start && time < seg.End) return seg;
        }

        return null;
    }

}
