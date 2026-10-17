namespace JizuraLive.Vts;

public enum VtsCueKind
{
    Hotkey,

    Expression,

    Move,

    Pulse,
}

public sealed class VtsCue
{
    public double Time { get; set; }

    public VtsCueKind Kind { get; set; }

    public string Target { get; set; } = "";

    public double Value { get; set; }

    public double Duration { get; set; }

    public double X { get; set; }

    public double Y { get; set; }

    public double Rotation { get; set; }
}

public sealed class VtsCueTrack
{
    public List<VtsCue> Cues { get; set; } = new();

    public double LeadMs { get; set; } = 80;

    public Dictionary<string, string> Roles { get; set; } = new();
}

public static class VtsRoles
{
    public const string Chorus = "chorus";
    public const string Hit = "hit";
    public const string Bridge = "bridge";
    public const string Outro = "outro";

    public static readonly IReadOnlyList<(string Role, string Label)> All =
    [
        (Chorus, "副歌表情"),
        (Hit, "高潮动作"),
        (Bridge, "间奏表情"),
        (Outro, "结尾动作"),
    ];
}

public interface IVtsCueSink
{
    void TriggerHotkey(string nameOrId);

    void SetExpression(string expressionFile, bool active, double fadeSec);

    void MoveModelRelative(double x, double y, double rotation, double size, double seconds);

    void InjectAdd(IReadOnlyDictionary<string, double> values);
}
