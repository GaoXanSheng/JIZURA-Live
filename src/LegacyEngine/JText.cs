
using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.LegacyEngine;

public static class JText
{

        public static bool Typeset;

        public static void SetTypeset(bool on)
    {
        if (Typeset == on) return;
        Typeset = on;
        InvalidateLayoutCache();
    }

    private static bool ChHira(char c) => c >= 'ぁ' && c <= 'ゟ';
    private static bool ChKata(char c) => c is >= '゠' and <= 'ヿ' or >= '\u31F0' and <= '\u31FF';
    private static bool ChKanji(char c) => c is >= '\u3400' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF' or '々' or '〆';
    private static bool ChLatin(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static readonly HashSet<char> JsSpaceSet = BuildJsSpace();

    private static HashSet<char> BuildJsSpace()
    {
        var s = new HashSet<char>(" \t\n\v\f\r\u00a0\u1680\u2028\u2029\u202f\u205f\u3000\ufeff");
        for (var c = '\u2000'; c <= '\u200a'; c++) s.Add(c);
        return s;
    }

    private static bool ChSpace(char c) => JsSpaceSet.Contains(c);

    private static bool HasAny(string ch, Func<char, bool> f)
    {
        foreach (var c in ch)
            if (f(c))
                return true;
        return false;
    }

        private static char Cls(string? ch)
    {
        if (string.IsNullOrEmpty(ch)) return '\0';
        if (HasAny(ch, ChLatin)) return 'L';
        if (HasAny(ch, ChKanji)) return 'K';
        if (HasAny(ch, ChKata)) return 'T';
        if (HasAny(ch, ChHira)) return 'H';
        if (HasAny(ch, ChSpace)) return 'S';
        return 'P';
    }

    private const string Particles = "はがをにでとのへも";

    private static bool IsParticle(List<string> arr, int i)
    {
        var ch = arr[i];
        var prev = i > 0 ? arr[i - 1] : null;
        var next = i + 1 < arr.Count ? arr[i + 1] : null;
        if (prev == null || !Particles.Contains(ch) || prev is "っ" or "ッ" or "ー") return false;
        if (ch == "を") return true;
        var cp = Cls(prev);
        var cn = Cls(next);
        var nextOk = next == null || cn is 'K' or 'T' or 'L' or 'S' or 'P';
        return nextOk && cp is not ('S' or 'P') && !(cp == 'H' && cn == 'H');
    }

        public sealed class TypesetCell
    {
        public double F = 1, Gap, Adv = 1;
    }

        public static List<TypesetCell> TypesetLine(List<string> arr)
    {
        var outL = new List<TypesetCell>(arr.Count);
        for (var i = 0; i < arr.Count; i++) outL.Add(new TypesetCell());
        if (!Typeset) return outL;
        var content = 0;
        foreach (var c in arr)
            if (!HasAny(c, ChSpace))
                content++;
        var first = true;
        for (var i = 0; i < arr.Count; i++)
        {
            var ch = arr[i];
            var c = Cls(ch);
            var o = outL[i];
            if (c == 'S') continue;
            if (c is 'H' or 'T') o.Adv = J.IsSmallKana(ch[0]) ? 0.86 : ch == "ー" ? 0.94 : 0.9;             if (c == 'L') o.F = 1.08;
            if ((c is 'H' or 'T') && content >= 3 && IsParticle(arr, i)) o.F = 0.78;
            if (first && content >= 3 && c is 'K' or 'H' or 'T') o.F = 1.18;             first = false;
            var pc = i > 0 ? Cls(arr[i - 1]) : '\0';
            if (i > 0 && pc != 'S' && (c == 'L') != (pc == 'L') && pc != '\0' && pc != 'P' && c != 'P') o.Gap = 0.2;         }
        return outL;
    }

        public sealed class GlyphCell
    {
        public string Ch = "";
        public int I, Li, Ci, N;
        public double X, Y, W, H;
        public bool R90;
        public double Vx, Vy, Fs;

                public JObject? Json;
    }

        public sealed class TextLayout
    {
        public List<GlyphCell> Cells = new();
        public double W, H;
        public int N;

                public JObject ToJson()
        {
            var cells = new JArray();
            foreach (var g in Cells)
                cells.Add(new JObject
                {
                    ["ch"] = g.Ch, ["i"] = g.I, ["li"] = g.Li, ["ci"] = g.Ci, ["n"] = g.N,
                    ["x"] = g.X, ["y"] = g.Y, ["w"] = g.W, ["h"] = g.H,
                    ["r90"] = g.R90, ["vx"] = g.Vx, ["vy"] = g.Vy, ["fs"] = g.Fs
                });
            return new JObject { ["N"] = N, ["W"] = W, ["H"] = H, ["cells"] = cells };
        }
    }

        public static TextLayout LayoutText(JObject it)
    {
        var text = TextOf(it, "text");
        var size = Num(it, "size");
        var track = Num(it, "track");
        var sx = ZeroTo(Num(it, "sx"), 1);
        var sy = ZeroTo(Num(it, "sy"), 1);
        var lines = text.Split('\n');
        var outL = new TextLayout();
        var vertical = Truthy(it["vertical"]);
        var leadV = ZeroTo(Num(it, "lead"), 1.3) * size;
        var gi = 0;
        if (!vertical)
        {
            var sets = new List<(List<string> Arr, List<TypesetCell> Ts)>();
            foreach (var line in lines)
            {
                var arr = CodePoints(line);
                sets.Add((arr, TypesetLine(arr)));
            }
            var widths = sets.Select(s =>
            {
                double w = 0;
                for (var i = 0; i < s.Arr.Count; i++)
                    w += (Metrics.Adv(Str(it, "font") ?? "", s.Arr[i]) * s.Ts[i].Adv * s.Ts[i].F + s.Ts[i].Gap) * size
                         + (i < s.Arr.Count - 1 ? track * size : 0);
                return w;
            }).ToList();
            var maxW = widths.Prepend(1).Max();
            for (var li = 0; li < sets.Count; li++)
            {
                var (arr, ts) = sets[li];
                var align = Str(it, "align");
                var x = align == "left" ? 0 : align == "right" ? -widths[li] : -widths[li] / 2;
                var y = (li - (lines.Length - 1) / 2.0) * leadV;
                for (var ci = 0; ci < arr.Count; ci++)
                {
                    var t = ts[ci];
                    var a = Metrics.Adv(Str(it, "font") ?? "", arr[ci]) * size * t.Adv * t.F;
                    x += t.Gap * size;
                    outL.Cells.Add(new GlyphCell
                    {
                        Ch = arr[ci],
                        I = gi++,
                        Li = li,
                        Ci = ci,
                        N = arr.Count,
                        X = x + a / 2,
                        Y = y + (1 - t.F) * size * 0.36,
                        W = a,
                        H = size * t.F,
                        R90 = false,
                        Vx = 0,
                        Vy = 0,
                        Fs = t.F
                    });
                    x += a + track * size;
                }
            }
            outL.W = maxW;
            outL.H = lines.Length * leadV - (leadV - size);
        }
        else
        {
            var sets = new List<(List<string> Arr, List<TypesetCell> Ts)>();
            foreach (var line in lines)
            {
                var arr = CodePoints(line);
                sets.Add((arr, TypesetLine(arr)));
            }
            var font = Str(it, "font") ?? "";
            var heights = sets.Select(s =>
            {
                double h = 0;
                for (var i = 0; i < s.Arr.Count; i++)
                    h += VAdv(font, s.Arr[i], size) * s.Ts[i].Adv * s.Ts[i].F + s.Ts[i].Gap * size + track * size;
                return h - track * size;
            }).ToList();
            var maxH = heights.Prepend(1).Max();
            for (var li = 0; li < sets.Count; li++)
            {
                var (arr, ts) = sets[li];
                var align = Str(it, "align");
                var y = align == "left" ? 0 : -heights[li] / 2;
                var x = -(li - (lines.Length - 1) / 2.0) * leadV;
                for (var ci = 0; ci < arr.Count; ci++)
                {
                    var ch = arr[ci];
                    var t = ts[ci];
                    var a = VAdv(font, ch, size) * t.Adv * t.F;
                    y += t.Gap * size;
                    var r90 = J.VertRotate.Contains(ch) || HasAny(ch, ChLatin);
                    double vx = 0, vy = 0;
                    if (J.IsSmallKana(ch[0]))
                    {
                        vx = 0.11 * size;
                        vy = -0.11 * size;
                    }
                    if ("、。，．".Contains(ch))
                    {
                        vx = 0.3 * size;
                        vy = -0.3 * size;
                    }
                    outL.Cells.Add(new GlyphCell
                    {
                        Ch = ch,
                        I = gi++,
                        Li = li,
                        Ci = ci,
                        N = arr.Count,
                        X = x,
                        Y = y + a / 2,
                        W = size * t.F,
                        H = a,
                        R90 = r90,
                        Vx = vx,
                        Vy = vy,
                        Fs = t.F
                    });
                    y += a + track * size;
                }
            }
            outL.W = lines.Length * leadV - (leadV - size);
            outL.H = maxH;
        }
        outL.N = gi;
        return outL;
    }

    private static readonly ConditionalWeakTable<JObject, StrongBox<TextLayout>> LayCache = new();

    private static readonly Dictionary<(string Text, string? Font, long Size, long Track, long Lead,
        string? Align, bool Vert), TextLayout> LayContent = new();
    private static readonly Queue<(string Text, string? Font, long Size, long Track, long Lead,
        string? Align, bool Vert)> LayOrder = new();     private static long _layGen;            private static long _layCacheGen;       private const int LayCap = 2048;

        public static (long Hit, long Miss, long Evict, long Clear) LayStats;

    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

        internal static void InvalidateLayoutCache()
    {
        _layGen++;
        LayContent.Clear();
        LayOrder.Clear();
        LayStats.Clear++;
    }

    internal static TextLayout GetLay(JObject it)
    {
        var box = LayCache.GetOrCreateValue(it);
        if (box.Value != null) return box.Value;
        if (_layGen != _layCacheGen)
        {
            LayContent.Clear();
            LayOrder.Clear();
            LayStats.Clear++;
            _layCacheGen = _layGen;
        }
        var key = (TextOf(it, "text"), Str(it, "font"),
            Bits(Num(it, "size")), Bits(Num(it, "track")), Bits(Num(it, "lead")),
            Str(it, "align"), Truthy(it["vertical"]));
        if (!LayContent.TryGetValue(key, out var lay))
        {
            LayStats.Miss++;
            lay = LayoutText(it);
            if (LayContent.Count >= LayCap)
            {
                LayContent.Remove(LayOrder.Dequeue());                 LayStats.Evict++;
            }
            LayContent[key] = lay;
            LayOrder.Enqueue(key);
        }
        else LayStats.Hit++;
        box.Value = lay;
        return lay;
    }

        internal static void ShareLay(JObject target, TextLayout lay)
    {
        var box = LayCache.GetOrCreateValue(target);
        box.Value ??= lay;
    }

    private static double VAdv(string font, string ch, double size) =>
        HasAny(ch, ChLatin) ? Metrics.Adv(font, ch) * size : size;

        public sealed class GlyphLogEntry
    {
        public string Ch = "";

                public float[] M = new float[6];

                public string Font = "";

                public string? FontKey;

        public double Px;
        public string Color = "";
        public double A;
        public double Stroke;
        public string? StrokeColor;
        public bool Fill;
    }

        public delegate PieceXf? PieceFnEx(int ci, int pj, GlyphPiece pc, double ox, double oy, JObject? g);

        public static JObject CellJson(GlyphCell g) => new()
    {
        ["ch"] = g.Ch,
        ["i"] = g.I,
        ["li"] = g.Li,
        ["ci"] = g.Ci,
        ["n"] = g.N,
        ["x"] = g.X,
        ["y"] = g.Y,
        ["w"] = g.W,
        ["h"] = g.H,
        ["r90"] = g.R90,
        ["vx"] = g.Vx,
        ["vy"] = g.Vy,
        ["fs"] = g.Fs
    };

        public sealed class TextDrawOpts
    {
                public bool InLayer;

                public bool HideText;

                public bool AllowFilter = true;

                public List<GlyphLogEntry>? GlyphLog;

                public JAnim.CharFn? CharFn;

                public JAnim.PieceFn? PieceFn;

                public PieceFnEx? PieceFnEx;

                public bool NoPieces;

        public static readonly TextDrawOpts Default = new();
    }

    private static SKSurface? _layerCv;
    private static readonly object LayerLock = new();

        private static bool TryDrawItemLayered(Env env, JObject it, TextDrawOpts o, out BBox? bb)
    {
        bb = null;
        using var _ly = new Prof.Scope("item", "layered");         var ctx = env.Ctx;
        var lay = GetLay(it);
        var size = Num(it, "size");
        var sx = ZeroTo(Num(it, "sx"), 1);
        var sy = ZeroTo(Num(it, "sy"), 1);
        var charFnB = o.CharFn ?? env.CharFn;
        double x0 = 1e9, y0 = 1e9, x1 = -1e9, y1 = -1e9;
        foreach (var g in lay.Cells)
        {
            var c = charFnB?.Invoke(g.I, CellJson(g), lay.N);
            if (c != null && c.Hide) continue;
            var s = c != null && c.S != null ? Math.Abs(c.S.Value) : 1;
            var gw = g.W * sx * s * AbsOr1(c?.Sx);
            var gh = g.H * sy * s * AbsOr1(c?.Sy);
            var r = Math.Max(gw, gh) * (c is { Rot: not 0 } ? 0.75 : 0.55);
            var gx = g.X * sx + g.Vx * sx + (c?.Dx ?? 0);
            var gy = g.Y * sy + g.Vy * sy + (c?.Dy ?? 0);
            x0 = Math.Min(x0, gx - r);
            x1 = Math.Max(x1, gx + r);
            y0 = Math.Min(y0, gy - r);
            y1 = Math.Max(y1, gy + r);
        }
        if (x0 > x1) return true;         var sh = it["shadow"] as JObject;
        var ex = it["extrude"] as JObject;
        var pad = Math.Max(Num(it, "blur"), 0) * 2.6 + size * 0.12 + Math.Max(Num(it, "stroke"), 0)
                  + (sh != null
                      ? Math.Max(Num(sh, "blur"), 0) * 1.3 + Math.Abs(Num(sh, "dx")) + Math.Abs(Num(sh, "dy"))
                      : 0)
                  + (ex != null ? Math.Abs(Num(ex, "dx")) + Math.Abs(Num(ex, "dy")) : 0);
        x0 -= pad;
        y0 -= pad;
        x1 += pad;
        y1 += pad;
        var T = ctx.TotalMatrix;
        var k = Math.Max(0.05, Math.Sqrt(T.ScaleX * T.ScaleX + T.SkewY * T.SkewY));         var ow = (int)Math.Ceiling((x1 - x0) * k);
        var oh = (int)Math.Ceiling((y1 - y0) * k);
        var clipB = ctx.DeviceClipBounds;
        if (ow < 2 || oh < 2 || (double)ow * oh > clipB.Width * clipB.Height * 1.6)
            return false;         SKSurface surface;
        lock (LayerLock)
        {
            if (_layerCv == null || _layerCv.Canvas.DeviceClipBounds.Width < ow || _layerCv.Canvas.DeviceClipBounds.Height < oh)
            {
                _layerCv?.Dispose();
                _layerCv = SKSurface.Create(new SKImageInfo(Math.Max(ow, 64), Math.Max(oh, 64)));
            }
            surface = _layerCv;
        }
        var L = surface.Canvas;
        L.SetMatrix(SKMatrix.Concat(
            SKMatrix.CreateTranslation((float)(-x0 * k), (float)(-y0 * k)),
            SKMatrix.CreateScale((float)k, (float)k)));
        L.Clear(SKColors.Transparent);
        var inner = (JObject)it.DeepClone();
        inner["x"] = 0;
        inner["y"] = 0;
        inner["rot"] = 0;
        inner["skew"] = 0;
        inner["blur"] = 0;
        inner["shadow"] = null;
        inner["blend"] = null;
        ShareLay(inner, lay);         var bbInner = DrawItem(CloneEnv(env, L, k), inner, new TextDrawOpts { InLayer = true, AllowFilter = o.AllowFilter });
        ctx.Save();
        ctx.Translate((float)Num(it, "x"), (float)Num(it, "y"));
        if (Truthy(it["rot"])) ctx.RotateDegrees((float)Num(it, "rot"));
        if (Truthy(it["skew"])) ctx.Concat(SKMatrix.CreateSkew((float)Math.Tan(Num(it, "skew") * J.DEG), 0));
        using var comp = new SKPaint { BlendMode = env.Blend };
        var blend = Str(it, "blend");
        if (blend != null && BlendMap.TryGetValue(blend, out var bm)) comp.BlendMode = bm;
        SKImageFilter? imgFilter = null;
        var blur = Num(it, "blur");
        var hasBlur = blur > 0.4;
        if (hasBlur) imgFilter = BlurFilter(blur * env.Scale);
        if (sh != null && env.Pass == "main")
        {
            var sCol = CssColor(Str(sh, "color") ?? "rgba(0,0,0,0.6)");
            imgFilter = DropShadowFilter(Num(sh, "dx") * env.Scale, Num(sh, "dy") * env.Scale,
                Math.Max(Num(sh, "blur"), 0) * env.Scale, sCol, hasBlur ? blur * env.Scale : 0);
        }
        comp.ImageFilter = imgFilter;
        using var img = surface.Snapshot();
        ctx.DrawImage(img, new SKRect((float)x0, (float)y0, (float)(x0 + ow / k), (float)(y0 + oh / k)), comp);
        ctx.Restore();
        if (bbInner == null) return true;
        bb = new BBox
        {
            X0 = bbInner.X0 + Num(it, "x"),
            X1 = bbInner.X1 + Num(it, "x"),
            Y0 = bbInner.Y0 + Num(it, "y"),
            Y1 = bbInner.Y1 + Num(it, "y"),
            Cx = Num(it, "x"),
            Cy = Num(it, "y"),
        };
        bb.TakeGlyphsFrom(bbInner);         return true;
    }

    private static Env CloneEnv(Env e, SKCanvas ctx, double scale) => new()
    {
        Ctx = ctx,
        W = e.W,
        H = e.H,
        Sc = e.Sc,
        St = e.St,
        Fx = e.Fx,
        Fps = e.Fps,
        Cut = e.Cut,
        Plan = e.Plan,
        Lt = e.Lt,
        T = e.T,
        Ltb = e.Ltb,
        Step = e.Step,
        Energy = e.Energy,
        Beat = e.Beat,
        BeatHas = e.BeatHas,
        BeatSince = e.BeatSince,
        BeatLen = e.BeatLen,
        BeatIndex = e.BeatIndex,
        Zone = e.Zone,
        Pass = e.Pass,
        PassColor = e.PassColor,
        Scale = scale,
        PIn = e.PIn,
        POut = e.POut,
        It = e.It,
        Ap = e.Ap,
        CharFn = e.CharFn,
        PieceFn = e.PieceFn,
        Layer = e.Layer,
        HideText = e.HideText,
        AllowFilter = e.AllowFilter,
        GlyphLog = e.GlyphLog,
        Blend = e.Blend
    };

    private static readonly Regex CjkRe =
        new("[\\u3040-\\u30ff\\u3400-\\u9fff\\uf900-\\ufaff\\uac00-\\ud7af]", RegexOptions.Compiled);

    private static readonly Regex TimeGate =
        new("\\d{1,2}[:：]\\d{2}", RegexOptions.ECMAScript | RegexOptions.Compiled);

    private static readonly Regex TimeShape =
        new("^[A-Za-z]{0,4}[\\s.#\\-–—―−─━◆◇／·]*[\\d:：.;'\"\\s/\\-–—―−─━◆◇／·]+[A-Za-z]{0,3}$",
            RegexOptions.ECMAScript | RegexOptions.Compiled);

    private static readonly Regex NoShape =
        new("^[A-Za-z#№.\\s\\d/\\-–—―−─━◆◇／·+×x%:,'°]+$", RegexOptions.ECMAScript | RegexOptions.Compiled);

    private static readonly Regex NoToken =
        new("(?<![A-Za-z0-9])No\\s*[.．]?\\s*\\d{1,4}|№\\s*\\d{1,4}",
            RegexOptions.ECMAScript | RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TimeToken =
        new("\\d{1,2}[:：]\\d{2}", RegexOptions.ECMAScript | RegexOptions.Compiled);

    private const string SepChars = "·・:：;；,，.．\\-–—―−─━／/|\\\\~～*＊#№";

    private static readonly Regex SepRun =
        new("[" + SepChars + "\\s]{2,}", RegexOptions.ECMAScript | RegexOptions.Compiled);

    private static readonly Regex SepEdge =
        new("^[" + SepChars + "\\s]+|[" + SepChars + "\\s]+$", RegexOptions.ECMAScript | RegexOptions.Compiled);

        public static string? DecoTextKind(string? t)
    {
        t = (string.IsNullOrEmpty(t) ? "" : t).Trim();
        if (t.Length == 0 || !Regex.IsMatch(t, "\\d", RegexOptions.ECMAScript) || CjkRe.IsMatch(t)) return null;
        if (TimeGate.IsMatch(t) && TimeShape.IsMatch(t)) return "time";
        var latin = 0;
        foreach (var c in t)
            if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
                latin++;
        if (latin <= 5 && NoShape.IsMatch(t)) return "no";
        return null;
    }

        public static bool HideDecoText(Env env, string? text) => DecoText(env, text) is null;

    private static bool HasDigit(string s)
    {
        foreach (var c in s)
            if (c is >= '0' and <= '9')
                return true;
        return false;
    }

        /// <summary>
        /// 按「隐藏装饰编号/时间」设置过滤要绘制的文字。
        /// 返回 null 表示整条不画；返回原文表示照常绘制；其余为摘掉编号/时间后的片段。
        /// </summary>
        public static string? DecoText(Env env, string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var fx = env.Fx;
        if (!fx.HideNo && !fx.HideTime) return text;
        if (!HasDigit(text)) return text;
        var lyr = env.Cut != null
            ? !string.IsNullOrEmpty(env.Cut.LineText) ? env.Cut.LineText : env.Cut.Text ?? ""
            : "";
        if (lyr.Contains(text.Trim())) return text;

        var k = DecoTextKind(text);
        if (k != null && (k == "no" ? fx.HideNo : fx.HideTime)) return null;

        var cut = text;
        if (fx.HideNo) cut = NoToken.Replace(cut, " ");
        if (fx.HideTime) cut = TimeToken.Replace(cut, " ");
        if (cut == text) return text;
        cut = SepEdge.Replace(SepRun.Replace(cut, " "), "").Trim();
        return cut.Length == 0 ? null : cut;
    }

        public static BBox? DrawItem(Env env, JObject it, TextDrawOpts? opts = null)
    {
        if (env.ExtractText && env.TextCtx is { } textCtx && !ReferenceEquals(env.Ctx, textCtx))
        {
            var target = env.Ctx;
            var matrix = target.TotalMatrix;
            var clip = target.DeviceClipBounds;
            textCtx.Save();
            textCtx.SetMatrix(SKMatrix.Identity);
            if (clip.Width > 0 && clip.Height > 0)
                textCtx.ClipRect(new SKRect(clip.Left, clip.Top, clip.Right, clip.Bottom));
            textCtx.SetMatrix(matrix);
            env.Ctx = textCtx;
            try
            {
                return DrawItem(env, it, opts);
            }
            finally
            {
                env.Ctx = target;
                textCtx.Restore();
            }
        }
        var o = opts ?? TextDrawOpts.Default;
        var ctx = env.Ctx;
        var ghostPass = env.GhostPass;
        if (ghostPass && IsFalse(it["ghost"])) return null;
        var text = TextOf(it, "text");
        var size = Num(it, "size");
        if (text.Length == 0 || size <= 0.5) return null;
        var deco = DecoText(env, text);
        if (deco is null) return null;
        if (!string.Equals(deco, text, StringComparison.Ordinal))
        {
            it = (JObject)it.DeepClone();
            it["text"] = deco;
        }
        if (!o.InLayer && o.GlyphLog == null && !o.HideText && o.AllowFilter && o.PieceFn == null && o.PieceFnEx == null && env.PieceFn == null && !o.NoPieces)
        {
            var shadowTok = it["shadow"] as JObject;
            var blur = Num(it, "blur");
            var bigShadow = shadowTok != null && !ghostPass && Math.Max(Num(shadowTok, "blur"), 0) * (env.Scale != 0 ? env.Scale : 1) > 6;
            if (blur > 0.4 || bigShadow)
            {
                if (TryDrawItemLayered(env, it, o, out var layered)) return layered;
            }
        }
        var lay = GetLay(it);
        var sx = ZeroTo(Num(it, "sx"), 1);
        var sy = ZeroTo(Num(it, "sy"), 1);
        var baseAlpha = JAnim.NumNull(it, "alpha", 1) * (ghostPass ? JAnim.NumNull(it, "ghostAlpha", 1) : 1);
        if (baseAlpha <= 0.002) return null;
        var col = ghostPass ? env.PassColor ?? "#fff" : Str(it, "color") ?? "#fff";
        var sCol = ghostPass ? env.PassColor ?? "#fff" : Str(it, "strokeColor") ?? Str(it, "color") ?? "#fff";
        var fill = !IsFalse(it["fill"]);
        var fontKey = Str(it, "font") ?? "";
        ctx.Save();
        ctx.Translate((float)Num(it, "x"), (float)Num(it, "y"));
        if (Truthy(it["rot"])) ctx.RotateDegrees((float)Num(it, "rot"));
        if (Truthy(it["skew"])) ctx.Concat(SKMatrix.CreateSkew((float)Math.Tan(Num(it, "skew") * J.DEG), 0));
        var blend = Str(it, "blend");
        var blendMode = blend != null && BlendMap.TryGetValue(blend, out var bm) ? bm : env.Blend;
        var wg = env.Cut is { WeightGrow: true } && !Truthy(it["noWeight"]) ? WeightNow(env) : 0;
        var fontCss = wg > 0 ? VarFontCss(fontKey, size, wg) : JFonts.FontCss(fontKey, size);
        var font = FaceFont(fontKey, size, wg);         font.GetFontMetrics(out var fm);
        var yMid = (float)((-fm.Ascent - fm.Descent) / 2);         SKShader? gradShader = null;
        var hasGradient = Truthy(it["gradient"]);
        if (!ghostPass && hasGradient && fill)
        {
            var gTok = it["gradient"] as JArray;
            if (gTok is { Count: 2 } && gTok[0].Type == JTokenType.String && gTok[1].Type == JTokenType.String)
                gradShader = SKShader.CreateLinearGradient(
                    new SKPoint(0, (float)(-size * 0.5)), new SKPoint(0, (float)(size * 0.5)),
                    new[] { CssColor((string)gTok[0]!), CssColor((string)gTok[1]!) },
                    new[] { 0f, 1f }, SKShaderTileMode.Clamp);
            else if (gTok != null)
            {
                var stops = new List<float>();
                var cols = new List<SKColor>();
                foreach (var t in gTok)
                    if (t is JArray pair && pair.Count >= 2)
                    {
                        stops.Add((float)Num(pair[0] as JObject ?? new JObject(), "o", (double?)pair[0] ?? 0));
                        cols.Add(CssColor((string?)pair[1]));
                    }
                if (stops.Count > 0)
                    gradShader = SKShader.CreateLinearGradient(
                        new SKPoint(0, (float)(-size * 0.5)), new SKPoint(0, (float)(size * 0.5)),
                        cols.ToArray(), stops.ToArray(), SKShaderTileMode.Clamp);
            }
        }
        if (!ghostPass && Truthy(it["pattern"]) && fill && gradShader == null)
            gradShader = TextPattern(ctx, Str(it, "pattern") ?? "", Str(it, "patternColor") ?? Str(it, "color") ?? "#fff",
                Str(it, "patternBg"), size, env.Scale != 0 ? env.Scale : 1);
        var fillA = JAnim.NumNull(it, "fillAlpha", 1);         var shadow = !ghostPass ? it["shadow"] as JObject : null;
        double shBlur = 0, shDx = 0, shDy = 0;
        SKColor shColor = default;
        if (shadow != null)
        {
            var k0 = env.Scale != 0 ? env.Scale : 1;
            shColor = CssColor(Str(shadow, "color") ?? "rgba(0,0,0,0.6)");
            shBlur = o.AllowFilter ? Math.Max(Num(shadow, "blur"), 0) * k0 : 0;             shDx = Num(shadow, "dx") * k0;
            shDy = Num(shadow, "dy") * k0;
        }
        var extTok = !ghostPass ? it["extrude"] as JObject : null;
        var extN = extTok != null ? (int)Num(extTok, "n") : 0;
        var ext = extTok != null && extN > 0 ? extTok : null;
        var dash = it["dash"] != null && Num(it, "dash") < 1 ? (double?)Num(it, "dash") : null;
        float[]? strokeDashArr = null;
        if (it["strokeDash"] is JArray sd)
        {
            strokeDashArr = sd.Select(t => (float)((double?)t ?? 0)).ToArray();
        }
        var stroke = Num(it, "stroke");
        var bbOut = new BBox { Cx = Num(it, "x"), Cy = Num(it, "y") };
        double bx0 = 1e9, by0 = 1e9, bx1 = -1e9, by1 = -1e9;
        var escale = env.Scale != 0 ? env.Scale : 1;
        var pxScale = size * Math.Max(sx, sy) * escale;
        var itemBlur = Num(it, "blur") > 0.4 && o.AllowFilter ? Num(it, "blur") * escale : 0.0;
        var charFn = o.CharFn ?? env.CharFn;
        var pieceFn = o.NoPieces ? null : o.PieceFn ?? env.PieceFn;
        var pieceFnEx = o.PieceFnEx;
        using var paint = new SKPaint { IsAntialias = true };
        foreach (var g in lay.Cells)
        {
            if (g.Ch is " " or "　") continue;
            var gJ = charFn != null || pieceFn != null ? (g.Json ??= CellJson(g)) : null;
            var c = charFn?.Invoke(g.I, gJ, lay.N);
            if (c != null && c.Hide) continue;
            var a = baseAlpha * (c?.A ?? 1);
            if (a <= 0.002) continue;
            var ch = c?.Ch ?? g.Ch;
            var gFont = FaceFontFor(fontKey, ch, size, wg);             var cs = (c?.S ?? 1) * (g.Fs != 0 ? g.Fs : 1);
            var gx = g.X * sx + g.Vx * sx + (c?.Dx ?? 0);
            var gy = g.Y * sy + g.Vy * sy + (c?.Dy ?? 0);
            var crot = (c?.Rot ?? 0) + (g.R90 ? 90 : 0);
            var csx = sx * cs * TrueOr1(c?.Sx);
            var csy = sy * cs * TrueOr1(c?.Sy);
            var gcol = !ghostPass && !string.IsNullOrEmpty(c?.Color) ? c!.Color! : col;
            var bw = g.W * sx * cs / (g.Fs != 0 ? g.Fs : 1) * AbsOr1(c?.Sx);
            var bh = g.H * sy * cs / (g.Fs != 0 ? g.Fs : 1) * AbsOr1(c?.Sy);
            bbOut.AddGlyph(gx, gy, bw, bh);
            bx0 = Math.Min(bx0, gx - bw / 2);
            bx1 = Math.Max(bx1, gx + bw / 2);
            by0 = Math.Min(by0, gy - bh / 2);
            by1 = Math.Max(by1, gy + bh / 2);
            if (o.GlyphLog != null && !ghostPass)
            {
                ctx.Save();
                ctx.Translate((float)gx, (float)gy);
                if (crot != 0) ctx.RotateDegrees((float)crot);
                if (csx != 1 || csy != 1) ctx.Scale((float)csx, (float)csy);
                var T = ctx.TotalMatrix;
                ctx.Restore();
                o.GlyphLog.Add(new GlyphLogEntry
                {
                    Ch = ch,
                    M = new[] { T.ScaleX, T.SkewY, T.SkewX, T.ScaleY, T.TransX, T.TransY },
                    Font = fontCss,
                    FontKey = fontKey,
                    Px = size,
                    Color = gcol,
                    A = a * (fill ? fillA : 1),
                    Stroke = stroke > 0 ? stroke : 0,
                    StrokeColor = sCol,
                    Fill = fill && c?.Outline != true
                });
            }
            if (o.HideText) continue;
            var outlineOnly = c?.Outline == true;
            if ((pieceFnEx != null || pieceFn != null) && fill && c?.Ch == null && !hasGradient && dash == null
                && c?.ClipY == null && c?.ClipX == null && !outlineOnly)
            {
                if (DrawPieces(env, it, pieceFnEx, pieceFn, gJ!, g, ch, gx, gy, crot, csx, csy, gcol, a, pxScale * cs)) continue;
            }
            ctx.Save();
            ctx.Translate((float)gx, (float)gy);
            if (crot != 0) ctx.RotateDegrees((float)crot);
            if (c is { Skew: not null and not 0 }) ctx.Concat(SKMatrix.CreateSkew((float)Math.Tan(c.Skew.Value * J.DEG), 0));
            if (csx != 1 || csy != 1) ctx.Scale((float)csx, (float)csy);
            if (c?.ClipY != null || c?.ClipX != null)             {
                var cy = c.ClipY ?? new[] { -0.7, 0.7 };
                var cx = c.ClipX ?? new[] { -0.7, 0.7 };
                ctx.ClipRect(new SKRect(
                    (float)(cx[0] * g.W), (float)(cy[0] * g.H),
                    (float)(cx[0] * g.W + (cx[1] - cx[0]) * g.W),
                    (float)(cy[0] * g.H + (cy[1] - cy[0]) * g.H)));
            }
            var glyphBlur = c is { Blur: > 0.4 } && o.AllowFilter ? c.Blur.Value * escale : itemBlur;
            var withShadow = shadow != null
                ? DropShadowFilter(shDx, shDy, shBlur, shColor, glyphBlur > 0 ? glyphBlur : 0)
                : glyphBlur > 0 ? BlurFilter(glyphBlur) : null;
            paint.ImageFilter = withShadow;
            paint.BlendMode = blendMode;
            if (ext != null && !outlineOnly)
            {
                var extCol = CssColor(Str(ext, "color") ?? "#000");
                var ea = JAnim.NumNull(ext, "a", 1);                 var fade = Truthy(ext["fade"]);
                for (var k2 = extN; k2 >= 1; k2--)
                {
                    paint.Shader = null;
                    paint.Color = extCol.WithAlpha((byte)Math.Round(
                        J.Clamp(a * ea * (fade ? 1 - (k2 - 1) / (double)extN * 0.85 : 1)) * 255));
                    ctx.DrawText(ch, (float)(Num(ext, "dx") * k2 / extN / csx), (float)(yMid + Num(ext, "dy") * k2 / extN / csy),
                        SKTextAlign.Center, gFont, paint);
                }
            }
            if (fill && !outlineOnly && fillA > 0.002 && dash == null)
            {
                paint.Shader = gradShader;
                paint.Color = CssColor(gcol, a * fillA);                 ctx.DrawText(ch, 0, yMid, SKTextAlign.Center, gFont, paint);
                paint.Shader = null;
            }
            if (stroke > 0 || outlineOnly || dash != null)
            {
                var strokeF = shadow != null && fill ? null : withShadow;
                paint.ImageFilter = strokeF;
                paint.StrokeJoin = SKStrokeJoin.Round;
                paint.StrokeMiter = 2;
                paint.StrokeWidth = (float)((stroke > 0 ? stroke : Math.Max(1, size * 0.02))
                                            / Math.Max(0.05, Math.Sqrt(Math.Abs(csx * csy))));
                paint.Color = CssColor(!ghostPass && !string.IsNullOrEmpty(c?.Color) ? c!.Color! : sCol, a);
                if (dash != null)
                {
                    var L = size * 3.2;
                    paint.PathEffect = SKPathEffect.CreateDash(new[] { (float)Math.Max(0.01, L * dash.Value), (float)L }, 0);
                }
                else if (strokeDashArr != null) paint.PathEffect = SKPathEffect.CreateDash(strokeDashArr, 0);
                ctx.DrawText(ch, 0, yMid, SKTextAlign.Center, gFont, paint);
                paint.PathEffect?.Dispose();
                paint.PathEffect = null;
                if (fill && !outlineOnly && (Truthy(it["strokeUnder"]) || (dash != null && fillA > 0.002)))
                {
                    paint.Shader = gradShader;
                    paint.Color = CssColor(gcol, a * (dash != null ? fillA : 1));
                    ctx.DrawText(ch, 0, yMid, SKTextAlign.Center, gFont, paint);
                    paint.Shader = null;
                }
                paint.ImageFilter = withShadow;
            }
            paint.ImageFilter = null;
            ctx.Restore();
        }
        paint.ImageFilter = null;
        ctx.Restore();
        gradShader?.Dispose();
        if (bbOut.BoxCount == 0) return null;
        bbOut.X0 = Num(it, "x") + bx0;
        bbOut.Y0 = Num(it, "y") + by0;
        bbOut.X1 = Num(it, "x") + bx1;
        bbOut.Y1 = Num(it, "y") + by1;
        return bbOut;
    }

    private static readonly Dictionary<string, SKBlendMode> BlendMap = new()
    {
        ["source-over"] = SKBlendMode.SrcOver,
        ["lighter"] = SKBlendMode.Plus,
        ["screen"] = SKBlendMode.Screen,
        ["overlay"] = SKBlendMode.Overlay,
        ["multiply"] = SKBlendMode.Multiply,
        ["lighten"] = SKBlendMode.Lighten,
        ["darken"] = SKBlendMode.Darken,
        ["color-dodge"] = SKBlendMode.ColorDodge,
        ["color-burn"] = SKBlendMode.ColorBurn,
        ["hard-light"] = SKBlendMode.HardLight,
        ["soft-light"] = SKBlendMode.SoftLight,
        ["difference"] = SKBlendMode.Difference,
        ["exclusion"] = SKBlendMode.Exclusion,
        ["hue"] = SKBlendMode.Hue,
        ["saturation"] = SKBlendMode.Saturation,
        ["color"] = SKBlendMode.Color,
        ["luminosity"] = SKBlendMode.Luminosity
    };

        public static void FastRow(Env env, string? text, string font, double size, double x, double y, double sp,
        string? color, double alpha, string align = "left")
    {
        if (env.Pass != "main" || alpha <= 0.01) return;
        text = DecoText(env, text);
        if (string.IsNullOrEmpty(text)) return;
        var ctx = env.Ctx;
        var cps = CodePoints(text);
        double total = sp * cps.Count;
        foreach (var cp in cps) total += FaceFontFor(font, cp, size).MeasureText(cp);
        var cx = align == "left" ? x : align == "right" ? x - total : x - total / 2;
        ctx.Save();
        using var p = new SKPaint { IsAntialias = true, Color = CssColor(color, alpha) };
        foreach (var cp in cps)
        {
            var f = FaceFontFor(font, cp, size);             f.GetFontMetrics(out var fm);
            var yMid = (float)((-fm.Ascent - fm.Descent) / 2);
            ctx.DrawText(cp, (float)cx, (float)y + yMid, SKTextAlign.Left, f, p);
            cx += f.MeasureText(cp) + sp;
        }
        ctx.Restore();
    }

        public static double WeightNow(Env env)
    {
        var cut = env.Cut!;
        var span = Math.Max(0.5, Math.Min(1.4, cut.Dur * 0.5));
        var baseT = env.Ltb != 0 ? env.Ltb : env.Lt;         var k = J.Clamp(baseT / span);
        return 1e-3 + (1 - Math.Pow(1 - k, 3)) * (1 - 1e-3);
    }

        public static string VarFontCss(string key, double px, double e)
    {
        var f = JLang.FaceOf(key);
        var serif = f.Kind == "mincho";
        var w = (int)Math.Round(serif ? 200 + e * 700 : 100 + e * 800);
        return string.Create(CultureInfo.InvariantCulture,
            $"{w} {px.ToString("F2", CultureInfo.InvariantCulture)}px {(serif ? "\"Noto Serif JP\"" : "\"Noto Sans JP\"")},{(string.IsNullOrEmpty(f.Fb) ? "sans-serif" : f.Fb)}");
    }

        private static bool DrawPieces(Env env, JObject it, PieceFnEx? pieceFnEx, JAnim.PieceFn? pieceFn, JObject gJ, GlyphCell g,
        string ch, double gx, double gy, double crot, double sx, double sy, string col, double alpha, double px)
    {
        var glyph = Glyphs.Get(Str(it, "font") ?? "", ch, px);
        var list = Truthy(it["shatter"]) ? FragList(glyph, (int)(ZeroTo(Num(it, "seed"), 1))) : glyph.Pieces;
        if (list.Count == 0) return false;
        var size = Num(it, "size");
        var res = (double)glyph.Res;
        var pts = new (GlyphPiece p, PieceXf? t, double ox, double oy)[list.Count];
        var moving = false;
        var cr = Math.Cos(crot * J.DEG);
        var sr = Math.Sin(crot * J.DEG);
        for (var j = 0; j < list.Count; j++)
        {
            var p = list[j];
            var ex = p.Cx * size * sx;
            var ey = p.Cy * size * sy;             var ox = gx + ex * cr - ey * sr;
            var oy = gy + ex * sr + ey * cr;
            var t = pieceFnEx != null ? pieceFnEx(g.I, j, p, ox, oy, gJ) : pieceFn!(g.I, j, 0, ox, oy, gJ);
            if (!ReferenceEquals(t, JAnim.Pid) && t != null) moving = true;
            if (t == null) moving = true;
            pts[j] = (p, t, ox, oy);
        }
        if (!moving) return false;
        var ctx = env.Ctx;
        using var paint = new SKPaint { IsAntialias = true };
        for (var j = 0; j < list.Count; j++)
        {
            var (p, t, ox, oy) = pts[j];
            if (t == null || t.A <= 0.003) continue;
            ctx.Save();
            ctx.Translate((float)(ox + t.Dx), (float)(oy + t.Dy));
            if (t.St != 1)
            {
                ctx.RotateDegrees((float)t.Sdir);
                ctx.Scale((float)t.St, (float)(1 / Math.Sqrt(t.St)));
                ctx.RotateDegrees((float)-t.Sdir);
            }
            ctx.RotateDegrees((float)(crot + t.Rot));
            var k = size / res * t.S;
            ctx.Scale((float)(sx * k), (float)(sy * k));
            paint.Color = CssColor(col, alpha * t.A);
            if (p.Poly != null)
            {
                var src = p.Src!;
                using var clipP = new SKPath();
                for (var qi = 0; qi < p.Poly.Length; qi++)
                {
                    var q = p.Poly[qi];
                    var X = (float)((q[0] - p.Fx) * res);
                    var Y = (float)((q[1] - p.Fy) * res);
                    if (qi == 0) clipP.MoveTo(X, Y);
                    else clipP.LineTo(X, Y);
                }
                clipP.Close();
                ctx.ClipPath(clipP);
                ctx.Save();
                ctx.Translate((float)-(src.CenterX + p.Fx * res), (float)-(src.CenterY + p.Fy * res));
                ctx.DrawPath(src.Path, paint);
                ctx.Restore();
            }
            else
            {
                ctx.Save();
                ctx.Translate((float)-p.CenterX, (float)-p.CenterY);
                ctx.DrawPath(p.Path, paint);
                ctx.Restore();
            }
            ctx.Restore();
        }
        return true;
    }

        private static List<GlyphPiece> FragList(GlyphInfo glyph, int seed)
    {
        var by = glyph.FragLists ??= new Dictionary<int, List<GlyphPiece>>();
        if (by.TryGetValue(seed, out var got)) return got;
        var outL = new List<GlyphPiece>();
        foreach (var p in glyph.Pieces)
        {
            var fr = Fragments(p, seed);
            if (fr.Count <= 1)
            {
                outL.Add(p);
                continue;
            }
            foreach (var f in fr)
                outL.Add(new GlyphPiece
                {
                    Src = p,
                    Poly = f.Poly,
                    Fx = f.Cx,
                    Fy = f.Cy,
                    Cx = p.Cx + f.Cx,
                    Cy = p.Cy + f.Cy,
                    W = p.W,
                    H = p.H,
                    Area = p.Area / fr.Count,
                    Id = p.Id * 8 + outL.Count
                });
        }
        by[seed] = outL;
        return outL;
    }

    private static readonly Dictionary<string, SKBitmap> PatCache = new();

        public static SKShader TextPattern(SKCanvas ctx, string kind, string color, string? bg, double size, double scale)
    {
        var cell = Math.Max(3, (int)Math.Round(size * (kind == "dots" ? 0.075 : 0.06)));
        var px = Math.Max(2, (int)Math.Round(cell * scale));
        var key = kind + color + (bg ?? "") + px;
        if (!PatCache.TryGetValue(key, out var cv))
        {
            cv = BuildPatternTile(kind, color, bg, px);
            if (PatCache.Count > 80)
            {
                foreach (var b in PatCache.Values) b.Dispose();
                PatCache.Clear();
            }
            PatCache[key] = cv;
        }
        var m = scale != 1
            ? SKMatrix.CreateScale((float)(1 / scale), (float)(1 / scale))
            : SKMatrix.Identity;
        return SKShader.CreateBitmap(cv, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat, m);
    }

    private static SKBitmap BuildPatternTile(string kind, string color, string? bg, int px)
    {
        var bmp = new SKBitmap(px, px, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var x = new SKCanvas(bmp);
        x.Clear(SKColors.Transparent);
        if (bg != null)
            using (var p = new SKPaint { Color = Env.SkColor(bg) })
                x.DrawRect(0, 0, px, px, p);
        var col = Env.SkColor(color);
        using var fill = new SKPaint { Color = col, IsAntialias = true };
        using var stroke = new SKPaint { Color = col, IsAntialias = true, IsStroke = true };
        if (kind == "dots")
        {
            x.DrawCircle(px / 2f, px / 2f, (float)(px * 0.34), fill);
        }
        else if (kind == "stripes")
        {
            stroke.StrokeWidth = (float)(px * 0.38);
            using var path = new SKPath();
            path.MoveTo(-px, px * 2f);
            path.LineTo(px * 2f, -px);
            path.MoveTo(-px, px);
            path.LineTo(px, -px);
            path.MoveTo(0, px * 2f);
            path.LineTo(px * 2f, 0);
            x.DrawPath(path, stroke);
        }
        else if (kind == "hatch")
        {
            stroke.StrokeWidth = (float)Math.Max(1, px * 0.16);
            using var path = new SKPath();
            path.MoveTo(0, 0);
            path.LineTo(px, px);
            path.MoveTo(px, 0);
            path.LineTo(0, px);
            x.DrawPath(path, stroke);
        }
        else if (kind == "grid")
        {
            x.DrawRect(0, 0, px, (float)Math.Max(1, px * 0.18), fill);
            x.DrawRect(0, 0, (float)Math.Max(1, px * 0.18), px, fill);
        }
        else
        {
            x.DrawRect(0, 0, px, (float)Math.Max(1, px * 0.45), fill);
        }         return bmp;
    }

        public sealed class MeasureResult
    {
        public double W, H;
        public TextLayout Lay = null!;
    }

    private static readonly Dictionary<string, MeasureResult> MMemo = new();
    private static readonly Queue<string> MMemoOrder = new();
    private const string MSENT = "\u0001measure";

    private static string MKey(JObject it) =>
        TextOf(it, "text") + "\u0002" + (Str(it, "font") ?? "undefined") + "\u0002" + NumStr(Num(it, "size")) + "\u0002" +
        NumStr(Num(it, "track")) + "\u0002" + NumStr(ZeroTo(Num(it, "lead"), 1.3)) + "\u0002" + NumStr(ZeroTo(Num(it, "sx"), 1)) + "\u0002" +
        NumStr(ZeroTo(Num(it, "sy"), 1)) + "\u0002" + (Str(it, "align") ?? "") + "\u0002" + (Truthy(it["vertical"]) ? 1 : 0) +
        "\u0002" + (Typeset ? 1 : 0);

    private static string NumStr(double v) => v.ToString(CultureInfo.InvariantCulture);

        public static MeasureResult Measure(JObject it)
    {
        if (!Metrics.M.ContainsKey(MSENT))
        {
            MMemo.Clear();
            MMemoOrder.Clear();
            Metrics.M[MSENT] = 1;
        }
        var k = MKey(it);
        if (!MMemo.TryGetValue(k, out var m))
        {
            var l = LayoutText(it);
            m = new MeasureResult { W = l.W * ZeroTo(Num(it, "sx"), 1), H = l.H * ZeroTo(Num(it, "sy"), 1), Lay = l };
            if (MMemo.Count > 600)
                MMemo.Remove(MMemoOrder.Dequeue());
            MMemo[k] = m;
            MMemoOrder.Enqueue(k);
        }
        return m;
    }

        public static double FitSize(string text, string font, double maxW, double maxH, JObject? opt = null)
    {
        var probe = new JObject { ["text"] = text, ["font"] = font, ["size"] = 100, ["track"] = 0 };
        if (opt != null)
            foreach (var p in opt.Properties())
                probe[p.Name] = p.Value;
        var m = Measure(probe);
        var k = Math.Min(maxW / Math.Max(1, m.W), maxH / Math.Max(1, m.H));
        return 100 * k;
    }

        public sealed class MetricsCache : IDictionary
    {
                public readonly Hashtable M = new();

                public double Adv(string fontKey, string ch)
        {
            var k = fontKey + '\u0000' + ch;
            if (M[k] is double v) return v;
            var font = FaceFontFor(fontKey, ch, 100);             var r = (double)font.MeasureText(ch) / 100;
            if (!(r > 0)) r = ch == " " ? 0.3 : 1;
            M[k] = r;
            return r;
        }

        public void Clear() => M.Clear();
        public int Count => M.Count;
        public bool IsSynchronized => M.IsSynchronized;
        public object SyncRoot => M.SyncRoot;
        public bool IsReadOnly => false;
        public bool IsFixedSize => false;
        public object? this[object key] { get => M[key]; set => M[key] = value; }
        public void Add(object key, object? value) => M.Add(key, value);
        public bool Contains(object key) => M.Contains(key);
        public void Remove(object key) => M.Remove(key);
        public void CopyTo(Array array, int index) => M.CopyTo(array, index);
        public ICollection Keys => M.Keys;
        public ICollection Values => M.Values;
        public IDictionaryEnumerator GetEnumerator() => M.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => M.GetEnumerator();
    }

    public static readonly MetricsCache Metrics = new();

        public sealed class GlyphPiece
    {
        public int Id;
        public SKPath Path = new();

                public GlyphPiece? Src;

                public double[][]? Poly;
        public double Fx, Fy;         public int Res;

                public double Cx, Cy, W, H, Area;

                internal double CenterX, CenterY;

                internal Dictionary<int, List<PolyFragment>>? Frags;
    }

        public sealed class GlyphInfo
    {
        public string Ch = "";
        public int Res;
        public List<GlyphPiece> Pieces = new();

                internal Dictionary<int, List<GlyphPiece>>? FragLists;

        public void Dispose()
        {
            foreach (var p in Pieces) p.Path.Dispose();
            Pieces.Clear();
            FragLists?.Clear();
        }
    }

        public sealed class GlyphCache : IDictionary
    {
        public const int MaxRes = 512;

                public readonly Hashtable Map = new();

        public void Clear()
        {
            foreach (var g in Map.Values.OfType<GlyphInfo>()) g.Dispose();
            Map.Clear();
        }

                public static int Bucket(double px)
        {
            var r = 64;
            while (r < px && r < MaxRes) r *= 2;
            return r;
        }

        public GlyphInfo Get(string fontKey, string ch, double px)
        {
            var res = Bucket(px);
            var key = fontKey + "|" + ch + "|" + res;
            if (Map[key] is GlyphInfo g) return g;
            g = Decompose(fontKey, ch, res);
            Map[key] = g;
            if (Map.Count > 1800) Evict();
            return g;
        }

        private void Evict()
        {
            var n = 0;
            foreach (var key in Map.Keys.Cast<object>().Take(600).ToList())
            {
                if (Map[key] is GlyphInfo g) g.Dispose();
                Map.Remove(key);
                if (++n >= 600) break;
            }
        }

        public int Count => Map.Count;
        public bool IsSynchronized => Map.IsSynchronized;
        public object SyncRoot => Map.SyncRoot;
        public bool IsReadOnly => false;
        public bool IsFixedSize => false;
        public object? this[object key] { get => Map[key]; set => Map[key] = value; }
        public void Add(object key, object? value) => Map.Add(key, value);
        public bool Contains(object key) => Map.Contains(key);
        public void Remove(object key) => Map.Remove(key);
        public void CopyTo(Array array, int index) => Map.CopyTo(array, index);
        public ICollection Keys => Map.Keys;
        public ICollection Values => Map.Values;
        public IDictionaryEnumerator GetEnumerator() => Map.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => Map.GetEnumerator();
    }

    public static readonly GlyphCache Glyphs = new();

    private static int _pid;

        internal static GlyphInfo Decompose(string fontKey, string ch, int res)
    {
        var s = RentScratch(out var pooled);
        try
        {
            return DecomposeCore(fontKey, ch, res, s);
        }
        finally
        {
            if (pooled) s.InUse = false;
        }
    }

        private sealed class DecomposeScratch
    {
        public int[] L = [];
        public int[] L2 = [];
        public int[] Stack = [];
        public int[] Remap = [];
        public int[] Hist = [];
        public (int X0, int Y0, int X1, int Y1, int Area)[] Boxes = [];
        public SKPath?[] Paths = [];
        public byte[] MaskGlyph = [];
        public byte[] MaskContour = [];
        public SKBitmap? Raster;
        public int RasterS = -1;
        public bool InUse;

                public static void Grow<T>(ref T[] a, int n)
        {
            if (a.Length < n) a = new T[n];
        }

                public byte[] RentGlyphMask(int n)
        {
            Grow(ref MaskGlyph, n);
            return MaskGlyph;
        }

                public byte[] RentContourMask(int n)
        {
            Grow(ref MaskContour, n);
            return MaskContour;
        }

                public SKBitmap RasterBitmap(int s)
        {
            if (Raster == null || RasterS != s)
            {
                Raster?.Dispose();
                Raster = new SKBitmap(s, s, SKColorType.Bgra8888, SKAlphaType.Premul);
                RasterS = s;
            }

            return Raster;
        }
    }

    [ThreadStatic] private static DecomposeScratch? _scratch;

    private static DecomposeScratch RentScratch(out bool pooled)
    {
        var s = _scratch ??= new DecomposeScratch();
        if (s.InUse)
        {
            pooled = false;
            return new DecomposeScratch();
        }

        s.InUse = true;
        pooled = true;
        return s;
    }

    private static GlyphInfo DecomposeCore(string fontKey, string ch, int res, DecomposeScratch s)
    {
        var S = (int)Math.Ceiling(res * 1.45);
        var half = S / 2;
        var outG = new GlyphInfo { Ch = ch, Res = res };
        if (ch.Length == 0) return outG;
        var font = FaceFontFor(fontKey, ch, res);         font.GetFontMetrics(out var fm);
        var yMid = (float)((-fm.Ascent - fm.Descent) / 2);
        var adv = (double)font.MeasureText(ch);
        var gid = font.GetGlyph(CodePointOf(ch));
        using var rawPath = font.GetGlyphPath(gid);
        if (rawPath.IsEmpty) return outG;
        using var glyphPath = new SKPath(rawPath);
        glyphPath.Transform(SKMatrix.CreateTranslation((float)(half - adv / 2), (float)(half + yMid)));
        var A = RasterAlpha(glyphPath, S, s.RentGlyphMask(S * S), s);         var N = S * S;
        DecomposeScratch.Grow(ref s.L, N);
        DecomposeScratch.Grow(ref s.L2, N);
        DecomposeScratch.Grow(ref s.Stack, N);
        var L = s.L;
        var L2 = s.L2;
        var stack = s.Stack;
        Array.Clear(L, 0, N);         const int TH = 60;
        var nl = 0;
        DecomposeScratch.Grow(ref s.Boxes, N / 2 + 2);
        var boxes = s.Boxes;
        for (var i = 0; i < N; i++)
        {
            if (A[i] < TH || L[i] != 0) continue;
            nl++;
            var sp = 0;
            stack[sp++] = i;
            L[i] = nl;
            int x0 = S, y0 = S, x1 = 0, y1 = 0, area = 0;
            while (sp > 0)
            {
                var p = stack[--sp];
                var pxI = p % S;
                var pyI = p / S;
                area++;
                if (pxI < x0) x0 = pxI;
                if (pxI > x1) x1 = pxI;
                if (pyI < y0) y0 = pyI;
                if (pyI > y1) y1 = pyI;
                for (var dy = -1; dy <= 1; dy++)
                {
                    var yy = pyI + dy;
                    if (yy < 0 || yy >= S) continue;
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        var xx = pxI + dx;
                        if (xx < 0 || xx >= S) continue;
                        var q = yy * S + xx;
                        if (L[q] == 0 && A[q] >= TH)
                        {
                            L[q] = nl;
                            stack[sp++] = q;
                        }
                    }
                }
            }
            boxes[nl] = (x0, y0, x1, y1, area);
        }
        for (var pass = 0; pass < 2; pass++)
        {
            Array.Copy(L, L2, N);
            for (var p = 0; p < N; p++)
            {
                if (L[p] != 0 || A[p] == 0) continue;
                var pxI = p % S;
                var pyI = p / S;
                var lab = 0;
                if (pxI > 0 && L[p - 1] != 0) lab = L[p - 1];
                else if (pxI < S - 1 && L[p + 1] != 0) lab = L[p + 1];
                else if (pyI > 0 && L[p - S] != 0) lab = L[p - S];
                else if (pyI < S - 1 && L[p + S] != 0) lab = L[p + S];
                if (lab != 0)
                {
                    L2[p] = lab;
                    var b = boxes[lab];
                    if (pxI < b.X0) b.X0 = pxI;
                    if (pxI > b.X1) b.X1 = pxI;
                    if (pyI < b.Y0) b.Y0 = pyI;
                    if (pyI > b.Y1) b.Y1 = pyI;
                    boxes[lab] = b;
                }
            }
            (L, L2) = (L2, L);         }
        var minA = res * res * 0.0012;
        DecomposeScratch.Grow(ref s.Remap, nl + 1);
        var remap = s.Remap;
        for (var l = 1; l <= nl; l++) remap[l] = l;
        for (var l = 1; l <= nl; l++)
        {
            var b = boxes[l];
            if (b.Area >= minA) continue;
            int best = 0;
            double bd = 1e9;
            var cx = (b.X0 + b.X1) / 2.0;
            var cy = (b.Y0 + b.Y1) / 2.0;
            for (var m = 1; m <= nl; m++)
            {
                if (m == l || boxes[m].Area < minA) continue;
                var o = boxes[m];
                var dx = Math.Max(o.X0 - cx, Math.Max(0, cx - o.X1));
                var dy = Math.Max(o.Y0 - cy, Math.Max(0, cy - o.Y1));
                var d = dx * dx + dy * dy;
                if (d < bd)
                {
                    bd = d;
                    best = m;
                }
            }
            if (best != 0)
            {
                remap[l] = best;
                var o = boxes[best];
                o.X0 = Math.Min(o.X0, b.X0);
                o.Y0 = Math.Min(o.Y0, b.Y0);
                o.X1 = Math.Max(o.X1, b.X1);
                o.Y1 = Math.Max(o.Y1, b.Y1);
                boxes[best] = o;
            }
        }
        var contours = SplitContours(glyphPath);
        DecomposeScratch.Grow(ref s.Paths, nl + 1);
        var paths = s.Paths;
        Array.Clear(paths, 0, nl + 1);         foreach (var cont in contours)
        {
            var ca = RasterAlpha(cont, S, s.RentContourMask(N), s);
            DecomposeScratch.Grow(ref s.Hist, nl + 1);
            var hist = s.Hist;
            Array.Clear(hist, 0, nl + 1);
            var votes = 0;
            for (var p = 0; p < N; p++)
                if (ca[p] >= TH && L[p] != 0)
                {
                    hist[L[p]]++;
                    votes++;
                }
            if (votes == 0)
                for (var p = 0; p < N; p++)
                    if (ca[p] > 0 && L[p] != 0)
                    {
                        hist[L[p]]++;
                        votes++;
                    }
            if (votes == 0)
            {
                cont.Dispose();
                continue;
            }
            var bestL = 1;
            for (var l = 2; l <= nl; l++)
                if (hist[l] > hist[bestL])
                    bestL = l;
            var target = remap[bestL];
            if (paths[target] == null) paths[target] = new SKPath();
            paths[target].AddPath(cont);             cont.Dispose();
        }
        for (var l = 1; l <= nl; l++)
        {
            if (remap[l] != l) continue;
            var b = boxes[l];
            var w = b.X1 - b.X0 + 1;
            var h = b.Y1 - b.Y0 + 1;
            outG.Pieces.Add(new GlyphPiece
            {
                Id = ++_pid,
                Path = paths[l] ?? new SKPath(),
                Res = res,
                Cx = ((b.X0 + b.X1 + 1) / 2.0 - half) / res,
                Cy = ((b.Y0 + b.Y1 + 1) / 2.0 - half) / res,
                W = w / (double)res,
                H = h / (double)res,
                Area = b.Area / (double)(res * res),
                CenterX = (b.X0 + b.X1 + 1) / 2.0,
                CenterY = (b.Y0 + b.Y1 + 1) / 2.0
            });
        }
        outG.Pieces = outG.Pieces.OrderByDescending(p => p.Area).ToList();
        return outG;
    }

        private static byte[] RasterAlpha(SKPath path, int S, byte[] dst, DecomposeScratch s)
    {
        var bmp = s.RasterBitmap(S);
        using (var cv = new SKCanvas(bmp))
        {
            cv.Clear(SKColors.Transparent);
            using var p = new SKPaint { IsAntialias = true, Color = SKColors.White };
            cv.DrawPath(path, p);
        }
        var n = S * S;
        unsafe
        {
            var src = (byte*)bmp.GetPixels().ToPointer();
            for (var i = 0; i < n; i++) dst[i] = src[i * 4 + 3];
        }

        return dst;
    }

        private static List<SKPath> SplitContours(SKPath p)
    {
        var list = new List<SKPath>();
        SKPath? cur = null;
        var pts = new SKPoint[4];
        var it = p.CreateIterator(false);
        SKPathVerb v;
        while ((v = it.Next(pts)) != SKPathVerb.Done)
        {
            switch (v)
            {
                case SKPathVerb.Move:
                    cur = new SKPath();
                    cur.MoveTo(pts[0]);
                    list.Add(cur);
                    break;
                case SKPathVerb.Line: cur?.LineTo(pts[1]); break;
                case SKPathVerb.Quad: cur?.QuadTo(pts[1], pts[2]); break;
                case SKPathVerb.Conic: cur?.ConicTo(pts[1], pts[2], it.ConicWeight()); break;
                case SKPathVerb.Cubic: cur?.CubicTo(pts[1], pts[2], pts[3]); break;
                case SKPathVerb.Close: cur?.Close(); break;
            }
        }
        return list;
    }

    private static int CodePointOf(string ch)
    {
        if (ch.Length >= 2 && char.IsSurrogatePair(ch[0], ch[1])) return char.ConvertToUtf32(ch, 0);
        return ch.Length > 0 ? ch[0] : 0;
    }

        public sealed class PolyFragment
    {
        public double[][] Poly = Array.Empty<double[]>();
        public double Cx, Cy;
    }

        public static List<PolyFragment> Fragments(GlyphPiece pc, int seed)
    {
        var by = pc.Frags ??= new Dictionary<int, List<PolyFragment>>();
        if (by.TryGetValue(seed, out var f)) return f;
        var w = pc.W;
        var h = pc.H;
        var polys = new List<double[][]>
        {
            new[] { new[] { -w / 2, -h / 2 }, new[] { w / 2, -h / 2 }, new[] { w / 2, h / 2 }, new[] { -w / 2, h / 2 } }
        };
        var cuts = Math.Max(w, h) > 0.42 ? 2 : Math.Max(w, h) > 0.2 ? 1 : 0;
        for (var c = 0; c < cuts; c++)
        {
            var next = new List<double[][]>();
            foreach (var poly in polys)
            {
                var ang = (w > h ? Math.PI / 2 : 0) + J.Rs(seed, pc.Id, c) * 0.5;
                var nx = Math.Cos(ang);
                var ny = Math.Sin(ang);
                var cx = poly.Average(p => p[0]);
                var cy = poly.Average(p => p[1]);
                var off = J.Rs(seed, pc.Id, c, 7) * 0.12 * Math.Max(w, h);
                var d0 = nx * cx + ny * cy + off;
                next.Add(ClipHalf(poly, nx, ny, d0, 1));
                next.Add(ClipHalf(poly, nx, ny, d0, -1));
            }
            polys = next.Where(p => p.Length >= 3).ToList();
        }
        var outL = polys.Select(p =>
        {
            var cx = p.Average(q => q[0]);
            var cy = p.Average(q => q[1]);
            return new PolyFragment { Poly = p, Cx = cx, Cy = cy };
        }).ToList();
        by[seed] = outL;
        return outL;
    }

    private static double[][] ClipHalf(double[][] poly, double nx, double ny, double d0, double sgn)
    {
        var outL = new List<double[]>();
        for (var i = 0; i < poly.Length; i++)
        {
            var a = poly[i];
            var b = poly[(i + 1) % poly.Length];
            var da = sgn * (nx * a[0] + ny * a[1] - d0);
            var db = sgn * (nx * b[0] + ny * b[1] - d0);
            if (da >= 0) outL.Add(a);
            if (da >= 0 != db >= 0)
            {
                var t = da / (da - db);
                outL.Add(new[] { a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t });
            }
        }
        return outL.ToArray();
    }

    private static SKTypeface ResolveTypefaceChain(IEnumerable<string> families, int weight) =>
        JFonts.ResolveChain(families, weight);

        private static readonly Hashtable FontJobs = new();

        internal static void OnFontsChanged()
    {
        ClearFontJobs();
        InvalidateLayoutCache();     }

    private static void ClearFontJobs()
    {
        foreach (DictionaryEntry kv in FontJobs)
            if (kv.Value is IDisposable d) d.Dispose();
        FontJobs.Clear();
    }

    public static SKFont FaceFont(string fontKey, double size, double wg = 0)
    {
        if (wg <= 0)
        {
            var key = string.Create(CultureInfo.InvariantCulture, $"{fontKey}\u0001{size.ToString("R", CultureInfo.InvariantCulture)}");
            if (FontJobs[key] is SKFont hit) return hit;
            var nf = BuildFont(fontKey, size, 0);
            if (FontJobs.Count > 4096) ClearFontJobs();
            FontJobs[key] = nf;
            return nf;
        }

        var qWg = Math.Round(wg * 20.0) / 20.0;
        var qKey = string.Create(CultureInfo.InvariantCulture, $"{fontKey}\u0001{size.ToString("R", CultureInfo.InvariantCulture)}\u0001{qWg.ToString("F2", CultureInfo.InvariantCulture)}");
        if (FontJobs[qKey] is SKFont qHit) return qHit;
        var qNf = BuildFont(fontKey, size, qWg);
        if (FontJobs.Count > 4096) ClearFontJobs();
        FontJobs[qKey] = qNf;
        return qNf;
    }

        public static SKFont FaceFontFor(string fontKey, string ch, double size, double wg = 0)
    {
        var baseFont = FaceFont(fontKey, size, wg);
        var cp = CodePointOf(ch);
        if (baseFont.Typeface.ContainsGlyph(cp)) return baseFont;
        var f = JLang.FaceOf(fontKey);
        var weight = wg > 0
            ? (int)Math.Round(f.Kind == "mincho" ? 200 + wg * 700 : 100 + wg * 800)
            : f.Weight;
        var tf = JFonts.CoverFace(baseFont.Typeface, cp, weight, f.Kind);
        if (ReferenceEquals(tf, baseFont.Typeface)) return baseFont;

        var key = string.Create(CultureInfo.InvariantCulture,
            $"\u0002{tf.FamilyName}\u0001{(int)tf.FontStyle.Weight}\u0001{size.ToString("R", CultureInfo.InvariantCulture)}");
        if (FontJobs[key] is SKFont hit) return hit;
        var nf = new SKFont(tf, (float)size)
        {
            Edging = SKFontEdging.Antialias,
            Subpixel = true,
            LinearMetrics = true
        };
        if (FontJobs.Count > 4096) ClearFontJobs();
        FontJobs[key] = nf;
        return nf;
    }

    private static SKFont BuildFont(string fontKey, double size, double wg)
    {
        SKTypeface tf;
        var f0 = JLang.FaceOf(fontKey);
        if (wg > 0)
        {
            var serif = f0.Kind == "mincho";
            var w = (int)Math.Round(serif ? 200 + wg * 700 : 100 + wg * 800);
            tf = ResolveTypefaceChain(
                new[] { serif ? "\"Noto Serif JP\"" : "\"Noto Sans JP\"" }
                    .Concat(FbFamilies(f0.Fb)).Concat(JFonts.StackFor(f0.Kind)), w);
        }
        else
        {
            tf = ResolveTypefaceChain(JFonts.FaceChain(f0), f0.Weight);
        }
        return new SKFont(tf, (float)size)
        {
            Edging = SKFontEdging.Antialias,
            Subpixel = true,
            LinearMetrics = true
        };
    }

        private static IEnumerable<string> FbFamilies(string fb) =>
        (fb ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0);

    [ThreadStatic] private static Dictionary<(int Kind, float A, float B, float C, float D, uint Col, float Aux), SKImageFilter>? _fxMemo;

        internal static SKImageFilter? BlurFilter(double blurPx)
    {
        if (blurPx <= 0) return null;
        var s = (float)(blurPx * 0.5);
        var map = _fxMemo ??= new Dictionary<(int, float, float, float, float, uint, float), SKImageFilter>();
        var key = (0, s, s, 0f, 0f, 0u, 0f);
        if (map.TryGetValue(key, out var hit)) return hit;
        if (map.Count > 400) map.Clear();         var f = SKImageFilter.CreateBlur(s, s);
        map[key] = f;
        return f;
    }

        internal static SKImageFilter ShadowSigmaFilter(double dx, double dy, double sigmaX, double sigmaY, SKColor col)
    {
        var sx = (float)sigmaX;
        var sy = (float)sigmaY;
        var map = _fxMemo ??= new Dictionary<(int, float, float, float, float, uint, float), SKImageFilter>();
        var key = (2, (float)dx, (float)dy, sx, sy, (uint)col, 0f);
        if (map.TryGetValue(key, out var hit)) return hit;
        if (map.Count > 400) map.Clear();
        var f = SKImageFilter.CreateDropShadow((float)dx, (float)dy, sx, sy, col);
        map[key] = f;
        return f;
    }

        private static SKImageFilter? DropShadowFilter(double dx, double dy, double blurPx, SKColor col, double baseBlurPx)
    {
        var s = (float)(blurPx * 0.5);
        var aux = (float)baseBlurPx;
        var map = _fxMemo ??= new Dictionary<(int, float, float, float, float, uint, float), SKImageFilter>();
        var key = (1, (float)dx, (float)dy, s, s, (uint)col, aux);
        if (map.TryGetValue(key, out var hit)) return hit;
        if (map.Count > 400) map.Clear();
        var f = SKImageFilter.CreateDropShadow((float)dx, (float)dy, s, s, col,
            baseBlurPx > 0 ? BlurFilter(baseBlurPx) : null);
        map[key] = f;
        return f;
    }

        private static SKColor CssColor(string? s, double alpha = 1)
    {
        if (string.IsNullOrEmpty(s) || s == "transparent") return new SKColor(0, 0, 0, 0);
        s = s.Trim();
        if (s.StartsWith('#')) return Env.SkColor(s, alpha);
        var open = s.IndexOf('(');
        var close = s.LastIndexOf(')');
        if ((s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)) && open > 0 && close > open)
        {
            var parts = s.Substring(open + 1, close - open - 1).Split(',');
            if (parts.Length >= 3
                && byte.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var r)
                && byte.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var g)
                && byte.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var b))
            {
                var ca = 1.0;
                if (parts.Length > 3 && !double.TryParse(parts[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out ca)) ca = 1;
                return new SKColor(r, g, b, (byte)Math.Round(J.Clamp(alpha * ca) * 255));
            }
        }
        return new SKColor(0, 0, 0, (byte)Math.Round(J.Clamp(alpha) * 255));
    }

    private static double Num(JObject o, string k, double d = 0)
    {
        var v = o[k];
        if (v == null || v.Type == JTokenType.Null) return d;
        return v.Type switch
        {
            JTokenType.Integer => Convert.ToDouble(((JValue)v).Value, CultureInfo.InvariantCulture),
            JTokenType.Float => Convert.ToDouble(((JValue)v).Value, CultureInfo.InvariantCulture),
            JTokenType.Boolean => (bool)((JValue)v).Value! ? 1 : 0,
            JTokenType.String => double.TryParse((string?)((JValue)v).Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : d,
            _ => d
        };
    }

    private static string? Str(JObject o, string k)
    {
        var v = o[k];
        return v != null && v.Type == JTokenType.String ? (string?)v : null;
    }

        private static string TextOf(JObject o, string k)
    {
        var v = o[k];
        if (v == null || v.Type == JTokenType.Null) return "";
        return v.Type == JTokenType.String ? (string)v! : v.ToString();
    }

        private static bool Truthy(JToken? t) => t switch
    {
        null => false,
        JValue { Type: JTokenType.Null } => false,
        JValue { Type: JTokenType.Boolean } b => (bool)b.Value!,
        JValue { Type: JTokenType.Integer } i => Convert.ToInt64(((JValue)i).Value, CultureInfo.InvariantCulture) != 0,
        JValue { Type: JTokenType.Float } f => !double.IsNaN(Convert.ToDouble(((JValue)f).Value, CultureInfo.InvariantCulture))
                                               && Convert.ToDouble(((JValue)f).Value, CultureInfo.InvariantCulture) != 0,
        JValue { Type: JTokenType.String } s => ((string?)((JValue)s).Value)?.Length > 0,
        _ => true     };

        private static bool IsFalse(JToken? t) => t is JValue { Type: JTokenType.Boolean } b && !(bool)b.Value!;

        private static double ZeroTo(double v, double def) => v == 0 ? def : v;

        private static double TrueOr1(double? v) => v != null && v.Value != 0 ? v.Value : 1;

        private static double AbsOr1(double? v) => v != null && v.Value != 0 ? Math.Abs(v.Value) : 1;

        private static List<string> CodePoints(string s)
    {
        var l = new List<string>(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            if (i + 1 < s.Length && char.IsHighSurrogate(s[i]) && char.IsLowSurrogate(s[i + 1]))
            {
                l.Add(s.Substring(i, 2));
                i += 2;
            }
            else
            {
                l.Add(s[i].ToString());
                i++;
            }
        }
        return l;
    }

        public static void Install()
    {
        JLang.Glyphs = Glyphs;
        JLang.Metrics = Metrics;
    }

    static JText() => Install();
}
