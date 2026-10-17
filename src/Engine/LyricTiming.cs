using System.Text;

namespace JizuraLive.Engine;

public static class LyricTiming
{
    private const double MaxWordMs = 450.0;

    private const double CoverageRatio = 0.85;

    public static IReadOnlyList<LyricClip> EnsureWords(IReadOnlyList<LyricClip> clips)
    {
        var result = new List<LyricClip>(clips.Count);
        foreach (var clip in clips)
        {
            if (clip.Words is { Count: > 0 }) { result.Add(clip); continue; }

            var text = clip.Text.Replace("\n", "");
            var tokens = Tokenize(text);
            var spanMs = clip.EndMs - clip.StartMs;
            if (tokens.Count == 0 || spanMs <= 0) { result.Add(clip); continue; }

            var wordMs = Math.Min(spanMs * CoverageRatio / tokens.Count, MaxWordMs);
            var words = new List<KrcWord>(tokens.Count);
            var atMs = clip.StartMs;
            foreach (var token in tokens)
            {
                words.Add(new KrcWord(atMs, (long)wordMs, token));
                atMs += (long)wordMs;
            }

            result.Add(clip with { Words = words });
        }

        return result;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var open = false;
        var lead = "";

        void Add(string token)
        {
            tokens.Add(lead + token);
            lead = "";
        }

        void Flush()
        {
            if (current.Length > 0) Add(current.ToString());
            current.Clear();
            open = false;
        }

        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0) { current.Append(ch); open = false; }
                else if (tokens.Count > 0) tokens[^1] += ch;
                else lead += ch;
                continue;
            }

            if (char.IsPunctuation(ch) || char.IsSymbol(ch))
            {
                if (current.Length > 0) { current.Append(ch); open = true; }
                else if (tokens.Count > 0) tokens[^1] += ch;
                else { current.Append(ch); open = true; }
                continue;
            }

            if (IsCjk(ch))
            {
                Flush();
                Add(ch.ToString());
                continue;
            }

            if (!open) { Flush(); open = true; }
            current.Append(ch);
        }

        Flush();
        return tokens;
    }

    private static bool IsCjk(char ch) =>
        (ch is >= '\u3040' and <= '\u30ff') ||
        (ch is >= '\u3400' and <= '\u4dbf') ||
        (ch is >= '\u4e00' and <= '\u9fff') ||
        (ch is >= '\uac00' and <= '\ud7af') ||
        (ch is >= '\uf900' and <= '\ufaff') ||
        (ch is >= '\uff10' and <= '\uff19') ||
        (ch is >= '\uff21' and <= '\uff3a') ||
        (ch is >= '\uff41' and <= '\uff5a');
}
