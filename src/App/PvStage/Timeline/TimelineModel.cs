using JizuraLive.Engine;
using JizuraLive.Pv;
using JizuraLive.Vts;

namespace JizuraLive.App.PvStage.Timeline;

public readonly record struct TKey(double TimeSeconds, double Value, string Interp);

public sealed class TCurve
{
    public ParamAddress Address;

    public string Label = "";

    public double Min = 0, Max = 1;

    public double SpecMin = double.NegativeInfinity, SpecMax = double.PositiveInfinity;

    public List<TKey> Keys = new();
}

public sealed class TClip
{
    public double Start, End;
    public string Text = "";
}

public sealed class TDesignSegment
{
    public string RefId = "";
    public int Track;
    public double Start, End, FadeIn, FadeOut;
    public string Technique = "";
    public string Label = "";
    public string Source = "user";
    public bool Locked;
    public Dictionary<string, double> Params = new(StringComparer.Ordinal);
}

public sealed class TDesignTrack
{
    public DesignTrackKind Kind;
    public List<TDesignSegment> Segments = new();
}

public sealed class TVtsCue
{
    public double Time;
    public double Duration;
    public string Target = "";
    public string Label = "";
    public VtsCueMode Mode;
}

public sealed class TVtsCueTrack
{
    public VtsCueKind Kind;
    public string Id = "";
    public string Label = "";
    public List<TVtsCue> Cues = new();
}

public sealed class TSnapshot
{
    public double DurationSeconds;
    public double PlayheadSeconds;
    public List<TCurve> Curves = new();
    public List<TClip> Lyrics = new();
    public List<TDesignTrack> DesignTracks = new();
    public List<TVtsCueTrack> VtsCueTracks = new();

    public HashSet<string> CollapsedTracks = new(StringComparer.Ordinal);

    public Dictionary<string, double> TrackHeights = new(StringComparer.Ordinal);

    public WaveformPeaks? Waveform;
}

public readonly record struct TClipEdit(int Index, double Start, double End);

public sealed class TEdit
{
    public ParamAddress Address;
    public List<TKey> Before = new();
    public List<TKey> After = new();

    public string Reason = "";

    public TClipEdit? Clip;
}
