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
    /// </summary>
    public static class LrcParser
    {
        private static readonly Regex TimestampRegex = new(@"\[(\d{1,2}):(\d{2})(?:\.(\d{1,3}))?\]", RegexOptions.Compiled);

        public static List<KrcLine> Parse(string? raw)
        {
            var result = new List<KrcLine>();
            if (string.IsNullOrWhiteSpace(raw)) return result;

            var rawLines = raw.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var parsed = new List<(long StartMs, string Text)>();

            foreach (var line in rawLines)
            {
                var text = line.Trim();
                if (string.IsNullOrEmpty(text)) continue;

                var matches = TimestampRegex.Matches(text);
                if (matches.Count == 0) continue;

                var lyricText = TimestampRegex.Replace(text, "").Trim();

                foreach (Match m in matches)
                {
                    if (int.TryParse(m.Groups[1].Value, out var min) &&
                        int.TryParse(m.Groups[2].Value, out var sec))
                    {
                        var frac = 0;
                        if (m.Groups[3].Success)
                        {
                            var fracStr = m.Groups[3].Value;
                            if (fracStr.Length == 2) frac = int.Parse(fracStr) * 10;
                            else if (fracStr.Length == 3) frac = int.Parse(fracStr);
                            else if (fracStr.Length == 1) frac = int.Parse(fracStr) * 100;
                        }

                        var totalMs = (min * 60 + sec) * 1000L + frac;
                        parsed.Add((totalMs, lyricText));
                    }
                }
            }

            parsed.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));

            for (var i = 0; i < parsed.Count; i++)
            {
                var cur = parsed[i];
                long durMs = 3000;
                if (i + 1 < parsed.Count)
                {
                    var next = parsed[i + 1];
                    durMs = Math.Clamp(next.StartMs - cur.StartMs, 500, 15000);
                }

                result.Add(new KrcLine(cur.StartMs, durMs, cur.Text));
            }

            return result;
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
                if (tMs <= line.StartMs + line.DurMs) return match;
            }

            return -1;
        }
    }
}
