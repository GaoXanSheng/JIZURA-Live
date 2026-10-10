// 移植自 frontend/src/engine/03_text.js
//
// 逐字排版（typesetLine/layoutText）、文本绘制（drawItem/drawItemLayered/fastRow）、
// 测量（measure/fitSize）、装饰数字识别（decoTextKind/hideDecoText）、文字填充图案
// （textPattern）、太さ（weightNow/varFontCSS）。
//
// JS 02_fonts.js 的 J.metrics / GlyphCache / J.fragments 按 README 对位表归入本文件。
// GlyphCache 按「移植约定 2」改为矢量路径版：
//   JS 版把字形按 alpha 掩膜（阈值 60）光栅化后做 8-连通域 BFS 拆笔画（morph/shatter 原料）；
//   C# 版先对整字形做同一光栅 + 同一 BFS（box/area/中心等 em 元数据公式与 JS 逐字一致），
//   再按「轮廓独立光栅的多数像素标签」把 SKFont.GetGlyphPath 的各 contour 归入连通域，
//   piece 直接持矢量 SKPath，分辨率无关。偏差（README 已批准）：光栅元数据逐字对齐，但
//   「一个 contour 的像素横跨两个连通域」的病态字形会整块画进多数侧 piece（JS 可再拆）；
//   算法保证同 seed 同输入产出同一部件集合。
//
// 其余对位：JS J.xxx → JText.Xxx；Canvas2D → SkiaSharp（fillText/textBaseline=middle →
// canvas.DrawText(x, y + 中线偏移, SKTextAlign.Center, font, paint)；ctx.filter blur 与
// shadowBlur → SKImageFilter.CreateBlur / CreateDropShadow，blur px ≈ 2σ）。
// env 的 inLayer/glyphLog/hideText/allowFilter 是渲染器内部标记、item 的 charFn/pieceFn
// 是函数值字段，JObject 装不下 —— 经 TextDrawOpts 传入（渲染器移植 09_render 时接线）。

using System.Collections;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine;

public static class JText
{
    // ---------------- J.PID / J.PT（piece 变换） ----------------
    // 已由 05_anim 移植方定型为 PieceXf / JAnim.Pid / JAnim.Pt（Env.cs 的动画管线桥同型）。
    // drawPieces 用 ReferenceEquals(t, JAnim.Pid) 识别静止位——同一哨兵必须全管线共用。

    // ---------------- 文字整列（typeset） ----------------

    /// <summary>J.TYPESET：planner/renderer 按方案设置（假名排紧、助词缩小、首字放大、拉丁稍大）。</summary>
    public static bool Typeset;

    /// <summary>J.setTypeset。整列开关影响 TypesetLine → 排版内容缓存键随代数失效（性能专项缓存）。</summary>
    public static void SetTypeset(bool on)
    {
        if (Typeset == on) return;
        Typeset = on;
        InvalidateLayoutCache();
    }

    // JS 源的字符类正则：HIRA /[ぁ-ゟ]/、KATA /[゠-ヿㇰ-ㇿ]/、KANJI /[㐀-鿿豈-﫿々〆]/、
    // LATIN /[A-Za-z0-9]/、/\s/（JS \s 的精确字符集）。与 J.cs 的 IsKanji 等 helper 范围
    // 略有出入（如 ヶ），为逐行对齐这里按 03_text 的正则单独实现。
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

    /// <summary>JS cls(ch)：'' | L(拉丁) | K(汉字) | T(片假名) | H(平假名) | S(空白) | P(其余)。</summary>
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

    /// <summary>J.typesetLine 每字记录：f = 字号系数，gap = 它前面多出的空隙（em），adv = 步进系数。</summary>
    public sealed class TypesetCell
    {
        public double F = 1, Gap, Adv = 1;
    }

    /// <summary>J.typesetLine。</summary>
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
            if (c is 'H' or 'T') o.Adv = J.IsSmallKana(ch[0]) ? 0.86 : ch == "ー" ? 0.94 : 0.9; // 假名排紧
            if (c == 'L') o.F = 1.08;
            if ((c is 'H' or 'T') && content >= 3 && IsParticle(arr, i)) o.F = 0.78;
            if (first && content >= 3 && c is 'K' or 'H' or 'T') o.F = 1.18; // 頭の字を大きく
            first = false;
            var pc = i > 0 ? Cls(arr[i - 1]) : '\0';
            if (i > 0 && pc != 'S' && (c == 'L') != (pc == 'L') && pc != '\0' && pc != 'P' && c != 'P') o.Gap = 0.2; // 英字と日本語の間
        }
        return outL;
    }

    // ---------------- 排版（layoutText） ----------------

    /// <summary>layoutText 的每个字形：中心相对 item 原点，未乘 sx/sy 的 item 空间。</summary>
    public sealed class GlyphCell
    {
        public string Ch = "";
        public int I, Li, Ci, N;
        public double X, Y, W, H;
        public bool R90;
        public double Vx, Vy, Fs;

        /// <summary>CellJson 的惰性缓存：字段全是排版期常量，charFn/pieceFn 只读不写（跨帧复用省每字 JObject 分配）。</summary>
        public JObject? Json;
    }

    /// <summary>J.layoutText 的返回：数组 + W/H/N 属性合一。</summary>
    public sealed class TextLayout
    {
        public List<GlyphCell> Cells = new();
        public double W, H;
        public int N;

        /// <summary>排版结果 → JS 数组同构 JObject：{N, W, H, cells:[…]}。JLayouts.TextMetrics.Lay（JObject 桩）回接用。</summary>
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

    /// <summary>J.layoutText。</summary>
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
                    // 大小不同的字形保持行基线
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
                // 竖排时 'left' 即顶对齐（JS 不处理 'right'，右对齐落到居中）
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

    // it._lay 对位：JObject 挂不上函数/对象值字段，用按对象弱引用的侧表缓存（随 item 回收）。
    private static readonly ConditionalWeakTable<JObject, StrongBox<TextLayout>> LayCache = new();

    // ---- 排版内容缓存（性能专项）----
    // 包侧逐帧 new JObject 构造 item（JS 字面量语义）→ 上面的按对象缓存逐帧全 miss，同一文本
    // 每帧整段重排版（LayoutText 全量码点+度量），glitchGrid/slotMachine 等格子版式实测是
    // cutpass 分配大头。这里按 LayoutText 的真实输入（text/font/size/track/lead/align/vertical）
    // 建 tuple 键缓存：命中零分配、值共享（GlyphCell 只读 + g.Json 幂等 memo，跨 item 共享安全——
    // streak/echo 副本的 ShareLay 早已是同一用法）。字体/语言/用户字体装载经 InvalidateLayoutCache 失效。
    // 注：sx/sy 不进键——LayoutText 计算后并未使用它们（缩放由 DrawItem 在绘制期施加），
    // 而很多版式的 sx 逐帧动画，进键会让这些 item 永远 miss。
    private static readonly Dictionary<(string Text, string? Font, long Size, long Track, long Lead,
        string? Align, bool Vert), TextLayout> LayContent = new();
    private static readonly Queue<(string Text, string? Font, long Size, long Track, long Lead,
        string? Align, bool Vert)> LayOrder = new(); // FIFO 驱逐序（保险丝走驱逐，不走 Clear——Clear 会让稳定键全量重排成突发）
    private static long _layGen;        // 失效代数：InvalidateLayoutCache 时 ++
    private static long _layCacheGen;   // 缓存当前对齐的代数
    private const int LayCap = 2048;

    /// <summary>性能诊断（探针用）：命中 / 未命中 / 驱逐 / 清空计数。</summary>
    public static (long Hit, long Miss, long Evict, long Clear) LayStats;

    private static long Bits(double v) => BitConverter.DoubleToInt64Bits(v);

    /// <summary>字体/语言变化后清排版内容缓存（JLang.SetLang / JFonts 装载路径调）。</summary>
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
                LayContent.Remove(LayOrder.Dequeue()); // 驱逐最旧（FIFO）：滚动文本流不无界，稳定键不被一次性清掉
                LayStats.Evict++;
            }
            LayContent[key] = lay;
            LayOrder.Enqueue(key);
        }
        else LayStats.Hit++;
        box.Value = lay;
        return lay;
    }

    /// <summary>JS 内层 item（Object.assign 克隆）共享同一 _lay 时用：把排版结果挂到克隆对象上。</summary>
    internal static void ShareLay(JObject target, TextLayout lay)
    {
        var box = LayCache.GetOrCreateValue(target);
        box.Value ??= lay;
    }

    private static double VAdv(string font, string ch, double size) =>
        HasAny(ch, ChLatin) ? Metrics.Adv(font, ch) * size : size;

    // ---------------- env 内部标记 / 函数值字段的载体 ----------------

    /// <summary>env.glyphLog 的条目（モーフ抓取：每个字形的落点，设备空间矩阵）。</summary>
    public sealed class GlyphLogEntry
    {
        public string Ch = "";

        /// <summary>设备空间矩阵 [a,b,c,d,e,f]（对齐 JS ctx.getTransform 的 [T.a,T.b,T.c,T.d,T.e,T.f]）。</summary>
        public float[] M = new float[6];

        /// <summary>ctx.font 对位（"{weight} {px}px {family},…"，varFont 时 weight 为生长后的字重）。</summary>
        public string Font = "";

        /// <summary>fontKey（drawItem 侧回填）：Skia 侧 FaceFont(fontKey, Px) 直接重建字体，免解析 CSS 串。</summary>
        public string? FontKey;

        public double Px;
        public string Color = "";
        public double A;
        public double Stroke;
        public string? StrokeColor;
        public bool Fill;
    }

    // charFn/pieceFn 的返回与委托类型已由 05_anim 移植方定型：
    //   CharXf（逐字覆盖）= JS charFn 返回的 duck-typed 对象；
    //   PieceXf + Pid/Pt  = JS J.PT/J.PID；
    //   JAnim.CharFn(i, g, n) / JAnim.PieceFn(ci, pj, pc, ox, oy, g)。
    // drawItem 从 env.CharFn / env.PieceFn 取（mainDraw 的 combineChar/combinePiece 产物）；
    // g 形参是 layoutText 的字形对象 —— 这里把 GlyphCell 折算成同字段 JObject 传入。
    // pieceFn 的 pc 形参 JS 侧是 piece 对象（11p 包读 pc.cx/cy/w/h），JAnim 定型为 int；
    // 精确形状走 PieceFnEx（TextDrawOpts 直传）。

    /// <summary>pieceFn 精确形状：pc 收整件 GlyphPiece（其余与 JAnim.PieceFn 同位）。</summary>
    public delegate PieceXf? PieceFnEx(int ci, int pj, GlyphPiece pc, double ox, double oy, JObject? g);

    /// <summary>GlyphCell → JS 字形对象同字段 JObject（charFn/pieceFn 的 g 形参）。</summary>
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

    /// <summary>env 的内部渲染标记（JS env.inLayer/hideText/allowFilter/glyphLog；09_render 移植时传入）。</summary>
    public sealed class TextDrawOpts
    {
        /// <summary>env.inLayer：正在离屏层里绘制（不再套层）。</summary>
        public bool InLayer;

        /// <summary>env.hideText：morph 抓取期间只记不画。</summary>
        public bool HideText;

        /// <summary>env.allowFilter：渲染器 filterOK &amp;&amp; !opt.fast。</summary>
        public bool AllowFilter = true;

        /// <summary>env.glyphLog：非 null 时抓取字形落点。</summary>
        public List<GlyphLogEntry>? GlyphLog;

        /// <summary>覆盖 env.CharFn（直调 drawItem、不走 mainDraw 时用）。</summary>
        public JAnim.CharFn? CharFn;

        /// <summary>覆盖 env.PieceFn。</summary>
        public JAnim.PieceFn? PieceFn;

        /// <summary>
        /// pieceFn 的精确形状：pc 收整件 GlyphPiece（JS 传 piece 对象，11p 包读 pc.cx/cy/w/h）。
        /// JAnim.PieceFn 的 pc 形参定型为 int 装不下 piece —— 两者都给时 Ex 优先。
        /// </summary>
        public PieceFnEx? PieceFnEx;

        /// <summary>
        /// 强制不走 piece 管线（JS 副本 item 的 pieceFn:null——drawFx 的 streak/echo 副本）。
        /// C# 的 pieceFn 挂在 env 上，副本无法置空，用本旗标压掉 env.PieceFn。
        /// </summary>
        public bool NoPieces;

        public static readonly TextDrawOpts Default = new();
    }

    // ---------------- 模糊/发光的离屏层（drawItemLayered） ----------------

    private static SKSurface? _layerCv;
    private static readonly object LayerLock = new();

    /// <summary>
    /// drawItemLayered：blur/shadow 的 item 只画一次到离屏层，整层做 blur/glow。
    /// 返回 false = JS 的 undefined（层太大画不下，走直画路径）；true 时 bb 可为 null（画了但不可见）。
    /// </summary>
    private static bool TryDrawItemLayered(Env env, JObject it, TextDrawOpts o, out BBox? bb)
    {
        bb = null;
        using var _ly = new Prof.Scope("item", "layered"); // 叶子归因（性能专项）
        var ctx = env.Ctx;
        var lay = GetLay(it);
        var size = Num(it, "size");
        var sx = ZeroTo(Num(it, "sx"), 1);
        var sy = ZeroTo(Num(it, "sy"), 1);
        // 全部字形的 item 本地包围盒，含每字偏移
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
        if (x0 > x1) return true; // JS: return null（画了但啥也没有）
        var sh = it["shadow"] as JObject;
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
        var k = Math.Max(0.05, Math.Sqrt(T.ScaleX * T.ScaleX + T.SkewY * T.SkewY)); // Math.hypot(T.a, T.b)
        var ow = (int)Math.Ceiling((x1 - x0) * k);
        var oh = (int)Math.Ceiling((y1 - y0) * k);
        var clipB = ctx.DeviceClipBounds;
        if (ow < 2 || oh < 2 || (double)ow * oh > clipB.Width * clipB.Height * 1.6)
            return false; // 层太大画不下，走直画路径
        SKSurface surface;
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
        ShareLay(inner, lay); // JS 的 Object.assign 克隆带走 _lay，同一排版结果
        var bbInner = DrawItem(CloneEnv(env, L, k), inner, new TextDrawOpts { InLayer = true, AllowFilter = o.AllowFilter });
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
            // ctx.shadowColor/Blur/Offset → 整层的 drop shadow（挂在 blur 结果上）；滤镜全走记忆工厂，不 Dispose
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
        bb.TakeGlyphsFrom(bbInner); // 紧凑搬运，不物化 JObject（消费方读 Boxes 时才物化）
        return true;
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

    // ---------------- 装饰性数字/时刻的识别 ----------------

    private static readonly Regex CjkRe =
        new("[\\u3040-\\u30ff\\u3400-\\u9fff\\uf900-\\ufaff\\uac00-\\ud7af]", RegexOptions.Compiled);

    private static readonly Regex TimeGate =
        new("\\d{1,2}[:：]\\d{2}", RegexOptions.ECMAScript | RegexOptions.Compiled);

    private static readonly Regex TimeShape =
        new("^[A-Za-z]{0,4}[\\s.#]*[\\d:：.;'\"\\s/\\-–—+]+[A-Za-z]{0,3}$", RegexOptions.ECMAScript | RegexOptions.Compiled);

    private static readonly Regex NoShape =
        new("^[A-Za-z#№.\\s\\d/\\-–—+×x%:,'°]+$", RegexOptions.ECMAScript | RegexOptions.Compiled);

    /// <summary>J.decoTextKind：'time' | 'no' | null。同样组合出现在切分歌词里时算歌词（上层 hideDecoText 判）。</summary>
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

    /// <summary>J.hideDecoText：编号/时刻装饰字按 fx.hideNo/hideTime 隐藏，但歌词里出现的同样组合保留。</summary>
    public static bool HideDecoText(Env env, string? text)
    {
        var fx = env.Fx;
        if (!fx.HideNo && !fx.HideTime) return false;
        var k = DecoTextKind(text);
        if (k == null || !(k == "no" ? fx.HideNo : fx.HideTime)) return false;
        var lyr = env.Cut != null
            ? !string.IsNullOrEmpty(env.Cut.LineText) ? env.Cut.LineText : env.Cut.Text ?? ""
            : "";
        return !lyr.Contains((text ?? "").Trim());
    }

    // ---------------- drawItem ----------------

    /// <summary>J.drawItem：逐字排版绘制 item，返回设计空间 bbox（JLayouts.BBox）。</summary>
    public static BBox? DrawItem(Env env, JObject it, TextDrawOpts? opts = null)
    {
        var o = opts ?? TextDrawOpts.Default;
        var ctx = env.Ctx;
        var ghostPass = env.GhostPass;
        if (ghostPass && IsFalse(it["ghost"])) return null;
        var text = TextOf(it, "text");
        var size = Num(it, "size");
        if (text.Length == 0 || size <= 0.5) return null;
        if (HideDecoText(env, text)) return null;
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
        // JS (it.alpha ?? 1) / (it.ghostAlpha ?? 1)：只把「缺字段」当 1——显式 0 就是 0（退场靠它让字幕离场）。
        // 不能用 ZeroTo：那是 JS `|| 1` 的语义，会把效果写入的 0 还原成 1（文字永不消失）。
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
        // item 未带 blend 时取 env.Blend（ghost pass 的整 pass multiply 对位；JS 的 ctx 状态机里
        // item blend 会整体替换 pass blend，两者语义一致）
        var blendMode = blend != null && BlendMap.TryGetValue(blend, out var bm) ? bm : env.Blend;
        // （ctx.filter 的 blur 对位：blur 走逐 paint 的 itemBlur ImageFilter，见字形循环）
        // 太さ（統一感）：歌词在可变字体（Noto Sans / Serif JP）上从细长长到粗
        var wg = env.Cut is { WeightGrow: true } && !Truthy(it["noWeight"]) ? WeightNow(env) : 0;
        var fontCss = wg > 0 ? VarFontCss(fontKey, size, wg) : JFonts.FontCss(fontKey, size);
        var font = FaceFont(fontKey, size, wg); // 缓存实例，禁 Dispose
        font.GetFontMetrics(out var fm);
        var yMid = (float)((-fm.Ascent - fm.Descent) / 2); // Canvas textBaseline='middle' 的基线偏移
        // 渐变/图案坐标在每字形自己的局部空间（Canvas fillStyle 与 Skia shader 都按绘制时的
        // 当前变换解释坐标），这里只建一次
        SKShader? gradShader = null;
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
        var fillA = JAnim.NumNull(it, "fillAlpha", 1); // JS (it.fillAlpha ?? 1)：0 即 0
        var shadow = !ghostPass ? it["shadow"] as JObject : null;
        double shBlur = 0, shDx = 0, shDy = 0;
        SKColor shColor = default;
        if (shadow != null)
        {
            var k0 = env.Scale != 0 ? env.Scale : 1;
            shColor = CssColor(Str(shadow, "color") ?? "rgba(0,0,0,0.6)");
            shBlur = o.AllowFilter ? Math.Max(Num(shadow, "blur"), 0) * k0 : 0; // allowFilter === false → blur 0
            shDx = Num(shadow, "dx") * k0;
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
        // 逐字盒走紧凑结构（AddGlyph）：每帧每字一个 JObject 曾是最大单点分配（cutpass ~400KB/帧）
        var bbOut = new BBox { Cx = Num(it, "x"), Cy = Num(it, "y") };
        double bx0 = 1e9, by0 = 1e9, bx1 = -1e9, by1 = -1e9;
        var escale = env.Scale != 0 ? env.Scale : 1;
        var pxScale = size * Math.Max(sx, sy) * escale;
        var itemBlur = Num(it, "blur") > 0.4 && o.AllowFilter ? Num(it, "blur") * escale : 0.0;
        var charFn = o.CharFn ?? env.CharFn;
        var pieceFn = o.NoPieces ? null : o.PieceFn ?? env.PieceFn;
        var pieceFnEx = o.PieceFnEx;
        using var paint = new SKPaint { IsAntialias = true };
        // 逐字滤镜走 JText 共享记忆工厂（性能专项）：同参跨 item/跨帧复用，命中零新建零终结，也不再有
        // 「每 item 一档、档间 Dispose」的机器。滤镜是共享只读资源，此处禁 Dispose。
        foreach (var g in lay.Cells)
        {
            if (g.Ch is " " or "　") continue;
            var gJ = charFn != null || pieceFn != null ? (g.Json ??= CellJson(g)) : null;
            var c = charFn?.Invoke(g.I, gJ, lay.N);
            if (c != null && c.Hide) continue;
            var a = baseAlpha * (c?.A ?? 1);
            if (a <= 0.002) continue;
            var ch = c?.Ch ?? g.Ch;
            var cs = (c?.S ?? 1) * (g.Fs != 0 ? g.Fs : 1);
            var gx = g.X * sx + g.Vx * sx + (c?.Dx ?? 0);
            var gy = g.Y * sy + g.Vy * sy + (c?.Dy ?? 0);
            var crot = (c?.Rot ?? 0) + (g.R90 ? 90 : 0);
            var csx = sx * cs * TrueOr1(c?.Sx);
            var csy = sy * cs * TrueOr1(c?.Sy);
            var gcol = !ghostPass && !string.IsNullOrEmpty(c?.Color) ? c!.Color! : col;
            // bbox 与 layered 路径同口径：计入 per-glyph 缩放 c.sx/c.sy，否则压扁/拉伸入场时 decor 锚定偏小
            var bw = g.W * sx * cs / (g.Fs != 0 ? g.Fs : 1) * AbsOr1(c?.Sx);
            var bh = g.H * sy * cs / (g.Fs != 0 ? g.Fs : 1) * AbsOr1(c?.Sy);
            bbOut.AddGlyph(gx, gy, bw, bh);
            bx0 = Math.Min(bx0, gx - bw / 2);
            bx1 = Math.Max(bx1, gx + bw / 2);
            by0 = Math.Min(by0, gy - bh / 2);
            by1 = Math.Max(by1, gy + bh / 2);
            // モーフ：记录每个字形的落点（设备空间）/ morph 绘制期间本处先不出字形
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
            if (c?.ClipY != null || c?.ClipX != null) // 每字形遮罩，字形盒子的比例值（中心为 0）
            {
                var cy = c.ClipY ?? new[] { -0.7, 0.7 };
                var cx = c.ClipX ?? new[] { -0.7, 0.7 };
                ctx.ClipRect(new SKRect(
                    (float)(cx[0] * g.W), (float)(cy[0] * g.H),
                    (float)(cx[0] * g.W + (cx[1] - cx[0]) * g.W),
                    (float)(cy[0] * g.H + (cy[1] - cy[0]) * g.H)));
            }
            // ctx.filter 对位：逐字 blur 优先（JS 里 ctx.filter 赋值会整体替换）；滤镜来自共享记忆工厂
            var glyphBlur = c is { Blur: > 0.4 } && o.AllowFilter ? c.Blur.Value * escale : itemBlur;
            var withShadow = shadow != null
                ? DropShadowFilter(shDx, shDy, shBlur, shColor, glyphBlur > 0 ? glyphBlur : 0)
                : glyphBlur > 0 ? BlurFilter(glyphBlur) : null;
            paint.ImageFilter = withShadow;
            paint.BlendMode = blendMode;
            if (ext != null && !outlineOnly)
            {
                var extCol = CssColor(Str(ext, "color") ?? "#000");
                var ea = JAnim.NumNull(ext, "a", 1); // JS (ext.a ?? 1)：0 即 0
                var fade = Truthy(ext["fade"]);
                for (var k2 = extN; k2 >= 1; k2--)
                {
                    paint.Shader = null;
                    paint.Color = extCol.WithAlpha((byte)Math.Round(
                        J.Clamp(a * ea * (fade ? 1 - (k2 - 1) / (double)extN * 0.85 : 1)) * 255));
                    ctx.DrawText(ch, (float)(Num(ext, "dx") * k2 / extN / csx), (float)(yMid + Num(ext, "dy") * k2 / extN / csy),
                        SKTextAlign.Center, font, paint);
                }
            }
            if (fill && !outlineOnly && fillA > 0.002 && dash == null)
            {
                paint.Shader = gradShader;
                paint.Color = gradShader != null
                    ? CssColor(gcol, a * fillA) // shader 乘色用 alpha（Canvas: fillStyle=pattern + globalAlpha）
                    : CssColor(gcol, a * fillA);
                ctx.DrawText(ch, 0, yMid, SKTextAlign.Center, font, paint);
                paint.Shader = null;
            }
            if (stroke > 0 || outlineOnly || dash != null)
            {
                // 描边时 Canvas 会把 shadow 清掉（仅 fill 同时存在时）
                var strokeF = shadow != null && fill ? null : withShadow;
                paint.ImageFilter = strokeF;
                paint.StrokeJoin = SKStrokeJoin.Round;
                paint.StrokeMiter = 2;
                // c.sx/c.sy 可为 0 而 alpha>0：除零会得到 Infinity 描边（Canvas 会静默丢弃）
                paint.StrokeWidth = (float)((stroke > 0 ? stroke : Math.Max(1, size * 0.02))
                                            / Math.Max(0.05, Math.Sqrt(Math.Abs(csx * csy))));
                paint.Color = CssColor(!ghostPass && !string.IsNullOrEmpty(c?.Color) ? c!.Color! : sCol, a);
                if (dash != null)
                {
                    var L = size * 3.2;
                    paint.PathEffect = SKPathEffect.CreateDash(new[] { (float)Math.Max(0.01, L * dash.Value), (float)L }, 0);
                }
                else if (strokeDashArr != null) paint.PathEffect = SKPathEffect.CreateDash(strokeDashArr, 0);
                ctx.DrawText(ch, 0, yMid, SKTextAlign.Center, font, paint);
                paint.PathEffect?.Dispose();
                paint.PathEffect = null;
                if (fill && !outlineOnly && (Truthy(it["strokeUnder"]) || (dash != null && fillA > 0.002)))
                {
                    paint.Shader = gradShader;
                    paint.Color = CssColor(gcol, a * (dash != null ? fillA : 1));
                    ctx.DrawText(ch, 0, yMid, SKTextAlign.Center, font, paint);
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
        // 设计空间 bbox（旋转只算平移）
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

    // ---------------- 密排整行快绘（fastRow） ----------------

    /// <summary>
    /// J.fastRow：密排小字一次画一行，省掉逐字 draw；只该在 main pass 用，sp 为每字额外间距 px。
    /// Skia 无 ctx.letterSpacing：按 CSS 语义逐字排（每字后加 sp、对齐宽度含末尾 sp）——
    /// 与 JS 的 letterSpacing 分支输出一致（JS 无该能力时的 env.draw 退路不再需要）。
    /// </summary>
    public static void FastRow(Env env, string? text, string font, double size, double x, double y, double sp,
        string? color, double alpha, string align = "left")
    {
        if (env.Pass != "main" || alpha <= 0.01 || string.IsNullOrEmpty(text) || HideDecoText(env, text)) return;
        var ctx = env.Ctx;
        var cps = CodePoints(text);
        // 逐字取脸（缺字回退）：整行宽度按逐字量宽累加，与绘制用同一张脸，混合字形也不会错位
        double total = sp * cps.Count;
        foreach (var cp in cps) total += FaceFontFor(font, cp, size).MeasureText(cp);
        var cx = align == "left" ? x : align == "right" ? x - total : x - total / 2;
        ctx.Save();
        using var p = new SKPaint { IsAntialias = true, Color = CssColor(color, alpha) };
        foreach (var cp in cps)
        {
            var f = FaceFontFor(font, cp, size); // 缓存实例，禁 Dispose
            f.GetFontMetrics(out var fm);
            var yMid = (float)((-fm.Ascent - fm.Descent) / 2);
            ctx.DrawText(cp, (float)cx, (float)y + yMid, SKTextAlign.Left, f, p);
            cx += f.MeasureText(cp) + sp;
        }
        ctx.Restore();
    }

    // ---------------- 太さ（weightGrow） ----------------

    /// <summary>J.weightNow：当前 cut 的生长度 0..1（永不到 0，非 0 同时表示"开"）；eased，前半段走完。</summary>
    public static double WeightNow(Env env)
    {
        var cut = env.Cut!;
        var span = Math.Max(0.5, Math.Min(1.4, cut.Dur * 0.5));
        var baseT = env.Ltb != 0 ? env.Ltb : env.Lt; // (env.ltb ?? env.lt) || 0 的 C# 对位
        var k = J.Clamp(baseT / span);
        return 1e-3 + (1 - Math.Pow(1 - k, 3)) * (1 - 1e-3);
    }

    /// <summary>J.varFontCSS：太さ用可变字体的 CSS font 串（glyphLog 里记录用）。</summary>
    public static string VarFontCss(string key, double px, double e)
    {
        var f = JLang.FaceOf(key);
        var serif = f.Kind == "mincho";
        var w = (int)Math.Round(serif ? 200 + e * 700 : 100 + e * 800);
        return string.Create(CultureInfo.InvariantCulture,
            $"{w} {px.ToString("F2", CultureInfo.InvariantCulture)}px {(serif ? "\"Noto Serif JP\"" : "\"Noto Sans JP\"")},{(string.IsNullOrEmpty(f.Fb) ? "sans-serif" : f.Fb)}");
    }

    // ---------------- piece 绘制（drawPieces / fragList） ----------------

    /// <summary>返回 true 表示 pieces 画了（不在静止位）。Ex 优先（pc 收 GlyphPiece）；否则走 JAnim.PieceFn（pc 形参传 0，JS 传 piece 对象）。</summary>
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
            var ey = p.Cy * size * sy; // piece 中心偏移（em→px，乘缩放）
            var ox = gx + ex * cr - ey * sr;
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
                // fragment：先按多边形裁剪（em 坐标，绕 fragment 中心），再画母 piece 的矢量路径
                //（JS 画染色 sprite 位图 + drawImage 偏移；矢量版等价于把母路径平移到 fragment 中心）
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

    /// <summary>fragList：按 seed 键控的碎片列表（glyph 缓存共享，不同碎裂 cut 的碎片图案各自独立）。</summary>
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

    // ---------------- 文字填充图案（textPattern） ----------------

    private static readonly Dictionary<string, SKBitmap> PatCache = new();

    /// <summary>J.textPattern：'dots' | 'stripes' | 'hatch' | 'grid' | 'lines'，格子大小跟随字号。
    /// CanvasPattern → SKShader（位图瓦片 repeat + 1/scale 矩阵）。</summary>
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
        } // 'lines'
        return bmp;
    }

    // ---------------- 测量（measure / fitSize） ----------------

    /// <summary>J.measure 的返回 {w, h, lay}。</summary>
    public sealed class MeasureResult
    {
        public double W, H;
        public TextLayout Lay = null!;
    }

    // 测量 memo：版式每帧每 pass 重复 measure/fitSize（fitSize 的探测也走这里），结果
    // 纯数值 + lay 只读，可跨 item/跨帧共享。哨兵挂在 metrics 表上：字体装载后
    // metrics.Clear() 哨兵消失，整表作废（同 11p_layoutsA 的 SENT 模式）。
    private static readonly Dictionary<string, MeasureResult> MMemo = new();
    private static readonly Queue<string> MMemoOrder = new();
    private const string MSENT = "\u0001measure";

    private static string MKey(JObject it) =>
        TextOf(it, "text") + "\u0002" + (Str(it, "font") ?? "undefined") + "\u0002" + NumStr(Num(it, "size")) + "\u0002" +
        NumStr(Num(it, "track")) + "\u0002" + NumStr(ZeroTo(Num(it, "lead"), 1.3)) + "\u0002" + NumStr(ZeroTo(Num(it, "sx"), 1)) + "\u0002" +
        NumStr(ZeroTo(Num(it, "sy"), 1)) + "\u0002" + (Str(it, "align") ?? "") + "\u0002" + (Truthy(it["vertical"]) ? 1 : 0) +
        "\u0002" + (Typeset ? 1 : 0);

    private static string NumStr(double v) => v.ToString(CultureInfo.InvariantCulture);

    /// <summary>J.measure。</summary>
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

    /// <summary>J.fitSize：求正好装进盒子的字号。</summary>
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

    // ---------------- J.metrics（02_fonts.js → 本文件） ----------------

    /// <summary>
    /// J.metrics：单字 advance（em 计）缓存。JLang.Metrics 登记本实例，SetLang/EnsureFonts 清缓存
    /// （对齐 JS 的 if (J.metrics) J.metrics.clear()）。
    /// </summary>
    public sealed class MetricsCache : IDictionary
    {
        /// <summary>metrics.m：fontKey\0ch → em advance；MSENT 哨兵也住这里。</summary>
        public readonly Hashtable M = new();

        /// <summary>J.metrics.adv：步进，em 计。JS 用 100px measureText/100；空字形兜底 空格 0.3 / 其他 1。
        /// 逐字取脸（FaceFontFor）：主脸缺这个字时用能画它的脸量，宽度才与画出来的一致。</summary>
        public double Adv(string fontKey, string ch)
        {
            var k = fontKey + '\u0000' + ch;
            if (M[k] is double v) return v;
            var font = FaceFontFor(fontKey, ch, 100); // 缓存实例，禁 Dispose
            var r = (double)font.MeasureText(ch) / 100;
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

    // ---------------- GlyphCache（02_fonts.js → 本文件，矢量路径版） ----------------

    /// <summary>连通域 piece：矢量版持 SKPath（tile 坐标，字形已按 res 居中摆放）。
    /// cx/cy/w/h/area 与 JS 同式（来自光栅 box 的 em 换算）；fragment 件另持 Src/Poly/Fx/Fy。</summary>
    public sealed class GlyphPiece
    {
        public int Id;
        public SKPath Path = new();

        /// <summary>shatter fragment：母 piece。</summary>
        public GlyphPiece? Src;

        /// <summary>shatter fragment：剪裁多边形（局部 em 坐标、绕 fragment 中心）。</summary>
        public double[][]? Poly;
        public double Fx, Fy; // fragment 中心（em，相对母 piece 中心）
        public int Res;

        /// <summary>中心与尺寸以 em 计，相对字形中心。</summary>
        public double Cx, Cy, W, H, Area;

        /// <summary>tile 坐标里 piece 盒中心 ((x0+x1+1)/2, (y0+y1+1)/2)，画 piece 时平移到原点用。</summary>
        internal double CenterX, CenterY;

        /// <summary>J.fragments 的按 seed 缓存（挂在 piece 上，对齐 JS 的 pc.frags）。</summary>
        internal Dictionary<int, List<PolyFragment>>? Frags;
    }

    /// <summary>decompose 产物：一个 (font, ch, res) 的全部笔画部件。</summary>
    public sealed class GlyphInfo
    {
        public string Ch = "";
        public int Res;
        public List<GlyphPiece> Pieces = new();

        /// <summary>fragList 的按 seed 缓存（对齐 JS 的 glyph.frags）。</summary>
        internal Dictionary<int, List<GlyphPiece>>? FragLists;

        public void Dispose()
        {
            foreach (var p in Pieces) p.Path.Dispose();
            Pieces.Clear();
            FragLists?.Clear();
        }
    }

    /// <summary>J.glyphs：矢量字形缓存。JLang.Glyphs 登记本实例（SetLang/EnsureFonts/用户字体装载清缓存）。</summary>
    public sealed class GlyphCache : IDictionary
    {
        public const int MaxRes = 512;

        /// <summary>glyphs.map：fontKey|ch|res → GlyphInfo。</summary>
        public readonly Hashtable Map = new();

        public void Clear()
        {
            foreach (var g in Map.Values.OfType<GlyphInfo>()) g.Dispose();
            Map.Clear();
        }

        /// <summary>glyphs.bucket：分辨率档（64 起、×2 到 maxRes）。</summary>
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
            // JS 删最旧 600 条（Map 插入序）；Hashtable 是散列序，淘汰条目可能不同，仅影响内存占用
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

    /// <summary>JS decompose：字形 → 笔画部件。矢量版：光栅连通域产元数据（同 JS 公式），路径按多数标签归属。</summary>
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

    /// <summary>
    /// decompose 的线程本地暂存区（性能专项）。字形分解一次要开 L/L2/stack/boxes/掩膜/光栅位图等
    /// 多块 S×S 级缓冲：res=512 时 S=743，单块 int[N] 就是 2.2MB，整趟（含逐轮廓 RasterAlpha）
    /// 实测 60MB+ —— 全是 LOH 分配，播放时每遇到一个新字形就灌一轮 gen2（实测停顿 0.8-4.3s，
    /// 表现就是「界面一卡一卡」+ 常驻内存涨到 GB 级）。
    /// 渲染全程过 EngineRenderGate 串行，同线程重入只可能来自嵌套调用，用 InUse 兜住
    /// （嵌套时退回局部新分配，正确性优先于省内存）。
    /// </summary>
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

        /// <summary>按需扩容（只在不够长时新开，够长就原地复用）。</summary>
        public static void Grow<T>(ref T[] a, int n)
        {
            if (a.Length < n) a = new T[n];
        }

        /// <summary>字形级掩膜（整趟 decompose 都持用，与逐轮廓掩膜分开两块）。</summary>
        public byte[] RentGlyphMask(int n)
        {
            Grow(ref MaskGlyph, n);
            return MaskGlyph;
        }

        /// <summary>轮廓级掩膜（逐个 contour 复用同一块）。</summary>
        public byte[] RentContourMask(int n)
        {
            Grow(ref MaskContour, n);
            return MaskContour;
        }

        /// <summary>光栅用位图（按 S 复用；原来每次 RasterAlpha 都新建一块再整块拷出来）。</summary>
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
        var font = FaceFontFor(fontKey, ch, res); // 逐字取脸：缺字时用能画它的脸（缓存实例，禁 Dispose）
        font.GetFontMetrics(out var fm);
        var yMid = (float)((-fm.Ascent - fm.Descent) / 2);
        // 整字形光栅：等价 JS 的 fillText(ch, half, half)（center/middle 对齐）
        var adv = (double)font.MeasureText(ch);
        var gid = font.GetGlyph(CodePointOf(ch));
        using var rawPath = font.GetGlyphPath(gid);
        if (rawPath.IsEmpty) return outG;
        using var glyphPath = new SKPath(rawPath);
        glyphPath.Transform(SKMatrix.CreateTranslation((float)(half - adv / 2), (float)(half + yMid)));
        var A = RasterAlpha(glyphPath, S, s.RentGlyphMask(S * S), s); // alpha 掩膜（JS 的 A）
        var N = S * S;
        DecomposeScratch.Grow(ref s.L, N);
        DecomposeScratch.Grow(ref s.L2, N);
        DecomposeScratch.Grow(ref s.Stack, N);
        var L = s.L;
        var L2 = s.L2;
        var stack = s.Stack;
        Array.Clear(L, 0, N); // 暂存区复用：L 必须从全 0 起（下面用 L[i] != 0 判「已访问」）
        const int TH = 60;
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
        // 把抗锯齿的边缘像素贴给相邻标签（两轮膨胀）。L2 是暂存区里另一块等长数组：
        // 原来是 L.Clone()（每轮再开 2.2MB），现在改成拷进 L2 + 换引用，两块轮转
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
            (L, L2) = (L2, L); // 换引用轮转：两块都在暂存区里，下一轮把新标签再拷回去
        }
        // 小斑点并入最近的大块
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
        // 矢量归属：每个 contour 独立光栅，按像素标签多数归组（TH 以上算票；整轮廓全为
        // 抗锯齿时放宽到任意 alpha —— 保证极细笔画不丢）
        var contours = SplitContours(glyphPath);
        DecomposeScratch.Grow(ref s.Paths, nl + 1);
        var paths = s.Paths;
        Array.Clear(paths, 0, nl + 1); // 暂存区复用：paths[target] 靠 == null 判「还没建」
        foreach (var cont in contours)
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
            paths[target].AddPath(cont); // AddPath 拷贝动词，源随后可弃
            cont.Dispose();
        }
        // pieces：与 JS 同式（em 元数据来自光栅 box；几何为该组 contour 的矢量并集）
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
        // pieces.sort((a,b) => b.area - a.area)：OrderByDescending 稳定，等面积保持序
        outG.Pieces = outG.Pieces.OrderByDescending(p => p.Area).ToList();
        return outG;
    }

    /// <summary>
    /// SKPath → S×S alpha 掩膜（白字黑底，取 B 通道等价的 A）。位图与掩膜都走暂存区复用：
    /// 原来是「新建 SKBitmap + bmp.Bytes（S×S×4 整块托管拷贝）+ new byte[S×S]」×（1 + 轮廓数），
    /// 一个多笔画的 CJK 字形就能灌出几十 MB LOH。
    /// </summary>
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

    /// <summary>字形路径按 MoveTo 切轮廓（每 contour 一个 SKPath，tile 坐标）。</summary>
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

    // ---------------- 多边形碎裂（J.fragments，02_fonts.js → 本文件） ----------------

    /// <summary>J.fragments 的碎片：poly 局部 em 坐标、绕 (cx, cy)。</summary>
    public sealed class PolyFragment
    {
        public double[][] Poly = Array.Empty<double[]>();
        public double Cx, Cy;
    }

    /// <summary>J.fragments：把 piece 切成最多 3 个凸碎片（shatter 用）。
    /// 缓存按 seed 键控：glyph 由 GlyphCache 按 font|ch|res 共享，不同碎裂 cut 的 seed 不同。
    /// 随机走 J.Rs（mulberry 哈希），与 JS 位级一致。</summary>
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

    // ---------------- 字体解析 ----------------

    private static SKTypeface ResolveTypeface(string familyQuoted, int weight) => ResolveTypefaceChain(new[] { familyQuoted }, weight);

    // 解析集中在 JFonts（用户字体 → 字体目录 → 系统族 → 语言×气质系统栈 → 逐字兜底）：
    // 这里只保留薄封装，字脸缓存与失效规则都在 JFonts 侧统一管理（此前两处各有一份判定）。
    private static SKTypeface ResolveTypefaceChain(IEnumerable<string> families, int weight) =>
        JFonts.ResolveChain(families, weight);

    /// <summary>ctx.font 对位：fontKey（或太さ wg）+ size → SKFont。LinearMetrics 保证排版数值与字号线性。
    /// internal：Renderer.drawMorph 从 GlyphLogEntry.FontKey/Px 重建字体。
    /// 性能：返回实例缓存复用（每帧每 item 新建会把 SKFont 灌满 finalizer 队列），调用方不得 Dispose。
    /// 只缓存 wg==0（weightGrow 的字重连续变化，键基数无界）；用户字体重登记时经 OnFontsChanged 清表。
    /// Hashtable 与 Metrics/Glyphs 同款：读免锁（预览泵/导出可能双线程并发渲染）。</summary>
    private static readonly Hashtable FontJobs = new();

    /// <summary>字体目录变化（AddUserFont/EnsureFonts）后清 SKFont 缓存；typeface 表（FaceJobs）同语义不动。</summary>
    internal static void OnFontsChanged()
    {
        ClearFontJobs();
        InvalidateLayoutCache(); // 排版内容缓存随字体度量失效（性能专项缓存）
    }

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
            // 键用 R 格式避免不同来源的 size 字符串化差异；容量护栏防 fitSize 探测类长尾撑爆
            var key = string.Create(CultureInfo.InvariantCulture, $"{fontKey}\u0001{size.ToString("R", CultureInfo.InvariantCulture)}");
            if (FontJobs[key] is SKFont hit) return hit;
            var nf = BuildFont(fontKey, size, 0);
            if (FontJobs.Count > 4096) ClearFontJobs();
            FontJobs[key] = nf;
            return nf;
        }

        // wg > 0: 量化到 20 档（0.05 步长），使动态连续变化的 wg 能够命中缓存，杜绝逐帧 new SKFont 灌死 finalizer 队列
        var qWg = Math.Round(wg * 20.0) / 20.0;
        var qKey = string.Create(CultureInfo.InvariantCulture, $"{fontKey}\u0001{size.ToString("R", CultureInfo.InvariantCulture)}\u0001{qWg.ToString("F2", CultureInfo.InvariantCulture)}");
        if (FontJobs[qKey] is SKFont qHit) return qHit;
        var qNf = BuildFont(fontKey, size, qWg);
        if (FontJobs.Count > 4096) ClearFontJobs();
        FontJobs[qKey] = qNf;
        return qNf;
    }

    /// <summary>
    /// 逐字取字脸：主脸能画这个字就用主脸，否则问 JFonts 要一张能画的（气质栈/系统逐字匹配）。
    /// 中文歌词里混的拉丁字母、日文歌词里的简体字、mono 键下的汉字都靠它，避免 .notdef 方块。
    /// 返回实例缓存复用（键含码点与字号），调用方不得 Dispose。
    /// </summary>
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
            // varFontCSS 的 CSS 链：Noto Sans/Serif JP + f.fb + 气质系统栈
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

    /// <summary>CSS fallback 串（"\"A\",\"B\",sans-serif"）→ family 列表。</summary>
    private static IEnumerable<string> FbFamilies(string fb) =>
        (fb ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0);

    // ---------------- 小助手 ----------------

    // ---------------- 原生滤镜记忆（性能专项） ----------------
    // 逐帧新建的 SKImageFilter 全部堆在 finalizer 队列上：dotnet-trace 采样实测终结器线程
    // SKImageFilter.Dispose 独占全进程 CPU 12%（≈13s / 40s 采样窗口，全部渲染工作才 37s）。
    // 参数相同的滤镜在这里复用：命中零新建、零终结。键含全部创建参数（σ 直给 / px 两族以 Kind 区分）。
    // [ThreadStatic]：与 Env.SharedPaint/SharedPath 同款，渲染单线程与测试并行都不跨线程共享。
    // 铁律：记忆滤镜是共享只读资源，任何调用方不得 Dispose；动画参数（σ 逐帧变化）由容量保险丝兜底。
    [ThreadStatic] private static Dictionary<(int Kind, float A, float B, float C, float D, uint Col, float Aux), SKImageFilter>? _fxMemo;

    /// <summary>Canvas2D blur(r)/shadowBlur ≈ 2σ：px → SKImageFilter blur sigma（记忆复用）。internal：Renderer 复用同一换算。</summary>
    internal static SKImageFilter? BlurFilter(double blurPx)
    {
        if (blurPx <= 0) return null;
        var s = (float)(blurPx * 0.5);
        var map = _fxMemo ??= new Dictionary<(int, float, float, float, float, uint, float), SKImageFilter>();
        var key = (0, s, s, 0f, 0f, 0u, 0f);
        if (map.TryGetValue(key, out var hit)) return hit;
        if (map.Count > 400) map.Clear(); // 容量保险丝：动画参数不无界增长
        var f = SKImageFilter.CreateBlur(s, s);
        map[key] = f;
        return f;
    }

    /// <summary>σ 直给的 drop shadow（记忆复用）：JLayouts/P11 各包的 ctx.shadowColor/shadowBlur 对位。</summary>
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

    /// <summary>px 口径的 drop shadow（σ=px/2，记忆复用）；baseBlurPx>0 时挂在同参数模糊结果上（链内引用保活）。</summary>
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

    /// <summary>#hex / rgb() / rgba() / transparent → SKColor（styles 侧都是 hex；shadow 缺省是 rgba）。</summary>
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

    /// <summary>String(it.x ?? '')：数值/布尔也转串。</summary>
    private static string TextOf(JObject o, string k)
    {
        var v = o[k];
        if (v == null || v.Type == JTokenType.Null) return "";
        return v.Type == JTokenType.String ? (string)v! : v.ToString();
    }

    /// <summary>JS 真值（[]/{} 为真；0/NaN/''/null 为假）。</summary>
    private static bool Truthy(JToken? t) => t switch
    {
        null => false,
        JValue { Type: JTokenType.Null } => false,
        JValue { Type: JTokenType.Boolean } b => (bool)b.Value!,
        JValue { Type: JTokenType.Integer } i => Convert.ToInt64(((JValue)i).Value, CultureInfo.InvariantCulture) != 0,
        JValue { Type: JTokenType.Float } f => !double.IsNaN(Convert.ToDouble(((JValue)f).Value, CultureInfo.InvariantCulture))
                                               && Convert.ToDouble(((JValue)f).Value, CultureInfo.InvariantCulture) != 0,
        JValue { Type: JTokenType.String } s => ((string?)((JValue)s).Value)?.Length > 0,
        _ => true // JArray/JObject：JS 恒真
    };

    /// <summary>=== false 判定（仅显式 false 为假）。</summary>
    private static bool IsFalse(JToken? t) => t is JValue { Type: JTokenType.Boolean } b && !(bool)b.Value!;

    /// <summary>JS (v || 1)：null/0 → 1。</summary>
    private static double ZeroTo(double v, double def) => v == 0 ? def : v;

    /// <summary>JS (c &amp;&amp; c.sx ? c.sx : 1)：null/0 → 1。</summary>
    private static double TrueOr1(double? v) => v != null && v.Value != 0 ? v.Value : 1;

    /// <summary>JS (c && c.sx ? Math.abs(c.sx) : 1)。</summary>
    private static double AbsOr1(double? v) => v != null && v.Value != 0 ? Math.Abs(v.Value) : 1;

    /// <summary>[...String(s)]：按码点切（含增补平面），每码点一个 string。</summary>
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

    // ---------------- 装配 ----------------

    /// <summary>幂等初始化：把缓存登记到 JLang.Glyphs/Metrics（SetLang/EnsureFonts/AddUserFont 经此清缓存，
    /// 对齐 JS 的 if (J.glyphs) J.glyphs.clear()）。静态构造器即调用（JS 是载入即建）。</summary>
    public static void Install()
    {
        JLang.Glyphs = Glyphs;
        JLang.Metrics = Metrics;
    }

    static JText() => Install();
}
