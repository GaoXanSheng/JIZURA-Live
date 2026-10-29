using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace JizuraLive.Engine;

public static class LrcTimingEditor
{
    private static readonly Regex KrcHeader = new(@"^\[(\d+),(\d+)\]", RegexOptions.Compiled);
    private static readonly Regex LrcStamp = new(@"\[(\d+):(\d{2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);
    private static readonly Regex AngleStamp = new(@"<(\d+):(\d{2})(?:[.:](\d{1,3}))?>", RegexOptions.Compiled);

    public static string RewriteLine(string raw, LyricClip oldClip, LyricClip newClip)
    {
        if (string.IsNullOrEmpty(raw) || oldClip.SourceIndex < 0 || newClip.StartMs < 0) return raw;

        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (oldClip.SourceIndex >= lines.Length) return raw;

        var line = lines[oldClip.SourceIndex];
        var trimmed = line.TrimStart();
        var indent = line[..^trimmed.Length];

        var krc = KrcHeader.Match(trimmed);
        if (krc.Success)
        {
            var duration = Math.Max(1, newClip.EndMs - newClip.StartMs);
            lines[oldClip.SourceIndex] = indent + $"[{newClip.StartMs},{duration}]" + trimmed[krc.Length..];
            return string.Join("\n", lines);
        }

        var stamps = LrcStamp.Matches(trimmed);
        if (stamps.Count == 0) return raw;

        Match? target = null;
        foreach (Match match in stamps)
        {
            if (!TryStampMs(match, out var ms)) continue;
            if (Math.Abs(ms - oldClip.StartMs) <= 2)
            {
                target = match;
                break;
            }
        }

        target ??= stamps[0];
        if (!TryStampMs(target, out var oldStartMs)) return raw;
        long delta = newClip.StartMs - oldStartMs;

        var body = trimmed[(target.Index + target.Length)..];
        if (delta != 0 && AngleStamp.IsMatch(body))
        {
            body = AngleStamp.Replace(body, match =>
            {
                if (!TryStampMs(match, out var ms)) return match.Value;
                return Stamp(Math.Max(0, ms + delta));
            });
        }

        lines[oldClip.SourceIndex] = indent + Stamp(newClip.StartMs) + body;
        return string.Join("\n", lines);
    }

    private static bool TryStampMs(Match match, out long ms)
    {
        ms = 0;
        if (!int.TryParse(match.Groups[1].Value, out var min)) return false;
        if (!int.TryParse(match.Groups[2].Value, out var sec)) return false;
        var frac = 0;
        if (match.Groups[3].Success)
        {
            var text = match.Groups[3].Value;
            frac = text.Length switch
            {
                1 => int.Parse(text, CultureInfo.InvariantCulture) * 100,
                2 => int.Parse(text, CultureInfo.InvariantCulture) * 10,
                _ => int.Parse(text, CultureInfo.InvariantCulture),
            };
        }

        ms = (min * 60L + sec) * 1000L + frac;
        return true;
    }

    private static string Stamp(long ms)
    {
        var value = Math.Max(0, ms);
        var minutes = value / 60000;
        var seconds = value / 1000 % 60;
        var millis = value % 1000;
        var sb = new StringBuilder(12);
        sb.Append('[');
        sb.Append(minutes.ToString("00", CultureInfo.InvariantCulture));
        sb.Append(':');
        sb.Append(seconds.ToString("00", CultureInfo.InvariantCulture));
        sb.Append('.');
        sb.Append(millis.ToString("000", CultureInfo.InvariantCulture));
        sb.Append(']');
        return sb.ToString();
    }
}
