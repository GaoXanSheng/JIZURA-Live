namespace JizuraLive.Engine;

public sealed class Section
{
    public double Start;
    public double End;
    public string Kind = "intro";

    public string Label = "A";

    public double Energy;
}

public sealed class BeatMap
{
    public double Bpm;
    public List<double> Beats = new();
    public List<double> Downbeats = new();
    public List<Section> Sections = new();
    public List<double> Peaks = new();
    public float[] Pitch = [];
    public float PitchRate;
}
