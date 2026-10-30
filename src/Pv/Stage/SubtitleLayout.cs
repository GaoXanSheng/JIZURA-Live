using System.Globalization;
using System.Numerics;
using JizuraLive.Engine;
using Vortice.Mathematics;

namespace JizuraLive.Pv;

public readonly record struct SubtitlePlan(
    string ClipId,
    string Text,
    string[] Lines,
    float FontSizePx,
    float CenterX,
    float FirstBaselineY,
    float LineHeight,
    float StrokeWidthPx,
    IReadOnlyList<Vector2> StrokeOffsets,
    Color4 Fill,
    Color4 Stroke,
    Rect? Background);

public static class SubtitleLayout
{
    private const int StrokeDirections = 16;

    private const float BackgroundPaddingEm = 0.25f;

    private const float HalfWidthEm = 0.55f;

    public static float FontSizeOf(SubtitleConfig? cfg, int height)
    {
        var size = cfg?.Size ?? 0;
        return MathF.Max(1f, (float)(size / 100.0 * Math.Max(1, height)));
    }

    public static Color4 ParseColor(string? text, Color4 fallback)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return fallback;

        if (t[0] == '#')
        {
            var hex = t.TrimStart('#');
            return IsHex(hex, 3) || IsHex(hex, 6) ? PvScene.ParseHex(hex) : fallback;
        }

        if (t.StartsWith("rgba(", StringComparison.OrdinalIgnoreCase) && t.EndsWith(')'))
        {
            var parts = t[5..^1].Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length == 4
                && TryNum(parts[0], out var r) && TryNum(parts[1], out var g)
                && TryNum(parts[2], out var b) && TryNum(parts[3], out var a))
            {
                return new Color4(
                    Math.Clamp(r, 0f, 255f) / 255f,
                    Math.Clamp(g, 0f, 255f) / 255f,
                    Math.Clamp(b, 0f, 255f) / 255f,
                    Math.Clamp(a, 0f, 1f));
            }
        }

        return fallback;
    }

    public static SubtitlePlan? Plan(SubtitleConfig? cfg, IReadOnlyList<LyricClip>? clips,
        double timeSeconds, int width, int height, float ascentPx, float lineHeightPx)
    {
        if (cfg is null || cfg.Content == SubContent.Off) return null;
        if (clips is null || clips.Count == 0) return null;

        var sample = LrcParser.FindSample(clips, timeSeconds);
        if (sample is null && cfg.HoldLastLine) sample = LrcParser.FindLastBefore(clips, timeSeconds);
        if (sample is not { } s) return null;

        var text = s.Text;
        if (string.IsNullOrWhiteSpace(text)) return null;

        var lines = SelectLines(SplitLines(text), cfg.Content);
        if (lines.Length == 0) return null;
        var fontSizePx = FontSizeOf(cfg, height);
        var lineHeight = MathF.Max(1f, lineHeightPx);
        var ascent = MathF.Max(0f, ascentPx);
        var strokeWidthPx = MathF.Max(0f, (float)cfg.Stroke);

        var marginPx = (float)((cfg.Pos == SubPos.Bottom ? cfg.MarginBottom : cfg.MarginTop) / 100.0 * height);
        var blockHeight = lines.Length * lineHeight;
        var firstBaseline = cfg.Pos == SubPos.Bottom
            ? height - marginPx - blockHeight + ascent
            : marginPx + ascent;

        var centerX = width / 2f;
        return new SubtitlePlan(
            s.ClipId,
            text,
            lines,
            fontSizePx,
            centerX,
            firstBaseline,
            lineHeight,
            strokeWidthPx,
            BuildStrokeOffsets(strokeWidthPx),
            ParseColor(cfg.Color, new Color4(1f, 1f, 1f, 1f)),
            ParseColor(cfg.StrokeColor, new Color4(0f, 0f, 0f, 1f)),
            BuildBackground(cfg, lines, fontSizePx, blockHeight, firstBaseline - ascent, centerX));
    }

    private static string[] SplitLines(string text)
    {
        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var bar = line.IndexOf('|');
            if (bar < 0)
            {
                lines.Add(line);
                continue;
            }

            lines.Add(line[..bar]);
            foreach (var note in line[(bar + 1)..].Split('\u3000'))
            {
                var value = note.Trim();
                if (value.Length > 0) lines.Add(value);
            }
        }

        return lines.ToArray();
    }

    private static string[] SelectLines(string[] lines, SubContent content)
    {
        if (lines.Length <= 1) return lines;
        return content switch
        {
            SubContent.Trans => SelectTrans(lines),
            SubContent.Roma => SelectRoma(lines),
            _ => lines,
        };
    }

    private static string[] SelectTrans(string[] lines)
    {
        var trans = lines.Skip(1).Where(l => !LooksLatin(l)).ToArray();
        return trans.Length > 0 ? trans : lines[1..];
    }

    private static string[] SelectRoma(string[] lines)
    {
        var latin = lines.Skip(1).Where(LooksLatin).ToArray();
        return latin.Length > 0 ? latin : lines;
    }

    private static bool LooksLatin(string text)
    {
        var latin = 0;
        var cjk = 0;
        foreach (var c in text)
        {
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') latin++;
            else if (c is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff'
                     or >= '\uac00' and <= '\ud7af') cjk++;
        }

        return latin > cjk;
    }

    private static IReadOnlyList<Vector2> BuildStrokeOffsets(float strokeWidthPx)
    {
        if (strokeWidthPx <= 0f) return Array.Empty<Vector2>();

        var radius = strokeWidthPx / 2f;
        var offsets = new Vector2[StrokeDirections];
        for (var i = 0; i < StrokeDirections; i++)
        {
            var angle = i * 2f * MathF.PI / StrokeDirections;
            offsets[i] = new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
        }

        return offsets;
    }

    private static Rect? BuildBackground(SubtitleConfig cfg, string[] lines, float fontSizePx,
        float blockHeight, float blockTop, float centerX)
    {
        if (ParseColor(cfg.BgColor, new Color4(0f, 0f, 0f, 0f)).A <= 0f) return null;

        var widest = 0f;
        foreach (var line in lines) widest = MathF.Max(widest, EstimateWidth(line, fontSizePx));

        var pad = fontSizePx * BackgroundPaddingEm;
        var w = widest + pad * 2f;
        return new Rect(centerX - w / 2f, blockTop - pad, w, blockHeight + pad * 2f);
    }

    private static float EstimateWidth(string line, float fontSizePx)
    {
        var em = 0f;
        foreach (var ch in line) em += ch < 0x2E80 ? HalfWidthEm : 1f;
        return em * fontSizePx;
    }

    private static bool TryNum(string s, out float value) =>
        float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool IsHex(string s, int length)
    {
        if (s.Length != length) return false;
        foreach (var c in s)
        {
            var hex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!hex) return false;
        }

        return true;
    }
}
