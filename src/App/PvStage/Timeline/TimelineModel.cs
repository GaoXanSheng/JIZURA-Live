using JizuraLive.Engine;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage.Timeline;

public readonly record struct TKey(double TimeSeconds, double Value, string Interp,
    double OutX = 0.333, double OutY = 0, double InX = 0.333, double InY = 0);

public sealed class TCurve
{
    public ParamAddress Address;

    public string Label = "";

    public double Min = 0, Max = 1;

    public List<TKey> Keys = new();
}

public sealed class TClip
{
    public string Id = "";
    public double Start, End;
    public string Text = "";
}

public sealed class TSnapshot
{
    public double DurationSeconds;
    public double PlayheadSeconds;
    public List<TCurve> Curves = new();
    public List<TClip> Lyrics = new();

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
