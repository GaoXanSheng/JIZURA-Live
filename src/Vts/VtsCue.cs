namespace JizuraLive.Vts;

public enum VtsCueKind
{
    Hotkey,

    Expression,

    Move,

    Pulse,

    Tint,
}

public enum VtsCueMode
{
    Toggle,

    Once,

    Manual,
}

public sealed class VtsCue
{
    public double Time { get; set; }

    public VtsCueKind Kind { get; set; }

    public VtsCueMode Mode { get; set; } = VtsCueMode.Toggle;

    public string Target { get; set; } = "";

    public string Source { get; set; } = "user";

    public double Duration { get; set; }

    public int TintR { get; set; } = 255;

    public int TintG { get; set; } = 255;

    public int TintB { get; set; } = 255;
}

public sealed class VtsCueTrack
{
    public List<VtsCue> Cues { get; set; } = new();
}
