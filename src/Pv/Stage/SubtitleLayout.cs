using System.Globalization;
using System.Numerics;
using JizuraLive.Engine;
using Vortice.Mathematics;

namespace JizuraLive.Pv;

/// <summary>
/// 一帧字幕的排版规划：纯数据，不含任何 GPU / DWrite 资源，所以能脱设备测试。
/// 由 <see cref="SubtitleProvider"/> 每帧现算，再把结果落到 D2D 上。
///
/// <see cref="Lines"/> 是拆好行的文本，第 i 行的基线 = <see cref="FirstBaselineY"/> + i × <see cref="LineHeight"/>。
/// </summary>
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

/// <summary>
/// 字幕排版规划（纯函数，不碰 GPU/DWrite）。
///
/// 单位换算在这里定死（界面阶段核对用，依据是默认值量级）：
///   * <c>Size</c>（默认 4.5）与 <c>MarginTop/MarginBottom</c>（默认 4.0）是**设计高度百分比**：
///     Size 4.5 → 1080 下 48.6px，与动效歌词层 TypographyLayerProvider 的 52px 同量级；
///     Margin 4.0 → 1080 下 43.2px 边距。
///   * <c>Stroke</c>（默认 4.0）是**设计像素**，不随画布高度缩放。
///
/// 锚点：Pos.Bottom → 文本块底边落在 height - margin；Pos.Top → 顶边落在 margin。
/// 多行按字体行高逐行堆叠，首行基线由锚点与 ascent/行高推出（行内基线以下留 lineHeight - ascent）。
/// </summary>
public static class SubtitleLayout
{
    /// <summary>描边方向数：16 向在 4px 描边下已看不出花瓣状缺口，再密只是白画。</summary>
    private const int StrokeDirections = 16;

    /// <summary>背景块外扩 padding（em）：与字号同比，换字号时留白比例不变。</summary>
    private const float BackgroundPaddingEm = 0.25f;

    /// <summary>半角字符的估宽（em）：全角按 1em。</summary>
    private const float HalfWidthEm = 0.55f;

    /// <summary>
    /// 字号（设计像素）：<c>Size</c> 是设计高度百分比。
    /// 单独暴露是因为绘制方得先建字体、才能拿到 ascent/行高，而这两者又是 <see cref="Plan"/> 的入参——
    /// 两边必须是同一个公式，否则字号与锚点会各算一套。
    /// </summary>
    public static float FontSizeOf(SubtitleConfig? cfg, int height)
    {
        var size = cfg?.Size ?? 0;
        return MathF.Max(1f, (float)(size / 100.0 * Math.Max(1, height)));
    }

    /// <summary>
    /// 解析颜色串：<c>#rgb</c> / <c>#rrggbb</c>（与 <see cref="PvScene.ParseHex"/> 同规则）与
    /// <c>rgba(r,g,b,a)</c>（<see cref="SubtitleConfig.BgColor"/> 的默认形态）。
    /// 非法串回落 <paramref name="fallback"/>——绝不抛，颜色串来自可手改的配置。
    /// </summary>
    public static Color4 ParseColor(string? text, Color4 fallback)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0) return fallback;

        if (t[0] == '#')
        {
            var hex = t.TrimStart('#');
            // 只认 #rgb / #rrggbb：PvScene.ParseHex 对逐通道解析失败会兜成白色，
            // 那是它自己的契约；这里非法串必须回落到调用方给的 fallback
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

    /// <summary>
    /// 规划本帧字幕。返回 null = 本帧不画（清屏语义，见契约 PV-DIRECTOR-SYSTEM.md:397）。
    /// 取样与动效歌词层同一份文本样本（LyricSample），不另算一套当前句。
    /// </summary>
    public static SubtitlePlan? Plan(SubtitleConfig? cfg, IReadOnlyList<LyricClip>? clips,
        double timeSeconds, int width, int height, float ascentPx, float lineHeightPx)
    {
        // Content 只在 Off 与「画主文本」之间分流：LyricClip 只有单一 text，
        // 没有独立翻译内容时不做 Trans/Roma 双语小字（契约 PV-DIRECTOR-SYSTEM.md:44）
        if (cfg is null || cfg.Content == SubContent.Off) return null;
        if (clips is null || clips.Count == 0) return null;

        var sample = LrcParser.FindSample(clips, timeSeconds);
        if (sample is null && cfg.HoldLastLine) sample = LrcParser.FindLastBefore(clips, timeSeconds);
        if (sample is not { } s) return null;

        var text = s.Text;
        if (string.IsNullOrWhiteSpace(text)) return null; // 没字可画 = 与清屏等价

        var lines = SplitLines(text);
        var fontSizePx = FontSizeOf(cfg, height);
        var lineHeight = MathF.Max(1f, lineHeightPx);
        var ascent = MathF.Max(0f, ascentPx);
        var strokeWidthPx = MathF.Max(0f, (float)cfg.Stroke);

        var marginPx = (float)((cfg.Pos == SubPos.Bottom ? cfg.MarginBottom : cfg.MarginTop) / 100.0 * height);
        var blockHeight = lines.Length * lineHeight;
        // 底边锚点：块底 = height - margin，而块底到末行基线还差「行高 - 基线以上」
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

    /// <summary>按 '\n' 拆行（容忍 CRLF）：换行由文本自己给，SubtitleConfig 没有换行策略字段。</summary>
    private static string[] SplitLines(string text)
    {
        var raw = text.Split('\n');
        for (var i = 0; i < raw.Length; i++) raw[i] = raw[i].TrimEnd('\r');
        return raw;
    }

    /// <summary>
    /// 描边方向：半径 StrokeWidthPx/2 的圆周偏移（不含 (0,0)，中心由填充那笔覆盖）。
    /// D2D 的 DrawText 没有描边笔，描边只能靠沿圆周偏移重画若干遍来模拟。
    /// </summary>
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

    /// <summary>BgColor 的 alpha &gt; 0 才给底块；矩形 = 文本包围盒外扩 padding。</summary>
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

    /// <summary>
    /// 估宽：纯排版层不碰 DWrite，按「全角 1em / 半角 0.55em」算。底块只要包住文本，
    /// 不必与字形测量逐像素一致（真宽由 DirectWrite 绘制时自己算）。
    /// </summary>
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
