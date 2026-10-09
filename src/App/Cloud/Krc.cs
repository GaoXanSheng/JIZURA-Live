using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace JizuraLive.App.Cloud;

/// <summary>逐字时间轴：一个字（或连续的一段字）在 [StartMs, StartMs+DurMs) 内唱出。</summary>
public sealed record KrcWord(long StartMs, long DurMs, string Text);

/// <summary>一行歌词：[StartMs,DurMs] 行时间 + 逐字时间轴。</summary>
public sealed record KrcLine(long StartMs, long DurMs, string Text, IReadOnlyList<KrcWord> Words);

/// <summary>
/// 酷狗 KRC 解密与解析，对位 frontend/src/cloud/krc.ts（逐字节同算法）：
/// 解密（krc1 魔数 + 16 字节 key 循环异或 + zlib inflate）→ 逐字时间轴解析（跟随模式高亮用）
/// → JIZURA 语法的 LRC 生成（音译/翻译注音随行）。
/// 另含网易云 LRC 的展开/重建（neParseLrc / neBuildLrc）。
/// 纯逻辑，无 IO 无 UI——KRC 加密样例的可 roundtrip 构造在 CloudTests 里。
/// </summary>
public static class Krc
{
    // 酷狗固定的解密 key（krc.ts 同款 16 字节）
    private static readonly byte[] KrcKey =
    [
        0x40, 0x47, 0x61, 0x77, 0x5E, 0x32, 0x74, 0x47,
        0x51, 0x36, 0x31, 0x2D, 0xCE, 0xD2, 0x6E, 0x69
    ];

    /// <summary>
    /// 解密 KRC：去 krc1 魔数 → body 按 key 循环异或 → inflate（Web 的 'deflate' 是 RFC1950
    /// zlib 包，对应 ZLibStream；个别源直接给裸 deflate，按头字节兜底 DeflateStream）。
    /// </summary>
    public static string Decrypt(byte[] data)
    {
        if (data.Length < 8 || data[0] != (byte)'k' || data[1] != (byte)'r' || data[2] != (byte)'c'
            || data[3] != (byte)'1')
            throw new InvalidDataException("不是 .krc 文件（缺少 krc1 魔数）");
        var body = new byte[data.Length - 4];
        Array.Copy(data, 4, body, 0, body.Length);
        for (var i = 0; i < body.Length; i++) body[i] ^= KrcKey[i % 16];

        using var src = new MemoryStream(body, false);
        // zlib 流头：CMF=0x78（低 4 位必须是 8 = deflate）
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

    // ---------------- 逐字时间轴解析（跟随模式高亮用） ----------------

    private static readonly Regex ReTimed = new(@"^\[(\d+),(\d+)\](.*)$", RegexOptions.Compiled);
    private static readonly Regex ReWord = new(@"<(\d+),(\d+),\d+>", RegexOptions.Compiled);

    /// <summary>
    /// 酷狗歌词响应 → 字幕层时间轴。fmt=krc 才有逐字：内容字段是 base64，必须先解码再解密，
    /// 直接把 base64 丢给 <see cref="Parse"/> 会一行都匹配不上（返回空表）——字幕通道「无渲染」
    /// 的元凶就是这个。其余格式（酷狗普通 LRC / 网易云）没有逐字，返回 null。
    /// </summary>
    public static List<KrcLine>? ParseKgLyric(string? fmt, string? contentBase64)
    {
        if (!string.Equals(fmt, "krc", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(contentBase64))
            return null;
        var lines = Parse(Decrypt(Convert.FromBase64String(contentBase64)));
        return lines.Count > 0 ? lines : null;
    }

    /// <summary>
    /// 解析解密后的 KRC 文本为行/字结构。空行（间奏）也是 timed 行（无字），按出现顺序保留，
    /// 供逐行对位（音译/翻译行序）使用；非 timed 行（元数据/语言块）跳过。
    /// </summary>
    public static List<KrcLine> Parse(string krcText)
    {
        var lines = new List<KrcLine>();
        foreach (var raw in krcText.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            var m = ReTimed.Match(line);
            if (!m.Success) continue;
            var body = m.Groups[3].Value;
            var words = new List<KrcWord>();
            Match? last = null;
            foreach (Match w in ReWord.Matches(body))
            {
                // 字文本 = 上一个字标签结束到本标签开始之间的原文
                if (last != null)
                {
                    var prev = body.Substring(last.Index + last.Length, w.Index - (last.Index + last.Length));
                    if (prev.Length > 0)
                        words.Add(new KrcWord(long.Parse(last.Groups[1].Value), long.Parse(last.Groups[2].Value), prev));
                }

                last = w;
            }

            if (last != null)
            {
                var tail = body[(last.Index + last.Length)..];
                if (tail.Length > 0)
                    words.Add(new KrcWord(long.Parse(last.Groups[1].Value), long.Parse(last.Groups[2].Value), tail));
            }

            lines.Add(new KrcLine(long.Parse(m.Groups[1].Value), long.Parse(m.Groups[2].Value),
                string.Concat(words.Select(w => w.Text)).Trim(), words));
        }

        return lines;
    }

    // ---------------- KRC → JIZURA LRC ----------------

    /// <summary>[language:] 块里的 type 字段不可靠（同一首歌可能 1 是翻译 0 是音译），
    /// 按字符脚本猜：拉丁为主算音译，假名/汉字/谚文为主算翻译。</summary>
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
        var line = raw.Trim(); // KRC 是 CRLF，不先去 \r 的话结尾 ] 会被算进 base64
        try
        {
            var b64 = line["[language:".Length..^1];
            var bin = Convert.FromBase64String(b64);
            var j = JObject.Parse(Encoding.UTF8.GetString(bin)); // language 块是 JSON 对象
            List<string>? roma = null, trans = null;
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

    /// <summary>
    /// KRC → JIZURA 语法的 LRC（对位 krcToLrc）：[mm:ss.cc]文本|音译　翻译；
    /// 空行 = JIZURA 的停顿；半角 / 是 JIZURA 的切镜头指令，统一换全角 ／。
    /// wantRoma/wantTrans 控制注音是否写入；间奏空行也占位（音译/翻译按第几条 timed 行对齐）。
    /// </summary>
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
                continue; // 空行 = JIZURA 的停顿
            }

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

    // ---------------- 网易云 LRC ----------------

    /// <summary>base64 → UTF8 文本（网易云歌词接口的 content/trans/roma 都是 base64）。</summary>
    public static string B64Text(string b64) => Encoding.UTF8.GetString(Convert.FromBase64String(b64));

    private static readonly Regex NeStamp = new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

    /// <summary>展开后的一行：多个时间戳拆成多条；ms 为毫秒，null = 没时间戳。</summary>
    public sealed record NeRow(long? Ms, string Text);

    /// <summary>
    /// 网易云 LRC 展开（对位 neParseLrc）：多时间戳行展开成多条；[ti:..] 之类元数据跳过；
    /// 制作信息行（一段 JSON {"c":[{"tx":..}]}）转纯文本。
    /// </summary>
    public static List<NeRow> NeParseLrc(string src)
    {
        var outRows = new List<NeRow>();
        foreach (var raw in (src ?? "").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            if (Regex.IsMatch(line, @"^\s*\{"""))
            {
                try
                {
                    var j = JObject.Parse(line.Trim());
                    var txt = string.Concat((j["c"] as JArray ?? new JArray())
                        .Select(p => p["tx"]?.ToString() ?? "")).Trim();
                    if (txt.Length > 0) outRows.Add(new NeRow(null, txt));
                }
                catch
                {
                    // 脏行丢弃
                }

                continue;
            }

            var stamps = new List<long>();
            var last = 0;
            Match m;
            while ((m = NeStamp.Match(line, last)).Success && m.Index == last)
            {
                var frac = m.Groups[3].Success
                    ? (m.Groups[3].Value + "00")[..3].PadRight(3, '0')
                    : "0";
                stamps.Add(long.Parse(m.Groups[1].Value) * 60000 + long.Parse(m.Groups[2].Value) * 1000 +
                           long.Parse(frac));
                last = m.Index + m.Length;
            }

            if (stamps.Count == 0)
            {
                // [ti:..] [offset:..] 这类元数据标签跳过；其他没时间戳的行留着，后面补默认时间
                if (Regex.IsMatch(line, @"^\s*\[[a-zA-Z]+:.*]\s*$")) continue;
                outRows.Add(new NeRow(null, line.Trim()));
                continue;
            }

            var text = line[last..].Trim();
            outRows.AddRange(stamps.Select(ms => new NeRow(ms, text)));
        }

        return outRows;
    }

    private static string NeFmtMs(long ms)
    {
        ms = Math.Max(0, ms);
        return $"{ms / 60000:D2}:{ms / 1000 % 60:D2}.{ms % 1000 / 10:D2}";
    }

    /// <summary>
    /// 网易云 LRC 重建（对位 neBuildLrc）：主 LRC + 翻译/音译按时间戳对齐成注音；
    /// 没时间戳的行（作词作曲之类）在上一句和下一句之间均摊时间；整首都没时间戳就不补，
    /// 交给 JIZURA 自动排时。
    /// </summary>
    public static string NeBuildLrc(IReadOnlyList<NeRow> lrc, IReadOnlyList<NeRow> trans,
        IReadOnlyList<NeRow> roma, bool wantRoma = true, bool wantTrans = true)
    {
        static Func<long, string> At(IReadOnlyList<NeRow> rows)
        {
            var m = new Dictionary<long, string>();
            foreach (var e in rows)
                if (e.Ms != null && e.Text.Length > 0)
                    m[(long)Math.Round(e.Ms.Value / 10.0)] = e.Text;
            return ms => m.TryGetValue((long)Math.Round(ms / 10.0), out var v) ? v : "";
        }

        var getRoma = At(roma);
        var getTrans = At(trans);
        var hasTimed = lrc.Any(e => e.Ms != null);
        var msArr = lrc.Select(e => e.Ms).ToArray();
        if (hasTimed)
        {
            for (var i = 0; i < lrc.Count; i++)
            {
                if (msArr[i] != null) continue;
                var prev = 0L;
                long? next = null;
                var runEnd = i;
                for (var j = i - 1; j >= 0; j--)
                    if (msArr[j] != null)
                    {
                        prev = msArr[j]!.Value;
                        break;
                    }

                for (var j = i + 1; j < lrc.Count; j++)
                    if (msArr[j] != null)
                    {
                        next = msArr[j];
                        break;
                    }

                while (runEnd < lrc.Count && msArr[runEnd] == null) runEnd++;
                var run = runEnd - i;
                for (var k = 0; k < run; k++)
                    msArr[i + k] = next != null
                        ? prev + (long)((next.Value - prev) * (k + 1) / (double)(run + 1))
                        : prev + 2000 * (k + 1);
                i = runEnd - 1;
            }
        }

        var outLines = new List<string>();
        for (var i = 0; i < lrc.Count; i++)
        {
            var e = lrc[i];
            var ts = hasTimed ? msArr[i] : e.Ms;
            if (ts == null)
            {
                if (e.Text.Length > 0) outLines.Add(e.Text.Replace("/", "／"));
                continue; // 全曲没时间戳时的兜底
            }

            var text = e.Text.Replace("/", "／");
            if (text.Length == 0)
            {
                outLines.Add("");
                continue; // 空行 = JIZURA 的停顿
            }

            var notes = new List<string>();
            foreach (var (get, want) in new[] { (getRoma, wantRoma), (getTrans, wantTrans) })
            {
                if (!want) continue;
                var s = get(ts.Value);
                if (s.Length > 0 && s != text && !notes.Contains(s)) notes.Add(s);
            }

            outLines.Add("[" + NeFmtMs(ts.Value) + "]" + text +
                         (notes.Count > 0 ? "|" + string.Join("　", notes) : ""));
        }

        return string.Join('\n', outLines).TrimEnd('\n') + "\n";
    }
}
