using System.Collections;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace JizuraLive.Engine;

[Flags]
[JsonConverter(typeof(StringEnumConverter))]
public enum SubContent
{
    Off = 0,
    Original = 1,
    Trans = 2,
    Roma = 4,
}

[JsonConverter(typeof(StringEnumConverter))]
public enum SubPos
{
    Bottom,
    Top
}

public sealed class SubtitleConfig
{
    public SubContent Content { get; set; } = SubContent.Off;
    public SubPos Pos { get; set; } = SubPos.Bottom;
    public double Size { get; set; } = 4.5;
    public double Stroke { get; set; } = 4.0;
    public string Color { get; set; } = "#ffffff";
    public string StrokeColor { get; set; } = "#000000";
    public string BgColor { get; set; } = "rgba(0,0,0,0.6)";
    public double MarginBottom { get; set; } = 4.0;
    public double MarginTop { get; set; } = 4.0;
    public bool HoldLastLine { get; set; }
}

