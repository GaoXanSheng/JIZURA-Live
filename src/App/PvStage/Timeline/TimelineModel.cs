using JizuraLive.Engine;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage.Timeline;

/// <summary>关键帧。<see cref="Interp"/> 与 <see cref="CurveInterp"/> 同名："Step" | "Linear" | "SmoothStep"。</summary>
public readonly record struct TKey(double TimeSeconds, double Value, string Interp);

/// <summary>一条自动化轨。轨道身份 = 参数地址（同址只有一条活动曲线）。</summary>
public sealed class TCurve
{
    public ParamAddress Address;

    /// <summary>轨道头显示，如「镜头 · 缩放」。</summary>
    public string Label = "";

    /// <summary>值显示域。</summary>
    public double Min = 0, Max = 1;

    public List<TKey> Keys = new();
}

/// <summary>歌词轨一句，半开区间 [Start, End)。</summary>
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

    /// <summary>null = 波形轨留空。</summary>
    public WaveformPeaks? Waveform;
}

/// <summary>一次编辑事务（对应一次撤销）。Before/After 是该轨**完整** key 列表。</summary>
public sealed class TEdit
{
    public ParamAddress Address;
    public List<TKey> Before = new();
    public List<TKey> After = new();

    /// <summary>"add" | "move" | "delete" | "interp" | "value"。</summary>
    public string Reason = "";
}
