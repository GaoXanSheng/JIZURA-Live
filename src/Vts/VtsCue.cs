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

    public double Value { get; set; }

    public double Duration { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Rotation { get; set; }

    public int TintR { get; set; } = 255;

    public int TintG { get; set; } = 255;

    public int TintB { get; set; } = 255;
}

public sealed class VtsCueTrack
{
    public List<VtsCue> Cues { get; set; } = new();

    public double LeadMs { get; set; } = 80;

    public Dictionary<string, string> Roles { get; set; } = new();
}

public interface IVtsCueSink
{
    void TriggerHotkey(string nameOrId);

    void SetExpression(string expressionFile, bool active, double fadeSec);

    void MoveModelRelative(double x, double y, double rotation, double size, double seconds);

    void InjectAdd(IReadOnlyDictionary<string, double> values);
}
