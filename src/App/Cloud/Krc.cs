using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using JizuraLive.App.I18n;

namespace JizuraLive.App.Cloud;

public static class Krc
{
    private static readonly byte[] KrcKey =
    [
        0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47,
        0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69
    ];

        public static string Decrypt(byte[] data)
    {
        if (data.Length < 8 || data[0] != (byte)'k' || data[1] != (byte)'r' || data[2] != (byte)'c'
            || data[3] != (byte)'1')
            throw new InvalidDataException(L.T("StageUi.Media.KrcBadMagic"));
        var body = new byte[data.Length - 4];
        Array.Copy(data, 4, body, 0, body.Length);
        for (var i = 0; i < body.Length; i++) body[i] ^= KrcKey[i % 16];

        using var src = new MemoryStream(body, false);
        Stream inflater = body[0] == 0x78 && (body[0] & 0x0f) == 8
            ? new ZLibStream(src, CompressionMode.Decompress)
            : new DeflateStream(src, CompressionMode.Decompress);
        using (inflater)
        using (var ms = new MemoryStream())
        {
            inflater.CopyTo(ms);
            return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    private static readonly Regex ReTimed = new(@"^\[(\d+),(\d+)\](.*)$", RegexOptions.Compiled);
    private static readonly Regex ReWord = new(@"<(\d+),(\d+),\d+>", RegexOptions.Compiled);

        public static string ClassifyLangRows(IReadOnlyList<string> rows)
    {
        var latin = 0;
        var cjk = 0;
        foreach (var r in rows)
            foreach (var ch in r)
            {
                if (ch is >= 'a' and <= 'z' or >= 'A' and <= 'Z') latin++;
                else if (ch is >= '\u3040' and <= '\u30ff' or >= '\u3400' and <= '\u9fff'
                         or >= '\uac00' and <= '\ud7af') cjk++;
            }

        if (latin + cjk == 0) return "none";
        return latin >= cjk ? "roma" : "trans";
    }

    private static (List<string>? Roma, List<string>? Trans) ParseLangBlobs(string text)
    {
        var raw = text.Split('\n').FirstOrDefault(l => l.StartsWith("[language:", StringComparison.Ordinal));
        if (raw == null) return (null, null);
        var line = raw.Trim();         try
        {
            var b64 = line["[language:".Length..^1];
            var bin = Convert.FromBase64String(b64);
            var j = JObject.Parse(Encoding.UTF8.GetString(bin));             List<string>? roma = null, trans = null;
            foreach (var item in j["content"] as JArray ?? new JArray())
            {
                var rows = (item["lyricContent"] as JArray ?? new JArray())
                    .Select(blocks => blocks.Type == JTokenType.Array
                        ? string.Concat(((JArray)blocks).Select(x => x.ToString()))
                        : blocks.ToString())
                    .Select(s => s.Trim()).ToList();
                if (!rows.Any(r => r.Length > 0)) continue;
                switch (ClassifyLangRows(rows))
                {
                    case "roma" when roma == null: roma = rows; break;
                    case "trans" when trans == null: trans = rows; break;
                }
            }

            return (roma, trans);
        }
        catch
        {
            return (null, null);
        }
    }

    private static string FmtMs(long ms)
    {
        ms = Math.Max(0, ms);
        var cs = ms % 1000 / 10;
        return $"{ms / 60000:D2}:{ms / 1000 % 60:D2}.{cs:D2}";
    }

        public static string KrcToLrc(string text, bool wantRoma = true, bool wantTrans = true)
    {
        var blobs = ParseLangBlobs(text);
        var (roma, trans) = (wantRoma ? blobs.Roma : null, wantTrans ? blobs.Trans : null);
        var offsetMs = 0L;
        foreach (var tag in new[] { "offset", "manualoffset" })
        {
            var m = Regex.Match(text, @"^\[" + tag + @":(-?\d+)\]", RegexOptions.Multiline);
            if (m.Success) offsetMs += long.Parse(m.Groups[1].Value);
        }

        var outLines = new List<string>();
        var idx = -1;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            var m = ReTimed.Match(line);
            if (!m.Success) continue;
            idx++;
            var start = long.Parse(m.Groups[1].Value) - offsetMs;
            var words = new List<string>();
            foreach (Match w in ReWord.Matches(m.Groups[3].Value))
            {
                var after = m.Groups[3].Value[(w.Index + w.Length)..];
                var next = ReWord.Match(after);
                words.Add(next.Success ? after[..next.Index] : after);
            }

            var textLine = string.Concat(words).Trim();
            if (textLine.Length == 0)
            {
                outLines.Add("");
                continue;             }

            textLine = textLine.Replace("/", "／");
            var notes = new List<string>();
            foreach (var rows in new[] { roma, trans })
            {
                var s = rows != null && idx < rows.Count ? rows[idx] : null;
                if (string.IsNullOrWhiteSpace(s)) continue;
                s = s.Replace("/", "／").Trim();
                if (s.Length > 0 && s != textLine && !notes.Contains(s)) notes.Add(s);
            }

            outLines.Add("[" + FmtMs(start) + "]" + textLine + (notes.Count > 0 ? "|" + string.Join("　", notes) : ""));
        }

        return string.Join('\n', outLines).TrimEnd('\n') + "\n";
    }

    private static readonly Regex ReStdLrc =
        new(@"^\[(\d+):(\d{2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

        public static string MergeVariantLrc(string lrc, string? trans, string? roma)
    {
        var transMap = ParseStdLrcMap(trans);
        var romaMap = ParseStdLrcMap(roma);
        if (transMap.Count == 0 && romaMap.Count == 0) return lrc;

        var outLines = new List<string>();
        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = ReStdLrc.Match(line);
            if (!m.Success)
            {
                outLines.Add(line);
                continue;
            }

            var notes = new List<string>();
            if (transMap.TryGetValue(StdMs(m), out var t)) AddNote(notes, t);
            if (romaMap.TryGetValue(StdMs(m), out var r)) AddNote(notes, r);
            outLines.Add(notes.Count == 0 ? line : line + "|" + string.Join("　", notes));
        }

        return string.Join('\n', outLines);
    }

    private static Dictionary<long, string> ParseStdLrcMap(string? text)
    {
        var map = new Dictionary<long, string>();
        if (string.IsNullOrWhiteSpace(text)) return map;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var m = ReStdLrc.Match(line);
            if (!m.Success) continue;
            var value = ReStdLrc.Replace(line, "").Trim();
            if (value.Length == 0) continue;
            map[StdMs(m)] = value;
        }

        return map;
    }

    private static long StdMs(Match m)
    {
        long ms = 0;
        var frac = m.Groups[3].Value;
        if (frac.Length > 0)
        {
            ms = long.Parse(frac);
            if (frac.Length == 1) ms *= 100;
            else if (frac.Length == 2) ms *= 10;
        }

        return (long.Parse(m.Groups[1].Value) * 60 + long.Parse(m.Groups[2].Value)) * 1000 + ms;
    }

    private static void AddNote(List<string> notes, string text)
    {
        var value = text.Replace("/", "／").Trim();
        if (value.Length > 0 && !notes.Contains(value)) notes.Add(value);
    }

        public static string B64Text(string b64) => Encoding.UTF8.GetString(Convert.FromBase64String(b64));

    }
