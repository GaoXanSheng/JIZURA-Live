using JizuraLive.Engine;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage.Timeline;

/// <summary>
/// 关键帧。<see cref="Interp"/> 与 <see cref="CurveInterp"/> 同名：
/// "Step" | "Linear" | "SmoothStep" | "Bezier" | "Spring"。
/// <see cref="OutX"/>/<see cref="OutY"/>/<see cref="InX"/>/<see cref="InY"/> 是 Bezier 控制柄的归一化 u/v，仅 Bezier 段参与求值。
/// </summary>
public readonly record struct TKey(double TimeSeconds, double Value, string Interp,
    double OutX = 0.333, double OutY = 0, double InX = 0.333, double InY = 0);

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

/// <summary>歌词轨拖动后的落点。定位用<b>下标</b>：宿主填的 <see cref="TClip.Id"/> 目前是空串。</summary>
public readonly record struct TClipEdit(int Index, double Start, double End);

/// <summary>
/// 一次编辑事务（对应一次撤销）。<see cref="Reason"/> = "clip" 时是歌词轨（带 <see cref="Clip"/>），
/// 否则是自动化轨，Before/After 是该轨**完整** key 列表。
/// </summary>
public sealed class TEdit
{
    public ParamAddress Address;
    public List<TKey> Before = new();
    public List<TKey> After = new();

    /// <summary>"add" | "move" | "delete" | "interp" | "value" | "clip"。</summary>
    public string Reason = "";

    /// <summary>Reason = "clip"：拖动的歌词句，区间规则见 <see cref="TimelineMath.ClipFault"/>。</summary>
    public TClipEdit? Clip;
}
