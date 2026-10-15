using JizuraLive.Engine;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// LRC → LyricClip 的导入规则（M5-LRC）：稳定排序 + 同刻合并、空标签清屏边界、半开区间、
/// 去掉 0.5–15s 夹断、末句越界规则，以及「Clip 与 KrcLine 两条入口同一结论」。
/// 全部是纯数值/文本断言，不需要 GPU。
/// </summary>
public class LrcTimelineTests
{
    [Fact]
    public void LrcParserMergesDuplicateTimestampsIntoOneClip()
    {
        const string lrc = """
            [00:00.50]前の句
            [00:04.00]同じ刻の一行目
            [00:04.00]同じ刻の二行目
            [00:08.00]最後の句
            """;

        var result = LrcParser.ParseClips(lrc);

        Assert.Equal(3, result.Clips.Count); // 4 个时间戳 → 3 个 Clip（同刻合并）
        Assert.Empty(result.Issues);

        var merged = result.Clips[1];
        Assert.Equal(4000, merged.StartMs);
        Assert.Equal(8000, merged.EndMs);
        Assert.Equal("同じ刻の一行目\n同じ刻の二行目", merged.Text); // '\n' 连接，顺序 = 文件出现顺序
    }

    [Fact]
    public void EmptyTimestampIsClearBoundary()
    {
        const string lrc = """
            [00:01]A
            [00:05]
            [00:09]B
            """;

        var result = LrcParser.ParseClips(lrc);

        Assert.Equal(2, result.Clips.Count); // 空标签不生成 Clip
        Assert.Equal(1000, result.Clips[0].StartMs);
        Assert.Equal(5000, result.Clips[0].EndMs); // 前一句被空标签收在 5s
        Assert.Equal("A", result.Clips[0].Text);
        Assert.Equal(9000, result.Clips[1].StartMs);
        Assert.Equal(12000, result.Clips[1].EndMs); // 末句无音频时长 → start+3s 兜底
        Assert.Equal("B", result.Clips[1].Text);
        Assert.DoesNotContain(result.Clips, c => c.EndMs <= c.StartMs); // 不生成零时长 Clip

        Assert.Equal("A", LrcParser.FindSample(result.Clips, 4.9)?.Text);
        Assert.Null(LrcParser.FindSample(result.Clips, 5.0));    // 间奏起点即清屏
        Assert.Null(LrcParser.FindSample(result.Clips, 8.999)); // 间奏里不残留上一句
    }

    [Fact]
    public void DenseShortLinesAreNotClamped()
    {
        const string lrc = """
            [00:00.00]一
            [00:00.30]二
            [00:00.60]三
            [00:20.60]四
            """;

        var clips = LrcParser.ParseClips(lrc).Clips;

        Assert.Equal(4, clips.Count);
        Assert.Equal(300, clips[0].EndMs - clips[0].StartMs); // 不再被抬到 500ms
        Assert.Equal(300, clips[1].EndMs - clips[1].StartMs);
        Assert.Equal(clips[1].StartMs, clips[0].EndMs);       // 每句收在下一句 start
        Assert.Equal(clips[2].StartMs, clips[1].EndMs);
        Assert.Equal(20000, clips[2].EndMs - clips[2].StartMs); // 长句不再被压到 15000

        // 旧入口（KrcLine）走同一套边界
        var lines = LrcParser.Parse(lrc);
        Assert.Equal(300, lines[0].DurMs);
        Assert.Equal(20000, lines[2].DurMs);
    }

    [Fact]
    public void HalfOpenIntervalClearsExactlyAtEnd()
    {
        // 末尾的空标签让「t == EndMs」这一刻没有后继句，才能验证半开终点本身（
        // 若下一句正好从 EndMs 开始，那一刻命中的是下一句——那是正确行为）
        const string lrc = """
            [00:01.00]前
            [00:05.00]
            """;

        var clips = LrcParser.ParseClips(lrc).Clips;
        var lines = LrcParser.Parse(lrc);

        Assert.Single(clips);
        Assert.Equal(1000, clips[0].StartMs);
        Assert.Equal(5000, clips[0].EndMs);

        // t == EndMs：半开区间终点 = 清屏（两条入口同语义）
        Assert.Null(LrcParser.FindSample(clips, 5.0));
        Assert.Equal(-1, LrcParser.FindLineIndex(lines, 5.0));

        // t == EndMs - 1ms：仍然是该句
        Assert.Equal("前", LrcParser.FindSample(clips, 4.999)?.Text);
        Assert.Equal(0, LrcParser.FindLineIndex(lines, 4.999));

        // 末句的兜底区间同样是半开的（无后继句，t == EndMs 也不命中）
        var tailOnly = LrcParser.ParseClips("[00:01.00]前").Clips;
        Assert.Equal(4000, tailOnly[0].EndMs);
        Assert.Null(LrcParser.FindSample(tailOnly, 4.0));
        Assert.Equal("前", LrcParser.FindSample(tailOnly, 3.999)?.Text);
    }

    [Fact]
    public void LastLineBeyondAudioDurationIsKeptAndFlagged()
    {
        const string lrc = """
            [00:02.00]句一
            [00:12.00]越界末句
            """;

        var result = LrcParser.ParseClips(lrc, audioDurationSeconds: 10.0);
        var last = result.Clips[^1];

        Assert.True(last.OutOfRange);              // 起点已在音频尾外：标注待校准
        Assert.True(last.EndMs > last.StartMs);    // 绝不产生负/零时长
        Assert.Equal(15000, last.EndMs);           // start + 3s 兜底

        // 越界标记只加在末句：前一句照旧收在下一句 start
        Assert.Equal(12000, result.Clips[0].EndMs);
        Assert.False(result.Clips[0].OutOfRange);

        var sample = LrcParser.FindSample(result.Clips, 12.5);
        Assert.NotNull(sample);
        var hit = sample!.Value;
        Assert.Equal("越界末句", hit.Text);
        Assert.True(hit.OutOfRange);
    }

    [Fact]
    public void LastLineEndsAtAudioDurationWhenKnown()
    {
        const string lrc = """
            [00:02.00]句一
            [00:12.00]末句
            """;

        var result = LrcParser.ParseClips(lrc, audioDurationSeconds: 20.0);
        var last = result.Clips[^1];

        Assert.Equal(20000, last.EndMs);
        Assert.False(last.OutOfRange);
        Assert.Equal("末句", LrcParser.FindSample(result.Clips, 19.5)?.Text);
        Assert.Null(LrcParser.FindSample(result.Clips, 20.0)); // 音频尾 = 半开终点
    }

    [Fact]
    public void LastLineWithoutAudioUsesThreeSecondsAndIsFlagged()
    {
        var result = LrcParser.ParseClips("[00:05.00]只有一句");

        var clip = Assert.Single(result.Clips);
        Assert.Equal(8000, clip.EndMs);
        Assert.Equal(3000, clip.EndMs - clip.StartMs);
        Assert.True(clip.OutOfRange);
    }

    [Fact]
    public void EmptyLabelConflictingWithTextKeepsTextAndReportsIssue()
    {
        const string lrc = """
            [00:01.00]
            [00:01.00]同刻的词
            [00:04.00]下一句
            """;

        var result = LrcParser.ParseClips(lrc);

        Assert.Equal(2, result.Clips.Count); // 同刻只留一个非空 Clip（不隐式择一）
        Assert.Equal(1000, result.Clips[0].StartMs);
        Assert.Equal(4000, result.Clips[0].EndMs);
        Assert.Equal("同刻的词", result.Clips[0].Text);

        var issue = Assert.Single(result.Issues);
        Assert.Equal(1, issue.LineNumber); // 指向那条被忽略的空标签（文件第 1 行）
        Assert.False(string.IsNullOrWhiteSpace(issue.Reason));
    }

    [Fact]
    public void ToClipsMatchesLegacyEntryAndSharesOneLookup()
    {
        // 带一个空标签，让「空档」也进入两条入口的比对
        const string lrc = """
            [00:00.00]一
            [00:02.00]
            [00:03.00]二
            """;

        var lines = LrcParser.Parse(lrc);
        var clips = LrcParser.ToClips(lines);

        Assert.Equal(lines.Count, clips.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            Assert.Equal($"krc_{i}", clips[i].Id);
            Assert.Equal(lines[i].StartMs, clips[i].StartMs);
            Assert.Equal(lines[i].StartMs + lines[i].DurMs, clips[i].EndMs);
            Assert.False(clips[i].OutOfRange);
        }

        // 同一 t 上两条入口必须指向同一句（空档上必须同时为空）
        double[] times = [0.5, 1.999, 2.0, 2.5, 3.0, 4.0, 5.999, 6.0, 9.0];
        foreach (var t in times)
        {
            var idx = LrcParser.FindLineIndex(lines, t);
            var sample = LrcParser.FindSample(clips, t);

            if (idx < 0)
            {
                Assert.Null(sample);
            }
            else
            {
                Assert.NotNull(sample);
                var hit = sample!.Value;
                Assert.Equal(lines[idx].StartMs, hit.StartMs);
                Assert.Equal(lines[idx].Text, hit.Text);
            }
        }
    }
}
