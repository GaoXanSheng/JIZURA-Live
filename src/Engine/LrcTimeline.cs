using System.Text;
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

    public static class LrcParser
    {
        private static readonly Regex TimestampRegex = new(@"\[(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?\]", RegexOptions.Compiled);

        private static readonly Regex KrcLineRegex = new(@"^\[(\d+),(\d+)\](.*)$", RegexOptions.Compiled);

        private static readonly Regex AngleTimestampRegex = new(@"<(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?>", RegexOptions.Compiled);

        private static readonly Regex KrcWordMarkerRegex = new(@"<(\d+),(\d+),(\d+)>", RegexOptions.Compiled);

        private const long TailFallbackMs = 3000;

        public static List<KrcLine> Parse(string? raw)
        {
            var result = ParseClips(raw);
            var lines = new List<KrcLine>(result.Clips.Count);
            foreach (var clip in result.Clips)
                lines.Add(new KrcLine(clip.StartMs, clip.EndMs - clip.StartMs, clip.Text, clip.Words));

            return lines;
        }

        public static LrcImportResult ParseClips(string? raw, double? audioDurationSeconds = null)
        {
            var clips = new List<LyricClip>();
            var issues = new List<LrcImportIssue>();
            if (string.IsNullOrWhiteSpace(raw)) return new LrcImportResult(clips, issues);

            var rawLines = raw.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var events = new List<LyricEvent>();

            for (var i = 0; i < rawLines.Length; i++)
            {
                var text = rawLines[i].Trim();
                if (string.IsNullOrEmpty(text)) continue;

                var lineNo = i + 1;

                var krc = KrcLineRegex.Match(text);
                if (krc.Success)
                {
                    var startMs = long.Parse(krc.Groups[1].Value);
                    var (krcText, krcWords) = SplitKrcWords(krc.Groups[3].Value, startMs);
                    if (krcText.Length > 0)
                        events.Add(new LyricEvent(lineNo, startMs, krcText, krcWords, long.Parse(krc.Groups[2].Value)));
                    continue;
                }

                var matches = TimestampRegex.Matches(text);
                if (matches.Count == 0) continue;

                if (AngleTimestampRegex.IsMatch(text))
                {
                    if (!TryTimestampMs(matches[0], out var lineStartMs)) continue;
                    var body = text[(matches[0].Index + matches[0].Length)..];
                    var (wordText, words, endMarkerMs) = SplitLrcWords(body);
                    if (wordText.Length > 0)
                        events.Add(new LyricEvent(lineNo, lineStartMs, wordText, words, null, endMarkerMs));
                    continue;
                }

                var lyricText = TimestampRegex.Replace(text, "").Trim();

                foreach (Match m in matches)
                {
                    if (TryTimestampMs(m, out var startMs)) events.Add(new LyricEvent(lineNo, startMs, lyricText));
                }
            }

            var ordered = events.OrderBy(e => e.StartMs).ToList();

            var groups = new List<List<LyricEvent>>();
            foreach (var e in ordered)
            {
                if (groups.Count == 0 || groups[^1][0].StartMs != e.StartMs) groups.Add(new List<LyricEvent>());
                groups[^1].Add(e);
            }

            for (var g = 0; g < groups.Count; g++)
            {
                var group = groups[g];
                var startMs = group[0].StartMs;

                var texts = new List<string>();
                var emptyLineNos = new List<int>();
                var words = new List<KrcWord>();
                var wordsComplete = true;
                long? explicitDurMs = null;
                long? endMarkerMs = null;

                foreach (var e in group)
                {
                    if (e.Text.Length == 0)
                    {
                        emptyLineNos.Add(e.LineNo);
                        continue;
                    }

                    texts.Add(e.Text);
                    explicitDurMs ??= e.ExplicitDurMs;
                    if (e.LrcEndMarkerMs is { } marker) endMarkerMs = marker;
                    if (e.Words is { } w) words.AddRange(w);
                    else wordsComplete = false;
                }

                var nextStartMs = g + 1 < groups.Count ? groups[g + 1][0].StartMs : (long?)null;

                if (texts.Count == 0) continue;

                if (emptyLineNos.Count > 0)
                {
                    foreach (var lineNo in emptyLineNos)
                        issues.Add(new LrcImportIssue(lineNo,
                            $"{startMs}ms 处同时存在空标签与歌词文本：保留非空文本，该空标签不作为清屏边界"));
                }

                long endMs;
                bool outOfRange;
                if (explicitDurMs is { } dur)
                {
                    endMs = startMs + dur;
                    if (nextStartMs is { } krcNext) endMs = Math.Min(endMs, krcNext);
                    outOfRange = false;
                    if (endMs <= startMs) continue;
                }
                else if (nextStartMs is { } next)
                {
                    endMs = next;
                    outOfRange = false;
                }
                else
                {
                    (endMs, outOfRange) = TailEnd(startMs, audioDurationSeconds);
                }

                var clipWords = wordsComplete && words.Count > 0 ? words : null;
                if (clipWords is not null && explicitDurMs is null)
                {
                    var last = clipWords[^1];
                    var wordEnd = endMarkerMs is { } m2 ? Math.Min(m2, endMs) : endMs;
                    clipWords[^1] = last with { DurMs = Math.Max(0, wordEnd - last.StartMs) };
                }

                clips.Add(new LyricClip($"lrc_{clips.Count}", startMs, endMs, string.Join("\n", texts), outOfRange, clipWords));
            }

            return new LrcImportResult(clips, issues);
        }

        public static List<LyricClip> ToClips(IReadOnlyList<KrcLine> lines)
        {
            var clips = new List<LyricClip>(lines.Count);
            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                clips.Add(new LyricClip($"krc_{i}", line.StartMs, line.StartMs + line.DurMs, line.Text, false, line.Words));
            }

            return clips;
        }

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
                var line = lines[match];
                if (tMs < line.StartMs + line.DurMs) return match;
            }

            return -1;
        }

        private sealed record LyricEvent(
            int LineNo, long StartMs, string Text,
            IReadOnlyList<KrcWord>? Words = null, long? ExplicitDurMs = null, long? LrcEndMarkerMs = null);

        private static (string Text, List<KrcWord>? Words) SplitKrcWords(string body, long lineStartMs)
        {
            var markers = KrcWordMarkerRegex.Matches(body);
            if (markers.Count == 0) return (body.Trim(), null);

            var words = new List<KrcWord>(markers.Count);
            var sb = new StringBuilder();
            for (var i = 0; i < markers.Count; i++)
            {
                var m = markers[i];
                var segEnd = i + 1 < markers.Count ? markers[i + 1].Index : body.Length;
                var seg = body[(m.Index + m.Length)..segEnd];
                if (i == 0 && m.Index > 0) seg = body[..m.Index] + seg;

                words.Add(new KrcWord(
                    lineStartMs + long.Parse(m.Groups[1].Value), long.Parse(m.Groups[2].Value), seg));
                sb.Append(seg);
            }

            return (sb.ToString(), words);
        }

        private static (string Text, List<KrcWord>? Words, long? EndMarkerMs) SplitLrcWords(string body)
        {
            var markers = AngleTimestampRegex.Matches(body);
            var words = new List<KrcWord>(markers.Count);
            var sb = new StringBuilder();
            long? endMarkerMs = null;

            for (var i = 0; i < markers.Count; i++)
            {
                if (!TryTimestampMs(markers[i], out var atMs)) continue;

                var segEnd = i + 1 < markers.Count ? markers[i + 1].Index : body.Length;
                var seg = body[(markers[i].Index + markers[i].Length)..segEnd];
                if (i == 0 && markers[i].Index > 0) seg = body[..markers[i].Index] + seg;

                if (i == markers.Count - 1 && seg.Length == 0)
                {
                    endMarkerMs = atMs;
                    break;
                }

                var nextMs = i + 1 < markers.Count && TryTimestampMs(markers[i + 1], out var n) ? n : (long?)null;
                words.Add(new KrcWord(atMs, nextMs is { } next ? Math.Max(0, next - atMs) : 0, seg));
                sb.Append(seg);
            }

            return (sb.ToString(), words, endMarkerMs);
        }

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

        private static (long EndMs, bool OutOfRange) TailEnd(long startMs, double? audioDurationSeconds)
        {
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
