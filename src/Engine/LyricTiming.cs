using System.Text;

namespace JizuraLive.Engine;

/// <summary>
/// 给没有逐字时间的歌词补一份合成词表（普通 LRC 导入的句子）：渲染与导演都只认
/// <see cref="LyricClip.Words"/>，不再各自决定「一个字该显示多久」。
///
/// 合成规则是**保底**不是对时：每词等长，总长只占本句 85%（末词不贴到 EndMs，留一口气），
/// 且单词不超过 450ms——超过就说明该按更细的时间信息来，而不是把字拖长。
/// </summary>
public static class LyricTiming
{
    /// <summary>单词时长上限：再长的句子也只按这个速度出字，宁可留白不拖字。</summary>
    private const double MaxWordMs = 450.0;

    /// <summary>合成词表只覆盖本句时长的这个比例，末词不贴到 EndMs。</summary>
    private const double CoverageRatio = 0.85;

    /// <summary>
    /// 已有 <see cref="LyricClip.Words"/> 的句子原样返回（同一个实例，不动时长）；其余按字合成。
    /// </summary>
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

    /// <summary>
    /// 分词：CJK（汉字/假名/全角/谚文）每字一词；拉丁文按空白分词且空白归前一个词；
    /// 标点/符号并入前一个词。依次拼接即原文。
    /// </summary>
    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var open = false; // 当前词还在收字符（空白已把它收尾）
        var lead = "";    // 行首空白：并入第一个词，拼回去才与原文逐字符相等

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
                // 前一个字已单独成词（CJK）时它不在 current 里，标点要贴回 tokens 的末词
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
        (ch is >= '\u3040' and <= '\u30ff') ||   // 平假名/片假名
        (ch is >= '\u3400' and <= '\u4dbf') ||   // 汉字扩展 A
        (ch is >= '\u4e00' and <= '\u9fff') ||   // 汉字
        (ch is >= '\uac00' and <= '\ud7af') ||   // 谚文
        (ch is >= '\uf900' and <= '\ufaff') ||   // 兼容汉字
        (ch is >= '\uff10' and <= '\uff19') ||   // 全角数字
        (ch is >= '\uff21' and <= '\uff3a') ||   // 全角大写
        (ch is >= '\uff41' and <= '\uff5a');     // 全角小写
}
