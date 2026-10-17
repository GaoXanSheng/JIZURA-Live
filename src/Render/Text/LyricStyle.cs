using JizuraLive.Pv;
using Newtonsoft.Json;
using Vortice.Mathematics;

namespace JizuraLive.Render.Text;

public sealed record LyricStyle
{
    public const string ClassicName = "Classic";

    public float FontSize { get; init; } = 52f;

    public string Color { get; init; } = "#ffffff";

    public string GlowColor { get; init; } = "rgba(0,13,38,0.6)";

    public float GlowSize { get; init; } = 2f;

    public float StrokeWidth { get; init; }

    public string StrokeColor { get; init; } = "#000000";

    public string Anim { get; init; } = "none";

    public string HighlightColor { get; init; } = "#ff6fae";

    [JsonIgnore]
    public Color4 ColorValue => SubtitleLayout.ParseColor(Color, new Color4(1f, 1f, 1f, 1f));

    [JsonIgnore]
    public Color4 HighlightColorValue => SubtitleLayout.ParseColor(HighlightColor, new Color4(1f, 1f, 1f, 1f));

    [JsonIgnore]
    public Color4 GlowColorValue => SubtitleLayout.ParseColor(GlowColor, new Color4(0f, 0f, 0f, 0f));

    [JsonIgnore]
    public Color4 StrokeColorValue => SubtitleLayout.ParseColor(StrokeColor, new Color4(0f, 0f, 0f, 1f));

    public float FontSizePx(double scale) => FontSize * (float)scale;

    public static LyricStyle Classic { get; } = new();

    public static LyricStyle Neon { get; } = new()
    {
        FontSize = 56f,
        Color = "#eafcff",
        GlowColor = "rgba(0,229,255,0.85)",
        GlowSize = 10f,
    };

    public static LyricStyle Outline { get; } = new()
    {
        GlowColor = "rgba(0,0,0,0)",
        GlowSize = 0f,
        StrokeWidth = 6f,
        StrokeColor = "#101010",
    };

    public static LyricStyle Minimal { get; } = new()
    {
        FontSize = 44f,
        Color = "#f2f2f2",
        GlowColor = "rgba(0,0,0,0)",
        GlowSize = 0f,
    };

    public static LyricStyle Karaoke { get; } = new()
    {
        FontSize = 54f,
        Color = "#ffffff",
        HighlightColor = "#ff6fae",
        StrokeWidth = 5f,
        StrokeColor = "#2a1030",
        GlowSize = 0f,
        Anim = "karaoke",
    };

    public static LyricStyle Pop { get; } = new()
    {
        FontSize = 70f,
        Color = "#ffffff",
        GlowColor = "rgba(0,229,255,0.85)",
        GlowSize = 12f,
        StrokeWidth = 5f,
        StrokeColor = "#1a1040",
        Anim = "pop",
    };

    public static LyricStyle Typewriter { get; } = new()
    {
        FontSize = 46f,
        Color = "#f5f5f5",
        GlowSize = 0f,
        StrokeWidth = 3f,
        StrokeColor = "#000000",
        Anim = "typewriter",
    };

    public static IReadOnlyList<string> PresetNames { get; } =
        ["Classic", "Neon", "Outline", "Minimal", "Karaoke", "Pop", "Typewriter"];

    public static LyricStyle FromName(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "classic" => Classic,
        "neon" => Neon,
        "outline" => Outline,
        "minimal" => Minimal,
        "karaoke" => Karaoke,
        "pop" => Pop,
        "typewriter" => Typewriter,
        _ => Classic,
    };
}
