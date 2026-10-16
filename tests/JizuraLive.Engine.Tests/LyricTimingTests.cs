using JizuraLive.Engine;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 合成逐字时间（M5）：普通 LRC 没有词时间，渲染与导演都只认 Words，
/// 所以缺词表的句子必须在这里补齐，且补出来的时间满足「单调、起于行首、不越句尾、单词不超 450ms」。
/// </summary>
public class LyricTimingTests
{
    private static LyricClip Clip(string text, long startMs = 1000, long endMs = 2000) =>
        new("c", startMs, endMs, text, false);

    private static string[] Texts(LyricClip clip) => clip.Words!.Select(w => w.Text).ToArray();

    /// <summary>词表的共同不变量：非空、起于 StartMs、单调不重叠、不越 EndMs、单词 ≤ 450ms、拼接 = 整句去 '\n'。</summary>
    private static void AssertWordInvariants(LyricClip clip)
    {
        var words = clip.Words!;
        Assert.NotEmpty(words);
        Assert.Equal(clip.StartMs, words[0].StartMs);

        for (var i = 0; i < words.Count; i++)
        {
            Assert.True(words[i].DurMs <= 450, $"第 {i} 词 {words[i].DurMs}ms 超过 450ms");
            Assert.True(words[i].StartMs + words[i].DurMs <= clip.EndMs, $"第 {i} 词越出句尾");
            if (i > 0)
                Assert.True(words[i].StartMs >= words[i - 1].StartMs + words[i - 1].DurMs, $"第 {i} 词时间倒挂");
        }

        Assert.Equal(clip.Text.Replace("\n", ""), string.Concat(words.Select(w => w.Text)));
    }

    [Fact]
    public void CjkGetsOneWordPerCharacter()
    {
        var clip = LyricTiming.EnsureWords([Clip("你好世界")])[0];

        Assert.Equal(new[] { "你", "好", "世", "界" }, Texts(clip));
        AssertWordInvariants(clip);
    }

    [Fact]
    public void LatinSplitsOnSpacesAndKeepsThemInThePreviousWord()
    {
        var clip = LyricTiming.EnsureWords([Clip("Hello brave world")])[0];

        Assert.Equal(new[] { "Hello ", "brave ", "world" }, Texts(clip));
        AssertWordInvariants(clip);
    }

    [Theory]
    [InlineData("a  b")]
    [InlineData(" 你好")]
    [InlineData("你 好 world")]
    [InlineData("  lead and  double  ")]
    public void WordsConcatenateBackToTheExactText(string text)
    {
        var clip = LyricTiming.EnsureWords([Clip(text)])[0];

        Assert.Equal(text, string.Concat(Texts(clip)));
    }

    [Fact]
    public void PunctuationJoinsThePreviousWord()
    {
        var clip = LyricTiming.EnsureWords([Clip("你好，世界！")])[0];

        Assert.Equal(new[] { "你", "好，", "世", "界！" }, Texts(clip));
        AssertWordInvariants(clip);
    }

    [Fact]
    public void WordTimingStartsAtClipStartAndStopsShortOfTheEnd()
    {
        var clip = LyricTiming.EnsureWords([Clip("你好世界", 1000, 2000)])[0];
        var words = clip.Words!;

        // (2000-1000) * 0.85 / 4 = 212.5 → 212ms（截断），末词收在 1848 < 2000
        Assert.All(words, w => Assert.Equal(212, w.DurMs));
        Assert.Equal(new long[] { 1000, 1212, 1424, 1636 }, words.Select(w => w.StartMs).ToArray());
        Assert.Equal(1848, words[^1].StartMs + words[^1].DurMs);
        AssertWordInvariants(clip);
    }

    [Fact]
    public void LongLinesCapEachWordAt450Ms()
    {
        var clip = LyricTiming.EnsureWords([Clip("你好世界", 0, 60000)])[0];

        // 60000 * 0.85 / 4 = 12750 → 被 450ms 上限压住，宁可留白也不拖字
        Assert.All(clip.Words!, w => Assert.Equal(450, w.DurMs));
        Assert.Equal(1800, clip.Words![^1].StartMs + clip.Words![^1].DurMs);
        AssertWordInvariants(clip);
    }

    [Fact]
    public void MultiLineTextIsJoinedBeforeSplitting()
    {
        var clip = LyricTiming.EnsureWords([Clip("一二\n三四")])[0];

        Assert.Equal(new[] { "一", "二", "三", "四" }, Texts(clip));
        AssertWordInvariants(clip);
    }

    [Fact]
    public void ClipsWithWordsAreReturnedUnchanged()
    {
        var words = new List<KrcWord> { new(1000, 400, "あ"), new(1400, 600, "い") };
        var withWords = new LyricClip("c", 1000, 2000, "あい", false, words);
        var empty = new LyricClip("c2", 1000, 2000, "", false);

        var result = LyricTiming.EnsureWords([withWords, empty]);

        Assert.Same(withWords, result[0]);       // 同一个实例，时长一字不动
        Assert.Same(words, result[0].Words);
        Assert.Same(empty, result[1]);           // 没字可切：不编出空词表
        Assert.Null(result[1].Words);
    }
}
