using System.Text.RegularExpressions;

namespace JizuraLive.Engine
{
    public sealed record KrcWord(long StartMs, long DurMs, string Text)
    {
        public override string ToString() => Text;
    }

    public sealed record KrcLine(long StartMs, long DurMs, string Text, IReadOnlyList<KrcWord>? Words = null)
    {
        public override string ToString() => $"[{StartMs}ms+{DurMs}ms] {Text}";
    }

    /// <summary>
    /// 标准 LRC 歌词解析器与时间轴检索器。
    /// 纯函数、轻量、支持毫秒级二分定位。
    ///
    /// 唯一真源是 <see cref="ParseClips"/>（Clip 区间）；<see cref="Parse"/> 只是它的薄适配，
    /// KrcLine 与 LyricClip 共用同一条查找路径，不允许两条路径各算出不同的「当前句」。
    /// </summary>
    public static class LrcParser
    {
        private static readonly Regex TimestampRegex = new(@"\[(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?\]", RegexOptions.Compiled);

        /// <summary>末句无音频时长（或起点已在音频尾外）时的临时区间长度，同时标 OutOfRange 待校准。</summary>
        private const long TailFallbackMs = 3000;

        public static List<KrcLine> Parse(string? raw)
        {
            var result = ParseClips(raw);
            var lines = new List<KrcLine>(result.Clips.Count);
            foreach (var clip in result.Clips)
                lines.Add(new KrcLine(clip.StartMs, clip.EndMs - clip.StartMs, clip.Text));

            return lines;
        }

        /// <summary>
        /// LRC → Clip 序列（唯一真源）。
        ///
        /// 规则（DOC/PV-DIRECTOR-SYSTEM.md §13.5）：
        /// 多标签展开、按时间**稳定**排序、同刻文本合并成多行 Clip、同刻空标签只作清屏边界（不生成零时长 Clip）；
        /// 相邻边界是「下一个时间戳」而不是下一句非空文本的 start，因此没有 0.5–15s 的时长夹断。
        /// 末句优先收在音频尾，否则取 start+3s 并标 <see cref="LyricClip.OutOfRange"/>。
        /// </summary>
        /// <param name="audioDurationSeconds">音频时长（秒）。null = 未知，末句只能给待校准的临时区间。</param>
        public static LrcImportResult ParseClips(string? raw, double? audioDurationSeconds = null)
        {
            var clips = new List<LyricClip>();
            var issues = new List<LrcImportIssue>();
            if (string.IsNullOrWhiteSpace(raw)) return new LrcImportResult(clips, issues);

            var rawLines = raw.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var events = new List<(int LineNo, long StartMs, string Text)>();

            for (var i = 0; i < rawLines.Length; i++)
            {
                var text = rawLines[i].Trim();
                if (string.IsNullOrEmpty(text)) continue;

                var matches = TimestampRegex.Matches(text);
                if (matches.Count == 0) continue; // 元数据行（[ti:]/[ar:]/[al:]…）没有 mm:ss 时间戳

                var lyricText = TimestampRegex.Replace(text, "").Trim();

                foreach (Match m in matches)
                {
                    // 同一行多个时间戳 = 同一句复用（多标签），每条时间戳各算一次出现
                    if (TryTimestampMs(m, out var startMs)) events.Add((i + 1, startMs, lyricText));
                }
            }

            // OrderBy 是稳定排序：同刻多条保持文件出现顺序，合并出的多行文本才可复现
            var ordered = events.OrderBy(e => e.StartMs).ToList();

            // 先按时刻分组，才知道每组的下一个时间戳（边界）与同刻是否空/非空混排
            var groups = new List<(long StartMs, List<string> Texts, List<int> EmptyLineNos)>();
            foreach (var e in ordered)
            {
                if (groups.Count == 0 || groups[^1].StartMs != e.StartMs)
                    groups.Add((e.StartMs, new List<string>(), new List<int>()));

                var g = groups[^1];
                if (e.Text.Length == 0) g.EmptyLineNos.Add(e.LineNo);
                else g.Texts.Add(e.Text);
            }

            for (var g = 0; g < groups.Count; g++)
            {
                var (startMs, texts, emptyLineNos) = groups[g];

                // 相邻边界 = 下一个时间戳（空标签也算）：密集短句不被抬到 500ms，长句不被压到 15s
                var nextStartMs = g + 1 < groups.Count ? groups[g + 1].StartMs : (long?)null;

                // 空标签组 = 清屏边界：上一句的 EndMs 用的就是这个时刻（它自己的下一个时间戳），
                // 这里只负责不生成 Clip（契约「不生成零时长 Clip」）
                if (texts.Count == 0) continue;

                if (emptyLineNos.Count > 0)
                {
                    // 同刻既清屏又有词：不隐式择一，保留非空文本，把空标签的行号报出来让人去改
                    foreach (var lineNo in emptyLineNos)
                        issues.Add(new LrcImportIssue(lineNo,
                            $"{startMs}ms 处同时存在空标签与歌词文本：保留非空文本，该空标签不作为清屏边界"));
                }

                long endMs;
                bool outOfRange;
                if (nextStartMs is { } next)
                {
                    endMs = next;
                    outOfRange = false;
                }
                else
                {
                    (endMs, outOfRange) = TailEnd(startMs, audioDurationSeconds);
                }

                clips.Add(new LyricClip($"lrc_{clips.Count}", startMs, endMs, string.Join("\n", texts), outOfRange));
            }

            return new LrcImportResult(clips, issues);
        }

        /// <summary>
        /// 旧入口（KrcLine 列表）→ Clip：让 <c>PvPipeline.SetLyrics</c> 进来的文本与新的 Clip 路径
        /// 汇合成同一份样本，不再分叉成两套查找。DurMs 直接搬到区间上，不做任何时长修正。
        /// </summary>
        public static List<LyricClip> ToClips(IReadOnlyList<KrcLine> lines)
        {
            var clips = new List<LyricClip>(lines.Count);
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                clips.Add(new LyricClip($"krc_{i}", line.StartMs, line.StartMs + line.DurMs, line.Text, false));
            }

            return clips;
        }

        /// <summary>
        /// 唯一查找入口：二分最后一个 StartMs ≤ t，再判 <c>t &lt; EndMs</c>（半开区间）。
        /// 越界、空档、t == EndMs 一律 null——调用方据此清屏，不留上一句。
        /// 要求 <paramref name="clips"/> 按 StartMs 升序（ParseClips/ToClips 的输出即文件顺序）。
        /// </summary>
        public static LyricSample? FindSample(IReadOnlyList<LyricClip> clips, double timeSeconds)
        {
            if (clips is null || clips.Count == 0) return null;

            var tMs = (long)(timeSeconds * 1000.0);
            var match = LastStartAtOrBefore(clips, tMs);
            if (match < 0) return null;

            var clip = clips[match];
            if (tMs >= clip.EndMs) return null;

            return new LyricSample(clip.Id, clip.Text, clip.StartMs, clip.EndMs, clip.OutOfRange);
        }

        /// <summary>
        /// HoldLastLine 用（副标题的「保留上一句」开关）：只取最后一个 StartMs ≤ t 的样本，
        /// 不看 EndMs——空档与越界时刻也要能拿到末句，清屏与否由消费方决定。
        /// </summary>
        public static LyricSample? FindLastBefore(IReadOnlyList<LyricClip> clips, double timeSeconds)
        {
            if (clips is null || clips.Count == 0) return null;

            var tMs = (long)(timeSeconds * 1000.0);
            var match = LastStartAtOrBefore(clips, tMs);
            if (match < 0) return null;

            var clip = clips[match];
            return new LyricSample(clip.Id, clip.Text, clip.StartMs, clip.EndMs, clip.OutOfRange);
        }

        public static int FindLineIndex(IReadOnlyList<KrcLine> lines, double timeSeconds)
        {
            if (lines.Count == 0) return -1;
            var tMs = (long)(timeSeconds * 1000.0);

            var low = 0;
            var high = lines.Count - 1;
            var match = -1;

            while (low <= high)
            {
                var mid = (low + high) / 2;
                var line = lines[mid];

                if (line.StartMs <= tMs)
                {
                    match = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            if (match >= 0)
            {
                // 半开区间：t == end 即清屏，与 LyricClip 同语义（两个入口必须给同一结论）
                var line = lines[match];
                if (tMs < line.StartMs + line.DurMs) return match;
            }

            return -1;
        }

        /// <summary>二分「最后一个 StartMs ≤ tMs」的下标；没有则 -1。</summary>
        private static int LastStartAtOrBefore(IReadOnlyList<LyricClip> clips, long tMs)
        {
            var low = 0;
            var high = clips.Count - 1;
            var match = -1;

            while (low <= high)
            {
                var mid = (low + high) / 2;
                if (clips[mid].StartMs <= tMs)
                {
                    match = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return match;
        }

        /// <summary>末句区间：优先收在音频尾；起点已在尾外或没有时长 → start+3s 且标越界（绝不为负时长）。</summary>
        private static (long EndMs, bool OutOfRange) TailEnd(long startMs, double? audioDurationSeconds)
        {
            // 只有已知且为正的有限时长才算「有音频」：NaN/0/负值是坏输入，按未知处理
            if (audioDurationSeconds is { } seconds && double.IsFinite(seconds) && seconds > 0)
            {
                var durationMs = (long)Math.Round(seconds * 1000.0);
                if (startMs < durationMs) return (durationMs, false);
            }

            return (startMs + TailFallbackMs, true);
        }

        private static bool TryTimestampMs(Match m, out long resultMs)
        {
            resultMs = 0;
            if (!int.TryParse(m.Groups[1].Value, out var min)) return false;
            if (!int.TryParse(m.Groups[2].Value, out var sec)) return false;

            var frac = 0;
            if (m.Groups[3].Success)
            {
                var fracStr = m.Groups[3].Value;
                if (fracStr.Length == 2) frac = int.Parse(fracStr) * 10;
                else if (fracStr.Length == 3) frac = int.Parse(fracStr);
                else if (fracStr.Length == 1) frac = int.Parse(fracStr) * 100;
            }

            resultMs = (min * 60 + sec) * 1000L + frac;
            return true;
        }
    }
}
