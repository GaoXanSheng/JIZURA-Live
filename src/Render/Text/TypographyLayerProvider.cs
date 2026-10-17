using System.Numerics;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JizuraLive.Render.Text;

public readonly record struct GlyphRef(
    string Text,
    string Line,
    int LineIndex,
    int TokenIndex,
    int TokenCount,
    float LocalX,
    float GlyphWidth,
    float ScaleFactor,
    long ClipStartMs,
    long ClipEndMs);

public readonly record struct GlyphTransform
{
    public float ScaleX { get; init; }
    public float ScaleY { get; init; }
    public float RotationRad { get; init; }
    public float OffsetX { get; init; }
    public float OffsetY { get; init; }
    public float AlphaScale { get; init; }
    public float SkewRad { get; init; }

    public static GlyphTransform Identity { get; } = new()
    {
        ScaleX = 1f,
        ScaleY = 1f,
        RotationRad = 0f,
        OffsetX = 0f,
        OffsetY = 0f,
        AlphaScale = 1f,
        SkewRad = 0f,
    };
}

public sealed class TypographyLayerProvider : IPvLayerProvider, IDisposable
{
    private readonly D3DDevice _gpu;
    private readonly D2DHost _host;
    private OffscreenTarget? _target;
    private IReadOnlyList<LyricClip> _clips = Array.Empty<LyricClip>();

    private static readonly string[] LineSeparators = ["\r\n", "\r", "\n"];

    private const int StrokeDirections = 16;

    private const float PopMs = 180f;
    private const float TypewriterMs = 80f;

    private LyricStyle? _style;

    public int MotionSeed { get; set; } = 1;

    private SentenceLayout? _layout;
    private string? _layoutClipId;
    private float _layoutFontSize = -1f;

    private long _clipStartMs;
    private long _clipEndMs;

    private readonly HashSet<string> _wordsMismatch = new();

    private readonly HashSet<string> _mismatchWarned = new();

    private int _w, _h;
    private double _scale = 1.0;
    private long _version = 1;

    public string Id { get; }
    public long Version => _version;
    public int Width => _w;
    public int Height => _h;
    public int DesignWidth { get; }
    public int DesignHeight { get; }

    public TypographyLayerProvider(string id, D3DDevice gpu, D2DHost host,
        int designWidth = 1920, int designHeight = 1080)
    {
        Id = id;
        _gpu = gpu;
        _host = host;
        DesignWidth = designWidth;
        DesignHeight = designHeight;
    }

    public void SetLyrics(IReadOnlyList<KrcLine>? lines) =>
        SetClips(lines is null ? Array.Empty<LyricClip>() : LrcParser.ToClips(lines));

    public void SetClips(IReadOnlyList<LyricClip>? clips)
    {
        _clips = clips is null ? Array.Empty<LyricClip>() : LyricTiming.EnsureWords(clips);

        _wordsMismatch.Clear();
        foreach (var clip in _clips)
        {
            // 没有词表（空文本 / 零时长句）本来就整句绘制，谈不上「不一致」
            if (clip.Words is not { Count: > 0 } || WordsMatchText(clip)) continue;
            _wordsMismatch.Add(clip.Id);
            if (_mismatchWarned.Add(clip.Id))
                Log.Warn($"[Typography] 歌词 {clip.Id} 的逐字时间与整句文本不一致，该句按整句绘制");
        }

        _layout = null;
        _layoutClipId = null;
        _version++;
    }

    public void SetScale(double scale)
    {
        scale = Math.Clamp(scale, 0.05, 1.0);
        if (Math.Abs(scale - _scale) < 1e-4) return;
        _scale = scale;
        _version++;
    }

    public void SetStyle(LyricStyle? style)
    {
        _style = style;
        _version++;
    }

    public Func<GlyphRef, GlyphTransform>? GlyphTransformFor { get; set; }

    public Func<GlyphRef, GlyphTransform>? OverrideGlyphTransformFor { get; set; }

    public LyricStyle EffectiveStyle => _style ?? LyricStyle.Classic;

    public void Render(double time)
    {
        var w = Math.Max(16, (int)Math.Round(DesignWidth * _scale));
        var h = Math.Max(16, (int)Math.Round(DesignHeight * _scale));

        if (_target is null || w != _w || h != _h)
        {
            _target?.Dispose();
            _target = OffscreenTarget.Create(_gpu, _host.Ctx, w, h, sampleable: true);
            _w = w;
            _h = h;
            _version++;
        }

        var d = _host.Draw;
        d.SetTarget(_target.Bitmap, w, h);
        d.Begin();
        d.Clear(new Color4(0f, 0f, 0f, 0f));

        if (_clips.Count > 0)
        {
            var sample = LrcParser.FindSample(_clips, time);
            if (sample is { } cur)
            {
                var clip = FindClip(cur.ClipId);
                var style = ResolveStyle(clip);
                var font = d.Fonts.Get("Microsoft YaHei UI", style.FontSizePx(_scale), weight: Vortice.DirectWrite.FontWeight.Bold);

                if (clip is not null)
                {
                    _clipStartMs = clip.StartMs;
                    _clipEndMs = clip.EndMs;
                }

                var motion = clip is null ? null : LyricMotion.For(clip, MotionSeed);
                var glyphCount = cur.Text.Replace("\r", "").Replace("\n", "").Length;
                var tpl = LayoutTemplates.Pick(glyphCount);
                var block = tpl.BlockFor(glyphCount);
                var blockCx = tpl.Anchor.X * w + block.OffsetX * w;
                var blockCy = tpl.Anchor.Y * h + block.OffsetY * h;
                var mf = clip is null
                    ? new LyricMotion.Frame(1f, System.Numerics.Matrix3x2.Identity, null, blockCx, blockCy)
                    : motion!.Evaluate(time, clip, w, h, (float)_scale, blockCx / w, blockCy / h);

                if (motion is not null && clip is not null)
                    GlyphTransformFor = g =>
                        GlyphAnim.ToGlyphTransform(
                            motion.GlyphAt(time, clip, g.TokenIndex, g.TokenCount, w, h, (float)_scale),
                            block.Scale);
                else if (OverrideGlyphTransformFor is null)
                    GlyphTransformFor = null;

                var alpha = mf.Alpha * block.AlphaScale;
                var cx = mf.Cx;
                var cy = mf.Cy;
                var glow = style.GlowColorValue;
                var fill = style.ColorValue;
                var stroke = style.StrokeColorValue;
                var strokeOffsets = style.StrokeWidth > 0f ? BuildStrokeOffsets(style.StrokeWidth) : null;

                var anim = NormalizeAnim(style.Anim);
                var layout = clip is null ? null : GetLayout(clip, font);

                d.Save();
                d.SetMatrix(mf.Matrix);
                if (mf.Clip is { } clipRect) d.ClipRect(clipRect);

                if (anim is not null && layout is not null)
                {
                    DrawAnimated(d, layout, anim, time, cx, cy, alpha, font, fill, glow, style.GlowSize,
                        stroke, strokeOffsets, style.HighlightColorValue);
                }
                else
                {
                    var blockLines = cur.Text.Split(LineSeparators, StringSplitOptions.None);
                    var lineHeight = font.LineHeight;
                    var firstBaseline = cy - (blockLines.Length - 1) * lineHeight / 2f;
                    var scratch = new Paint2D();

                    for (var i = 0; i < blockLines.Length; i++)
                    {
                        var line = blockLines[i];
                        if (line.Length == 0) continue;

                        DrawRun(d, line, cx, firstBaseline + i * lineHeight, TextAlign2D.Center, font,
                            fill, glow, style.GlowSize, stroke, strokeOffsets, alpha, scratch);
                    }
                }

                d.Restore();
            }
        }

        d.End();
    }

    public PvLayerImage Pump(in PvFrameContext ctx) =>
        _target is null ? PvLayerImage.Empty : new PvLayerImage(_target.Bitmap, null, _w, _h);

    public ReadbackImage? ReadbackTarget() => _target?.Readback();

    private LyricStyle ResolveStyle(LyricClip? clip) =>
        string.IsNullOrWhiteSpace(clip?.Style) ? EffectiveStyle : LyricStyle.FromName(clip!.Style);

    private LyricClip? FindClip(string id)
    {
        for (var i = 0; i < _clips.Count; i++)
            if (string.Equals(_clips[i].Id, id, StringComparison.Ordinal)) return _clips[i];

        return null;
    }

    private SentenceLayout? GetLayout(LyricClip clip, DwFont font)
    {
        if (clip.Words is not { Count: > 0 } || _wordsMismatch.Contains(clip.Id)) return null;

        if (_layout is not null && _layoutClipId == clip.Id && Math.Abs(_layoutFontSize - font.Size) < 1e-3f)
            return _layout;

        _layout = BuildLayout(clip, font);
        _layoutClipId = clip.Id;
        _layoutFontSize = font.Size;
        return _layout;
    }

    private static string? NormalizeAnim(string? anim) => anim?.Trim().ToLowerInvariant() switch
    {
        "karaoke" => "karaoke",
        "pop" => "pop",
        "typewriter" => "typewriter",
        _ => null,
    };

    private void DrawAnimated(Immediate2D d, SentenceLayout layout, string anim, double time,
        float cx, float cy, float alpha, DwFont font, in Color4 fill, in Color4 glow, float glowDy,
        in Color4 stroke, Vector2[]? strokeOffsets, in Color4 highlight)
    {
        var tMs = (long)(time * 1000.0);
        var lineHeight = layout.LineHeight;
        var firstBaseline = cy - (layout.Lines.Length - 1) * lineHeight / 2f;
        var pad = MathF.Max(glowDy, strokeOffsets is null ? 0f : strokeOffsets[0].Length()) + 4f;
        var scratch = new Paint2D();

        if (anim == "karaoke")
        {
            for (var i = 0; i < layout.Lines.Length; i++)
            {
                var line = layout.Lines[i];
                if (line.Text.Length == 0) continue;
                DrawRun(d, line.Text, cx, firstBaseline + i * lineHeight, TextAlign2D.Center, font,
                    fill, glow, glowDy, stroke, strokeOffsets, alpha, scratch);
            }

            for (var i = 0; i < layout.Lines.Length; i++)
            {
                var line = layout.Lines[i];
                var baseline = firstBaseline + i * lineHeight;

                for (var s = 0; s < line.Spans.Length; s++)
                {
                    var span = line.Spans[s];
                    var wordStart = layout.WordStartMs[span.Word];
                    if (tMs < wordStart) continue;

                    var dur = layout.WordDurMs[span.Word];
                    var x0 = cx + line.X[span.First];
                    var x1 = cx + line.X[span.Last] + line.W[span.Last];
                    var left = tMs >= wordStart + dur || dur <= 0
                        ? x1
                        : x0 + (x1 - x0) * ((tMs - wordStart) / (float)dur);

                    d.Save();
                    if (left < x1) d.ClipRect(SweepBand(x0, left, baseline, font, pad));
                    scratch.Brush = new SolidBrush2D(WithAlpha(highlight, alpha));
                    for (var t = span.First; t <= span.Last; t++)
                        d.DrawText(line.Tokens[t], cx + line.X[t], baseline, TextAlign2D.Left, font, scratch);
                    d.Restore();
                }
            }

            return;
        }

        for (var i = 0; i < layout.Lines.Length; i++)
        {
            var line = layout.Lines[i];
            var baseline = firstBaseline + i * lineHeight;

            for (var s = 0; s < line.Spans.Length; s++)
            {
                var span = line.Spans[s];
                var wordStart = layout.WordStartMs[span.Word];
                if (tMs < wordStart) continue;

                var e = Math.Min(1f, (tMs - wordStart) / (anim == "pop" ? PopMs : TypewriterMs));
                var a = alpha * e;
                if (a <= 0.002f) continue;

                if (anim == "pop")
                {
                    var scale = e < 0.6f
                        ? 0.4f + 0.75f * (e / 0.6f)
                        : 1.15f - 0.15f * EaseOut((e - 0.6f) / 0.4f);
                    var yc = baseline + (1f - e) * 10f * (float)_scale;
                    var mcx = cx + (line.X[span.First] + line.X[span.Last] + line.W[span.Last]) / 2f;

                    d.Save();
                    d.SetMatrix(new Matrix3x2(scale, 0f, 0f, scale, mcx * (1f - scale), yc * (1f - scale)));
                    DrawTokens(d, line, span, cx, yc, font, fill, glow, glowDy, stroke, strokeOffsets, a, scratch, i);
                    d.Restore();
                }
                else
                {
                    DrawTokens(d, line, span, cx, baseline, font, fill, glow, glowDy, stroke, strokeOffsets, a, scratch, i);
                }
            }
        }
    }

    private void DrawTokens(Immediate2D d, LineLayout line, WordSpan span, float cx, float baseline,
        DwFont font, in Color4 fill, in Color4 glow, float glowDy, in Color4 stroke, Vector2[]? strokeOffsets,
        float alpha, Paint2D scratch, int lineIndex)
    {
        var xf = OverrideGlyphTransformFor ?? GlyphTransformFor;
        for (var t = span.First; t <= span.Last; t++)
        {
            var gx = cx + line.X[t];
            var gw = t < line.W.Length ? line.W[t] : 0f;
            var f = t < line.F.Length ? line.F[t] : 1f;
            var xfNow = xf is null
                ? TypesetScale(f)
                : ComposeTypeset(f, xf(new GlyphRef(
                    line.Tokens[t], line.Text, lineIndex, t, line.Tokens.Length, line.X[t], gw, f,
                    _clipStartMs, _clipEndMs)));
            DrawRun(d, line.Tokens[t], gx, baseline, TextAlign2D.Left, font,
                fill, glow, glowDy, stroke, strokeOffsets, alpha * xfNow.AlphaScale, scratch,
                gw * 0.5f, baseline, xfNow);
        }
    }

    private static GlyphTransform TypesetScale(float f)
    {
        if (MathF.Abs(f - 1f) < 1e-3f) return GlyphTransform.Identity;
        return new GlyphTransform { ScaleX = f, ScaleY = f };
    }

    private static GlyphTransform ComposeTypeset(float f, in GlyphTransform xf)
    {
        if (MathF.Abs(f - 1f) < 1e-3f) return xf;
        return new GlyphTransform
        {
            ScaleX = xf.ScaleX * f,
            ScaleY = xf.ScaleY * f,
            RotationRad = xf.RotationRad,
            OffsetX = xf.OffsetX,
            OffsetY = xf.OffsetY,
            AlphaScale = xf.AlphaScale,
            SkewRad = xf.SkewRad,
        };
    }

    private readonly record struct GlyphXf(float ScaleX, float ScaleY, float Rot, float Ox, float Oy,
        float CenterOffsetX, float CenterY, float Skew)
    {
        public bool IsIdentity =>
            MathF.Abs(ScaleX - 1f) < 1e-4f && MathF.Abs(ScaleY - 1f) < 1e-4f &&
            MathF.Abs(Rot) < 1e-4f && MathF.Abs(Ox) < 1e-4f && MathF.Abs(Oy) < 1e-4f &&
            MathF.Abs(Skew) < 1e-4f;
    }

    private static void DrawRun(Immediate2D d, string text, float x, float baseline, TextAlign2D align, DwFont font,
        in Color4 fill, in Color4 glow, float glowDy, in Color4 stroke, Vector2[]? strokeOffsets, float alpha,
        Paint2D scratch, float centerOffsetX = 0f, float centerY = 0f, in GlyphTransform xf = default)
    {
        if (text.Length == 0 || alpha <= 0.002f) return;

        var g = new GlyphXf(xf.ScaleX, xf.ScaleY, xf.RotationRad, xf.OffsetX, xf.OffsetY, centerOffsetX, centerY,
            xf.SkewRad);
        if (g.IsIdentity || centerOffsetX == 0f)
        {
            DrawRunInner(d, text, x, baseline, align, font, fill, glow, glowDy, stroke, strokeOffsets, alpha, scratch);
            return;
        }

        var ax = x + g.CenterOffsetX;
        var ay = g.CenterY;
        d.Save();
        var m = Matrix3x2.CreateTranslation(-ax, -ay);
        if (MathF.Abs(g.Skew) > 1e-5f)
            m = Matrix3x2.Multiply(m, Matrix3x2.CreateSkew(MathF.Tan(g.Skew), 0f));
        if (MathF.Abs(g.Rot) > 1e-5f)
            m = Matrix3x2.Multiply(m, Matrix3x2.CreateRotation(g.Rot));
        m = Matrix3x2.Multiply(m, Matrix3x2.CreateScale(g.ScaleX, g.ScaleY));
        m = Matrix3x2.Multiply(m, Matrix3x2.CreateTranslation(ax + g.Ox, ay + g.Oy));
        d.SetMatrix(Matrix3x2.Multiply(d.Matrix, m));
        DrawRunInner(d, text, x, baseline, align, font, fill, glow, glowDy, stroke, strokeOffsets, alpha, scratch);
        d.Restore();
    }

    private static void DrawRunInner(Immediate2D d, string text, float x, float baseline, TextAlign2D align, DwFont font,
        in Color4 fill, in Color4 glow, float glowDy, in Color4 stroke, Vector2[]? strokeOffsets, float alpha,
        Paint2D scratch)
    {
        if (text.Length == 0 || alpha <= 0.002f) return;

        if (glowDy != 0f && glow.A > 0f)
        {
            scratch.Brush = new SolidBrush2D(new Color4(glow.R, glow.G, glow.B, glow.A * alpha));
            d.DrawText(text, x, baseline + glowDy, align, font, scratch);
        }

        if (strokeOffsets is not null && stroke.A > 0f)
        {
            scratch.Brush = new SolidBrush2D(new Color4(stroke.R, stroke.G, stroke.B, stroke.A * alpha));
            foreach (var offset in strokeOffsets)
                d.DrawText(text, x + offset.X, baseline + offset.Y, align, font, scratch);
        }

        scratch.Brush = new SolidBrush2D(new Color4(fill.R, fill.G, fill.B, fill.A * alpha));
        d.DrawText(text, x, baseline, align, font, scratch);
    }

    private static Rect SweepBand(float x0, float x1, float baseline, DwFont font, float pad) =>
        new(x0, baseline - font.Ascent - pad, MathF.Max(x1 - x0, 0.01f), font.LineHeight + 2f * pad);

    private static Color4 WithAlpha(in Color4 c, float alpha) => new(c.R, c.G, c.B, c.A * alpha);

    private static float EaseOut(float p) => 1f - (1f - p) * (1f - p);

    private static string FlatText(string text) =>
        text.IndexOf('\r') < 0 ? text.Replace("\n", "") : text.Replace("\r", "").Replace("\n", "");

    private static bool WordsMatchText(LyricClip clip)
    {
        if (clip.Words is not { Count: > 0 } words) return false;

        var flat = FlatText(clip.Text);
        var g = 0;
        foreach (var word in words)
        {
            if (word.Text.Length == 0) continue;
            if (g + word.Text.Length > flat.Length) return false;
            if (string.CompareOrdinal(word.Text, 0, flat, g, word.Text.Length) != 0) return false;
            g += word.Text.Length;
        }

        return g == flat.Length;
    }

    private static SentenceLayout BuildLayout(LyricClip clip, DwFont font)
    {
        var words = clip.Words ?? Array.Empty<KrcWord>();
        var wordStartMs = new long[words.Count];
        var wordDurMs = new long[words.Count];
        var wordOffset = new int[words.Count];
        var offset = 0;
        for (var i = 0; i < words.Count; i++)
        {
            wordStartMs[i] = words[i].StartMs;
            wordDurMs[i] = words[i].DurMs;
            wordOffset[i] = offset;
            offset += words[i].Text.Length;
        }

        var source = clip.Text.Split(LineSeparators, StringSplitOptions.None);
        var lines = new LineLayout[source.Length];
        var w = 0;
        var g = 0;

        for (var li = 0; li < source.Length; li++)
        {
            var text = source[li];
            var tokens = TextTypeset.SplitGlyphs(text);
            var cells = TextTypeset.Line(tokens);
            var xs = new List<float>(tokens.Count);
            var ws = new List<float>(tokens.Count);
            var fs = new List<float>(tokens.Count);
            var spans = new List<WordSpan>();
            var x = 0f;

            for (var c = 0; c < tokens.Count; c++)
            {
                var token = tokens[c];
                var cell = cells[c];
                var glyphW = font.MeasureWidth(token) * (float)cell.F;

                while (w < words.Count && g >= wordOffset[w] + words[w].Text.Length) w++;
                if (w < words.Count && g >= wordOffset[w])
                {
                    if (spans.Count > 0 && spans[^1].Word == w) spans[^1] = spans[^1] with { Last = c };
                    else spans.Add(new WordSpan(w, c, c));
                }

                fs.Add((float)cell.F);
                ws.Add(glyphW);
                x += (float)cell.Gap * font.Size;
                xs.Add(x);
                x += glyphW * (float)cell.Adv;
                g += token.Length;
            }

            var halfLine = x / 2f;
            for (var k = 0; k < xs.Count; k++) xs[k] -= halfLine;

            lines[li] = new LineLayout
            {
                Text = text,
                Tokens = tokens.ToArray(),
                X = xs.ToArray(),
                W = ws.ToArray(),
                F = fs.ToArray(),
                Spans = spans.ToArray(),
            };
        }

        return new SentenceLayout
        {
            Lines = lines,
            WordStartMs = wordStartMs,
            WordDurMs = wordDurMs,
            LineHeight = font.LineHeight,
        };
    }

    private sealed class SentenceLayout
    {
        public LineLayout[] Lines = [];
        public long[] WordStartMs = [];
        public long[] WordDurMs = [];
        public float LineHeight;
    }

    private sealed class LineLayout
    {
        public string Text = "";
        public string[] Tokens = [];
        public float[] X = [];
        public float[] W = [];
        public float[] F = [];
        public WordSpan[] Spans = [];
    }

    private readonly record struct WordSpan(int Word, int First, int Last);

    private static Vector2[] BuildStrokeOffsets(float strokeWidth)
    {
        var radius = strokeWidth / 2f;
        var offsets = new Vector2[StrokeDirections];
        for (var i = 0; i < StrokeDirections; i++)
        {
            var angle = i * 2f * MathF.PI / StrokeDirections;
            offsets[i] = new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
        }

        return offsets;
    }

    public void Dispose()
    {
        _target?.Dispose();
    }
}
