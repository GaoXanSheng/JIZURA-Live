using System.Numerics;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using Vortice.Mathematics;

namespace JizuraLive.Render.Text;

public enum TreatPass
{
    Plan,
    Pre,
    Post,
}

public sealed record TreatParamDef(string Key, string Label, double Def);

public sealed class TreatPalette
{
    public string Bg = "#000000";
    public string Fg = "#FFFFFF";
    public string Sub = "#9A9A9A";
    public string Accent = "#F5A50C";
    public string Accent2 = "#16F4D4";
    public string Ink = "#141414";
    public string GhostA = "#FF3B30";
    public string GhostB = "#16F4D4";

    public string Str(string key) => key switch
    {
        "bg" => Bg,
        "fg" => Fg,
        "sub" => Sub,
        "accent" => Accent,
        "accent2" => Accent2,
        "ink" => Ink,
        "ghostA" => GhostA,
        "ghostB" => GhostB,
        _ => "",
    };
}

public delegate TreatGlyphXf? TreatGlyphFn(int glyphIndex, int lineIndex, int tokenIndex, int tokenCount, string token);

public readonly record struct TreatGlyphXf(
    float Scale,
    float RotDeg,
    float Dx,
    float Dy,
    float Alpha,
    Color4? Color)
{
    public TreatGlyphXf() : this(1f, 0f, 0f, 0f, 1f, null)
    {
    }

    public static TreatGlyphXf Identity { get; } = new(1f, 0f, 0f, 0f, 1f, null);
}

public sealed class TreatLine
{
    public string[] Tokens = [];
    public float[] X = [];
    public float[] W = [];
    public float[] F = [];
    public float Baseline;
    public int GlobalStart;
}

public sealed class TreatCopyOpts
{
    public Color4? Fill { get; init; }
    public float Alpha { get; init; } = 1f;
    public float StrokeW { get; init; }
    public Color4? StrokeColor { get; init; }
    public (float Lo, float Hi)? ClipY { get; init; }
}

public sealed class TreatContext
{
    public TreatPass Pass;

    public required Immediate2D D;
    public required IReadOnlyList<TreatLine> Lines;
    public required DwFont Font;
    public required float Size;
    public required float Cx;
    public required float LineHeight;
    public required TreatPalette Sc;
    public required Dictionary<string, object> P;
    public required double Lt;
    public required double Ltb;
    public required double Dur;
    public required double InDur;
    public required double OutDur;
    public required double PIn;
    public required double POut;
    public required float Alpha;
    public required int Seed;
    public required int Step;

    public readonly List<TreatGlyphFn> GlyphFns = new();
    public (float Lo, float Hi)? ClipY;
    public bool HideFill;
    public bool UseKaraokeAnim;
    public float? StrokeWEm;
    public Color4? StrokeColor;
    public Color4? GlowColor;
    public float? GlowSize;
    public (float Pos, Color4 Color)[]? Gradient;
    public float TrackEm;

    public float Top => Lines[0].Baseline - Size * 1.05f;
    public float Bottom => Lines[^1].Baseline + Size * 0.25f;
    public float CenterY => (Top + Bottom) / 2f;

    public double Num(string k, double def)
    {
        if (!P.TryGetValue(k, out var v)) return def;
        return v switch
        {
            double d => d,
            int i => i,
            _ => def,
        };
    }

    public string? Str(string k) => P.TryGetValue(k, out var v) ? v as string : null;

    public double InP(double delay, double len) => TreatEase.Clamp((Lt - delay) / len);

    public Color4 Col(string? hex, double a = 1) => TreatMath.Hex(hex, a);

    public string K(string key, string fb)
    {
        var v = Sc.Str(key);
        return string.IsNullOrEmpty(v) ? fb : v;
    }

    private static readonly Vector2[] StrokeOffsets = TreatStrokeOffsets.Offsets;

    public float GlyphRadius => Size * 0.5f;

    private float YOf(float baseline, double u) => baseline - Size * (0.36f + (float)u);

    public (float Y0, float Y1) BandOf(float baseline, double lo, double hi) =>
        (YOf(baseline, hi), YOf(baseline, lo));

    public Rect BandRect(TreatLine line, double lo, double hi)
    {
        var (y0, y1) = BandOf(line.Baseline, lo, hi);
        return new Rect(Cx - Size * 24f, y0, Size * 48f, MathF.Max(0.01f, y1 - y0));
    }

    public void FillRect(float x, float y, float w, float h, Color4 c, float a = 1f)
    {
        if (a <= 0.003f || w <= 0f || h <= 0f) return;
        var p = new Paint2D { Brush = new SolidBrush2D(new Color4(c.R, c.G, c.B, c.A * a)) };
        D.FillRect(new Rect(x, y, w, h), p);
    }

    public void Line(float x0, float y0, float x1, float y1, Color4 c, float lw, float a = 1f)
    {
        if (a <= 0.003f) return;
        var p = new Paint2D { Brush = new SolidBrush2D(new Color4(c.R, c.G, c.B, c.A * a)), StrokeWidth = MathF.Max(0.01f, lw) };
        D.DrawLine(x0, y0, x1, y1, p);
    }

    public void Circle(float cx, float cy, float r, Color4? fill, Color4? stroke, float sw, float a = 1f)
    {
        if (a <= 0.003f || r <= 0f) return;
        if (fill is { } f)
        {
            var p = new Paint2D { Brush = new SolidBrush2D(new Color4(f.R, f.G, f.B, f.A * a)) };
            D.FillCircle(cx, cy, r, p);
        }

        if (stroke is { } s)
        {
            var p = new Paint2D
            {
                Brush = new SolidBrush2D(new Color4(s.R, s.G, s.B, s.A * a)),
                IsStroke = true,
                StrokeWidth = MathF.Max(0.01f, sw),
            };
            D.StrokeCircle(cx, cy, r, p);
        }
    }

    public void ClipPush(TreatLine line, double lo, double hi)
    {
        D.Save();
        D.ClipRect(BandRect(line, lo, hi));
    }

    public void ClipPop() => D.Restore();

    public void CopyBlock(float dx, float dy, in TreatCopyOpts o)
    {
        if (o.Alpha <= 0.003f) return;
        foreach (var line in Lines)
        {
            if (o.ClipY is { } band) D.Save();
            for (var t = 0; t < line.Tokens.Length; t++)
            {
                var token = line.Tokens[t];
                if (string.IsNullOrEmpty(token) || token == " ") continue;
                var x = Cx + line.X[t] + dx;
                var baseline = line.Baseline + dy;
                if (o.ClipY is { } b)
                {
                    var (y0, y1) = BandOf(baseline, b.Lo, b.Hi);
                    if (y1 - y0 < 0.01f) continue;
                    D.Save();
                    D.ClipRect(new Rect(x - Size * 8f, y0, Size * 16f, y1 - y0));
                }

                var scratch = new Paint2D();
                var alpha = o.Alpha;
                if (o.StrokeColor is { } sc && o.StrokeW > 0f)
                {
                    scratch.Brush = new SolidBrush2D(new Color4(sc.R, sc.G, sc.B, sc.A * alpha));
                    var radius = o.StrokeW / 2f;
                    for (var i = 0; i < StrokeOffsets.Length; i++)
                    {
                        var off = StrokeOffsets[i];
                        D.DrawText(token, x + off.X * radius, baseline + off.Y * radius,
                            TextAlign2D.Left, Font, scratch);
                    }
                }

                if (o.Fill is { } f)
                {
                    scratch.Brush = new SolidBrush2D(new Color4(f.R, f.G, f.B, f.A * alpha));
                    D.DrawText(token, x, baseline, TextAlign2D.Left, Font, scratch);
                }

                if (o.ClipY is not null) D.Restore();
            }

            if (o.ClipY is not null) D.Restore();
        }
    }

    public void ForGlyphs(Action<int, TreatLine, int, float, float, float> fn)
    {
        foreach (var line in Lines)
            for (var t = 0; t < line.Tokens.Length; t++)
            {
                var token = line.Tokens[t];
                if (string.IsNullOrEmpty(token) || token == " ") continue;
                fn(line.GlobalStart + t, line, t, Cx + line.X[t], line.Baseline,
                    MathF.Max(line.W[t], Size * 0.5f));
            }
    }

    public float GlyphCenterY(TreatLine line) => line.Baseline - Size * 0.36f;
}

public static class TreatEase
{
    public static double Clamp(double x, double a = 0, double b = 1) => x < a ? a : x > b ? b : x;
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    public static double Smooth(double a, double b, double x)
    {
        var t = Clamp((x - a) / (b - a));
        return t * t * (3 - 2 * t);
    }

    public static double InQuad(double x)
    {
        x = Clamp(x);
        return x * x;
    }

    public static double InCubic(double x)
    {
        x = Clamp(x);
        return x * x * x;
    }

    public static double OutCubic(double x) => 1 - Math.Pow(1 - Clamp(x), 3);

    public static double InOutCubic(double x)
    {
        x = Clamp(x);
        return x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
    }

    public static double OutBack(double x, double s = 1.9)
    {
        x = Clamp(x);
        var c = s + 1;
        return 1 + c * Math.Pow(x - 1, 3) + s * Math.Pow(x - 1, 2);
    }

    public static double InOutSine(double x)
    {
        x = Clamp(x);
        return -(Math.Cos(Math.PI * x) - 1) / 2;
    }
}

public static class TreatMath
{
    public static (int R, int G, int B) HexRgb(string? h)
    {
        if (string.IsNullOrEmpty(h)) return (0, 0, 0);
        var s = h.AsSpan().Trim();
        if (s.Length > 0 && s[0] == '#') s = s[1..];
        if (s.Length == 3)
        {
            var r = HexVal(s[0]);
            var g = HexVal(s[1]);
            var b = HexVal(s[2]);
            return (r * 17, g * 17, b * 17);
        }

        if (s.Length >= 6 && int.TryParse(s[..6], System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out var n))
            return ((n >> 16) & 255, (n >> 8) & 255, n & 255);
        return (0, 0, 0);
    }

    private static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => 0,
    };

    public static Color4 Hex(string? h, double a = 1)
    {
        var (r, g, b) = HexRgb(h);
        return new Color4(r / 255f, g / 255f, b / 255f, (float)TreatEase.Clamp(a, 0, 1));
    }

    public static string Mix(string h1, string h2, double t)
    {
        var a = HexRgb(h1);
        var b = HexRgb(h2);
        int F(int v1, int v2) => (int)Math.Round(TreatEase.Lerp(v1, v2, t));
        return "#" + F(a.R, b.R).ToString("x2") + F(a.G, b.G).ToString("x2") + F(a.B, b.B).ToString("x2");
    }

    public static double Lum(string h)
    {
        var (r, g, b) = HexRgb(h);
        return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
    }

    public static double Contrast(string a, string b)
    {
        double L(string h)
        {
            var (r0, g0, b0) = HexRgb(h);
            double Ch(int v)
            {
                var x = v / 255.0;
                return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Ch(r0) + 0.7152 * Ch(g0) + 0.0722 * Ch(b0);
        }

        var x = L(a);
        var y = L(b);
        return (Math.Max(x, y) + 0.05) / (Math.Min(x, y) + 0.05);
    }

    public static string ToHex(double r, double g, double b)
    {
        string F(double v) => ((int)Math.Round(TreatEase.Clamp(v, 0, 255))).ToString("x2");
        return ("#" + F(r) + F(g) + F(b)).ToUpperInvariant();
    }

    public static string Hsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360 / 360;
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        double F(double t)
        {
            t = (t + 1) % 1;
            if (t < 1 / 6.0) return p + (q - p) * 6 * t;
            if (t < 1 / 2.0) return q;
            return t < 2 / 3.0 ? p + (q - p) * (2 / 3.0 - t) * 6 : p;
        }

        return ToHex(F(h + 1 / 3.0) * 255, F(h) * 255, F(h - 1 / 3.0) * 255);
    }

    public static (double H, double S, double L) ToHsl(string hex)
    {
        var (r0, g0, b0) = HexRgb(hex);
        double r = r0 / 255, g = g0 / 255, b = b0 / 255;
        var mx = Math.Max(r, Math.Max(g, b));
        var mn = Math.Min(r, Math.Min(g, b));
        var l = (mx + mn) / 2;
        if (mx == mn) return (0, 0, l);
        var d = mx - mn;
        var s = l > 0.5 ? d / (2 - mx - mn) : d / (mx + mn);
        double h;
        if (mx == r) h = (g - b) / d + (g < b ? 6 : 0);
        else if (mx == g) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;
        return (h * 60, s, l);
    }

    public static string FitContrast(string hex, string bg, double min = 3)
    {
        if (Contrast(hex, bg) >= min) return hex.ToUpperInvariant();
        var (h, s, l) = ToHsl(hex);
        var dark = Lum(bg) < 0.5;
        for (var i = 0; i < 24; i++)
        {
            l = dark ? Math.Min(0.96, l + 0.035) : Math.Max(0.04, l - 0.035);
            var c = Hsl(h, s, l);
            if (Contrast(c, bg) >= min) return c;
        }

        return dark ? "#FFFFFF" : "#111111";
    }

    public static bool IsKanji(char c) =>
        c is >= '\u3400' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF' or '々' or '〆' or 'ヶ';

    public static bool IsKata(char c) =>
        c is >= '゠' and <= 'ヿ' or >= '\u31F0' and <= '\u31FF' or >= 'ｦ' and <= 'ﾟ';

    public static bool IsLatin(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    public static bool IsPunct(char c) =>
        "、。，．,.!?！？…‥・「」『』（）()【】〈〉《》〔〕［］[]'\"“”‘’ー〜～:：;；-—―".Contains(c);

    public static string? FirstOK(IEnumerable<string?> list, Func<string, bool> test, string? fb)
    {
        foreach (var c in list)
            if (c != null && test(c))
                return c;
        return fb;
    }

    public static string Best(IEnumerable<string?> list, string against)
    {
        string b = "";
        var bv = -1.0;
        foreach (var c in list)
        {
            if (c == null) continue;
            var v = Contrast(c, against);
            if (!(v > bv)) continue;
            bv = v;
            b = c;
        }

        return b;
    }

    public static bool IsDark(string? bg) => Lum(bg ?? "#000000") < 0.45;

    public static string AccentFor(TreatPalette sc, string col, double min = 1.6) =>
        FirstOK(new[] { sc.Str("accent"), sc.Str("accent2"), sc.Str("ghostA"), sc.Str("ghostB") },
            c => Contrast(c, col) >= min && Contrast(c, sc.Bg) >= 1.5,
            FitContrast(sc.Accent, col, min + 0.3))!;

    public static string TextOn(string box, string pref, TreatPalette sc) =>
        Contrast(pref, box) >= 3 ? pref : Best(new[] { sc.Bg, sc.Fg, sc.Ink, "#111111", "#FFFFFF" }, box);

    public static string MarkCol(TreatPalette sc, string col) =>
        FirstOK(new[] { sc.Str("accent"), sc.Str("accent2"), col }, c => Contrast(c, sc.Bg) >= 2, col)!;
}

public sealed class TreatDef
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string[] Tags { get; init; } = [];
    public TreatParamDef[] Params { get; init; } = [];
    public required Func<CameraRng, TreatPalette, Dictionary<string, object>> Plan { get; init; }
    public required Action<TreatContext> Draw { get; init; }
}

internal static class TreatStrokeOffsets
{
    public static readonly Vector2[] Offsets = Build(16);

    private static Vector2[] Build(int n)
    {
        var offsets = new Vector2[n];
        for (var i = 0; i < n; i++)
        {
            var angle = i * 2f * MathF.PI / n;
            offsets[i] = new Vector2(MathF.Cos(angle), MathF.Sin(angle));
        }

        return offsets;
    }
}

public static class TreatPresets
{
    public static readonly IReadOnlyDictionary<string, TreatDef> All = Build();

    public static TreatDef? Get(string? id)
    {
        TreatDef? d = null;
        if (!string.IsNullOrEmpty(id)) All.TryGetValue(id!.Trim(), out d);
        return d;
    }

    public static IReadOnlyList<string> Ids { get; } = [.. All.Keys];

    private static readonly string[] PaletteKeys =
        ["ink", "fg", "accent", "accent2", "sub", "ghostB"];

    private static List<string> PaletteOf(TreatContext ctx, string col, double minBg = 1.5, double minPair = 1.2)
    {
        var plates = new List<string>();
        foreach (var k in PaletteKeys)
        {
            var c = ctx.K(k, "");
            if (c.Length > 0 && TreatMath.Contrast(c, ctx.Sc.Bg) >= minBg &&
                plates.All(q => TreatMath.Contrast(q, c) >= minPair))
                plates.Add(c);
        }

        if (plates.Count == 0)
            plates.Add(TreatMath.FitContrast(ctx.Sc.Accent, ctx.Sc.Bg, 2));
        return plates;
    }

    private static Dictionary<string, TreatDef> Build()
    {
        var list = new List<TreatDef>
        {
            new TreatDef
            {
                Id = "neonOutline",
                Name = "霓虹管",
                Tags = ["glitch", "emotional", "pop"],
                Params = [new TreatParamDef("k", "线宽", 0.028), new TreatParamDef("fl", "闪炼", 1)],
                Plan = (rng, _) => new Dictionary<string, object> { ["k"] = rng.Range(0.024, 0.032), ["fl"] = rng.Next() < 0.75 ? 1.0 : 0.0 },
                Draw = ctx =>
                {
                    if (ctx.Pass == TreatPass.Plan)
                    {
                        var bg = ctx.Sc.Bg;
                        var col = ctx.Sc.Fg;
                        var dk = TreatMath.IsDark(bg);
                        var gc = dk
                            ? TreatMath.FirstOK(
                                new[] { ctx.Sc.Str("accent"), ctx.Sc.Str("accent2"), ctx.Sc.Str("ghostA"), ctx.Sc.Str("ghostB") },
                                c => TreatMath.Lum(c) > TreatMath.Lum(bg) + 0.15 && TreatMath.Contrast(c, col) >= 1.2, col)!
                            : TreatMath.FirstOK(
                                new[] { ctx.Sc.Str("accent"), ctx.Sc.Str("accent2"), ctx.Sc.Str("ghostA"), ctx.Sc.Str("ghostB") },
                                c => TreatMath.Contrast(c, bg) >= 1.8, col)!;
                        var tube = dk ? TreatMath.Mix(col, "#FFFFFF", 0.4) : col;
                        var k = ctx.Num("k", 0.028);
                        ctx.HideFill = true;
                        ctx.StrokeColor = TreatMath.Hex(tube);
                        ctx.StrokeWEm = (float)Math.Max(1.4, ctx.Size * k) / ctx.Size;
                        ctx.GlowColor = TreatMath.Hex(gc, dk ? 1 : 0.7);
                        ctx.GlowSize = ctx.Size * 0.07f;
                        if (ctx.Num("fl", 0) != 0)
                        {
                            var s0 = unchecked((int)CameraHash.H(ctx.Seed, 3, 41));
                            ctx.GlyphFns.Add((gi, _, _, _, _) =>
                            {
                                var r = CameraHash.R(s0, gi, ctx.Step);
                                return r < 0.03 ? new TreatGlyphXf { Scale = 1f, Alpha = 0.22f }
                                    : r < 0.05 ? new TreatGlyphXf { Scale = 1f, Alpha = 0.6f }
                                    : null;
                            });
                        }
                    }
                    else if (ctx.Pass == TreatPass.Pre && ctx.Alpha >= 0.5)
                    {
                        var k = ctx.Num("k", 0.028);
                        var gc = ctx.GlowColor ?? TreatMath.Hex(ctx.Sc.Accent);
                        ctx.CopyBlock(0f, 0f, new TreatCopyOpts
                        {
                            Fill = null,
                            StrokeColor = gc,
                            StrokeW = ctx.Size * (float)k * 4.5f,
                            Alpha = (float)(TreatMath.IsDark(ctx.Sc.Bg) ? 0.22 : 0.16) * ctx.Alpha,
                        });
                    }
                },
            },
            new TreatDef
            {
                Id = "chrome",
                Name = "铬字",
                Tags = ["pop", "graphic"],
                Params = [new TreatParamDef("h", "分界", 0.53)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["v"] = rng.Pick(["silver", "silver", "sunset"]),
                    ["h"] = rng.Range(0.5, 0.56),
                },
                Draw = ctx =>
                {
                    if (ctx.Pass != TreatPass.Plan || ctx.Alpha < 0.9) return;
                    var col = ctx.Sc.Fg;
                    var light = TreatMath.Lum(col) > 0.42;
                    var h = ctx.Num("h", 0.53);
                    var acc = TreatMath.AccentFor(ctx.Sc, col, 1.3);
                    var top0 = light ? "#FFFFFF" : TreatMath.Mix(col, "#FFFFFF", 0.55);
                    var top1 = light ? TreatMath.Mix(col, "#000000", 0.42) : col;
                    var band = TreatMath.Mix(col, "#000000", light ? 0.62 : 0.45);
                    var sunset = ctx.Str("v") == "sunset";
                    var low0 = sunset ? TreatMath.Mix(acc, "#FFFFFF", 0.5) : TreatMath.Mix(col, "#FFFFFF", light ? 0.75 : 0.42);
                    var low1 = sunset ? acc : TreatMath.Mix(col, "#000000", light ? 0.12 : 0.25);
                    ctx.Gradient = new[]
                    {
                        (0.08f, TreatMath.Hex(top0)),
                        ((float)h, TreatMath.Hex(top1)),
                        ((float)h, TreatMath.Hex(band)),
                        ((float)h + 0.03f, TreatMath.Hex(band)),
                        ((float)h + 0.03f, TreatMath.Hex(low0)),
                        (0.94f, TreatMath.Hex(low1)),
                    };
                    ctx.StrokeColor = TreatMath.Hex(TreatMath.Mix(col, "#000000", light ? 0.7 : 0.35));
                    ctx.StrokeWEm = Math.Max(1.2f, ctx.Size * 0.022f) / ctx.Size;
                },
            },
            new TreatDef
            {
                Id = "rainbow",
                Name = "虹色",
                Tags = ["pop", "emotional"],
                Params = [new TreatParamDef("v", "模式", 0), new TreatParamDef("sp", "速度", 0.36)],
                Plan = (rng, sc) => new Dictionary<string, object>
                {
                    ["v"] = rng.Pick(["drift", "drift", "steps"]),
                    ["sp"] = rng.Range(0.28, 0.45),
                    ["dir"] = rng.Next() < 0.5 ? 1.0 : -1.0,
                },
                Draw = ctx =>
                {
                    if (ctx.Pass != TreatPass.Plan || ctx.Alpha < 0.9) return;
                    var col = ctx.Sc.Fg;
                    if (TreatMath.Contrast(col, ctx.Sc.Bg) < 1.5) return;
                    var pal = new List<string> { col };
                    foreach (var c in new[] { ctx.Sc.Str("accent"), ctx.Sc.Str("accent2"), ctx.Sc.Str("ghostA"), ctx.Sc.Str("ghostB"), ctx.Sc.Str("sub") })
                        if (c != null && TreatMath.Contrast(c, ctx.Sc.Bg) >= 1.8 && pal.All(q => TreatMath.Contrast(q, c) >= 1.25))
                            pal.Add(c);
                    if (pal.Count < 3)
                        pal.Add(TreatMath.FitContrast(
                            TreatMath.Mix(ctx.Sc.Accent,
                                string.IsNullOrEmpty(ctx.Sc.Accent2) ? col : ctx.Sc.Accent2, 0.5),
                            ctx.Sc.Bg, 2));
                    var l = pal.Count;
                    var off = Math.Abs(ctx.Seed) % 7;
                    var v = ctx.Str("v");
                    var sp = ctx.Num("sp", 0.36);
                    var dir = ctx.Num("dir", 1);
                    var colors = pal.ConvertAll(c => TreatMath.Hex(c));
                    ctx.GlyphFns.Add((gi, _, _, _, _) =>
                    {
                        if (v == "steps")
                        {
                            var k = ((gi + off + (int)Math.Floor(ctx.Ltb * 2.2) * (int)dir) % l + l) % l;
                            return new TreatGlyphXf { Color = colors[k] };
                        }

                        var u = (gi + off) * sp - ctx.Ltb * 0.75 * dir;
                        var k2 = ((u % l) + l) % l;
                        var a = (int)Math.Floor(k2);
                        var f = TreatEase.Smooth(0.3, 0.7, k2 - a);
                        var c0 = colors[a];
                        var c1 = colors[(a + 1) % l];
                        return new TreatGlyphXf
                        {
                            Color = new Color4(
                                c0.R + (c1.R - c0.R) * (float)f,
                                c0.G + (c1.G - c0.G) * (float)f,
                                c0.B + (c1.B - c0.B) * (float)f, 1f),
                        };
                    });
                },
            },
            new TreatDef
            {
                Id = "glitchSplit",
                Name = "色版错位",
                Tags = ["glitch", "pop"],
                Params = [new TreatParamDef("d", "错位", 0.07), new TreatParamDef("up", "上移", 0)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["d"] = rng.Range(0.06, 0.08),
                    ["up"] = rng.Next() < 0.3 ? 1.0 : 0.0,
                },
                Draw = ctx =>
                {
                    if (ctx.Pass != TreatPass.Pre || ctx.Alpha < 0.9) return;
                    var col = ctx.Sc.Fg;
                    var cands = new[] { ctx.Sc.Str("ghostA"), ctx.Sc.Str("ghostB"), ctx.Sc.Str("accent"), ctx.Sc.Str("accent2") }
                        .Where(c => c != null && TreatMath.Contrast(c, ctx.Sc.Bg) >= 1.4 && TreatMath.Contrast(c, col) >= 1.15)
                        .ToList();
                    var ca = cands.Count > 0 ? cands[0] : TreatMath.AccentFor(ctx.Sc, col, 1.4);
                    var cb = cands.Find(c => TreatMath.Contrast(c, ca) >= 1.3) ?? TreatMath.Mix(ca, ctx.Sc.Bg, 0.4);
                    var s0 = unchecked((int)CameraHash.H(ctx.Seed, 5, 43));
                    var step = ctx.Step;
                    var r = CameraHash.R(s0, step);
                    var hit = r < 0.2 && ctx.Lt > ctx.InDur * 0.5;
                    var d = ctx.Size * (float)ctx.Num("d", 0.07) * (float)(hit ? 1.8 + CameraHash.R(s0, step, 2) : 1);
                    var dy = (float)((ctx.Num("up", 0) != 0 ? d * 0.45 : 0)
                                     + (hit ? CameraHash.Rs(s0, step, 3) * ctx.Size * 0.02 : 0));
                    var alpha = 0.95f * ctx.Alpha;
                    ctx.CopyBlock(-d, dy, new TreatCopyOpts { Fill = TreatMath.Hex(ca), Alpha = alpha });
                    ctx.CopyBlock(d, dy, new TreatCopyOpts { Fill = TreatMath.Hex(cb), Alpha = alpha });
                },
            },
            new TreatDef
            {
                Id = "shadowStack",
                Name = "多重影",
                Tags = ["pop", "graphic"],
                Params = [new TreatParamDef("d", "间距", 0.048), new TreatParamDef("n", "层数", 3)],
                Plan = (rng, sc) => new Dictionary<string, object>
                {
                    ["d"] = rng.Range(0.04, 0.055),
                    ["dirX"] = rng.Pick([1.0, 1.0, -1.0, 1.0, 0.0]),
                    ["dirY"] = rng.Pick([1.0, 1.0, 1.0, 0.5, 1.0]),
                    ["n"] = (double)rng.Pick([3, 3, 4]),
                },
                Draw = ctx =>
                {
                    var col = ctx.Sc.Fg;
                    if (ctx.Pass == TreatPass.Plan)
                    {
                        if (ctx.Alpha < 0.9) return;
                        var cols = new List<string>();
                        foreach (var c in new[] { ctx.Sc.Str("accent"), ctx.Sc.Str("accent2"), ctx.Sc.Str("ghostB"), ctx.Sc.Str("ghostA"), ctx.Sc.Str("ink"), ctx.Sc.Str("sub"), ctx.Sc.Str("fg") })
                            if (c != null && TreatMath.Contrast(c, ctx.Sc.Bg) >= 1.4 && TreatMath.Contrast(c, col) >= 1.12 &&
                                cols.All(q => TreatMath.Contrast(q, c) >= 1.12))
                                cols.Add(c);
                        var n = (int)ctx.Num("n", 3);
                        while (cols.Count < n)
                            cols.Add(TreatMath.Mix(cols.Count > 0 ? cols[^1] : TreatMath.AccentFor(ctx.Sc, col), ctx.Sc.Bg, 0.35));
                        for (var i = 0; i < Math.Min(n, cols.Count); i++)
                            ctx.P[$"_col{i}"] = cols[i];
                        ctx.StrokeColor = TreatMath.Hex(ctx.Sc.Bg);
                        ctx.StrokeWEm = Math.Max(1.5f, ctx.Size * 0.028f) / ctx.Size;
                        return;
                    }

                    if (ctx.Pass != TreatPass.Pre || ctx.Alpha < 0.9) return;
                    var d = ctx.Size * (float)ctx.Num("d", 0.048);
                    var dx = (float)ctx.Num("dirX", 1) * d;
                    var dy = (float)ctx.Num("dirY", 1) * d;
                    var sw = Math.Max(1.5f, ctx.Size * 0.028f);
                    var bgC = TreatMath.Hex(ctx.Sc.Bg);
                    for (var k = (int)ctx.Num("n", 3); k >= 1; k--)
                    {
                        if (!ctx.P.TryGetValue($"_col{k - 1}", out var ck)) continue;
                        ctx.CopyBlock(dx * k, dy * k, new TreatCopyOpts
                        {
                            Fill = TreatMath.Hex(ck as string),
                            StrokeColor = bgC,
                            StrokeW = sw,
                            Alpha = ctx.Alpha,
                        });
                    }
                },
            },
            new TreatDef
            {
                Id = "stencilGap",
                Name = "镂空字",
                Tags = ["graphic", "editorial", "glitch"],
                Params = [new TreatParamDef("v", "切缝", 0), new TreatParamDef("at", "位置", 0), new TreatParamDef("g", "缝宽", 0.04)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["v"] = rng.Pick(["one", "one", "two"]),
                    ["at"] = rng.Range(-0.05, 0.06),
                    ["g"] = rng.Range(0.034, 0.048),
                },
                Draw = ctx =>
                {
                    var g = ctx.Size * (float)ctx.Num("g", 0.04)
                            * (float)TreatEase.OutCubic(ctx.InP(0.04, 0.4))
                            * (float)(1 - TreatEase.InCubic(ctx.POut)) / ctx.Size;
                    if (g < 0.003) return;
                    var cuts = ctx.Str("v") == "two" ? new float[] { -0.13f, 0.13f } : new[] { (float)ctx.Num("at", 0) };
                    var parts = new List<(float Lo, float Hi)>();
                    var lo = -0.8f;
                    foreach (var c in cuts)
                    {
                        parts.Add((lo, c - (float)g));
                        lo = c + (float)g;
                    }

                    parts.Add((lo, 0.8f));
                    if (ctx.Pass == TreatPass.Plan)
                    {
                        if (ctx.Alpha < 0.9) return;
                        ctx.ClipY = parts[0];
                        return;
                    }

                    if (ctx.Pass != TreatPass.Pre || ctx.Alpha < 0.9) return;
                    for (var k = 1; k < parts.Count; k++)
                        ctx.CopyBlock(0f, 0f, new TreatCopyOpts { Fill = TreatMath.Hex(ctx.Sc.Fg), ClipY = parts[k], Alpha = ctx.Alpha });
                },
            },
            new TreatDef
            {
                Id = "waterline",
                Name = "水位",
                Tags = ["emotional", "pop", "calm"],
                Params = [new TreatParamDef("lvl", "水位", 0.52), new TreatParamDef("c", "用色", 0), new TreatParamDef("k", "描边", 0.023)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["lvl"] = rng.Range(0.46, 0.58),
                    ["c"] = (double)rng.Pick([0, 1]),
                    ["k"] = rng.Range(0.02, 0.026),
                },
                Draw = ctx =>
                {
                    if (ctx.Pass != TreatPass.Plan || ctx.Alpha < 0.5) return;
                    var col = ctx.Sc.Fg;
                    var liq = ctx.Num("c", 0) != 0 ? TreatMath.AccentFor(ctx.Sc, col, 1.3) : col;
                    var q = TreatEase.InOutCubic(ctx.InP(0.05, 0.8)) * (1 - TreatEase.InCubic(ctx.POut));
                    var mi = Math.Abs(ctx.Seed) % 7;
                    var lvl = TreatEase.Lerp(1.02, ctx.Num("lvl", 0.52), q) + Math.Sin(ctx.Ltb * 2.6 + mi) * 0.022 * q;
                    var surf = TreatMath.Mix(liq, "#FFFFFF", TreatMath.IsDark(liq) ? 0.35 : 0.5);
                    double C(double v) => TreatEase.Clamp(v);
                    ctx.Gradient = new[]
                    {
                        (0f, new Color4(0f, 0f, 0f, 0f)),
                        ((float)C(lvl - 0.001), new Color4(0f, 0f, 0f, 0f)),
                        ((float)C(lvl), TreatMath.Hex(surf)),
                        ((float)C(lvl + 0.025), TreatMath.Hex(surf)),
                        ((float)C(lvl + 0.025), TreatMath.Hex(liq)),
                        (1f, TreatMath.Hex(liq)),
                    };
                    ctx.StrokeColor = TreatMath.Hex(col);
                    ctx.StrokeWEm = (float)Math.Max(1.3, ctx.Size * ctx.Num("k", 0.023)) / ctx.Size;
                },
            },
            new TreatDef
            {
                Id = "karaoke",
                Name = "卡拉OK",
                Tags = ["emotional", "pop", "editorial"],
                Params = [new TreatParamDef("sp", "进度", 0.78), new TreatParamDef("ol", "描边", 0)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["sp"] = rng.Range(0.7, 0.85),
                    ["ol"] = rng.Next() < 0.55 ? 1.0 : 0.0,
                },
                Draw = ctx =>
                {
                    if (ctx.Pass != TreatPass.Plan || ctx.Alpha < 0.9) return;
                    ctx.UseKaraokeAnim = true;
                    if (ctx.Num("ol", 0) != 0)
                    {
                        var col = ctx.Sc.Fg;
                        var oc = TreatMath.Best(new[] { ctx.Sc.Str("bg"), ctx.Sc.Str("ink"), "#111111", "#FFFFFF" }, col);
                        ctx.StrokeColor = TreatMath.Hex(oc);
                        ctx.StrokeWEm = Math.Max(1.5f, ctx.Size * 0.06f) / ctx.Size;
                    }
                },
            },
            new TreatDef
            {
                Id = "sizeWave",
                Name = "大小节奏",
                Tags = ["pop", "graphic", "editorial"],
                Params = [new TreatParamDef("v", "模式", 0), new TreatParamDef("k", "幅度", 0.7)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["v"] = rng.Pick(["alt", "kanji", "kanji", "ramp", "wave"]),
                    ["k"] = rng.Range(0.64, 0.74),
                    ["rev"] = rng.Next() < 0.4 ? 1.0 : 0.0,
                },
                Draw = ctx =>
                {
                    if (ctx.Pass != TreatPass.Plan || ctx.Alpha < 0.5) return;
                    var n = ctx.Lines.Sum(l => l.Tokens.Length);
                    if (n == 0) return;
                    var off = Math.Abs(ctx.Seed) % 7;
                    var k = ctx.Num("k", 0.7);
                    var v = ctx.Str("v") ?? "alt";
                    var chars = ctx.Lines.SelectMany(l => l.Tokens).Where(c => c.Trim().Length > 0).ToList();
                    bool IsK(string c) => TreatMath.IsKanji(c[0]) || TreatMath.IsKata(c[0]) || TreatMath.IsLatin(c[0]);
                    if (v == "kanji" && !(chars.Any(IsK) && chars.Any(c => !IsK(c)))) v = "alt";
                    if ((v is "ramp" or "wave") && chars.Count < 3) v = "alt";
                    var sp = new double[n];
                    Array.Fill(sp, 1.0);
                    var gi = 0;
                    foreach (var line in ctx.Lines)
                    {
                        for (var ci = 0; ci < line.Tokens.Length; ci++, gi++)
                        {
                            var token = line.Tokens[ci];
                            if (string.IsNullOrEmpty(token)) continue;
                            double s;
                            if (v == "alt") s = (ci + gi + off) % 2 != 0 ? k : 1.04;
                            else if (v == "kanji") s = IsK(token) ? 1.08 : k + 0.04;
                            else if (v == "ramp")
                            {
                                var u = line.Tokens.Length > 1 ? ci / (double)(line.Tokens.Length - 1) : 0.5;
                                s = TreatEase.Lerp(1.1, k, ctx.Num("rev", 0) != 0 ? 1 - u : u);
                            }
                            else s = 0.87 + 0.17 * Math.Sin(ci * 1.25 + off);
                            if (!string.IsNullOrEmpty(token) && TreatMath.IsPunct(token[0])) s = Math.Min(s, 0.9);
                            sp[gi] = s;
                        }
                    }

                    var size = ctx.Size;
                    var fArr = new double[n];
                    gi = 0;
                    foreach (var line in ctx.Lines)
                    {
                        double tot = 0;
                        for (var ci = 0; ci < line.Tokens.Length; ci++, gi++)
                        {
                            var gw = line.W[ci] * (line.F[ci] <= 0 ? 1f : line.F[ci]);
                            tot += gw * sp[gi];
                        }

                        var old0 = line.X[0];
                        var old1 = line.X[^1] + line.W[^1];
                        var pp = (old0 + old1) / 2 - tot / 2;
                        gi -= line.Tokens.Length;
                        for (var ci = 0; ci < line.Tokens.Length; ci++, gi++)
                        {
                            var gw = line.W[ci] * (line.F[ci] <= 0 ? 1f : line.F[ci]);
                            fArr[gi] = (pp + gw / 2 - (line.X[ci] + gw / 2)) / size;
                            pp += gw;
                        }
                    }

                    var spCapt = sp;
                    var fCapt = fArr;
                    ctx.GlyphFns.Add((i, _, _, _, _) =>
                    {
                        if (i < 0 || i >= n) return null;
                        var s = (float)spCapt[i];
                        return new TreatGlyphXf
                        {
                            Scale = s,
                            Dx = (float)fCapt[i] * size,
                            Dy = (1f - s) * 0.4f * size,
                        };
                    });
                },
            },
            new TreatDef
            {
                Id = "rotateAlt",
                Name = "摇摆字",
                Tags = ["pop", "emotional"],
                Params = [new TreatParamDef("a", "角度", 11), new TreatParamDef("v", "模式", 0)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["a"] = rng.Range(9, 14),
                    ["v"] = rng.Pick(["alt", "alt", "rand"]),
                },
                Draw = ctx =>
                {
                    if (ctx.Pass != TreatPass.Plan || ctx.Alpha < 0.5) return;
                    var off = Math.Abs(ctx.Seed) % 7;
                    var a = ctx.Num("a", 11);
                    var v = ctx.Str("v");
                    var s0 = unchecked((int)CameraHash.H(ctx.Seed, 9, 47));
                    ctx.GlyphFns.Add((gi, _, t, _, token) =>
                    {
                        if (!string.IsNullOrEmpty(token) && TreatMath.IsPunct(token[0])) return null;
                        var sg = (gi + off) % 2 != 0 ? 1 : -1;
                        var r = v == "rand" ? sg * a * CameraHash.Rr(0.45, 1.25, s0, gi) : sg * a;
                        return new TreatGlyphXf { RotDeg = (float)r, Scale = 0.94f };
                    });
                },
            },
            new TreatDef
            {
                Id = "baselineShift",
                Name = "错落基线",
                Tags = ["pop", "graphic"],
                Params = [new TreatParamDef("v", "模式", 0), new TreatParamDef("k", "幅度", 0.095), new TreatParamDef("dir", "方向", 1)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["v"] = rng.Pick(["alt", "alt", "stairs", "arc"]),
                    ["k"] = rng.Range(0.08, 0.11),
                    ["dir"] = rng.Next() < 0.5 ? 1.0 : -1.0,
                },
                Draw = ctx =>
                {
                    if (ctx.Pass != TreatPass.Plan || ctx.Alpha < 0.5) return;
                    var off = Math.Abs(ctx.Seed) % 7;
                    var kk = ctx.Lines.Count > 1 ? 0.7 : 1.0;
                    var v = ctx.Str("v");
                    var k = ctx.Num("k", 0.095);
                    var dir = ctx.Num("dir", 1);
                    ctx.GlyphFns.Add((_, _, t, cn, _) =>
                    {
                        var u = cn > 1 ? t / (double)(cn - 1) : 0.5;
                        double o;
                        if (v == "stairs") o = (u - 0.5) * k * Math.Min(3.2, cn * 0.55) * dir;
                        else if (v == "arc") o = (Math.Sin(Math.PI * u) - 0.6) * k * 2.2;
                        else o = ((t + off) % 2 != 0 ? 1 : -1) * k;
                        return new TreatGlyphXf { Dy = (float)(-o * kk * ctx.Size) };
                    });
                },
            },
            new TreatDef
            {
                Id = "fauxBold",
                Name = "加粗",
                Tags = ["graphic", "pop", "editorial"],
                Params = [new TreatParamDef("k", "加粗", 0.042)],
                Plan = (rng, _) => new Dictionary<string, object> { ["k"] = rng.Range(0.035, 0.05) },
                Draw = ctx =>
                {
                    if (ctx.Pass != TreatPass.Plan || ctx.Alpha < 0.9) return;
                    var k = ctx.Num("k", 0.042);
                    ctx.TrackEm = (float)k;
                    ctx.StrokeColor = TreatMath.Hex(ctx.Sc.Fg);
                    ctx.StrokeWEm = (float)Math.Max(1, ctx.Size * k) / ctx.Size;
                },
            },
            new TreatDef
            {
                Id = "circled",
                Name = "圈注",
                Tags = ["pop", "graphic", "editorial"],
                Params = [new TreatParamDef("v", "样式", 0), new TreatParamDef("k", "缩放", 0.71)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["v"] = rng.Pick(["ring", "ring", "disc"]),
                    ["k"] = rng.Range(0.68, 0.74),
                },
                Draw = ctx =>
                {
                    var col = ctx.Sc.Fg;
                    if (ctx.Pass == TreatPass.Plan)
                    {
                        if (ctx.Alpha < 0.9 || TreatMath.Contrast(col, ctx.Sc.Bg) < 1.5) return;
                        var disc = ctx.Str("v") == "disc";
                        var rc = disc
                            ? TreatMath.FirstOK(new[] { ctx.Sc.Str("accent"), ctx.Sc.Str("accent2"), ctx.Sc.Str("ink"), ctx.Sc.Str("fg") },
                                c => TreatMath.Contrast(c, ctx.Sc.Bg) >= 2 && TreatMath.Contrast(c, col) >= 1.3, ctx.Sc.Fg)!
                            : TreatMath.MarkCol(ctx.Sc, col);
                        var tc = disc ? TreatMath.TextOn(rc, col, ctx.Sc) : col;
                        ctx.P["_rc"] = rc;
                        ctx.P["_tc"] = tc;
                        var k = ctx.Num("k", 0.71);
                        ctx.GlyphFns.Add((gi, _, _, _, token) =>
                        {
                            if (!string.IsNullOrEmpty(token) && TreatMath.IsPunct(token[0])) return null;
                            var q = QOf(ctx, gi);
                            var useTc = disc && !string.Equals(tc, col, StringComparison.OrdinalIgnoreCase) && q > 0.55;
                            return new TreatGlyphXf
                            {
                                Scale = (float)k,
                                Color = useTc ? TreatMath.Hex(tc) : null,
                            };
                        });
                        return;
                    }

                    if (ctx.Pass != TreatPass.Pre || ctx.Alpha < 0.9) return;
                    var disc2 = ctx.Str("v") == "disc";
                        var rc2 = TreatMath.Hex(ctx.P.TryGetValue("_rc", out var rco) ? rco as string : ctx.Sc.Fg);
                    var k2 = ctx.Num("k", 0.71);
                    ctx.ForGlyphs((gi, _, _, x, y, gw) =>
                    {
                        if (TotalTokens(ctx) <= 0 || gi >= TotalTokens(ctx)) return;
                        var q = (float)QOf(ctx, gi);
                        if (q <= 0.01) return;
                        var r = ctx.Size * 0.49f * (float)(1 / k2) * q * 0.97f;
                        if (disc2) ctx.Circle(x, ctx.GlyphCenterY(FindLine(ctx, gi)), r, rc2, null, 0, ctx.Alpha);
                        else ctx.Circle(x, ctx.GlyphCenterY(FindLine(ctx, gi)), r, null, rc2, MathF.Max(1.5f, ctx.Size * 0.045f), ctx.Alpha);
                    });
                },
            },
            new TreatDef
            {
                Id = "bracketsQuote",
                Name = "括弧",
                Tags = ["editorial", "emotional", "calm"],
                Params = [new TreatParamDef("v", "层数", 0), new TreatParamDef("k", "线宽", 0.058)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["v"] = rng.Pick(["single", "single", "double"]),
                    ["k"] = rng.Range(0.05, 0.065),
                },
                Draw = ctx =>
                {
                    if (ctx.Pass != TreatPass.Post || ctx.Alpha < 0.9 || ctx.Lines.Count == 0) return;
                    var lc = TreatMath.Hex(TreatMath.MarkCol(ctx.Sc, ctx.Sc.Fg));
                    var l0 = ctx.Lines[0];
                    var l1 = ctx.Lines[^1];
                    var s = ctx.Size;
                    var a = ctx.Alpha;
                    var q = (float)TreatEase.OutCubic(ctx.InP(ctx.InDur * 0.45, 0.35)) * (1 - (float)TreatEase.InCubic(ctx.POut));
                    if (q <= 0.01) return;
                    var lw = MathF.Max(1.5f, s * (float)ctx.Num("k", 0.058));
                    var g = s * 0.3f;
                    var arm = s * 0.36f;
                    var leg = s * 0.74f;
                    var xl = ctx.Cx + l0.X[0] - g;
                    var yT = l0.Baseline - s * 0.52f;
                    var xr = ctx.Cx + l1.X[^1] + l1.W[^1] + g;
                    var yB = l1.Baseline + s * 0.52f;

                    void DrawSeg((float X, float Y)[] pts)
                    {
                        double total = 0;
                        for (var i = 1; i < pts.Length; i++)
                            total += Math.Sqrt(Math.Pow(pts[i].X - pts[i - 1].X, 2) + Math.Pow(pts[i].Y - pts[i - 1].Y, 2));
                        var rem = total * q;
                        for (var i = 1; i < pts.Length && rem > 0; i++)
                        {
                            var (x0, y0) = pts[i - 1];
                            var (x1, y1) = pts[i];
                            var len = Math.Sqrt(Math.Pow(x1 - x0, 2) + Math.Pow(y1 - y0, 2));
                            var take = Math.Min(len, rem);
                            rem -= take;
                            if (len <= 0) continue;
                            var f = take / len;
                            ctx.Line((float)x0, (float)y0, (float)(x0 + (x1 - x0) * f), (float)(y0 + (y1 - y0) * f), lc, lw, a);
                        }
                    }

                    void One(double d)
                    {
                        var dx = (float)d;
                        DrawSeg([(xl - dx + arm, yT - dx), (xl - dx, yT - dx), (xl - dx, yT - dx + leg)]);
                        DrawSeg([(xr + dx - arm, yB + dx), (xr + dx, yB + dx), (xr + dx, yB + dx - leg)]);
                    }

                    One(0);
                    if (ctx.Str("v") == "double") One(-lw * 2.2f);
                },
            },
            new TreatDef
            {
                Id = "reflection",
                Name = "倒影",
                Tags = ["emotional", "calm", "editorial"],
                Params = [new TreatParamDef("k", "长度", 0.7), new TreatParamDef("a", "浓度", 0.4), new TreatParamDef("gap", "间隔", 0.06)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["k"] = rng.Range(0.6, 0.78),
                    ["a"] = rng.Range(0.34, 0.46),
                    ["gap"] = rng.Range(0.04, 0.09),
                },
                Draw = ctx =>
                {
                    if (ctx.Pass != TreatPass.Pre || ctx.Alpha < 0.9) return;
                    var col = ctx.Sc.Fg;
                    var k = ctx.Num("k", 0.7);
                    var a = ctx.Num("a", 0.4);
                    var gap = ctx.Size * (float)ctx.Num("gap", 0.06);
                    var yb = (ctx.Bottom - ctx.Top) / 2f;
                    var axisY = ctx.CenterY + yb + gap;
                    ctx.D.Save();
                    ctx.D.ClipRect(new Rect(ctx.Cx - ctx.Size * 24f, axisY - gap * 0.5f,
                        ctx.Size * 48f, gap * 0.5f + ctx.Size * (float)k * 1.05f));
                    ctx.D.Translate(0, axisY);
                    ctx.D.Scale(1f, -1f);
                    ctx.D.Translate(0, -axisY);
                    ctx.CopyBlock(0f, 0f, new TreatCopyOpts
                    {
                        Fill = TreatMath.Hex(col, 0.9),
                        Alpha = (float)a * 0.55f * ctx.Alpha,
                    });
                    ctx.D.Restore();
                },
            },
            new TreatDef
            {
                Id = "inline",
                Name = "嵌线",
                Tags = ["editorial", "pop", "graphic"],
                Params = [new TreatParamDef("a", "细线", 0.02), new TreatParamDef("b", "间距", 0.018), new TreatParamDef("c", "用色", 0)],
                Plan = (rng, sc) => new Dictionary<string, object>
                {
                    ["a"] = rng.Range(0.017, 0.022),
                    ["b"] = rng.Range(0.016, 0.021),
                    ["c"] = rng.Next() < 0.3 ? 1.0 : 0.0,
                },
                Draw = ctx =>
                {
                    if (ctx.Pass != TreatPass.Post || ctx.Alpha < 0.9) return;
                    var col = ctx.Sc.Fg;
                    var line = ctx.Num("c", 0) != 0
                        ? TreatMath.FirstOK(new[] { ctx.Sc.Str("accent"), ctx.Sc.Str("accent2") }, c => TreatMath.Contrast(c, col) >= 2, ctx.Sc.Bg)!
                        : ctx.Sc.Bg;
                    var s = ctx.Size;
                    ctx.CopyBlock(0f, 0f, new TreatCopyOpts
                    {
                        StrokeColor = TreatMath.Hex(line),
                        StrokeW = s * (float)(ctx.Num("a", 0.02) + ctx.Num("b", 0.018)) * 2f,
                        Alpha = ctx.Alpha,
                    });
                    ctx.CopyBlock(0f, 0f, new TreatCopyOpts
                    {
                        StrokeColor = TreatMath.Hex(col),
                        StrokeW = s * (float)ctx.Num("a", 0.02) * 2f,
                        Alpha = ctx.Alpha,
                    });
                },
            },
            new TreatDef
            {
                Id = "sticker",
                Name = "贴纸描边",
                Tags = ["pop", "graphic"],
                Params = [new TreatParamDef("k", "白边", 0.15), new TreatParamDef("rot", "倾角", 3), new TreatParamDef("sh", "投影", 0.04)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["k"] = rng.Range(0.13, 0.17),
                    ["rot"] = rng.Range(2, 4) * (rng.Next() < 0.5 ? 1 : -1),
                    ["sh"] = rng.Range(0.035, 0.05),
                },
                Draw = ctx =>
                {
                    if (ctx.Pass != TreatPass.Pre || ctx.Alpha < 0.9) return;
                    var col = ctx.Sc.Fg;
                    var dk = TreatMath.IsDark(ctx.Sc.Bg);
                    var paper = TreatMath.FirstOK(new[] { ctx.Sc.Str("ink"), ctx.Sc.Str("fg"), ctx.Sc.Str("sub") },
                        c => TreatMath.Lum(c) > 0.78, "#FFFFFF")!;
                    var key = TreatMath.Contrast(col, paper) < 2.5
                        ? TreatMath.FirstOK(new[] { ctx.Sc.Str("accent"), ctx.Sc.Str("accent2"), ctx.Sc.Str("ghostB"), ctx.Sc.Str("ghostA") },
                            c => TreatMath.Contrast(c, paper) >= 2.2 && TreatMath.Contrast(c, col) >= 1.8, "#111111")
                        : null;
                    var s = ctx.Size;
                    var k = ctx.Num("k", 0.15);
                    var sh = ctx.Num("sh", 0.04);
                    ctx.CopyBlock(s * 0.015f, s * (float)sh, new TreatCopyOpts
                    {
                        StrokeColor = TreatMath.Hex("#000000"),
                        StrokeW = s * (float)(k + 0.016),
                        Alpha = (float)(dk ? 0.45 : 0.25) * ctx.Alpha,
                    });
                    ctx.CopyBlock(0f, 0f, new TreatCopyOpts
                    {
                        StrokeColor = TreatMath.Hex(TreatMath.Mix(paper, "#000000", 0.16)),
                        StrokeW = s * (float)(k + 0.016),
                        Alpha = ctx.Alpha,
                    });
                    ctx.CopyBlock(0f, 0f, new TreatCopyOpts
                    {
                        StrokeColor = TreatMath.Hex(paper),
                        StrokeW = s * (float)k,
                        Alpha = ctx.Alpha,
                    });
                    if (key != null)
                        ctx.CopyBlock(0f, 0f, new TreatCopyOpts
                        {
                            StrokeColor = TreatMath.Hex(key),
                            StrokeW = s * 0.07f,
                            Alpha = ctx.Alpha,
                        });
                },
            },
            new TreatDef
            {
                Id = "gradientSweep",
                Name = "光泽扫过",
                Tags = ["pop", "emotional"],
                Params = [new TreatParamDef("v", "模式", 0), new TreatParamDef("per", "周期", 1.9)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["v"] = rng.Pick(["glint", "glint", "tide"]),
                    ["per"] = rng.Range(1.5, 2.3),
                    ["ph"] = rng.Range(0, 1),
                },
                Draw = ctx =>
                {
                    if (ctx.Pass != TreatPass.Plan || ctx.Alpha < 0.9) return;
                    var col = ctx.Sc.Fg;
                    var t = ctx.Ltb / ctx.Num("per", 1.9) + ctx.Num("ph", 0);
                    if (ctx.Str("v") == "tide")
                    {
                        var c2 = TreatMath.AccentFor(ctx.Sc, col, 1.5);
                        var u = 0.52 + 0.3 * Math.Sin(t * (Math.PI * 2));
                        var line = TreatMath.Mix(c2, "#FFFFFF", 0.45);
                        ctx.Gradient = new[]
                        {
                            (0f, TreatMath.Hex(col)),
                            ((float)(u - 0.02), TreatMath.Hex(col)),
                            ((float)(u - 0.02), TreatMath.Hex(line)),
                            ((float)(u + 0.01), TreatMath.Hex(line)),
                            ((float)(u + 0.01), TreatMath.Hex(c2)),
                            (1f, TreatMath.Hex(c2)),
                        };
                        return;
                    }

                    var hl = TreatMath.Lum(col) > 0.6
                        ? TreatMath.FirstOK(
                            new[] { ctx.Sc.Str("accent"), ctx.Sc.Str("accent2"), ctx.Sc.Str("ghostA"), ctx.Sc.Str("ghostB") },
                            c => TreatMath.Lum(c) > 0.3 && TreatMath.Contrast(c, col) >= 1.3,
                            TreatMath.Mix(col, ctx.Sc.Bg, 0.45))!
                        : TreatMath.Mix(col, "#FFFFFF", 0.72);
                    var u2 = (t % 1) * 1.7 - 0.35;
                    const double w = 0.13;
                    double C(double v) => TreatEase.Clamp(v);
                    ctx.Gradient = new[]
                    {
                        (0f, TreatMath.Hex(col)),
                        ((float)C(u2 - w), TreatMath.Hex(col)),
                        ((float)C(u2), TreatMath.Hex(hl)),
                        ((float)C(u2 + w * 0.35), TreatMath.Hex(hl)),
                        ((float)C(u2 + w * 1.2), TreatMath.Hex(col)),
                        (1f, TreatMath.Hex(col)),
                    };
                },
            },
            new TreatDef
            {
                Id = "kerningWide",
                Name = "加宽字距",
                Tags = ["editorial", "calm", "emotional"],
                Params = [new TreatParamDef("t", "字距", 0.42)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["t"] = rng.Range(0.32, 0.55),
                    ["grow"] = rng.Range(1.04, 1.14),
                },
                Draw = ctx =>
                {
                    if (ctx.Pass != TreatPass.Plan) return;
                    var n = ctx.Lines.Sum(l => l.Tokens.Count(t => t.Trim().Length > 0));
                    if (n < 2 || ctx.Alpha < 0.9) return;
                    ctx.TrackEm = (float)ctx.Num("t", 0.42);
                },
            },
            new TreatDef
            {
                Id = "monoGrid",
                Name = "稿纸格",
                Tags = ["editorial", "calm", "emotional"],
                Params = [new TreatParamDef("pitch", "格距", 1.22), new TreatParamDef("c", "用色", 1)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["pitch"] = rng.Range(1.18, 1.26),
                    ["c"] = (double)(rng.Next() < 0.65 ? 1 : 0),
                },
                Draw = ctx =>
                {
                    if (ctx.Pass == TreatPass.Plan)
                    {
                        if (ctx.Alpha < 0.9) return;
                        var pitchP = ctx.Num("pitch", 1.22);
                        ctx.GlyphFns.Add((_, li, t, _, _) =>
                        {
                            var line = ctx.Lines[li];
                            var cn = line.Tokens.Length;
                            var tgt = (t - (cn - 1) / 2.0) * pitchP * ctx.Size;
                            return new TreatGlyphXf { Dx = (float)(tgt - line.X[t]), Scale = 0.86f };
                        });
                        return;
                    }

                    if (ctx.Pass != TreatPass.Pre || ctx.Alpha < 0.9) return;
                    var col = ctx.Sc.Fg;
                    var lc = ctx.Num("c", 0) != 0
                        ? TreatMath.FirstOK(new[] { ctx.Sc.Str("accent"), ctx.Sc.Str("accent2") },
                            c => TreatMath.Contrast(c, ctx.Sc.Bg) >= 1.5, ctx.Sc.Sub)!
                        : ctx.Sc.Sub;
                    var lcC = TreatMath.Hex(lc);
                    var tint = TreatMath.Hex(TreatMath.Mix(ctx.Sc.Bg, lc, TreatMath.IsDark(ctx.Sc.Bg) ? 0.1 : 0.08));
                    var pitch = (float)ctx.Num("pitch", 1.22);
                    var q = (float)TreatEase.OutCubic(ctx.InP(0, 0.45)) * (1 - (float)TreatEase.InCubic(ctx.POut));
                    if (q <= 0.01) return;
                    var a = ctx.Alpha * q;
                    var cell = pitch * ctx.Size;
                    var lw = MathF.Max(1f, ctx.Size * 0.013f);
                    var gut = cell * 0.24f;
                    foreach (var line in ctx.Lines)
                    {
                        var n = line.Tokens.Length;
                        if (n == 0) continue;
                        var c0 = (0 - (n - 1) / 2.0) * pitch * ctx.Size - cell / 2;
                        var len = (float)(n * cell * q);
                        var cross = ctx.GlyphCenterY(line);
                        var v0 = cross - cell / 2;
                        var v1 = cross + cell / 2;
                        ctx.FillRect(ctx.Cx + (float)c0, v0, len, v1 - v0, tint, a * 0.9f);
                        ctx.Line(ctx.Cx + (float)c0, v0, ctx.Cx + (float)c0 + len, v0, lcC, lw, a * 0.85f);
                        ctx.Line(ctx.Cx + (float)c0, v1, ctx.Cx + (float)c0 + len, v1, lcC, lw, a * 0.85f);
                        ctx.Line(ctx.Cx + (float)(c0 - cell * 0.1), v0 - gut, ctx.Cx + (float)(c0 + len + cell * 0.1), v0 - gut, lcC, lw, a * 0.55f);
                        for (var k = 0; k <= n; k++)
                        {
                            var pq = c0 + k * cell;
                            if (pq - c0 > len + 0.5) break;
                            ctx.Line(ctx.Cx + (float)pq, v0, ctx.Cx + (float)pq, v1, lcC, lw, a * 0.85f);
                        }
                    }
                },
            },
            new TreatDef
            {
                Id = "outlineOffset",
                Name = "版错位描边",
                Tags = ["pop", "graphic", "editorial"],
                Params = [new TreatParamDef("d", "错位", 0.068), new TreatParamDef("k", "描边", 0.026)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["d"] = rng.Range(0.055, 0.08),
                    ["dirX"] = rng.Pick([1.0, 1.0, -1.0, 1.0, 0.4]),
                    ["dirY"] = rng.Pick([1.0, 1.0, 1.0, 0.35, 1.0]),
                    ["k"] = rng.Range(0.022, 0.03),
                },
                Draw = ctx =>
                {
                    var col = ctx.Sc.Fg;
                    if (ctx.Pass == TreatPass.Plan)
                    {
                        if (ctx.Alpha < 0.9) return;
                        var fc = TreatMath.AccentFor(ctx.Sc, col, 1.4);
                        ctx.P["_fc"] = fc;
                        ctx.HideFill = true;
                        ctx.StrokeColor = TreatMath.Hex(col);
                        ctx.StrokeWEm = (float)Math.Max(1.4, ctx.Size * ctx.Num("k", 0.026)) / ctx.Size;
                        return;
                    }

                    if (ctx.Pass != TreatPass.Pre || ctx.Alpha < 0.9) return;
                    var d = ctx.Size * (float)ctx.Num("d", 0.068);
                    ctx.CopyBlock((float)ctx.Num("dirX", 1) * d, (float)ctx.Num("dirY", 1) * d,
                        new TreatCopyOpts
                        {
                            Fill = TreatMath.Hex(ctx.P.TryGetValue("_fc", out var fcOut) ? fcOut as string : ctx.Sc.Accent),
                            Alpha = ctx.Alpha,
                        });
                },
            },
            new TreatDef
            {
                Id = "toneShadow",
                Name = "网点影",
                Tags = ["pop", "graphic", "editorial"],
                Params = [new TreatParamDef("v", "图案", 0), new TreatParamDef("d", "偏移", 0.105)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["v"] = rng.Pick(["dots", "dots", "hatch", "stripes"]),
                    ["d"] = rng.Range(0.09, 0.12),
                    ["dirX"] = rng.Pick([1.0, 1.0, -1.0, 1.0]),
                    ["dirY"] = rng.Pick([1.0, 1.0, 1.0, 0.6]),
                },
                Draw = ctx =>
                {
                    if (ctx.Pass != TreatPass.Pre || ctx.Alpha < 0.9) return;
                    var col = ctx.Sc.Fg;
                    var tc = TreatMath.FirstOK(new[] { ctx.Sc.Str("accent"), ctx.Sc.Str("accent2"), ctx.Sc.Str("fg"), ctx.Sc.Str("ink") },
                        c => TreatMath.Contrast(c, ctx.Sc.Bg) >= 2.2 && TreatMath.Contrast(c, col) >= 1.3,
                        TreatMath.MarkCol(ctx.Sc, col))!;
                    var d = ctx.Size * (float)ctx.Num("d", 0.105);
                    ctx.CopyBlock((float)ctx.Num("dirX", 1) * d, (float)ctx.Num("dirY", 1) * d,
                        new TreatCopyOpts { Fill = TreatMath.Hex(tc), Alpha = 0.85f * ctx.Alpha });
                },
            },
            new TreatDef
            {
                Id = "fadeChars",
                Name = "余韵",
                Tags = ["emotional", "calm"],
                Params = [new TreatParamDef("v", "方向", 0), new TreatParamDef("lo", "最淡", 0.35)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["v"] = rng.Pick(["tail", "tail", "both", "head"]),
                    ["lo"] = rng.Range(0.3, 0.4),
                },
                Draw = ctx =>
                {
                    if (ctx.Pass != TreatPass.Plan || ctx.Alpha < 0.9) return;
                    var v = ctx.Str("v");
                    var lo = ctx.Num("lo", 0.35);
                    ctx.GlyphFns.Add((_, _, t, cn, _) =>
                    {
                        if (cn < 3) return null;
                        var u = t / (double)(cn - 1);
                        var f = v == "both" ? Math.Pow(Math.Abs(u - 0.5) * 2, 1.4)
                            : v == "head" ? TreatEase.InQuad(1 - u)
                            : TreatEase.InQuad(u);
                        return new TreatGlyphXf { Alpha = (float)(1 - (1 - lo) * f) };
                    });
                },
            },
            new TreatDef
            {
                Id = "cutShift",
                Name = "切断错位",
                Tags = ["graphic", "glitch", "pop"],
                Params = [new TreatParamDef("at", "切位", 0), new TreatParamDef("d", "错位", 0.135), new TreatParamDef("line", "切线", 1)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["at"] = rng.Range(-0.08, 0.06),
                    ["d"] = rng.Range(0.11, 0.16) * (rng.Next() < 0.5 ? 1 : -1),
                    ["line"] = rng.Next() < 0.6 ? 1.0 : 0.0,
                },
                Draw = ctx =>
                {
                    if (ctx.Pass == TreatPass.Plan)
                    {
                        if (ctx.Alpha < 0.9) return;
                        ctx.ClipY = (-0.75f, (float)ctx.Num("at", 0));
                        return;
                    }

                    if (ctx.Pass != TreatPass.Pre || ctx.Alpha < 0.9) return;
                    var at = ctx.Num("at", 0);
                    var dEm = ctx.Num("d", 0.135);
                    var q = TreatEase.OutBack(ctx.InP(ctx.InDur * 0.55, 0.3), 2.2) * (1 - TreatEase.InCubic(ctx.POut));
                    var d = ctx.Size * (float)dEm * (float)q;
                    ctx.CopyBlock(0f, d, new TreatCopyOpts
                    {
                        Fill = TreatMath.Hex(ctx.Sc.Fg),
                        ClipY = ((float)at, 0.75f),
                        Alpha = ctx.Alpha,
                    });
                    if (ctx.Num("line", 0) != 0 && q > 0.02)
                    {
                        var lc = TreatMath.Hex(TreatMath.AccentFor(ctx.Sc, ctx.Sc.Fg, 1.6));
                        var lw = MathF.Max(1.2f, ctx.Size * 0.012f);
                        var ex = ctx.Size * 0.35f * (float)q;
                        var alpha = ctx.Alpha * Math.Min(1f, (float)q);
                        foreach (var line in ctx.Lines)
                        {
                            var cp = ctx.GlyphCenterY(line) + (float)at * ctx.Size;
                            ctx.Line(ctx.Cx + line.X[0] - ex, cp, ctx.Cx + line.X[^1] + line.W[^1] + ex + MathF.Abs(d), cp,
                                lc, lw, alpha);
                        }
                    }
                },
            },
            new TreatDef
            {
                Id = "focusPull",
                Name = "焦点推移",
                Tags = ["emotional", "calm", "editorial"],
                Params = [new TreatParamDef("b", "虚化", 0.042), new TreatParamDef("rev", "反向", 0)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["b"] = rng.Range(0.035, 0.05),
                    ["rev"] = rng.Next() < 0.3 ? 1.0 : 0.0,
                },
                Draw = ctx =>
                {
                    if (ctx.Pass != TreatPass.Plan || ctx.Alpha < 0.9) return;
                    var n = ctx.Lines.Sum(l => l.Tokens.Length);
                    if (n < 3) return;
                    var f0 = TreatEase.Clamp((ctx.Lt - ctx.InDur * 0.4) / Math.Max(0.4, ctx.Dur - ctx.OutDur - ctx.InDur * 0.4));
                    var f = TreatEase.Lerp(-0.15, 1.15, ctx.Num("rev", 0) != 0 ? 1 - f0 : f0);
                    ctx.GlyphFns.Add((gi, _, _, _, _) =>
                    {
                        var d = TreatEase.Clamp(Math.Abs(gi / (double)(n - 1) - f) * 2.4 - 0.2);
                        if (d <= 0.02) return null;
                        return new TreatGlyphXf { Alpha = (float)(1 - 0.55 * d) };
                    });
                },
            },
            new TreatDef
            {
                Id = "spotChar",
                Name = "单字标记",
                Tags = ["pop", "graphic", "editorial", "emotional"],
                Params = [new TreatParamDef("v", "形状", 0), new TreatParamDef("k", "缩放", 1.1), new TreatParamDef("r", "选点", 0.5)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["v"] = rng.Pick(["disc", "disc", "square", "diamond"]),
                    ["k"] = rng.Range(1.06, 1.14),
                    ["r"] = rng.Range(0, 1),
                },
                Draw = ctx =>
                {
                    if (ctx.Pass == TreatPass.Plan)
                    {
                        if (ctx.Alpha < 0.9) return;
                        var pool = new List<(int Gi, TreatLine Line, int T, string Token)>();
                        foreach (var line in ctx.Lines)
                            for (var t = 0; t < line.Tokens.Length; t++)
                            {
                                var token = line.Tokens[t];
                                if (token.Trim().Length == 0 || TreatMath.IsPunct(token[0])) continue;
                                pool.Add((line.GlobalStart + t, line, t, token));
                            }

                        if (pool.Count == 0) return;
                        var r = ctx.Num("r", 0.5);
                        var kan = pool.Where(p => TreatMath.IsKanji(p.Token[0])).ToList();
                        var kata = pool.Where(p => TreatMath.IsKata(p.Token[0])).ToList();
                        var pick = kan.Count > 0 ? kan : kata.Count > 0 ? kata : pool;
                        var chosen = pick[(int)Math.Floor(r * pick.Count) % pick.Count];
                        ctx.P["_tIdx"] = (double)chosen.Gi;
                        ctx.P["_tLine"] = (double)ctx.Lines.Index().First(p => ReferenceEquals(p.Item, chosen.Line)).Index;
                        var col = ctx.Sc.Fg;
                        if (TreatMath.Contrast(col, ctx.Sc.Bg) < 1.5) return;
                        var pc = TreatMath.FirstOK(new[] { ctx.Sc.Str("accent"), ctx.Sc.Str("accent2"), ctx.Sc.Str("ink"), ctx.Sc.Str("fg") },
                            c => TreatMath.Contrast(c, ctx.Sc.Bg) >= 2 && TreatMath.Contrast(c, col) >= 1.4,
                            TreatMath.AccentFor(ctx.Sc, col, 1.6))!;
                        var tc = TreatMath.TextOn(pc, col, ctx.Sc);
                        ctx.P["_pc"] = pc;
                        ctx.P["_tc"] = tc;
                        var k = ctx.Num("k", 1.1);
                        ctx.GlyphFns.Add((gi, _, _, _, _) =>
                        {
                            if (gi != chosen.Gi) return null;
                            var q = QOfSpot(ctx);
                            return q > 0.5 && !string.Equals(tc, col, StringComparison.OrdinalIgnoreCase)
                                ? new TreatGlyphXf { Scale = (float)k, Color = TreatMath.Hex(tc) }
                                : new TreatGlyphXf { Scale = (float)k };
                        });
                        return;
                    }

                    if (ctx.Pass != TreatPass.Pre || ctx.Alpha < 0.9) return;
                    if (!ctx.P.TryGetValue("_tIdx", out var tIdxO)) return;
                    var tIdx = (int)(double)tIdxO;
                    var q2 = (float)QOfSpot(ctx);
                    if (q2 <= 0.01) return;
                    var pc2 = TreatMath.Hex(ctx.P.TryGetValue("_pc", out var pco) ? pco as string : ctx.Sc.Accent);
                    var a = ctx.Alpha;
                    ctx.ForGlyphs((gi, line, t, x, _, gw) =>
                    {
                        if (gi != tIdx) return;
                        var cy = ctx.GlyphCenterY(line);
                        var v = ctx.Str("v");
                        if (v == "disc")
                        {
                            ctx.Circle(x, cy, ctx.Size * 0.64f * q2, pc2, null, 0, a);
                            return;
                        }

                        ctx.D.Save();
                        ctx.D.Translate(x, cy);
                        ctx.D.RotateRadians((float)((v == "diamond" ? 45 : -6) * Math.PI / 180 + (1 - q2) * 40 * Math.PI / 180));
                        var h = ctx.Size * (v == "diamond" ? 0.68f : 0.6f) * q2;
                        ctx.FillRect(-h, -h, h * 2, h * 2, pc2, a);
                        ctx.D.Restore();
                    });
                },
            },
            new TreatDef
            {
                Id = "ransom",
                Name = "剪贴字",
                Tags = ["pop", "glitch", "graphic"],
                Params = [new TreatParamDef("s", "种子", 0)],
                Plan = (rng, _) => new Dictionary<string, object> { ["s"] = (double)rng.Pick([1, 2, 3, 4, 5]) * 137 + (int)(rng.Next() * 1000) },
                Draw = ctx =>
                {
                    var col = ctx.Sc.Fg;
                    if (ctx.Pass == TreatPass.Plan)
                    {
                        if (ctx.Alpha < 0.9 || TreatMath.Contrast(col, ctx.Sc.Bg) < 1.5) return;
                        var s0 = unchecked((int)CameraHash.H((int)ctx.Num("s", 0), Math.Abs(ctx.Seed), 53));
                        var plates = PaletteOf(ctx, col);
                        var n = ctx.Lines.Sum(l => l.Tokens.Length);
                        for (var k = 0; k < n; k++)
                        {
                            var pc = plates[(int)(CameraHash.H(s0, k, 1) % (uint)plates.Count)];
                            var alt = TreatMath.Best(
                                new[] { ctx.Sc.Str("bg"), col, ctx.Sc.Str("fg"), ctx.Sc.Str("ink"), "#111111", "#FFFFFF" }
                                    .Where(c => c != pc), pc);
                            ctx.P[$"_pc{k}"] = pc;
                            ctx.P[$"_tc{k}"] = TreatMath.Contrast(col, pc) >= 3 && CameraHash.R(s0, k, 2) < 0.5 ? col : alt;
                            ctx.P[$"_rot{k}"] = CameraHash.Rs(s0, k, 3) * 8;
                            ctx.P[$"_s{k}"] = CameraHash.Rr(0.84, 1.02, s0, k, 4);
                            ctx.P[$"_dy{k}"] = CameraHash.Rs(s0, k, 5) * 0.05;
                            for (var q = 0; q < 4; q++) ctx.P[$"_pad{k}_{q}"] = CameraHash.Rr(0.06, 0.16, s0, k, 6 + q);
                            for (var q = 0; q < 4; q++) ctx.P[$"_jj{k}_{q}"] = CameraHash.Rs(s0, k, 10 + q) * 0.06;
                        }

                        ctx.GlyphFns.Add((gi, line, _, _, _) =>
                        {
                            if (gi >= n) return null;
                            var qq = QOfRansom(ctx, gi);
                            return new TreatGlyphXf
                            {
                                RotDeg = (float)(double)ctx.P[$"_rot{gi}"],
                                Scale = (float)(double)ctx.P[$"_s{gi}"],
                                Dy = (float)(double)ctx.P[$"_dy{gi}"] * ctx.Size,
                                Color = qq > 0.5 ? TreatMath.Hex(ctx.P[$"_tc{gi}"] as string) : null,
                            };
                        });
                        return;
                    }

                    if (ctx.Pass != TreatPass.Pre || ctx.Alpha < 0.9) return;
                    var n2 = ctx.Lines.Sum(l => l.Tokens.Length);
                    ctx.ForGlyphs((gi, line, t, x, _, gw) =>
                    {
                        if (gi >= n2 || !ctx.P.TryGetValue($"_pc{gi}", out var pco)) return;
                        var qq = (float)QOfRansom(ctx, gi);
                        if (qq <= 0.01) return;
                        var size = ctx.Size;
                        var w = Math.Max(gw, size * 0.55f) / 2;
                        var h = size / 2;
                        var pc = TreatMath.Hex(pco as string);
                        double Pad(int i) => ctx.P.TryGetValue($"_pad{gi}_{i}", out var v) ? (double)v : 0.1;
                        double Jj(int i) => ctx.P.TryGetValue($"_jj{gi}_{i}", out var v) ? (double)v : 0;
                        using var path = new Render.Draw.Path2D(ctx.D.Factory);
                        path.MoveTo(x - w - (float)Pad(0) * size, ctx.GlyphCenterY(line) - h - (float)Pad(1) * size + (float)Jj(0) * size)
                            .LineTo(x + w + (float)Pad(2) * size, ctx.GlyphCenterY(line) - h - (float)Pad(1) * size + (float)Jj(1) * size)
                            .LineTo(x + w + (float)Pad(2) * size + (float)Jj(2) * size, ctx.GlyphCenterY(line) + h + (float)Pad(3) * size)
                            .LineTo(x - w - (float)Pad(0) * size + (float)Jj(3) * size, ctx.GlyphCenterY(line) + h + (float)Pad(3) * size)
                            .Close();
                        ctx.D.FillPath(path, new Paint2D { Brush = new SolidBrush2D(new Color4(pc.R, pc.G, pc.B, pc.A * ctx.Alpha * qq)) });
                    });
                },
            },
            new TreatDef
            {
                Id = "hrInkBleed",
                Name = "渗墨垂流",
                Tags = ["horror", "emotional"],
                Params = [new TreatParamDef("drips", "垂流率", 0.35), new TreatParamDef("red", "血红", 0)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["drips"] = rng.Range(0.25, 0.45),
                    ["red"] = rng.Next() < 0.4 ? 1.0 : 0.0,
                },
                Draw = ctx =>
                {
                    if (ctx.Pass == TreatPass.Plan)
                    {
                        if (ctx.Alpha < 0.9) return;
                        var col = ctx.Sc.Fg;
                        var halo = ctx.Num("red", 0) != 0
                            ? ctx.Sc.Accent
                            : TreatMath.IsDark(ctx.Sc.Bg) ? TreatMath.Mix(col, ctx.Sc.Bg, 0.5) : TreatMath.Mix(col, ctx.Sc.Bg, 0.3);
                        ctx.GlowColor = TreatMath.Hex(halo, 0.8);
                        ctx.GlowSize = ctx.Size * 0.07f;
                        return;
                    }

                    if (ctx.Pass != TreatPass.Post || ctx.Alpha < 0.05) return;
                    var dc = TreatMath.Hex(ctx.Num("red", 0) != 0 ? ctx.Sc.Accent : ctx.Sc.Fg);
                    var rate = ctx.Num("drips", 0.35);
                    var size = ctx.Size;
                    var cs = ctx.Seed;
                    var grow = TreatEase.Clamp((ctx.Lt - ctx.InDur * 1.1) / Math.Max(0.8, ctx.Dur * 0.7));
                    if (grow <= 0) return;
                    var a = ctx.Alpha * (1 - (float)ctx.POut);
                    ctx.ForGlyphs((gi, line, t, x, _, gw) =>
                    {
                        var k = unchecked((int)CameraHash.H(cs, gi, (int)Math.Round(x), 141));
                        if (CameraHash.R(k, 1) > rate || TreatMath.IsPunct(line.Tokens[t][0])) return;
                        var dx = x + (float)CameraHash.Rs(k, 2) * gw * 0.25f;
                        var y = ctx.GlyphCenterY(line) + size * 0.36f;
                        var l2 = size * (float)(0.2 + 0.9 * CameraHash.R(k, 3)) * (float)TreatEase.OutCubic(grow);
                        var lw = MathF.Max(1f, size * (float)(0.018 + 0.02 * CameraHash.R(k, 4)));
                        var ex = dx + (float)CameraHash.Rs(k, 5) * size * 0.02f;
                        ctx.Line(dx, y, ex, y + l2, dc, lw, a * 0.9f);
                        ctx.Circle(ex, y + l2, lw * 0.9f, dc, null, 0, a * 0.9f);
                    });
                },
            },
            new TreatDef
            {
                Id = "hrEroded",
                Name = "风化",
                Tags = ["horror", "editorial", "graphic"],
                Params = [new TreatParamDef("dens", "密度", 1), new TreatParamDef("scr", "划痕", 1)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["dens"] = rng.Range(0.7, 1.2),
                    ["scr"] = rng.Next() < 0.7 ? 1.0 : 0.0,
                },
                Draw = ctx =>
                {
                    if (ctx.Pass != TreatPass.Post || ctx.Alpha < 0.3) return;
                    var cs = ctx.Seed;
                    var bg = TreatMath.Hex(ctx.Sc.Bg);
                    var size = ctx.Size;
                    var dens = ctx.Num("dens", 1);
                    var budget = 420;
                    ctx.ForGlyphs((gi, line, t, x, _, gw) =>
                    {
                        if (budget <= 0) return;
                        var k = unchecked((int)CameraHash.H(cs, gi, 151));
                        var n = (int)Math.Round((16 + CameraHash.R(k, 1) * 14) * dens);
                        var cy = ctx.GlyphCenterY(line);
                        for (var j = 0; j < n && budget-- > 0; j++)
                        {
                            var r = size * (float)(0.01 + 0.045 * Math.Pow(CameraHash.R(k, j, 2), 2));
                            ctx.Circle(x + (float)CameraHash.Rs(k, j, 3) * gw * 0.45f,
                                cy + (float)CameraHash.Rs(k, j, 4) * size * 0.45f, r, bg, null, 0, 1f);
                        }

                        if (ctx.Num("scr", 0) != 0 && CameraHash.R(k, 5) < 0.45)
                        {
                            var ang = CameraHash.R(k, 6) * Math.PI;
                            var l2 = gw * 0.6f;
                            ctx.Line(x - MathF.Cos((float)ang) * l2 / 2, cy - MathF.Sin((float)ang) * l2 / 2,
                                x + MathF.Cos((float)ang) * l2 / 2, cy + MathF.Sin((float)ang) * l2 / 2,
                                bg, MathF.Max(1.5f, size * 0.022f), 1f);
                        }
                    });
                },
            },
            new TreatDef
            {
                Id = "hrRedact",
                Name = "黑涂",
                Tags = ["horror", "editorial", "graphic"],
                Params = [new TreatParamDef("at", "时刻", 0.4), new TreatParamDef("frac", "比例", 0.45), new TreatParamDef("from", "起点", 0)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["at"] = rng.Range(0.25, 0.5),
                    ["frac"] = rng.Range(0.3, 0.6),
                    ["from"] = rng.Range(0, 1),
                },
                Draw = ctx =>
                {
                    if (ctx.Pass != TreatPass.Post || ctx.Alpha < 0.9) return;
                    var t0 = ctx.InDur + (ctx.Dur - ctx.InDur - ctx.OutDur) * ctx.Num("at", 0.4);
                    var k = TreatEase.InOutCubic(TreatEase.Clamp((ctx.Lt - t0) / 0.35));
                    var glyphs = new List<(TreatLine Line, int T, float X, float W)>();
                    foreach (var line in ctx.Lines)
                        for (var t = 0; t < line.Tokens.Length; t++)
                        {
                            if (line.Tokens[t].Trim().Length == 0) continue;
                            glyphs.Add((line, t, ctx.Cx + line.X[t], MathF.Max(line.W[t], ctx.Size * 0.5f)));
                        }

                    if (glyphs.Count == 0) return;
                    var s0 = 0;
                    var s1 = glyphs.Count - 1;
                    if (glyphs.Count > 2)
                    {
                        var mm = Math.Max(1, (int)Math.Round(glyphs.Count * ctx.Num("frac", 0.45)));
                        s0 = Math.Min(glyphs.Count - mm, (int)Math.Floor(ctx.Num("from", 0) * (glyphs.Count - mm + 1)));
                        s1 = s0 + mm - 1;
                    }

                    double x0 = 1e9, x1 = -1e9, y0 = 1e9, y1 = -1e9;
                    for (var j = s0; j <= s1; j++)
                    {
                        var (line, t, x, w) = glyphs[j];
                        var cy = ctx.GlyphCenterY(line);
                        x0 = Math.Min(x0, x - w / 2);
                        x1 = Math.Max(x1, x + w / 2);
                        y0 = Math.Min(y0, cy - ctx.Size * 0.55);
                        y1 = Math.Max(y1, cy + ctx.Size * 0.45);
                    }

                    var pd = ctx.Size * 0.06f;
                    var c = TreatMath.Hex(ctx.Sc.Fg);
                    var w2 = (float)(x1 - x0 + pd * 2);
                    ctx.FillRect((float)(x0 - pd) + w2 * (float)k, (float)(y0 - pd * 0.5),
                        w2 * (1 - (float)k), (float)(y1 - y0 + pd), c, ctx.Alpha);
                    if (k > 0)
                    {
                        var sub = TreatMath.Hex(ctx.Sc.Sub);
                        var lw = MathF.Max(1f, ctx.Size * 0.01f);
                        var a = ctx.Alpha * 0.5f * (float)k;
                        ctx.Line((float)(x0 - pd), (float)(y0 - pd * 0.5), (float)(x1 + pd), (float)(y0 - pd * 0.5), sub, lw, a);
                        ctx.Line((float)(x1 + pd), (float)(y0 - pd * 0.5), (float)(x1 + pd), (float)(y1 + pd * 0.5), sub, lw, a);
                        ctx.Line((float)(x1 + pd), (float)(y1 + pd * 0.5), (float)(x0 - pd), (float)(y1 + pd * 0.5), sub, lw, a);
                        ctx.Line((float)(x0 - pd), (float)(y1 + pd * 0.5), (float)(x0 - pd), (float)(y0 - pd * 0.5), sub, lw, a);
                    }
                },
            },
            new TreatDef
            {
                Id = "hrDoubleExp",
                Name = "双重曝光",
                Tags = ["horror", "emotional", "glitch"],
                Params = [new TreatParamDef("amp", "漂移", 0.23), new TreatParamDef("k", "缩放", 1.05)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["amp"] = rng.Range(0.18, 0.28),
                    ["k"] = rng.Range(1.03, 1.08),
                },
                Draw = ctx =>
                {
                    if (ctx.Pass != TreatPass.Pre || ctx.Alpha < 0.05) return;
                    var cs = ctx.Seed;
                    string? col = null;
                    foreach (var c in new[] { ctx.Sc.Str("sub"), ctx.Sc.Str("ghostA"), ctx.Sc.Str("fg") })
                        if (!string.IsNullOrEmpty(c) && c != ctx.Sc.Fg && TreatMath.Contrast(c, ctx.Sc.Bg) > 1.3)
                        {
                            col = c;
                            break;
                        }

                    col ??= ctx.Sc.Sub;
                    var t = ctx.Ltb;
                    var sz = ctx.Size;
                    var amp = ctx.Num("amp", 0.12);
                    var k = ctx.Num("k", 1.05);
                    var cx = ctx.Cx;
                    var cy = ctx.CenterY;
                    var sx = 1 + (float)k - 1;
                    var sy = sx;
                    ctx.D.Save();
                    ctx.D.Translate(
                        (float)(CameraHash.Noise1(t * 0.6, cs + 3) * sz * amp),
                        (float)(CameraHash.Noise1(t * 0.5, cs + 4) * sz * amp * 0.7));
                    ctx.D.Translate(cx, cy);
                    ctx.D.Scale((float)k, (float)k);
                    ctx.D.Translate(-cx, -cy);
                    ctx.CopyBlock(0f, 0f, new TreatCopyOpts { Fill = TreatMath.Hex(col), Alpha = 0.4f * ctx.Alpha });
                    ctx.D.Restore();
                },
            },
            new TreatDef
            {
                Id = "knWordScale",
                Name = "关键词缩放",
                Tags = ["pop", "graphic", "editorial"],
                Params = [new TreatParamDef("big", "放大", 1.35), new TreatParamDef("small", "缩小", 0.82), new TreatParamDef("pick", "选词", 0)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["big"] = rng.Range(1.28, 1.42),
                    ["small"] = rng.Range(0.78, 0.86),
                    ["pick"] = rng.Pick(["long", "long", "last", "first"]),
                },
                Draw = ctx =>
                {
                    if (ctx.Pass != TreatPass.Plan || ctx.Alpha < 0.9) return;
                    var words = CollectWords(ctx);
                    if (words.Count < 2) return;
                    var big = ctx.Num("big", 1.35);
                    var small = ctx.Num("small", 0.82);
                    var pick = ctx.Str("pick");
                    var cnt = new int[words.Count];
                    var kan = new int[words.Count];
                    for (var j = 0; j < words.Count; j++)
                        foreach (var (_, _, _, _, token) in words[j])
                        {
                            cnt[j]++;
                            if (TreatMath.IsKanji(token[0])) kan[j]++;
                        }

                    var kw = pick == "last" ? words.Count - 1
                        : pick == "first" ? 0
                        : ArgMax(cnt, kan);
                    double tb = 0, ts = 0;
                    foreach (var w in words)
                        foreach (var (_, _, _, gw, _) in w)
                        {
                            if (w == words[kw]) tb += gw;
                            else ts += gw;
                        }

                    var norm = Math.Min(1, (tb + ts) / Math.Max(1e-6, tb * big + ts * small));
                    double F(int j) => (j == kw ? big : small) * norm;
                    var gi = 0;
                    foreach (var w in words)
                    {
                        var fj = F(words.IndexOf(w));
                        double pos = 0;
                        var old0 = w[0].X;
                        var last = w[^1];
                        var old1 = last.X + last.Gw;
                        var len = w.Sum(g => g.Gw);
                        var start = (old0 + old1) / 2 - len / 2;
                        foreach (var (line, t, _, gw, _) in w)
                        {
                            var na = start + pos + gw / 2;
                            var oa = line.X[t] + gw / 2;
                            var idx = line.GlobalStart + t;
                            var dx = na - oa;
                            var jj = gi;
                            ctx.GlyphFns.Add((i, _, _, _, _) => i != idx
                                ? null
                                : new TreatGlyphXf { Dx = (float)dx, Dy = (1f - (float)fj) * 0.36f * ctx.Size, Scale = (float)fj });
                            pos += gw;
                            gi++;
                        }
                    }
                },
            },
            new TreatDef
            {
                Id = "knWordPlate",
                Name = "逐词反白",
                Tags = ["pop", "graphic", "glitch"],
                Params = [new TreatParamDef("first", "起始", 0), new TreatParamDef("pad", "内边距", 0.11), new TreatParamDef("tilt", "倾角", 0)],
                Plan = (rng, _) => new Dictionary<string, object>
                {
                    ["first"] = rng.Next() < 0.5 ? 1.0 : 0.0,
                    ["pad"] = rng.Range(0.08, 0.14),
                    ["tilt"] = rng.Next() < 0.4 ? rng.Range(1.5, 3) : 0.0,
                },
                Draw = ctx =>
                {
                    var col = ctx.Sc.Fg;
                    if (ctx.Pass == TreatPass.Plan)
                    {
                        if (ctx.Alpha < 0.9) return;
                        var plate = TreatMath.Contrast(ctx.Sc.Ink, ctx.Sc.Bg) >= 2 ? ctx.Sc.Ink : ctx.Sc.Fg;
                        string? tc = null;
                        var bv = 0.0;
                        foreach (var c in new[] { ctx.Sc.Str("bg"), ctx.Sc.Str("fg"), ctx.Sc.Str("ink"), ctx.Sc.Str("accent") })
                        {
                            if (c == null || c == plate) continue;
                            var k = TreatMath.Contrast(c, plate);
                            if (k > bv)
                            {
                                bv = k;
                                tc = c;
                            }
                        }

                        if (bv < 2.6) tc = TreatMath.Lum(plate) > 0.5 ? "#111111" : "#FFFFFF";
                        ctx.P["_plate"] = plate;
                        ctx.P["_tc"] = tc;
                        var first = ctx.Num("first", 0) != 0;
                        bool On(int j) => (j % 2 == 0) == first;
                        foreach (var (j, w) in CollectWords(ctx).Index())
                        {
                            if (!On(j)) continue;
                            var x0 = w.Min(g => g.X);
                            var x1 = w.Max(g => g.X + g.Gw);
                            var lines = w.Select(g => g.Line).Distinct().ToList();
                            foreach (var line in lines)
                            {
                                var cy = ctx.GlyphCenterY(line);
                                ctx.P[$"_box{j}_{ctx.Lines.Index().First(p => ReferenceEquals(p.Item, line)).Index}"] = $"{x0},{x1},{cy}";
                            }

                            var tcLocal = tc;
                            foreach (var (line, t, _, _, _) in w)
                            {
                                var idx = line.GlobalStart + t;
                                ctx.GlyphFns.Add((i, _, _, _, _) => i != idx
                                    ? null
                                    : new TreatGlyphXf { Color = TreatMath.Hex(tcLocal) });
                            }
                        }

                        return;
                    }

                    if (ctx.Pass != TreatPass.Pre || ctx.Alpha < 0.9) return;
                    var plateC = TreatMath.Hex(ctx.P.TryGetValue("_plate", out var po) ? po as string : ctx.Sc.Ink);
                    var pad = ctx.Size * (float)ctx.Num("pad", 0.1);
                    var tilt = ctx.Num("tilt", 0);
                    var qi = 0;
                    foreach (var key in ctx.P.Keys.Where(k => k.StartsWith("_box")).OrderBy(k => k))
                    {
                        var parts = (ctx.P[key] as string)!.Split(',');
                        var x0 = float.Parse(parts[0]);
                        var x1 = float.Parse(parts[1]);
                        var cy = float.Parse(parts[2]);
                        var t = tilt != 0 ? (qi++ % 2 != 0 ? tilt : -tilt) : 0;
                        var mx = (x0 + x1) / 2;
                        ctx.D.Save();
                        ctx.D.Translate(mx, cy);
                        if (t != 0) ctx.D.RotateRadians((float)(t * Math.PI / 180));
                        ctx.FillRect(-(x1 - x0) / 2 - pad, -ctx.Size * 0.55f, x1 - x0 + pad * 2, ctx.Size * 1.1f,
                            plateC, ctx.Alpha);
                        ctx.D.Restore();
                    }
                },
            },
        };

        return list.ToDictionary(d => d.Id, d => d);
    }

    private static double QOf(TreatContext ctx, int gi) =>
        TreatEase.OutBack(TreatEase.Clamp((ctx.Lt - gi * 0.04) / 0.26), 1.6)
        * (1 - TreatEase.InCubic(TreatEase.Clamp(ctx.POut * 1.4 - gi * 0.03)));

    private static double QOfSpot(TreatContext ctx) =>
        TreatEase.OutBack(TreatEase.Clamp((ctx.Lt - ctx.InDur * 0.5) / 0.3), 1.8)
        * (1 - TreatEase.InCubic(TreatEase.Clamp(ctx.POut * 1.3)));

    private static double QOfRansom(TreatContext ctx, int gi) =>
        TreatEase.OutBack(TreatEase.Clamp((ctx.Lt - gi * 0.03) / 0.22), 1.5)
        * (1 - TreatEase.InCubic(TreatEase.Clamp(ctx.POut * 1.4 - gi * 0.03)));

    private static TreatLine? FindLine(TreatContext ctx, int gi)
    {
        foreach (var line in ctx.Lines)
            if (gi >= line.GlobalStart && gi < line.GlobalStart + line.Tokens.Length)
                return line;
        return null;
    }

    private static int TotalTokens(TreatContext ctx) => ctx.Lines.Sum(l => l.Tokens.Length);

    private sealed record WordGlyph(TreatLine Line, int T, float X, float Gw, string Token);

    private static List<List<WordGlyph>> CollectWords(TreatContext ctx)
    {
        var words = new List<List<WordGlyph>>();
        var current = new List<WordGlyph>();
        foreach (var line in ctx.Lines)
        {
            for (var t = 0; t < line.Tokens.Length; t++)
            {
                var token = line.Tokens[t];
                if (string.IsNullOrEmpty(token) || token.Trim().Length == 0)
                {
                    if (current.Count > 0)
                    {
                        words.Add(current);
                        current = new List<WordGlyph>();
                    }

                    continue;
                }

                current.Add(new WordGlyph(line, t, ctx.Cx + line.X[t], MathF.Max(line.W[t], ctx.Size * 0.5f), token));
                var boundary = TreatMath.IsPunct(token[0]) || current.Count >= 2;
                if (boundary)
                {
                    words.Add(current);
                    current = new List<WordGlyph>();
                }
            }

            if (current.Count > 0)
            {
                words.Add(current);
                current = new List<WordGlyph>();
            }
        }

        return words;
    }

    private static int ArgMax(int[] cnt, int[] kan)
    {
        var b = 0;
        for (var j = 1; j < cnt.Length; j++)
            if (cnt[j] + kan[j] * 0.5 > cnt[b] + kan[b] * 0.5)
                b = j;
        return b;
    }
}
