using System.Collections;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace JizuraLive.Engine;

/// <summary>字幕内容模式：对位 SubConfig.content（Off = 整层不画）。</summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum SubContent
{
    Off,
    Trans,
    Roma,
    Both
}

/// <summary>字幕位置：对位 SubConfig.pos。</summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum SubPos
{
    Bottom,
    Top
}

/// <summary>
/// 字幕配置：纯 POCO。
/// </summary>
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

/// <summary>
/// 字幕分词与时间轴纯逻辑辅助。
/// </summary>
public static class SubtitleHelper
{
    public static (string Roma, string Trans) NoteParts(string note)
    {
        if (string.IsNullOrWhiteSpace(note)) return ("", "");
        var parts = note.Split(['\t', '　', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1) return (parts[0], "");
        return (parts[0], string.Join(" ", parts.Skip(1)));
    }
}
