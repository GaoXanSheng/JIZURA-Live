namespace JizuraLive.Engine;

public sealed class Section
{
    public double Start;
    public double End;
    public string Kind = "intro";

    /// <summary>重复结构标签 A/B/C…：相同标签=相似段，按首次出现顺序命名。</summary>
    public string Label = "A";

    /// <summary>段平均能量，按全曲最大段能量归一化到 0..1。</summary>
    public double Energy;
}

public sealed class BeatMap
{
    public double Bpm;
    public List<double> Beats = new();
    public List<double> Downbeats = new();
    public List<Section> Sections = new();
    public List<double> Peaks = new();
}
