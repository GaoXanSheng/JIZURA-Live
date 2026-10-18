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

    public static IReadOnlyList<LyricStyle> Packs { get; } =
    [
        new() { Name = "noir", FontSize = 56f, Color = "#F5EEEA", GlowColor = "rgba(245,165,12,0.75)", GlowSize = 6f, StrokeWidth = 4f, StrokeColor = "#060607", HighlightColor = "#F5A50C", Anim = "karaoke",
            Alternate = new() { FontSize = 56f, Color = "#0B0B0C", GlowColor = "rgba(224,96,12,0.75)", GlowSize = 6f, StrokeWidth = 4f, StrokeColor = "#F2EDE8", HighlightColor = "#E0600C", Anim = "karaoke" } },
        new() { Name = "crimson", FontSize = 60f, Color = "#FFFFFF", GlowColor = "rgba(200,16,63,0.85)", GlowSize = 8f, StrokeWidth = 5f, StrokeColor = "#150509", HighlightColor = "#39F2C8", Anim = "pop",
            Alternate = new() { FontSize = 60f, Color = "#FF3D6E", GlowColor = "rgba(255,61,110,0.85)", GlowSize = 8f, StrokeWidth = 5f, StrokeColor = "#150509", HighlightColor = "#FFFFFF", Anim = "pop" } },
        new() { Name = "caution", FontSize = 62f, Color = "#141414", GlowColor = "rgba(224,35,28,0.8)", GlowSize = 6f, StrokeWidth = 4f, StrokeColor = "#F4D21F", HighlightColor = "#E0231C", Anim = "pop",
            Alternate = new() { FontSize = 62f, Color = "#F4D21F", GlowColor = "rgba(224,35,28,0.8)", GlowSize = 6f, StrokeWidth = 4f, StrokeColor = "#18181A", HighlightColor = "#FFFFFF", Anim = "pop" } },
        new() { Name = "magenta", FontSize = 66f, Color = "#FFFFFF", GlowColor = "rgba(255,10,140,0.9)", GlowSize = 10f, StrokeWidth = 5f, StrokeColor = "#2B2BD9", HighlightColor = "#2B2BD9", Anim = "pop",
            Alternate = new() { FontSize = 66f, Color = "#FF0A8C", GlowColor = "rgba(255,10,140,0.9)", GlowSize = 10f, StrokeWidth = 5f, StrokeColor = "#2B2BD9", HighlightColor = "#FFFFFF", Anim = "pop" } },
        new() { Name = "paper", FontSize = 54f, Color = "#1B2350", GlowColor = "rgba(194,24,91,0.6)", GlowSize = 5f, StrokeWidth = 3f, StrokeColor = "#ECE9E3", HighlightColor = "#C2185B", Anim = "typewriter",
            Alternate = new() { FontSize = 54f, Color = "#F0EDE7", GlowColor = "rgba(194,24,91,0.6)", GlowSize = 5f, StrokeWidth = 3f, StrokeColor = "#151515", HighlightColor = "#C2185B", Anim = "typewriter" } },
        new() { Name = "hud", FontSize = 56f, Color = "#EFEDEA", GlowColor = "rgba(242,90,43,0.85)", GlowSize = 7f, StrokeWidth = 3f, StrokeColor = "#0B0B0C", HighlightColor = "#F25A2B", Anim = "karaoke",
            Alternate = new() { FontSize = 56f, Color = "#F25A2B", GlowColor = "rgba(242,90,43,0.85)", GlowSize = 7f, StrokeWidth = 3f, StrokeColor = "#0B0B0C", HighlightColor = "#FFFFFF", Anim = "karaoke" } },
        new() { Name = "mint", FontSize = 54f, Color = "#E6FFF5", GlowColor = "rgba(156,255,58,0.8)", GlowSize = 6f, StrokeWidth = 3f, StrokeColor = "#0A0E0D", HighlightColor = "#9CFF3A", Anim = "karaoke",
            Alternate = new() { FontSize = 54f, Color = "#0A0E0D", GlowColor = "rgba(46,140,116,0.8)", GlowSize = 6f, StrokeWidth = 3f, StrokeColor = "#F2F2EE", HighlightColor = "#2E8C74", Anim = "karaoke" } },
        new() { Name = "specimen", FontSize = 50f, Color = "#F2F0EC", GlowColor = "rgba(200,185,140,0.6)", GlowSize = 4f, StrokeWidth = 2f, StrokeColor = "#1B1A1C", HighlightColor = "#C8B98C", Anim = "typewriter",
            Alternate = new() { FontSize = 50f, Color = "#1B1A1C", GlowColor = "rgba(138,122,78,0.6)", GlowSize = 4f, StrokeWidth = 2f, StrokeColor = "#F2F0EC", HighlightColor = "#8A7A4E", Anim = "typewriter" } },
        new() { Name = "transit", FontSize = 58f, Color = "#FFFFFF", GlowColor = "rgba(232,194,26,0.8)", GlowSize = 6f, StrokeWidth = 4f, StrokeColor = "#1A1A1A", HighlightColor = "#E8C21A", Anim = "pop",
            Alternate = new() { FontSize = 58f, Color = "#E8C21A", GlowColor = "rgba(232,194,26,0.8)", GlowSize = 6f, StrokeWidth = 4f, StrokeColor = "#1A1A1A", HighlightColor = "#FFFFFF", Anim = "pop" } },
        new() { Name = "blueprint", FontSize = 56f, Color = "#FFFFFF", GlowColor = "rgba(27,27,232,0.85)", GlowSize = 7f, StrokeWidth = 4f, StrokeColor = "#000000", HighlightColor = "#8C8CFF", Anim = "karaoke",
            Alternate = new() { FontSize = 56f, Color = "#1B1BE8", GlowColor = "rgba(27,27,232,0.85)", GlowSize = 7f, StrokeWidth = 4f, StrokeColor = "#FFFFFF", HighlightColor = "#000000", Anim = "karaoke" } },
        new() { Name = "rouge", FontSize = 60f, Color = "#141414", GlowColor = "rgba(227,20,31,0.85)", GlowSize = 8f, StrokeWidth = 4f, StrokeColor = "#E4E2E0", HighlightColor = "#D40F1C", Anim = "karaoke",
            Alternate = new() { FontSize = 60f, Color = "#FFFFFF", GlowColor = "rgba(227,20,31,0.85)", GlowSize = 8f, StrokeWidth = 4f, StrokeColor = "#140405", HighlightColor = "#FF4A52", Anim = "karaoke" } },
        new() { Name = "mono", FontSize = 56f, Color = "#FFFFFF", GlowColor = "rgba(255,42,42,0.6)", GlowSize = 5f, StrokeWidth = 4f, StrokeColor = "#3B3D41", HighlightColor = "#FFE34D", Anim = "karaoke",
            Alternate = new() { FontSize = 56f, Color = "#FFFFFF", GlowColor = "rgba(42,168,255,0.6)", GlowSize = 5f, StrokeWidth = 4f, StrokeColor = "#141517", HighlightColor = "#FFE34D", Anim = "karaoke" } },
    ];

    public string Name { get; init; } = "";

    public LyricStyle? Alternate { get; init; }

    [JsonIgnore]
    public IReadOnlyList<LyricStyle> Schemes => Alternate is null ? new[] { this } : new[] { this, Alternate! };

    public static LyricStyle FromName(string? name)
    {
        var n = name?.Trim().ToLowerInvariant();
        return n switch
        {
            "classic" => Classic,
            "neon" => Neon,
            "outline" => Outline,
            "minimal" => Minimal,
            "karaoke" => Karaoke,
            "pop" => Pop,
            "typewriter" => Typewriter,
            _ => Packs.FirstOrDefault(p => p.Name == n) ?? Classic,
        };
    }

    public static LyricStyle? FromAnyName(string? name)
    {
        var n = name?.Trim();
        if (string.IsNullOrEmpty(n)) return null;
        var r = FromName(n);
        if (!ReferenceEquals(r, Classic) || string.Equals(n, ClassicName, StringComparison.OrdinalIgnoreCase))
            return r;
        return Packs.FirstOrDefault(p => string.Equals(p.Name, n, StringComparison.OrdinalIgnoreCase));
    }
}
