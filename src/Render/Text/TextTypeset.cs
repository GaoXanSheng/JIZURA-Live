namespace JizuraLive.Render.Text;

public static class TextTypeset
{
    private const double DefaultKanaAdvance = 0.9;
    private const double SmallKanaAdvance = 0.86;
    private const double LongVowelAdvance = 0.94;
    private const double LatinScale = 1.08;
    private const double ParticleScale = 0.78;
    private const double FirstGlyphScale = 1.18;
    private const double LatinGapEm = 0.2;
    private const int RuleMinContent = 3;

    private const string Particles = "はがをにでとのへも";

    public readonly record struct Cell(double F, double Gap, double Adv)
    {
        public static Cell Identity { get; } = new(1, 0, 1);
    }

    public static bool Enabled { get; set; } = true;

    public static Cell[] Line(IReadOnlyList<string> glyphs)
    {
        var cells = new Cell[glyphs.Count];
        if (!Enabled)
        {
            for (var i = 0; i < cells.Length; i++) cells[i] = Cell.Identity;
            return cells;
        }

        var content = 0;
        foreach (var g in glyphs)
            if (!string.IsNullOrEmpty(g) && !IsSpace(g[0]))
                content++;

        var first = true;
        for (var i = 0; i < glyphs.Count; i++)
        {
            var ch = glyphs[i];
            if (string.IsNullOrEmpty(ch))
            {
                cells[i] = Cell.Identity;
                continue;
            }

            var cls = Classify(ch);
            if (cls == 'S')
            {
                cells[i] = Cell.Identity;
                continue;
            }

            var f = 1.0;
            var adv = 1.0;
            var gap = 0.0;

            if (cls is 'H' or 'T')
            {
                adv = IsSmallKana(ch[0]) ? SmallKanaAdvance : ch == "ー" ? LongVowelAdvance : DefaultKanaAdvance;
            }

            if (cls == 'L') f = LatinScale;

            if (cls is 'H' or 'T' && content >= RuleMinContent && IsParticle(glyphs, i)) f = ParticleScale;

            if (first && content >= RuleMinContent && cls is 'K' or 'H' or 'T') f = FirstGlyphScale;
            first = false;

            var prevCls = i > 0 && !string.IsNullOrEmpty(glyphs[i - 1]) ? Classify(glyphs[i - 1]) : '\0';
            if (i > 0 && prevCls != 'S' && (cls == 'L') != (prevCls == 'L') && prevCls != '\0' && prevCls != 'P' && cls != 'P')
                gap = LatinGapEm;

            cells[i] = new Cell(f, gap, adv);
        }

        return cells;
    }

    public static IReadOnlyList<string> SplitGlyphs(string text)
    {
        var glyphs = new List<string>(text.Length);
        for (var i = 0; i < text.Length;)
        {
            var len = char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            glyphs.Add(text.Substring(i, len));
            i += len;
        }

        return glyphs;
    }

    private static bool IsSmallKana(char c) =>
        "ぁぃぅぇぉっゃゅょゎァィゥェォッャュョヮヵヶ".Contains(c);

    private static bool IsParticle(IReadOnlyList<string> glyphs, int i)
    {
        var ch = glyphs[i];
        if (i == 0 || !Particles.Contains(ch)) return false;

        var prev = glyphs[i - 1];
        if (prev is "っ" or "ッ" or "ー") return false;

        if (ch == "を") return true;

        var cp = Classify(prev);
        var next = i + 1 < glyphs.Count ? glyphs[i + 1] : null;
        var cn = next is null ? '\0' : Classify(next);
        var nextOk = next is null || cn is 'K' or 'T' or 'L' or 'S' or 'P';
        return nextOk && cp is not ('S' or 'P') && !(cp == 'H' && cn == 'H');
    }

    private static char Classify(string? ch)
    {
        if (string.IsNullOrEmpty(ch)) return '\0';
        if (HasAny(ch, IsLatin)) return 'L';
        if (HasAny(ch, IsKanji)) return 'K';
        if (HasAny(ch, IsKata)) return 'T';
        if (HasAny(ch, IsHira)) return 'H';
        if (HasAny(ch, IsSpace)) return 'S';
        return 'P';
    }

    private static bool HasAny(string s, Func<char, bool> f)
    {
        foreach (var c in s)
            if (f(c))
                return true;
        return false;
    }

    private static bool IsLatin(char c) =>
        c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9';

    private static bool IsKanji(char c) =>
        c is >= '㐀' and <= '鿿' or >= '豈' and <= '﫿' or '々' or '〆';

    private static bool IsKata(char c) =>
        c is >= '゠' and <= 'ヿ' or >= 'ㇰ' and <= 'ㇿ';

    private static bool IsHira(char c) => c >= 'ぁ' && c <= 'ゟ';

    private static bool IsSpace(char c) =>
        c is ' ' or '\t' or '\n' or '\v' or '\f' or '\r'
            or '\u00a0' or '\u1680' or '\u2028' or '\u2029' or '\u202f' or '\u205f'
            or '\u3000' or '\ufeff'
            or >= '\u2000' and <= '\u200a';
}
