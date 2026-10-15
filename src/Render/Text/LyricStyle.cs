using JizuraLive.Pv;
using Newtonsoft.Json;
using Vortice.Mathematics;

namespace JizuraLive.Render.Text;

/// <summary>
/// 歌词排版样式（纯数据，可整体存进 PvScene）。
///
/// 颜色存 <c>#rrggbb</c> / <c>rgba(r,g,b,a)</c> 串而不是 <see cref="Color4"/>：Color4 的通道是公共
/// **字段**，Newtonsoft 序列化出来是 <c>{}</c>、读回全 0（与 PvScene.AmbientTint 同一个坑）。
/// </summary>
public sealed record LyricStyle
{
    /// <summary>默认预设名（PvScene.LyricStyleName 的初值）。</summary>
    public const string ClassicName = "Classic";

    /// <summary>设计像素字号。</summary>
    public float FontSize { get; init; } = 52f;

    /// <summary>正文字色。</summary>
    public string Color { get; init; } = "#ffffff";

    /// <summary>外发光色；alpha 是发光自身的不透明度，绘制时再乘进出场 alpha。</summary>
    public string GlowColor { get; init; } = "rgba(0,13,38,0.6)";

    /// <summary>发光下影相对基线的下移量（设计像素）；0 = 不画发光。</summary>
    public float GlowSize { get; init; } = 2f;

    /// <summary>描边宽度（设计像素）；0 = 不描边。</summary>
    public float StrokeWidth { get; init; }

    /// <summary>描边色。</summary>
    public string StrokeColor { get; init; } = "#000000";

    [JsonIgnore]
    public Color4 ColorValue => SubtitleLayout.ParseColor(Color, new Color4(1f, 1f, 1f, 1f));

    [JsonIgnore]
    public Color4 GlowColorValue => SubtitleLayout.ParseColor(GlowColor, new Color4(0f, 0f, 0f, 0f));

    [JsonIgnore]
    public Color4 StrokeColorValue => SubtitleLayout.ParseColor(StrokeColor, new Color4(0f, 0f, 0f, 1f));

    /// <summary>按画布缩放换算实际字号。</summary>
    public float FontSizePx(double scale) => FontSize * (float)scale;

    /// <summary>原硬编码外观（52px 白字 + 深蓝下影）。</summary>
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

    /// <summary>按名取预设；未知/空名回落 <see cref="Classic"/>（绝不抛，名字来自可手改的工程文件）。</summary>
    public static LyricStyle FromName(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "classic" => Classic,
        "neon" => Neon,
        "outline" => Outline,
        "minimal" => Minimal,
        _ => Classic,
    };
}
