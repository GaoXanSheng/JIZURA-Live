using System.Globalization;
using System.Runtime.CompilerServices;

namespace JizuraLive.LegacyEngine;

public static class J
{
    public const double TAU = Math.PI * 2;
    public const double DEG = Math.PI / 180;

    public static double Clamp(double x, double a = 0, double b = 1) => x < a ? a : x > b ? b : x;
    public static int Clamp(int x, int a, int b) => x < a ? a : x > b ? b : x;
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;
    public static double Inv(double a, double b, double x) => b == a ? 0 : (x - a) / (b - a);
    public static double Smooth(double a, double b, double x)
    {
        var t = Clamp(Inv(a, b, x));
        return t * t * (3 - 2 * t);
    }

    public static class E
    {
        public static double Lin(double x) => Clamp(x);
        public static double InQuad(double x) { x = Clamp(x); return x * x; }
        public static double OutQuad(double x) { x = Clamp(x); return 1 - (1 - x) * (1 - x); }
        public static double InCubic(double x) { x = Clamp(x); return x * x * x; }
        public static double OutCubic(double x) { x = Clamp(x); return 1 - Math.Pow(1 - x, 3); }
        public static double InOutCubic(double x)
        {
            x = Clamp(x);
            return x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
        }
        public static double OutExpo(double x) { x = Clamp(x); return x >= 1 ? 1 : 1 - Math.Pow(2, -10 * x); }
        public static double InExpo(double x) { x = Clamp(x); return x <= 0 ? 0 : Math.Pow(2, 10 * x - 10); }
        public static double InOutExpo(double x)
        {
            if (x <= 0 || x >= 1) return x;
            return x < 0.5 ? Math.Pow(2, 20 * x - 10) / 2 : (2 - Math.Pow(2, -20 * x + 10)) / 2;
        }
        public static double OutBack(double x, double s = 1.9)
        {
            x = Clamp(x);
            var c = s + 1;
            return 1 + c * Math.Pow(x - 1, 3) + s * Math.Pow(x - 1, 2);
        }
        public static double OutElastic(double x)
        {
            x = Clamp(x);
            if (x == 0 || x == 1) return x;
            return Math.Pow(2, -10 * x) * Math.Sin((x * 10 - 0.75) * (TAU / 3)) + 1;
        }
        public static double InOutSine(double x)
        {
            x = Clamp(x);
            return -(Math.Cos(Math.PI * x) - 1) / 2;
        }
    }

    private static readonly Dictionary<string, uint> SidCache = new();

        public static uint Sid(string s)
    {
        lock (SidCache)
        {
            if (SidCache.TryGetValue(s, out var v)) return v;
        }

        uint h = 2166136261;
        for (var i = 0; i < s.Length; i++)
        {
            h ^= s[i];
            h = unchecked(h * 16777619);
        }

        lock (SidCache)
        {
            SidCache[s] = h;
        }
        return h;
    }

        public static uint H(int a, int b = 0, int c = 0, int d = 0, int e = 0)
    {
        unchecked
        {
            var h = (int)(0x9e3779b9 ^ a);
            h = Imul(h ^ (int)((uint)h >> 16), unchecked((int)0x85ebca6b));
            h += Imul((int)((uint)b + 0x632be5ab), unchecked((int)0xc2b2ae35));
            h = Imul(h ^ (int)((uint)h >> 13), unchecked((int)0xc2b2ae35));
            h += Imul((int)((uint)c + 0x5bd1e995), unchecked((int)0x27d4eb2f));
            h = Imul(h ^ (int)((uint)h >> 15), unchecked((int)0x165667b1));
            h += Imul((int)((uint)d + 0x1b873593), unchecked((int)0x85ebca6b));
            h = Imul(h ^ (int)((uint)h >> 16), unchecked((int)0x27d4eb2f));
            h += Imul((int)((uint)e + 0x68e31da4), unchecked((int)0x9e3779b1));
            h ^= (int)((uint)h >> 15);
            h = Imul(h, unchecked((int)0x2c1b3c6d));
            h ^= (int)((uint)h >> 12);
            h = Imul(h, unchecked((int)0x297a2d39));
            h ^= (int)((uint)h >> 15);
            return (uint)h;
        }
    }

    private static int Imul(int a, int b) => unchecked(a * b);

    public static double R(int a, int b = 0, int c = 0, int d = 0, int e = 0) => H(a, b, c, d, e) / 4294967296.0;
    public static double Rs(int a, int b = 0, int c = 0, int d = 0, int e = 0) => R(a, b, c, d, e) * 2 - 1;
    public static double Rr(double lo, double hi, int a, int b = 0, int c = 0, int d = 0, int e = 0)
        => lo + (hi - lo) * R(a, b, c, d, e);

        public static Rng Rng(uint seed) => new(seed);

        public static double Noise1(double x, int seed = 0)
    {
        var i = Math.Floor(x);
        var f = x - i;
        var u = f * f * (3 - 2 * f);
        return Lerp(Rs(seed, (int)i), Rs(seed, (int)i + 1), u);
    }

        public static (int R, int G, int B) Hex(string? h)
    {
        if (string.IsNullOrEmpty(h)) return (0, 0, 0);
        var s = h.AsSpan().Trim();
        if (s.Length > 0 && s[0] == '#') s = s.Slice(1);
        if (s.Length == 3)
        {
            var r = HexVal(s[0]);
            var g = HexVal(s[1]);
            var b = HexVal(s[2]);
            return (r * 17, g * 17, b * 17);
        }
        if (s.Length >= 6)
        {
            if (int.TryParse(s.Slice(0, 6), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var n))
                return ((n >> 16) & 255, (n >> 8) & 255, n & 255);
        }
        return (0, 0, 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int HexVal(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => 0
    };

    public static string Mix(string h1, string h2, double t)
    {
        var a = Hex(h1);
        var b = Hex(h2);
        int F(int v1, int v2) => (int)Math.Round(Lerp(v1, v2, t));
        return "#" + F(a.R, b.R).ToString("x2") + F(a.G, b.G).ToString("x2") + F(a.B, b.B).ToString("x2");
    }

    public static double Lum(string h)
    {
        var (r, g, b) = Hex(h);
        return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
    }

    public static string ToHex(double r, double g, double b)
    {
        string F(double v) => ((int)Math.Round(Clamp(v, 0, 255))).ToString("x2");
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
        var (r0, g0, b0) = Hex(hex);
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

        public static double Contrast(string a, string b)
    {
        double L(string h)
        {
            var (r0, g0, b0) = Hex(h);
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

    public static bool IsKanji(char c) => c is >= '\u3400' and <= '\u9FFF' or >= '\uF900' and <= '\uFAFF' or '々' or '〆' or 'ヶ';
    public static bool IsHira(char c) => c is >= 'ぁ' and <= 'ゟ';
    public static bool IsKata(char c) => c is >= '゠' and <= 'ヿ' or >= '\u31F0' and <= '\u31FF' or >= 'ｦ' and <= 'ﾟ';
    public static bool IsSmallKana(char c) => "ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮヵヶ".Contains(c);
    public static bool IsPunct(char c) => "、。，．,.!?！？…‥・「」『』（）()【】〈〉《》〔〕［］[]'\"“”‘’ー〜～:：;；-—―".Contains(c);
    public static bool IsLatin(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';
    public const string VertRotate = "ー〜～…‥―—-()（）「」『』【】〈〉《》〔〕[]［］→←:：;；=＝";

    private static readonly Dictionary<string, string> RomajiBase = new()
    {
        ["あ"] = "a", ["い"] = "i", ["う"] = "u", ["え"] = "e", ["お"] = "o",
        ["か"] = "ka", ["き"] = "ki", ["く"] = "ku", ["け"] = "ke", ["こ"] = "ko",
        ["さ"] = "sa", ["し"] = "shi", ["す"] = "su", ["せ"] = "se", ["そ"] = "so",
        ["た"] = "ta", ["ち"] = "chi", ["つ"] = "tsu", ["て"] = "te", ["と"] = "to",
        ["な"] = "na", ["に"] = "ni", ["ぬ"] = "nu", ["ね"] = "ne", ["の"] = "no",
        ["は"] = "ha", ["ひ"] = "hi", ["ふ"] = "fu", ["へ"] = "he", ["ほ"] = "ho",
        ["ま"] = "ma", ["み"] = "mi", ["む"] = "mu", ["め"] = "me", ["も"] = "mo",
        ["や"] = "ya", ["ゆ"] = "yu", ["よ"] = "yo",
        ["ら"] = "ra", ["り"] = "ri", ["る"] = "ru", ["れ"] = "re", ["ろ"] = "ro",
        ["わ"] = "wa", ["を"] = "wo", ["ん"] = "n",
        ["が"] = "ga", ["ぎ"] = "gi", ["ぐ"] = "gu", ["げ"] = "ge", ["ご"] = "go",
        ["ざ"] = "za", ["じ"] = "ji", ["ず"] = "zu", ["ぜ"] = "ze", ["ぞ"] = "zo",
        ["だ"] = "da", ["ぢ"] = "ji", ["づ"] = "zu", ["で"] = "de", ["ど"] = "do",
        ["ば"] = "ba", ["び"] = "bi", ["ぶ"] = "bu", ["べ"] = "be", ["ぼ"] = "bo",
        ["ぱ"] = "pa", ["ぴ"] = "pi", ["ぷ"] = "pu", ["ぺ"] = "pe", ["ぽ"] = "po",
        ["ぁ"] = "a", ["ぃ"] = "i", ["ぅ"] = "u", ["ぇ"] = "e", ["ぉ"] = "o", ["ゔ"] = "vu"
    };

    private static readonly Dictionary<string, string> RomajiYo = new() { ["ゃ"] = "ya", ["ゅ"] = "yu", ["ょ"] = "yo" };

    private const string RomajiHW = "ｦｧｨｩｪｫｬｭｮｯｰｱｲｳｴｵｶｷｸｹｺｻｼｽｾｿﾀﾁﾂﾃﾄﾅﾆﾇﾈﾉﾊﾋﾌﾍﾎﾏﾐﾑﾒﾓﾔﾕﾖﾗﾘﾙﾚﾛﾜﾝ";
    private const string RomajiFW = "ヲァィゥェォッャュョーアイウエオカキクケコサシスセソタチツテトナニヌネノハヒフヘホマミムメモヤユヨラリルレロワン";

    public static string? Romaji(string? s)
    {
        var outS = "";
        var i = 0;
        var raw = (s ?? "").ToArray();
        var arr = new List<string>();
        for (var k = 0; k < raw.Length; k++)
        {
            var h = RomajiHW.IndexOf(raw[k]);
            if (h < 0)
            {
                var c = raw[k];
                arr.Add(IsKata(c) && c != 'ー' ? ((char)(c - 0x60)).ToString() : c.ToString());
                continue;
            }
            var f = RomajiFW[h].ToString();
            var mark = k + 1 < raw.Length ? raw[k + 1].ToString() : null;
            if (mark == "ﾞ")
            {
                if (f == "ウ") f = "ヴ";
                else if ("カキクケコサシスセソタチツテトハヒフヘホ".Contains(f)) f = ((char)(f[0] + 1)).ToString();
                if (f != RomajiFW[h].ToString()) k++;
            }
            else if (mark == "ﾟ" && "ハヒフヘホ".Contains(f))
            {
                f = ((char)(f[0] + 2)).ToString();
                k++;
            }
            arr.Add(f == "ー" ? f : ((char)(f[0] - 0x60)).ToString());
        }

        string At(int idx) => idx < arr.Count ? arr[idx] : "";
        while (i < arr.Count)
        {
            var c = At(i);
            var n = At(i + 1);
            if (c == "っ")
            {
                var nx = RomajiBase.GetValueOrDefault(n, "");
                if (nx.Length > 0) outS += nx[0];
                i++;
                continue;
            }
            if (c == "ー")
            {
                if (outS.Length > 0) outS += outS[^1];
                i++;
                continue;
            }
            if (n.Length > 0 && RomajiYo.ContainsKey(n) && RomajiBase.ContainsKey(c))
            {
                var bs = RomajiBase[c];
                var tail = bs.Length > 1 && bs.EndsWith("i") ? bs[..^1] : bs;
                outS += tail + (bs is "shi" or "chi" or "ji" ? RomajiYo[n][1..] : RomajiYo[n]);
                i += 2;
                continue;
            }
            if (RomajiBase.ContainsKey(c)) outS += RomajiBase[c];
            else if (c.Length == 1 && (char.IsAsciiLetterOrDigit(c[0]) || c[0] == ' ')) outS += c;
            else return null;
            i++;
        }
        return outS;
    }

    public static string FmtTime(double t, int? fps = null)
    {
        t = Math.Max(0, t);
        var m = (int)Math.Floor(t / 60);
        var s = (int)Math.Floor(t % 60);
        var f = (int)Math.Floor(t % 1 * (fps ?? 100));
        return fps.HasValue
            ? $"{m:D2}:{s:D2}:{f:D2}"
            : $"{m:D2}:{s:D2}.{f:D2}";
    }
}
