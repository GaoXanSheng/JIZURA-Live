// 移植自 frontend/src/engine/08_planner.js
//
// 歌词 → 行 → 词块 → 带时刻切分 + 事件的规划器。逐行对齐 JS：
// RNG 消费顺序、分支条件、clamp 边界与 JS 一致（同 seed 编排可复现）。
// 对位关系：
//   J.plan(project, audio)                → Planner.Plan(Project, AudioData?)
//   J.parseLyrics / J.parseOrderV1        → Planner.ParseLyrics / ParseOrderV1
//   J.computeTiming                       → Planner.ComputeTiming
//   J.segments / chunkText / phraseChunks → Planner.Segments / ChunkText / PhraseChunks
//   J.joinWords / isWordLike / isLatinText→ Planner.JoinWords / IsWordLike / IsLatinText
//   J.cutAt（09_render.js 开头）          → Planner.CutAt(Plan, double)
//   J.previewPlan / designSize / outputSize→ Planner.PreviewPlan / DesignSize / OutputSize
//   J.komaOf / J.stepDur                  → Planner.KomaOf / StepDur
//   J.lineSnapshot                        → Planner.LineSnapshot
//   J.sideZones                           → Planner.SideZones
//   J.LAYOUTS/ENTER/HOLD/EXIT/...         → Reg.Layouts/Enter/Hold/Exit/...
//   plan.evOwner（WeakMap）               → Plan.EvOwner（Dictionary<PlanEvent, Cut>）
//   J.resolveStyle / J.keyMode            → JStyles.ResolveStyle / KeyMode
//   J.resolveLang / J.setLang             → JLang.ResolveLang / SetLang
//   J.setTypeset                          → 省略（只影响排版量测；Plan.Typeset 已带，M1 无排版）

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Engine;

public static class Planner
{
    // ---------------- 解析（J.parseLyrics / J.parseOrderV1） ----------------

    /// <summary>parseLyrics 的单行结果（JS 的松散对象 base）。Emph/Manual 与 LRC 展开行共享引用，同 JS。</summary>
    public sealed class ParsedLine
    {
        public string Text = "";
        public bool Interlude;
        public double? Secs;
        public string? Note;
        public bool Impact;
        public List<string> Emph = new();
        public List<string>? Manual;
        public bool GapBefore;
        public int Src;
        public double? Lrc;
    }

    /// <summary>parseLyrics 的返回 {lines, meta}。meta 是 LRC 标签（ti/ar/al/by/offset）。</summary>
    public sealed class ParsedLyrics
    {
        public List<ParsedLine> Lines = new();
        public Dictionary<string, string> Meta = new();
    }

    private static readonly Regex MetaTagRe =
        new(@"^\[(ti|ar|al|by|offset):(.*)\]$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TimeTagRe =
        new(@"^\[(\d+):(\d+(?:[.:]\d+)?)\]", RegexOptions.Compiled);
    private static readonly Regex InterludeRe = new(
        @"^\[\s*(間奏|间奏|interlude|instrumental|inst|간주)(?:\s*[:：]?\s*(\d+(?:\.\d+)?)\s*(?:s|sec|秒|초)?)?\s*\]$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ImpactRe = new("[!！]$", RegexOptions.Compiled);
    private static readonly Regex EmphRe = new(@"\*([^*]+)\*", RegexOptions.Compiled);
    private static readonly Regex LatinRe = new("[A-Za-z]", RegexOptions.Compiled);

    /// <summary>J.parseLyrics：LRC 标签/间奏/记号（|备注、!强、*强调*、/手工切分）解析 + 时间排序。</summary>
    public static ParsedLyrics ParseLyrics(string? raw)
    {
        var lines = new List<ParsedLine>();
        var meta = new Dictionary<string, string>();
        var pendingGap = false;
        var rows = (raw ?? "").Replace("\r", "").Split('\n');
        for (var ri = 0; ri < rows.Length; ri++)
        {
            var s0 = rows[ri].Trim();
            if (s0.Length == 0)
            {
                if (lines.Count > 0) pendingGap = true;
                continue;
            }
            if (s0.StartsWith("#")) continue;
            var mm = MetaTagRe.Match(s0);
            if (mm.Success)
            {
                meta[mm.Groups[1].Value.ToLowerInvariant()] = mm.Groups[2].Value.Trim();
                continue;
            }
            var s = s0;
            var times = new List<double>();
            Match m;
            while ((m = TimeTagRe.Match(s)).Success)
            {
                times.Add(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60
                          + double.Parse(m.Groups[2].Value.Replace(":", "."), CultureInfo.InvariantCulture));
                s = s[m.Length..];
            }
            s = s.Trim();
            // 間奏：[間奏] / [間奏 8]（8 秒），也认 [interlude] [inst] [间奏] [간주]；没歌词，只出背景和装饰
            var im = InterludeRe.Match(s);
            if (im.Success)
            {
                var secs = im.Groups[2].Success ? double.Parse(im.Groups[2].Value, CultureInfo.InvariantCulture) : (double?)null;
                var gapBeforeI = pendingGap; // JS 的 base 在置 false 前取 pendingGap
                void PushInter(double? lrc)
                {
                    lines.Add(new ParsedLine
                    {
                        Text = "",
                        Interlude = true,
                        Secs = secs,
                        Note = null,
                        Impact = false,
                        Emph = new List<string>(),
                        Manual = null,
                        GapBefore = gapBeforeI,
                        Src = ri,
                        Lrc = lrc
                    });
                }
                pendingGap = false;
                if (times.Count > 0) foreach (var t in times) PushInter(t);
                else PushInter(null);
                continue;
            }
            string? note = null;
            var bar = s.IndexOf('|');
            if (bar >= 0)
            {
                var nt = s[(bar + 1)..].Trim();
                note = nt.Length > 0 ? nt : null;
                s = s[..bar].Trim();
            }
            var impact = false;
            if (ImpactRe.IsMatch(s) && s.Length > 1)
            {
                impact = true;
                s = s[..^1].Trim();
            }
            var emph = new List<string>();
            s = EmphRe.Replace(s, mt =>
            {
                emph.Add(mt.Groups[1].Value);
                return mt.Groups[1].Value;
            });
            List<string>? manual = null;
            if (s.Contains('/'))
            {
                manual = s.Split('/').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                var latin = manual.Any(x => LatinRe.IsMatch(x));
                s = string.Join(latin ? " " : "", manual);
            }
            if (s.Length == 0) continue;
            var text = s;
            var gapBeforeL = pendingGap; // JS 的 base 在置 false 前取 pendingGap
            void PushLine(double? lrc)
            {
                lines.Add(new ParsedLine
                {
                    Text = text,
                    Note = note,
                    Impact = impact,
                    Emph = emph,
                    Manual = manual,
                    GapBefore = gapBeforeL,
                    Src = ri,
                    Lrc = lrc
                });
            }
            pendingGap = false;
            if (times.Count > 0) foreach (var t in times) PushLine(t);
            else PushLine(null);
        }
        // LRC：按时间排序；无戳行（间奏、手工补的行）跟在文本里前一行后面（v0.10 起）
        if (lines.Any(l => l.Lrc != null))
        {
            double key = -1;
            var k = 0;
            var keyed = new List<(ParsedLine L, double Key)>();
            foreach (var l in lines)
            {
                double kk;
                if (l.Lrc != null)
                {
                    key = l.Lrc.Value;
                    k = 0;
                    kk = l.Lrc.Value;
                }
                else kk = key + 1e-6 * ++k;
                keyed.Add((l, kk));
            }
            lines = keyed.OrderBy(x => x.Key).Select(x => x.L).ToList(); // LINQ OrderBy 稳定排序，同 JS
        }
        return new ParsedLyrics { Lines = lines, Meta = meta };
    }

    /// <summary>
    /// v0.10 之前的行序（无戳行排最后），只用来把老工程的逐行设置搬过来。
    /// 返回旧索引 → 新索引；无混合时间戳时返回 null。
    /// </summary>
    public static int[]? ParseOrderV1(string? raw)
    {
        var lines = ParseLyrics(raw).Lines;
        if (!lines.Any(l => l.Lrc != null) || lines.All(l => l.Lrc != null)) return null;
        return Enumerable.Range(0, lines.Count)
            .OrderBy(i => lines[i].Lrc ?? 1e9)
            .ThenBy(i => lines[i].Src)
            .ToArray();
    }

    // ---------------- 分词 / 词块（J.segments / chunkText / phraseChunks / joinWords） ----------------

    private static List<string> CodePoints(string s)
    {
        var list = new List<string>(s.Length);
        for (var i = 0; i < s.Length; )
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                list.Add(s.Substring(i, 2));
                i += 2;
            }
            else
            {
                list.Add(s[i].ToString());
                i++;
            }
        }
        return list;
    }

    private static int CpLen(string s) => CodePoints(s).Count;

    private static string LastCp(string s)
    {
        if (s.Length == 0) return "";
        var i = s.Length - 1;
        if (char.IsLowSurrogate(s[i]) && i > 0 && char.IsHighSurrogate(s[i - 1])) return s.Substring(i - 1, 2);
        return s[i].ToString();
    }

    private static string FirstCp(string s)
    {
        if (s.Length == 0) return "";
        if (char.IsHighSurrogate(s[0]) && s.Length > 1 && char.IsLowSurrogate(s[1])) return s.Substring(0, 2);
        return s[0].ToString();
    }

    // WORDCH = /[A-Za-z\u00c0-\u024f0-9\uac00-\ud7af\u1100-\u11ff\u3130-\u318f]/（\u024f 后的 0-9 是独立区间）
    private static readonly Regex WordChRe =
        new("[A-Za-z\u00c0-\u024f0-9\uac00-\ud7af\u1100-\u11ff\u3130-\u318f]", RegexOptions.Compiled);
    private static readonly Regex WordLikeRe =
        new("^[A-Za-z\u00c0-\u024f0-9\uac00-\ud7af'’.,!?‐–—-]+$", RegexOptions.Compiled);
    private static readonly Regex HangulRe = new("[\uac00-\ud7af]", RegexOptions.Compiled);
    private static readonly Regex TailDashRe = new("[-‐–—]$", RegexOptions.Compiled);
    private static readonly Regex WsRe = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex DashSplitRe = new("(?<=[-‐–—])(?=\\S)", RegexOptions.Compiled);

    /// <summary>J.isWordLike：拉丁/韩文词（词间补空格用）。</summary>
    public static bool IsWordLike(string t) => WordLikeRe.IsMatch(t) && WordChRe.IsMatch(t);

    private static bool CpIsWs(string cp) => cp.Length == 1 && char.IsWhiteSpace(cp[0]);
    private static bool CpIsPunct(string cp) => cp.Length == 1 && J.IsPunct(cp[0]);
    private static bool CpIsKanji(string cp) => cp.Length == 1 && J.IsKanji(cp[0]);
    private static bool CpIsHira(string cp) => cp.Length == 1 && (J.IsHira(cp[0]) || cp[0] == 'ー');
    private static bool CpIsKata(string cp) => cp.Length == 1 && (J.IsKata(cp[0]) || cp[0] == 'ー');

    private static char SegType(string s)
    {
        var cps = CodePoints(s);
        if (cps.Count > 0 && cps.All(CpIsWs)) return 'S';
        if (cps.All(CpIsPunct)) return 'P';
        if (cps.Any(CpIsKanji)) return 'K';
        if (cps.All(CpIsHira)) return 'H';
        if (cps.All(CpIsKata)) return 'T';
        if (Regex.IsMatch(s, "[A-Za-z0-9]")) return 'L';
        return 'O';
    }

    /// <summary>
    /// J.segments：按字类切分。TODO ICU word segmentation：JS 的 Intl.Segmenter 分支省略，
    /// 直接用文件里的字符类回退算法（segType）；连字/汉字按类聚合，假名可附在汉字块后。
    /// </summary>
    public static List<string> Segments(string text)
    {
        var outList = new List<string>();
        var cur = new StringBuilder();
        var ct = '\0';
        var hasCur = false;
        foreach (var c in CodePoints(text))
        {
            var t = SegType(c);
            if (hasCur && cur.Length > 0 && t != ct && !(ct == 'K' && t == 'H'))
            {
                outList.Add(cur.ToString());
                cur.Clear();
            }
            cur.Append(c);
            ct = t;
            hasCur = true;
        }
        if (cur.Length > 0) outList.Add(cur.ToString());
        return outList;
    }

    /// <summary>J.joinWords：拼接词序列，词间补空格（拉丁/谚文），连字符后不补。</summary>
    public static string JoinWords(IReadOnlyList<string> arr)
    {
        var latin = LatinRe.IsMatch(string.Concat(arr));
        var acc = "";
        for (var i = 0; i < arr.Count; i++)
        {
            var w = arr[i];
            if (i == 0)
            {
                acc = w;
                continue;
            }
            if (TailDashRe.IsMatch(acc))
            {
                acc += w;
                continue;
            }
            var sep = latin || (HangulRe.IsMatch(LastCp(acc)) && HangulRe.IsMatch(FirstCp(w))) ? " " : "";
            acc += sep + w;
        }
        return acc;
    }

    /// <summary>J.isLatinText：以拉丁字母为主的文本（英语、印尼语、越南语……歌词）。</summary>
    public static bool IsLatinText(string? t)
    {
        var s = Regex.Replace(t ?? "", @"\s", "");
        if (s.Length == 0) return false;
        var cps = CodePoints(s);
        var n = cps.Count(cp => cp.Length == 1 && cp[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '\u00c0' and <= '\u024f' or >= '\u1e00' and <= '\u1eff');
        return n / (double)cps.Count >= 0.6;
    }

    private sealed class ChunkCur
    {
        public string S = "";
        public char K;
        public bool HasH;
    }

    /// <summary>J.chunkText：分词结果聚成词块（≤10 字），拆超长块、合并落单假名。</summary>
    public static List<string> ChunkText(string text)
    {
        var segs = Segments(text);
        var chunks = new List<string>();
        ChunkCur? cur = null;
        void Close()
        {
            if (cur != null && cur.S.Trim().Length > 0) chunks.Add(cur.S.Trim());
            cur = null;
        }
        foreach (var sg in segs)
        {
            var t = SegType(sg);
            if (t == 'S')
            {
                Close();
                continue;
            }
            if (t == 'P')
            {
                if (cur != null) cur.S += sg;
                else if (chunks.Count > 0) chunks[^1] += sg;
                else cur = new ChunkCur { S = sg, K = 'P', HasH = false };
                continue;
            }
            if (cur == null)
            {
                cur = new ChunkCur { S = sg, K = t, HasH = t == 'H' };
                continue;
            }
            if (t == 'H')
            {
                var len = CpLen(sg);
                if (len <= 3 || (cur.K != 'H' && !cur.HasH) || (cur.K == 'H' && CpLen(cur.S) + len <= 4))
                {
                    cur.S += sg;
                    cur.HasH = true;
                    continue;
                }
                Close();
                cur = new ChunkCur { S = sg, K = 'H', HasH = true };
                continue;
            }
            if (t == 'K' && cur.K == 'K' && !cur.HasH && CpLen(cur.S + sg) <= 6)
            {
                cur.S += sg;
                continue;
            }
            if (t == 'T' && cur.K == 'T')
            {
                cur.S += sg;
                continue;
            }
            if (t == 'L' && cur.K == 'L')
            {
                cur.S += sg;
                continue;
            }
            Close();
            cur = new ChunkCur { S = sg, K = t, HasH = t == 'H' };
        }
        Close();
        // 拆超长词块，合并落单的假名
        var outList = new List<string>();
        foreach (var c in chunks)
        {
            var n = CpLen(c);
            if (n > 10 && IsWordLike(c))
            {
                // 很长的拉丁/韩文词绝不在词中间切，只在连字符后面分（never-ending → never- / ending）
                var parts = DashSplitRe.Split(c);
                if (parts.Length > 1) outList.AddRange(parts);
                else outList.Add(c);
            }
            else if (n > 10)
            {
                outList.AddRange(JLayouts.SplitLines(c, (int)Math.Ceiling(n / Math.Ceiling(n / 8.0))).Split('\n'));
            }
            else outList.Add(c);
        }
        for (var i = outList.Count - 1; i > 0; i--)
        {
            var cpI = CodePoints(outList[i]);
            if (cpI.Count == 1 && !CpIsKanji(cpI[0]))
            {
                var prevLast = LastCp(outList[i - 1]);
                var joiner = WordChRe.IsMatch(prevLast) && WordChRe.IsMatch(cpI[0]) && !TailDashRe.IsMatch(outList[i - 1]) ? " " : "";
                outList[i - 1] += joiner + outList[i];
                outList.RemoveAt(i);
            }
        }
        return outList.Count > 0 ? outList : new List<string> { text };
    }

    /// <summary>J.phraseChunks：英文歌词按短语切，不逐词（一个日语词块约相当于 2–3 个英文单词）。</summary>
    public static List<string> PhraseChunks(IReadOnlyList<string> words)
    {
        var outList = new List<string>();
        var cur = new List<string>();
        var letters = 0;
        void Flush()
        {
            if (cur.Count > 0) outList.Add(JoinWords(cur));
            cur = new List<string>();
            letters = 0;
        }
        foreach (var w in words)
        {
            var n = Regex.Matches(w, "[A-Za-z\u00c0-\u024f0-9]").Count;
            cur.Add(w);
            letters += n;
            if (letters >= 9 || cur.Count >= 3 || Regex.IsMatch(w, "[,.;:!?]$")) Flush();
        }
        Flush();
        // 结尾落单的短词并进上一段
        if (outList.Count >= 2 && Regex.Replace(outList[^1], "[^A-Za-z]", "").Length <= 4)
        {
            var last = outList[^1];
            outList.RemoveAt(outList.Count - 1);
            outList[^1] = JoinWords(new List<string> { outList[^1], last });
        }
        return outList.Count > 0 ? outList : words.ToList();
    }

    // ---------------- 时刻估算（J.computeTiming） ----------------

    /// <summary>
    /// J.computeTiming：行起点/终点/总时长。
    /// 手工时刻（lineTimes）优先于 LRC 标签，估算行走 beat 量化 + 挤进固定时刻间隙。
    /// </summary>
    public static (double[] Starts, double[] Ends, double Duration) ComputeTiming(
        Project project, ParsedLyrics parsed, AudioData? audio)
    {
        var T = project.Timing ?? new TimingSettings();
        var lines = parsed.Lines;
        var beat = T.Bpm > 0 ? 60 / T.Bpm : 0;
        // 固定时刻：手工设定的时间（打字/打点/拖动）优先于 LRC 标签，其余行走估算
        var fixedT = new double?[lines.Count];
        for (var i = 0; i < lines.Count; i++)
        {
            double? man = null;
            if (T.LineTimes != null && T.LineTimes.TryGetValue(i.ToString(), out var mv)) man = mv;
            if (man != null && double.IsFinite(man.Value)) fixedT[i] = Math.Max(0, man.Value);
            else if (lines[i].Lrc is { } lrc2 && double.IsFinite(lrc2)) fixedT[i] = lrc2;
        }
        // 固定时刻不能倒退到更早的固定时刻之前（保存的值本身不动）
        double lastFix = double.NegativeInfinity;
        for (var i = 0; i < fixedT.Length; i++)
            if (fixedT[i] != null)
            {
                if (fixedT[i]!.Value < lastFix + 0.05) fixedT[i] = lastFix + 0.05;
                lastFix = fixedT[i]!.Value;
            }

        double LineScale(TimingSettings t2) => t2.LineScale != 0 ? t2.LineScale : 1;
        double Natural(int i)
        {
            var n = CpLen(lines[i].Text);
            var L = lines[i];
            var d = L.Interlude ? (L.Secs > 0 ? L.Secs.Value : 4) : J.Clamp(0.8 + n * 0.17, 1.3, 5.2) * LineScale(T);
            if (beat != 0 && !(L.Interlude && L.Secs > 0))
                d = Math.Max(2, Math.Round(d / beat, MidpointRounding.AwayFromZero)) * beat;
            return d;
        }
        double GapOf(int i) => i > 0 && i < lines.Count && lines[i].GapBefore ? (beat != 0 ? beat * 2 : 0.8) : 0;

        var starts = new double[lines.Count];
        var i2 = 0;
        var t = T.Offset;
        if (fixedT.Length > 0 && fixedT[0] != null) t = fixedT[0]!.Value;
        while (i2 < lines.Count)
        {
            if (fixedT[i2] != null)
            {
                starts[i2] = fixedT[i2]!.Value;
                t = starts[i2];
                i2++;
                continue;
            }
            // 一段估算行 [i, j)，夹在上一个起点与下一个固定时刻之间（若有的话）
            var j = i2;
            while (j < lines.Count && fixedT[j] == null) j++;
            if (i2 == 0 && j < lines.Count && t >= fixedT[j]!.Value) t = 0;
            var from = i2 > 0 ? starts[i2 - 1] : t;
            // 链式推进方式与旧版一致（无固定时刻时 plan 结果完全不变）
            for (var k = i2; k < j; k++) starts[k] = k > 0 ? starts[k - 1] + Natural(k - 1) + GapOf(k) : t;
            if (j < lines.Count)
            {
                // 这些行都必须在下一个固定时刻之前开始：把它们挤进可用的空间里
                var room = fixedT[j]!.Value - from;
                var need = starts[j - 1] + Natural(j - 1) + GapOf(j) - from;
                if (need > room)
                {
                    var f = room > 0 ? room / need : 0;
                    for (var k = i2; k < j; k++) starts[k] = from + (starts[k] - from) * f;
                }
            }
            i2 = j;
        }
        var ends = new double[lines.Count];
        for (var i = 0; i < starts.Length; i++)
        {
            if (i < starts.Length - 1)
            {
                ends[i] = Math.Max(starts[i] + 0.35, starts[i + 1]);
                continue;
            }
            var n = CpLen(lines[i].Text);
            var L = lines[i];
            var d = L.Interlude ? (L.Secs > 0 ? L.Secs.Value : 4) : J.Clamp(0.8 + n * 0.17, 1.5, 5.2) * LineScale(T);
            if (beat != 0 && !(L.Interlude && L.Secs > 0))
                d = Math.Max(2, Math.Round(d / beat, MidpointRounding.AwayFromZero)) * beat;
            ends[i] = starts[i] + d;
        }
        var duration = (ends.Length > 0 ? ends[^1] : 3) + T.Tail;
        if (audio != null && audio.Duration != 0 && (T.UseAudioLength ?? true))
            duration = Math.Max(audio.Duration, ends.Length > 0 ? ends[^1] + 0.2 : 1);
        return (starts, ends, duration);
    }

    // ---------------- koma / step（J.komaOf / J.stepDur） ----------------

    /// <summary>
    /// J.komaOf：24fps 时间轴上每秒作画张数（12 = 二拍一，8 = 三拍一，0 = 每个输出帧）。
    /// 逐字对位 JS：fx.koma != null ? +fx.koma : (fx.onTwos === false ? 0 : 12)——
    /// 显式档位永远优先；没写这个键才回退看 onTwos。C# 用可空 Koma 表达「工程里没有这个键」。
    /// 本 fork 的缺省是 Koma=null + OnTwos=false → 全帧（上游 JS 那个缺省是 onTwos=true → 12）。
    /// </summary>
    public static int KomaOf(FxSettings fx) => fx.Koma ?? (fx.OnTwos ? 12 : 0);

    public static double StepDur(FxSettings fx, int fps)
    {
        var k = KomaOf(fx);
        return k > 0 ? 1.0 / k : 1.0 / (fps != 0 ? fps : 24);
    }

    // ---------------- 覆盖/开关读取助手（JS duck-typed 访问的 C# 形态） ----------------

    /// <summary>JS 真值判定（if (x) 语义）。</summary>
    private static bool Truthy(JToken? t) => t switch
    {
        null or JValue { Value: null } => false,
        JValue { Value: string s } => s.Length > 0,
        JValue { Value: bool b } => b,
        JValue { Value: long l } => l != 0,
        JValue { Value: double d } => d != 0 && !double.IsNaN(d),
        _ => true
    };

    private static string? Str(JObject? o, string k)
        => o?[k] is JValue { Type: JTokenType.String } v ? (string?)v.Value : null;

    private static double? Num(JObject? o, string k)
        => o?[k] is JValue { Type: JTokenType.Float or JTokenType.Integer } v ? (double?)v : null;

    private static bool EnOn(Dictionary<string, Dictionary<string, bool>> en, string g, string k)
        => en.TryGetValue(g, out var m) && m.TryGetValue(k, out var v) && v; // JS 真值检查

    private static bool EnNotOff(Dictionary<string, Dictionary<string, bool>> en, string g, string k)
        => !(en.TryGetValue(g, out var m) && m.TryGetValue(k, out var v) && !v); // JS !== false 检查

    private static JObject? BiasOf(StyleDef st, string g) => st.Bias?[g] as JObject;

    /// <summary>wkey(obj, k, d)：obj[k] 存在且为数值时取之，否则用默认。</summary>
    private static double WKey(JObject? obj, string k, double d)
    {
        var v = obj?[k];
        return v != null && v.Type is JTokenType.Float or JTokenType.Integer ? (double)v : d;
    }

    private static FxSettings CloneFx(FxSettings? f) => new()
    {
        Motion = f?.Motion ?? 0.7,
        Glitch = f?.Glitch ?? 0.55,
        Chroma = f?.Chroma ?? 0.7,
        Decor = f?.Decor ?? 0.5,
        Density = f?.Density ?? 0.55,
        Texture = f?.Texture ?? 0.6,
        Flash = f?.Flash ?? true,
        OnTwos = f?.OnTwos ?? false,
        Koma = f?.Koma,
        Hud = f?.Hud ?? "auto",
        BgSwitch = f?.BgSwitch ?? 0.35,
        HideNo = f?.HideNo ?? false,
        HideTime = f?.HideTime ?? false
    };

    private static Zone? CloneZone(Zone? z) => z == null
        ? null
        : new Zone { X = z.X, Y = z.Y, W = z.W, H = z.H, Side = z.Side };

    /// <summary>单切分指定（ov.cutTech / ov.cutLayouts 合并），JS cutTechOf。</summary>
    private static JObject CutTechOf(JObject? ov, int k)
    {
        var t = (ov?["cutTech"] as JObject)?[k.ToString()] as JObject ?? new JObject();
        var fromLay = (ov?["cutLayouts"] as JObject)?[k.ToString()];
        if (fromLay != null && t["layout"] == null)
        {
            var merged = (JObject)t.DeepClone();
            merged["layout"] = fromLay.DeepClone();
            return merged;
        }
        return t;
    }

    private static string? TechStr(JObject tech, string k) => Str(tech, k);

    // ---------------- 主规划（J.plan） ----------------

    /// <summary>抽取结果的历史条目（JS 的 hist 对象），novelty/pickDecor 用。</summary>
    private sealed class Hist
    {
        public string? Layout;
        public string? Enter;
        public string? Exit;
        public string? Hold;
        public string? Treat;
        public string? Cam;
        public string? Trans;
        public List<string> Decor = new();
    }

    /// <summary>切分单元（units：{text, w, recap}）。</summary>
    private sealed class Unit
    {
        public string Text = "";
        public double W;
        public bool Recap;
    }

    /// <summary>統一感的重复行规格（remember 存、again 取）。</summary>
    private sealed class AgainSpec
    {
        public string Text = "";
        public string Layout = "";
        public string Enter = "";
        public string Exit = "";
        public string Hold = "";
        public JObject Params = new();
        public List<DecorSpec> Decor = new();
        public string Treat = "";
        public JObject TreatP = new();
        public string Cam = "";
        public JObject CamP = new();
        public int Scheme;
        public uint Seed;
        public string Bg = "";
        public JObject BgP = new();
        public bool WeightGrow;
        public bool Morph;
        public string? Trans;
        public JObject TransP = new();
        public double TransDur;
    }

    private static void AddEvent(Plan plan, double t, string type, double amp, double dur)
        => plan.Events.Add(new PlanEvent { T = t, Type = type, Amp = amp, Dur = dur });

    /// <summary>J.plan：歌词 → 行 → 词块 → 带时刻切分 + 事件。</summary>
    public static Plan Plan(Project project, AudioData? audio)
    {
        var st = JStyles.ResolveStyle(project);
        var fx = CloneFx(project.Fx);
        var parsed = ParseLyrics(project.Lyrics);
        var title = !string.IsNullOrEmpty(project.Title) ? project.Title : parsed.Meta.GetValueOrDefault("ti") ?? "";
        var artist = !string.IsNullOrEmpty(project.Artist) ? project.Artist : parsed.Meta.GetValueOrDefault("ar") ?? "";
        var tm = ComputeTiming(project, parsed, audio);
        // 文字整列：歌词比人声早 0.2s 出现，提前读着才觉得合拍
        if (project.Typeset)
        {
            const double lead = 0.2;
            tm.Starts = tm.Starts.Select(x => Math.Max(0, x - lead)).ToArray();
            tm.Ends = tm.Ends.Select((x, i) => Math.Max(tm.Starts[i] + 0.3, x - lead)).ToArray();
        }
        var (W, H) = DesignSize(project.Aspect);
        // enabled：没显式关掉的都算开（老工程里新手法包的条目自动可用）；
        // 追加分/和風 开关决定随机能选什么（逐行 override 不受影响）
        var en = new Dictionary<string, Dictionary<string, bool>>();
        foreach (var g in Reg.GroupKeys)
        {
            var m = new Dictionary<string, bool>();
            var src = project.Enabled != null && project.Enabled.TryGetValue(g, out var sv) ? sv : null;
            foreach (var k in Reg.OrderOf(g))
                m[k] = (src == null || !src.TryGetValue(k, out var v) || v) && Reg.RandomAllows(project, g, k);
            en[g] = m;
        }
        // 中央を空ける（给角色留位）：每个切分排进侧边条带，宽帧左右、竖帧上下，逐行交替；中间只留整帧背景和屏幕特效
        var zones = project.CenterFree ? SideZones(W, H, project.CenterDir, project.CenterGap, project.CenterZone) : null;
        // 每个切分的歌词拆两半：前半进条带 0（左/上），后半进条带 1（右/下）
        Zone? ZoneOf() => zones == null ? null : CloneZone(zones[0]);
        if (zones != null && en.TryGetValue("bg", out var enBg)) enBg["bigChar"] = false; // 唯一会自己画歌词的背景（大字居中）

        var plan = new Plan
        {
            Version = 1,
            Generator = "JIZURA",
            AppVersion = null, // JS 的 '@VERSION@' 构建期替换；C# 侧由宿主按需补
            Title = title,
            Artist = artist,
            W = W,
            H = H,
            Fps = project.Fps != 0 ? project.Fps : 24,
            Duration = tm.Duration,
            StyleKey = project.Style,
            Style = st,
            Fx = fx,
            Seed = project.Seed,
            Lines = new List<PlanLine>(),
            Cuts = new List<Cut>(),
            Events = new List<PlanEvent>(),
            Beats = audio?.Beats != null ? (double[])audio.Beats.Clone() : Array.Empty<double>(),
            Hud = fx.Hud == "on" ? true : fx.Hud == "off" ? false : st.Hud,
            KeyBg = JStyles.KeyMode(project), // 'green' | 'black' | null，抠像合成背景
            CenterFree = zones != null,
            Zones = zones,
            Typeset = project.Typeset,
            Unify = project.Unify,
            Lang = JLang.ResolveLang(project), // 歌词语言（auto → 检测结果）
        };
        JLang.SetLang(plan.Lang); // 下面的切词和排版量测都用这个语言
        // （JS 的 J.setTypeset(plan.typeset) 只影响排版量测，M1 无排版——省略调用点）

        var beats = plan.Beats;
        double Snap(double t2)
        {
            if (beats.Length == 0 || !project.Timing.Snap) return t2;
            int lo = 0, hi = beats.Length - 1;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (beats[mid] < t2) lo = mid + 1;
                else hi = mid;
            }
            var best = t2;
            var bd = 0.13;
            foreach (var k in new[] { lo - 1, lo })
                if (k >= 0 && k < beats.Length && Math.Abs(beats[k] - t2) < bd)
                {
                    bd = Math.Abs(beats[k] - t2);
                    best = beats[k];
                }
            return best;
        }

        var history = new List<Hist>();
        var bgHistory = new List<string>();
        var fxHistory = new List<string>();
        var schemeIdx = 0;
        var nSchemes = st.Schemes.Count;

        // 动作时长（切分时长 × 比例，被 enter/exit 注册项的 inDur/outDur 覆盖）
        static (double InD, double OutD) Durs(double dur, int nn, string en2, string ex2)
        {
            var a = J.Clamp(dur * 0.36, 0.12, 0.6);
            if (en2 == "type") a = J.Clamp(nn * 0.055 + 0.1, 0.15, dur * 0.65);
            if (en2 == "assemble") a = J.Clamp(dur * 0.45, 0.22, 0.75);
            var ed = Reg.Enter.GetValueOrDefault(en2);
            if (ed != null && ed.InDur != null) a = ed.InDur(dur, nn);
            if (en2 == "cut") a = 0.12;
            var b = ex2 == "cut" ? 0 : J.Clamp(dur * 0.3, 0.14, 0.55);
            if (ex2 is "explode" or "fall" or "drift") b = J.Clamp(dur * 0.38, 0.25, 0.7);
            var xd = Reg.Exit.GetValueOrDefault(ex2);
            if (xd != null && xd.OutDur != null) b = xd.OutDur(dur, nn);
            if (a + b > dur * 0.92)
            {
                var f = dur * 0.92 / (a + b);
                a *= f;
                b *= f;
            }
            return (a, b);
        }

        // 片头标题卡
        var firstStart = tm.Starts.Length > 0 ? tm.Starts[0] : 0;
        if (title.Length > 0 && firstStart >= 1.1)
        {
            var rng = J.Rng(J.H((int)project.Seed, 999));
            plan.Cuts.Add(MakeCut(
                text: title,
                lineText: title,
                note: artist,
                line: -1,
                start: 0.1,
                end: firstStart - 0.04,
                layout: "title",
                enter: rng.Pick(new[] { "blur", "type", "wipe", "assemble" }),
                exit: rng.Pick(new[] { "blur", "drift", "wipe" }),
                hold: "still",
                p: Reg.Layouts["title"].Plan!(rng, new LayoutCtx(), st) ?? new JObject(),
                decor: new List<DecorSpec>(),
                scheme: 0,
                seed: J.H((int)project.Seed, 999, 1)));
        }

        // 統一感：段落、重复行、キメ 行和各段调色板
        var U = plan.Unify ? MakeUnify(parsed.Lines, st, en, fx, history, plan.Lang) : null;

        for (var li = 0; li < parsed.Lines.Count; li++)
        {
            var ln = parsed.Lines[li];
            var ls = tm.Starts[li];
            var le = tm.Ends[li];
            project.Overrides.TryGetValue(li.ToString(), out var ov);
            var lineSeed =
                Truthy(ov?["lock"]) && Num(ov, "lockedSeed") is { } lsv
                    ? (uint)(long)lsv
                    : J.H((int)project.Seed, li + 1, (int)(long)(Num(ov, "seed") ?? 0));
            var rng = J.Rng(lineSeed);
            if (ln.Interlude)
            {
                // [間奏]：只有背景、装饰和屏幕特效
                plan.Lines.Add(new PlanLine
                {
                    Index = li,
                    Src = ln.Src,
                    Lrc = ln.Lrc,
                    Text = "",
                    Interlude = true,
                    Secs = ln.Secs,
                    Start = ls,
                    End = le,
                    VisEnd = le,
                    Note = null,
                    Impact = false,
                    Emph = Array.Empty<string>(),
                    Chunks = Array.Empty<string>(),
                    Seed = lineSeed
                });
                var bg = Str(ov, "bg") is { } obg && Reg.Bg.ContainsKey(obg) ? obg : PickBg(rng, st, en, fx, bgHistory);
                bgHistory.Add(bg);
                var durI = le - ls;
                var showTitle = durI >= 6 && (title.Length > 0 || artist.Length > 0);
                var fxCopy = CloneFx(fx);
                fxCopy.Decor = Math.Max(0.6, fx.Decor);
                plan.Cuts.Add(MakeCut(
                    text: "",
                    lineText: "",
                    line: li,
                    start: ls,
                    end: le,
                    layout: "interlude",
                    enter: "blur",
                    exit: "blur",
                    hold: "still",
                    inDur: 0.4,
                    outDur: 0.4,
                    p: new JObject
                    {
                        ["variant"] = "quiet",
                        ["showTitle"] = showTitle,
                        ["titleText"] = showTitle
                            ? string.Join("  /  ", new[] { title, artist }.Where(x => x.Length > 0))
                            : ""
                    },
                    decor: ov?["decor"] is JArray ovDecI ? OvDecor(rng, ovDecI) : PickDecor(rng, st, en, fxCopy, "interlude", history),
                    scheme: schemeIdx,
                    seed: J.H((int)lineSeed, 405),
                    bg: bg,
                    bgP: Reg.Bg.TryGetValue(bg, out var bgDI) && bgDI.Plan != null ? bgDI.Plan!(rng, st) ?? new JObject() : new JObject(),
                    cam: "push",
                    camP: new JObject()));
                if (EnNotOff(en, "fx", "chroma")) AddEvent(plan, ls, "chroma", 1 + fx.Chroma, 0.25);
                // 长间奏加几个特效点缀，免得太死
                for (double t2 = ls + 1.2; t2 < le - 0.8; t2 += J.Clamp(durI / 4, 1.6, 3.2))
                {
                    var pick = PickFx(rng, st, en, fx, false, fxHistory, "mid");
                    if (pick != null)
                    {
                        var d2 = Reg.Fxe[pick];
                        AddEvent(plan, t2, pick, d2.Amp * 0.6, d2.Dur / 24);
                        fxHistory.Add(pick);
                    }
                }
                continue;
            }
            var n = CpLen(WsRe.Replace(ln.Text, ""));
            var visEnd = Math.Min(le, ls + Math.Max(3.6, n * 0.5 + 1.2));
            var durL = visEnd - ls;
            plan.Lines.Add(new PlanLine
            {
                Index = li,
                Src = ln.Src,
                Lrc = ln.Lrc,
                Text = ln.Text,
                Start = ls,
                End = le,
                VisEnd = visEnd,
                Note = ln.Note,
                Impact = ln.Impact,
                Emph = ln.Emph.ToArray(),
                Chunks = null,
                Seed = lineSeed
            });
            var chunks = ln.Manual ?? (plan.Lang == "en" ? PhraseChunks(ChunkText(ln.Text)) : ChunkText(ln.Text));
            plan.Lines[li].Chunks = chunks.ToArray();
            var L = J.Lerp(1.3, 0.5, fx.Density);
            var nC = (int)Math.Round(durL / L, MidpointRounding.AwayFromZero);
            var maxC = chunks.Count + (chunks.Count >= 2 && durL > 2.0 ? 1 : 0);
            nC = J.Clamp(nC, 1, Math.Max(1, maxC));
            // 逐行设置（版式/动态/装饰/配色/强调之外的任意字段）优先于統一感
            var ovBookkeeping = new HashSet<string> { "lock", "lockedSeed", "seed", "cutTech", "cutLayouts", "cutQuiet", "cutTime" };
            var ovAny = false;
            if (ov != null)
                foreach (var kp in ov.Properties())
                    if (!ovBookkeeping.Contains(kp.Name))
                    {
                        ovAny = true;
                        break;
                    }
            var kime = U != null && U.Kime.Contains(li) && !(Num(ov, "cuts") is { } cv && (cv != 0 && !double.IsNaN(cv)));
            if (Truthy(ov?["single"]) || kime) nC = 1;
            if (zones != null) nC = Math.Max(1, Math.Min(nC, (int)Math.Floor(chunks.Count / 2.0))); // 每个切分要拆成两半，每半至少留 2 个词
            // 指定切分数（按行）：就切这么多刀，块不够就继续拆
            var fixedN = Num(ov, "cuts") is { } fv && fv > 0 ? Math.Min(12, (int)fv) : 0;
            var chunks2 = chunks;
            if (fixedN > 0)
            {
                nC = fixedN;
                chunks2 = SplitToCount(chunks, fixedN);
            }
            List<string> groups;
            var nG = Math.Min(nC, chunks2.Count);
            if (nG <= 1) groups = new List<string> { ln.Text };
            else groups = Partition(chunks2, nG).Select(g2 => JoinWords(g2)).ToList();
            var recap = fixedN == 0 && nC > groups.Count && groups.Count >= 2;
            var units = groups.Select(g2 => new Unit { Text = g2, W = CpLen(g2) + 1.6 }).ToList();
            if (recap)
                units.Add(new Unit
                {
                    Text = ln.Text,
                    W = units.Average(u => u.W) * 1.25,
                    Recap = true
                });
            // ロック：锁定的行也保留自己的切分，否则数量会跟着细密度滑块或おまかせ走
            if (LockedCutsValid(ov, ln.Text))
                units = ((JArray?)ov!["lockedCuts"])!.Select(c2 =>
                {
                    var c3 = (JObject)c2;
                    var ut = Str(c3, "utext") ?? "";
                    return new Unit { Text = ut, W = CpLen(ut) + 1.6, Recap = Truthy(c3["recap"]) };
                }).ToList();
            var tot = units.Sum(u => u.W);
            // ロック：锁定的行原样保留锁定时的内容（版式、动态、装饰、配色、强调）
            var lockSpecs = LockSpecsOf(ov, units);
            var acc = ls;
            var bounds = new List<double> { ls };
            foreach (var u in units)
            {
                acc += durL * u.W / tot;
                bounds.Add(u == units[^1] ? visEnd : acc);
            }
            for (var k = 1; k < bounds.Count - 1; k++)
            {
                // 相邻边界间距 < 0.44s 时下界会大于上界，clamp 要先归序，防止出现 start>end 的负时长 cut
                var loB = bounds[k - 1] + 0.22;
                var hiB = bounds[k + 1] - 0.22;
                bounds[k] = J.Clamp(Snap(bounds[k]), Math.Min(loB, hiB), Math.Max(loB, hiB));
            }
            // 切分开始时刻（详细模式）：行首起的秒数，手工填或打点（Tab），保持顺序、互距至少 0.22s
            if (ov?["cutTime"] is JObject cutTime)
            {
                var nb = bounds.Count - 1;
                for (var k = 1; k < nb; k++)
                {
                    var d2 = Num(cutTime, k.ToString());
                    if (d2 != null && double.IsFinite(d2.Value))
                        bounds[k] = J.Clamp(ls + d2.Value, ls + 0.22 * k, visEnd - 0.22 * (nb - k));
                }
                for (var k = 1; k < nb; k++)
                    if (bounds[k] < bounds[k - 1] + 0.22)
                        bounds[k] = bounds[k - 1] + 0.22;
                for (var k = nb - 1; k >= 1; k--)
                    if (bounds[k] > bounds[k + 1] - 0.22)
                        bounds[k] = bounds[k + 1] - 0.22;
            }
            // 配色方案按行切换
            if (nSchemes > 1 && li > 0)
            {
                var doSwitch = U != null
                    ? U.SectionStart(li) && rng.Chance(0.25 + fx.BgSwitch)
                    : rng.Chance(fx.BgSwitch * (ln.Impact ? 1.8 : 1));
                if (doSwitch) schemeIdx = (schemeIdx + 1 + rng.Int(0, nSchemes - 2)) % nSchemes;
            }
            // （JS 的 const emphLine 在源码里未被使用——不移植）
            // 背景图形按行选，偶尔在切分上重抽
            var lineBg = Str(ov, "bg") is { } obg2 && Reg.Bg.ContainsKey(obg2) ? obg2 : PickBg(rng, st, en, fx, bgHistory);
            bgHistory.Add(lineBg);
            var lineBgP = Reg.Bg.TryGetValue(lineBg, out var lbgD) && lbgD.Plan != null
                ? lbgD.Plan!(rng, st) ?? new JObject()
                : new JObject();

            for (var k = 0; k < units.Count; k++)
            {
                var u = units[k];
                var cs = bounds[k];
                var ce = bounds[k + 1];
                var dur = ce - cs;
                var halves = zones != null ? SplitHalf(u.Text, plan.Lang) : null; // 「花が」｜「咲いた」
                var txt = halves != null ? halves[0] : u.Text;
                var nn = (halves ?? new List<string> { u.Text }).Max(t2 => CpLen(WsRe.Replace(t2, "")));
                var emph = kime || (ln.Impact && (k == 0 || u.Recap)) || ln.Emph.Any(w2 => u.Text.Contains(w2));
                var Z = ZoneOf();
                var LW = Z != null ? Z.W : W;
                var LH = Z != null ? Z.H : H; // 这个切分布局用的画面区域
                var UU = U != null && !ovAny ? U : null; // 逐行设置优先于統一感
                var tech = CutTechOf(ov, k); // 只作用于这个切分的指定
                var layout = Str(ov, "layout") is { } ol && Reg.Layouts.ContainsKey(ol)
                    ? ol
                    : PickLayout(rng, st, en, nn, dur, history, emph, u.Recap, LH > LW);
                if (UU != null) layout = UU.Layout(li, layout, nn, kime, rng, LH > LW);
                var enter = Str(ov, "enter") is { } oe && Reg.Enter.ContainsKey(oe)
                    ? oe
                    : PickEnter(rng, st, en, layout, dur, history, emph, nn);
                var exit = Str(ov, "exit") is { } ox && Reg.Exit.ContainsKey(ox)
                    ? ox
                    : PickExit(rng, st, en, layout, dur, k == units.Count - 1, history);
                var hold = Str(ov, "hold") is { } oh && Reg.Hold.ContainsKey(oh)
                    ? oh
                    : PickHold(rng, en, fx, history);
                var weightGrow = false;
                if (UU != null)
                {
                    enter = UU.Enter(li, enter, layout, dur, emph, kime, rng, nn);
                    exit = UU.Exit(li, exit, layout, dur, kime, rng);
                    hold = UU.Hold(li, hold, kime, rng);
                    weightGrow = UU.WeightGrow(kime, nn, rng, dur);
                    if (weightGrow) enter = UU.SoftEnter(enter, rng);
                }
                var (inDur, outDur) = Durs(dur, nn, enter, exit);
                var sch = schemeIdx;
                if (U == null && nSchemes > 1 && k > 0 && rng.Chance(0.12 * fx.BgSwitch)) sch = (schemeIdx + 1) % nSchemes;
                var LD = Reg.Layouts.GetValueOrDefault(layout)!;
                var ps = LD.Plan!(rng, new LayoutCtx { Text = txt, N = nn, W = LW, H = LH, Dur = dur }, st) ?? new JObject();
                var decor = ov?["decor"] is JArray ovDec ? OvDecor(rng, ovDec) : PickDecor(rng, st, en, fx, layout, history);
                var treat = Str(ov, "treat") is { } ot && Reg.Treat.ContainsKey(ot)
                    ? ot
                    : PickTreat(rng, st, en, fx, LD, emph, history);
                if (UU != null)
                {
                    decor = UU.Decor(li, decor, kime, rng);
                    treat = UU.Treat(li, treat, kime, rng);
                }
                var treatP = Reg.Treat.TryGetValue(treat, out var trD) && trD.Plan != null
                    ? trD.Plan!(rng, st) ?? new JObject()
                    : new JObject();
                if (Str(ov, "bg") == null && k > 0 && U == null && rng.Chance(0.18 * fx.BgSwitch + 0.04))
                {
                    lineBg = PickBg(rng, st, en, fx, bgHistory);
                    lineBgP = Reg.Bg.TryGetValue(lineBg, out var lbg2) && lbg2.Plan != null
                        ? lbg2.Plan!(rng, st) ?? new JObject()
                        : new JObject();
                }
                var bg = LD.Busy && !(Reg.Bg.TryGetValue(lineBg, out var lbg3) && lbg3.Subtle) ? "none" : lineBg;
                var cam = Str(ov, "cam") is { } oc && Reg.Camera.ContainsKey(oc)
                    ? oc
                    : PickCam(rng, st, en, fx, LD, emph, history);
                if (UU != null) cam = UU.Cam(li, cam, kime, emph, rng);
                var camP = Reg.Camera.TryGetValue(cam, out var camD) && camD.Plan != null
                    ? camD.Plan!(rng, st) ?? new JObject()
                    : new JObject();
                var cutSeed = J.H((int)lineSeed, k, 17);
                // 統一感：重复出现的行（サビ 等）和第一次完全一样
                var again = UU?.Again(li, k, txt);
                if (again != null)
                {
                    layout = again.Layout;
                    enter = again.Enter;
                    exit = again.Exit;
                    hold = again.Hold;
                    ps = again.Params;
                    decor = again.Decor;
                    treat = again.Treat;
                    treatP = again.TreatP;
                    cam = again.Cam;
                    camP = again.CamP;
                    weightGrow = again.WeightGrow;
                    sch = again.Scheme;
                    cutSeed = again.Seed;
                    LD = Reg.Layouts.GetValueOrDefault(layout)!;
                    if (again.Bg.Length > 0 && again.Bg != "none")
                    {
                        bg = again.Bg;
                        lineBgP = again.BgP;
                        lineBg = bg;
                    }
                    (inDur, outDur) = Durs(dur, nn, enter, exit);
                }
                var LS = lockSpecs != null && k < lockSpecs.Count ? lockSpecs[k] as JObject : null;
                if (LS != null)
                {
                    layout = Str(LS, "layout") ?? layout;
                    enter = Str(LS, "enter") ?? enter;
                    exit = Str(LS, "exit") ?? exit;
                    hold = Str(LS, "hold") ?? hold;
                    ps = LS["params"] as JObject ?? new JObject();
                    decor = (LS["decor"] as JArray ?? new JArray())
                        .Select(t2 => DecorFromJson(t2 as JObject))
                        .Where(d2 => Reg.Decor.ContainsKey(d2.Id)).ToList();
                    treat = Str(LS, "treat") ?? treat;
                    treatP = LS["treatP"] as JObject ?? new JObject();
                    cam = Str(LS, "cam") ?? cam;
                    camP = LS["camP"] as JObject ?? new JObject();
                    if (!Reg.Enter.ContainsKey(enter)) enter = "blur";
                    if (!Reg.Exit.ContainsKey(exit)) exit = "blur";
                    if (!Reg.Hold.ContainsKey(hold)) hold = "still";
                    if (!Reg.Treat.ContainsKey(treat))
                    {
                        treat = "none";
                        treatP = new JObject();
                    }
                    if (!Reg.Camera.ContainsKey(cam))
                    {
                        cam = "push";
                        camP = new JObject();
                    }
                    decor = decor.Where(d2 => Reg.Decor.ContainsKey(d2.Id)).ToList();
                    weightGrow = Truthy(LS["weightGrow"]);
                    sch = (int)(Num(LS, "scheme") ?? 0);
                    cutSeed = (uint)(long)(Num(LS, "seed") ?? 0);
                    LD = Reg.Layouts.GetValueOrDefault(layout)!;
                    bg = Str(LS, "bg") is { } lsb && Reg.Bg.ContainsKey(lsb) ? lsb : "none";
                    if (bg != "none")
                    {
                        lineBg = bg;
                        lineBgP = LS["bgP"] as JObject ?? new JObject();
                    }
                    inDur = Num(LS, "inDur") ?? 0.3;
                    outDur = Num(LS, "outDur") ?? 0.25;
                }
                // 单切分指定：叠加在抽取结果之上，用独立的随机流，改一个切分不会挪动其他切分
                // （下面的 history 记录的仍是画出来的东西，当作这里什么都没改）
                var drawn = new Hist
                {
                    Layout = layout,
                    Enter = enter,
                    Exit = exit,
                    Hold = hold,
                    Treat = treat,
                    Cam = cam,
                    Decor = decor.Select(d2 => d2.Id).ToList()
                };
                JObject? techBgP = null;
                var techLayout = TechStr(tech, "layout");
                if (techLayout != null && Reg.Layouts.TryGetValue(techLayout, out var tld) && !tld.Special)
                {
                    layout = techLayout;
                    LD = tld;
                    try
                    {
                        ps = LD.Plan!(J.Rng(J.H((int)lineSeed, k, 91)), new LayoutCtx { Text = txt, N = nn, W = LW, H = LH, Dur = dur }, st)
                             ?? new JObject();
                    }
                    catch
                    {
                        // JS 的空 catch：layout plan 失败时保留原参数
                    }
                }
                var techEnter = TechStr(tech, "enter");
                if (techEnter != null && Reg.Enter.ContainsKey(techEnter)) enter = techEnter;
                var techExit = TechStr(tech, "exit");
                if (techExit != null && Reg.Exit.ContainsKey(techExit)) exit = techExit;
                var techHold = TechStr(tech, "hold");
                if (techHold != null && Reg.Hold.ContainsKey(techHold)) hold = techHold;
                if (tech.ContainsKey("decor"))
                {
                    var td = TechStr(tech, "decor");
                    if (td == null || td == "none" || !Reg.Decor.ContainsKey(td)) decor = new List<DecorSpec>();
                    else decor = new List<DecorSpec> { DecorParams(J.Rng(J.H((int)lineSeed, k, 92)), td) };
                }
                var techTreat = TechStr(tech, "treat");
                if (techTreat != null && Reg.Treat.ContainsKey(techTreat))
                {
                    treat = techTreat;
                    treatP = Reg.Treat[treat].Plan != null
                        ? Reg.Treat[treat].Plan!(J.Rng(J.H((int)lineSeed, k, 93)), st) ?? new JObject()
                        : new JObject();
                }
                var techBg = TechStr(tech, "bg");
                if (techBg != null && Reg.Bg.ContainsKey(techBg))
                {
                    bg = techBg;
                    techBgP = Reg.Bg[bg].Plan != null
                        ? Reg.Bg[bg].Plan!(J.Rng(J.H((int)lineSeed, k, 94)), st) ?? new JObject()
                        : new JObject();
                }
                var techCam = TechStr(tech, "cam");
                if (techCam != null && Reg.Camera.ContainsKey(techCam))
                {
                    cam = techCam;
                    camP = Reg.Camera[cam].Plan != null
                        ? Reg.Camera[cam].Plan!(J.Rng(J.H((int)lineSeed, k, 95)), st) ?? new JObject()
                        : new JObject();
                }
                if (techEnter != null || techExit != null)
                {
                    // 动作没变就保留（锁定的）时长
                    var (i2d, o2d) = Durs(dur, nn, enter, exit);
                    if (techEnter != null && Reg.Enter.ContainsKey(techEnter)) inDur = i2d;
                    if (techExit != null && Reg.Exit.ContainsKey(techExit)) outDur = o2d;
                }
                // 切分间转场（顶掉上一刀的 exit 和这一刀的 enter）
                var prevCut = plan.Cuts.Count > 0 ? plan.Cuts[^1] : null;
                string? trans = null;
                var transP = new JObject();
                var transDur = 0.0;
                JObject? morph = null;
                var joinSavedEnter = enter;
                var joinSavedInDur = inDur;
                var joinSavedPrevExit = prevCut?.Exit;
                var joinSavedPrevOut = prevCut?.OutDur;
                // 锁定行保留自己的 exit：下一个未锁定行不许用转场/モーフ顶掉它
                var prevLockedOther = false;
                if (prevCut != null && prevCut.Line != li && LS == null)
                {
                    project.Overrides.TryGetValue(prevCut.Line.ToString(), out var pov);
                    prevLockedOther = Truthy(pov?["lock"]);
                }
                var canTrans = prevCut != null && Math.Abs(prevCut.End - cs) < 0.06
                               && prevCut.Layout != "interlude" && dur > 0.5 && !prevLockedOther;
                // 統一感：モーフ——同一行的下一部分从这一部分里长出来（共享字滑动，其余融化）
                if (LS != null)
                {
                    // 锁定行：切分仍相接时沿用之前的接法
                    if (canTrans && LS["morph"] is JObject lm)
                    {
                        morph = new JObject { ["dur"] = lm["dur"]?.DeepClone() ?? new JValue(0.0) };
                        prevCut!.Exit = "cut";
                        prevCut!.OutDur = 0;
                    }
                    else if (canTrans && Str(LS, "trans") is { } lst && Reg.Trans.ContainsKey(lst))
                    {
                        trans = lst;
                        transP = LS["transP"] as JObject ?? new JObject();
                        transDur = Num(LS, "transDur") ?? 0;
                        prevCut!.Exit = "cut";
                        prevCut!.OutDur = 0;
                    }
                }
                else if (canTrans && UU != null && k > 0 && !kime
                         && (again != null ? again.Morph : rng.Chance(u.Recap ? 0.85 : 0.4)))
                {
                    morph = new JObject { ["dur"] = J.Clamp(dur * 0.45, 0.28, 0.6) };
                    enter = "cut";
                    inDur = 0.12;
                    prevCut!.Exit = "cut";
                    prevCut!.OutDur = 0;
                }
                else if (canTrans && again != null && Str(ov, "trans") == null)
                {
                    // 統一感：重复行的切分接法和第一次相同
                    if (again.Trans != null && Reg.Trans.ContainsKey(again.Trans))
                    {
                        trans = again.Trans;
                        transP = again.TransP ?? new JObject();
                        transDur = again.TransDur;
                        enter = "cut";
                        inDur = 0.12;
                        prevCut!.Exit = "cut";
                        prevCut!.OutDur = 0;
                    }
                }
                else if (canTrans)
                {
                    trans = Str(ov, "trans") is { } ovt && Reg.Trans.ContainsKey(ovt)
                        ? ovt
                        : PickTrans(rng, st, en, fx, emph, history);
                    if (trans != null && UU != null) trans = UU.Trans(li, trans, rng);
                    if (trans != null)
                    {
                        var TD = Reg.Trans[trans];
                        transDur = J.Clamp(TD.Dur ?? 0.35, 0.12, Math.Min(0.6, dur * 0.45));
                        transP = TD.Plan != null ? TD.Plan!(rng, st) ?? new JObject() : new JObject();
                        enter = "cut";
                        inDur = 0.12;
                        prevCut!.Exit = "cut";
                        prevCut!.OutDur = 0;
                    }
                }
                drawn.Trans = trans;
                var techTrans = TechStr(tech, "trans");
                if (techTrans == "none")
                {
                    // 单切分指定「つなぎなし」：撤销接缝
                    if (trans != null || morph != null)
                    {
                        enter = joinSavedEnter;
                        inDur = joinSavedInDur;
                        if (prevCut != null)
                        {
                            prevCut.Exit = joinSavedPrevExit!;
                            prevCut.OutDur = joinSavedPrevOut ?? 0;
                        }
                    }
                    trans = null;
                    transP = new JObject();
                    transDur = 0;
                    morph = null;
                }
                else if (techTrans != null && Reg.Trans.ContainsKey(techTrans) && canTrans)
                {
                    morph = null;
                    trans = techTrans;
                    var TD = Reg.Trans[trans];
                    transDur = J.Clamp(TD.Dur ?? 0.35, 0.12, Math.Min(0.6, dur * 0.45));
                    transP = TD.Plan != null
                        ? TD.Plan!(J.Rng(J.H((int)lineSeed, k, 96)), st) ?? new JObject()
                        : new JObject();
                }
                if (trans != null && canTrans)
                {
                    enter = "cut";
                    inDur = 0.12;
                    prevCut!.Exit = "cut";
                    prevCut!.OutDur = 0;
                }
                var cut = MakeCut(
                    text: txt,
                    lineText: ln.Text,
                    note: ln.Note,
                    line: li,
                    start: cs,
                    end: ce,
                    layout: layout,
                    enter: enter,
                    exit: exit,
                    hold: hold,
                    inDur: inDur,
                    outDur: outDur,
                    p: ps,
                    decor: decor,
                    scheme: sch,
                    seed: cutSeed,
                    emph: emph,
                    recap: u.Recap,
                    words: ChunkText(txt).ToArray(),
                    stagger: rng.Range(0.025, 0.06),
                    treat: treat,
                    treatP: treatP,
                    bg: bg,
                    bgP: techBgP ?? (bg == lineBg ? lineBgP : new JObject()),
                    cam: cam,
                    camP: camP,
                    trans: trans,
                    transP: transP,
                    transDur: transDur,
                    zone: Z,
                    utext: u.Text);
                if (kime || (LS != null && Truthy(LS["kime"]))) cut.Kime = true;
                if (weightGrow) cut.WeightGrow = true;
                if (morph != null) cut.Morph = morph;
                UU?.Remember(li, k, txt, cut);
                if (zones != null) SplitCut(cut, halves!, zones, st, dur, LS);
                // 文字整列：特效不叠加——至多一个装饰，上面不再叠文字处理
                if (plan.Typeset)
                {
                    cut.Decor = cut.Decor.Take(1).ToList();
                    if (cut.Decor.Count > 0 && cut.Treat != "none")
                    {
                        cut.Treat = "none";
                        cut.TreatP = new JObject();
                    }
                }
                plan.Cuts.Add(cut);
                var evMark = plan.Events.Count;
                // history 记抽取结果（含通常的接缝），单切分的改选才不会挪动后面的切分
                var hist = new Hist
                {
                    Layout = layout,
                    Enter = enter,
                    Exit = exit,
                    Hold = hold,
                    Treat = treat,
                    Cam = cam,
                    Trans = trans,
                    Decor = decor.Select(d2 => d2.Id).ToList()
                };
                if (tech.ContainsKey("layout")) hist.Layout = drawn.Layout;
                if (tech.ContainsKey("enter")) hist.Enter = drawn.Trans != null ? "cut" : drawn.Enter;
                if (tech.ContainsKey("exit")) hist.Exit = drawn.Exit;
                if (tech.ContainsKey("hold")) hist.Hold = drawn.Hold;
                if (tech.ContainsKey("treat")) hist.Treat = drawn.Treat;
                if (tech.ContainsKey("cam")) hist.Cam = drawn.Cam;
                if (tech.ContainsKey("trans")) hist.Trans = drawn.Trans;
                if (tech.ContainsKey("decor")) hist.Decor = drawn.Decor;
                history.Add(hist);
                // 切分开始处的事件——时长按 24fps 时间轴计，任何输出帧率下观感一致
                var gg = fx.Glitch * (st.GlitchBoost != 0 ? st.GlitchBoost : 1);
                const double F = 1.0 / 24;
                if (EnNotOff(en, "fx", "chroma"))
                    AddEvent(plan, cs, "chroma", 1.4 + rng.Range(0, 2) * fx.Chroma + (emph ? 2.5 : 0), 0.25);
                if (EnNotOff(en, "fx", "slice") && rng.Chance(gg * 0.5 + (emph ? 0.3 : 0)))
                    AddEvent(plan, cs, "slice", 0.6 + rng.Range(0, 0.8) * gg + (emph ? 0.5 : 0), rng.Pick(new[] { 2, 3, 4 }) * F);
                if (EnNotOff(en, "fx", "block") && rng.Chance(gg * 0.22))
                    AddEvent(plan, cs + rng.Range(0, 0.05), "block", 0.5 + gg, rng.Pick(new[] { 2, 4 }) * F);
                if (EnNotOff(en, "fx", "shake") && (emph || rng.Chance(fx.Motion * 0.18)))
                    AddEvent(plan, cs, "shake", (emph ? 1 : 0.5) * fx.Motion, 0.3);
                if (EnNotOff(en, "fx", "flash") && fx.Flash && (ln.Impact && k == 0 || kime))
                    AddEvent(plan, cs, "flash", 1, 3 * F);
                if (kime)
                {
                    // キメ：行落点上的重音
                    if (EnNotOff(en, "fx", "zoom")) AddEvent(plan, cs, "zoom", 1.1, 0.25);
                    if (EnNotOff(en, "fx", "shake"))
                        AddEvent(plan, cs + 0.04, "shake", 1.1 * Math.Max(0.5, fx.Motion), 0.35);
                }
                if (EnNotOff(en, "fx", "invert") && rng.Chance(0.035 * gg))
                    AddEvent(plan, cs, "invert", 1, 2 * F);
                if (EnNotOff(en, "fx", "zoom") && (emph && rng.Chance(0.6) || rng.Chance(0.06 * fx.Motion)))
                    AddEvent(plan, cs, "zoom", 0.7 + 0.5 * fx.Motion, 0.22);
                if (EnNotOff(en, "fx", "mosaic") && rng.Chance(0.04 * gg))
                    AddEvent(plan, cs, "mosaic", 1, 3 * F);
                if (EnNotOff(en, "fx", "slice") && dur > 0.8 && rng.Chance(gg * 0.4))
                    AddEvent(plan, cs + rng.Range(0.35, 0.8) * dur, "slice", 0.4 + gg * 0.4, 2 * F);
                // 新特效库：每个切分边界至多一个，偶尔切分中段加一个
                if (plan.Cuts.Count > 1 || k > 0 || li > 0)
                {
                    var pick = PickFx(rng, st, en, fx, emph, fxHistory, "edge");
                    if (pick != null)
                    {
                        var d2 = Reg.Fxe[pick];
                        AddEvent(plan, cs - (d2.Pre != null ? d2.Pre.Value * F : 0), pick,
                            d2.Amp * (0.7 + 0.5 * gg + (emph ? 0.3 : 0)), d2.Dur * F);
                        fxHistory.Add(pick);
                    }
                }
                if (dur > 1.1)
                {
                    var pick = PickFx(rng, st, en, fx, emph, fxHistory, "mid");
                    if (pick != null)
                    {
                        var d2 = Reg.Fxe[pick];
                        AddEvent(plan, cs + rng.Range(0.4, 0.75) * dur, pick, d2.Amp * (0.5 + 0.4 * gg), d2.Dur * F);
                    }
                }
                if (LS != null && LS["events"] is JArray lsEvents)
                {
                    plan.Events.RemoveRange(evMark, plan.Events.Count - evMark);
                    foreach (var et in lsEvents)
                    {
                        var eo = (JObject)et;
                        AddEvent(plan, cs + (Num(eo, "dt") ?? 0), Str(eo, "type") ?? "",
                            Num(eo, "amp") ?? 0, Num(eo, "dur") ?? 0);
                    }
                }
                // 文字整列：除色差外每个切分至多一个屏幕特效（キメ 行保留全部）
                if (plan.Typeset && !cut.Kime && LS == null)
                {
                    var mine = plan.Events.Skip(evMark).ToList();
                    plan.Events.RemoveRange(evMark, plan.Events.Count - evMark);
                    var extra = 0;
                    foreach (var ev in mine)
                        if (ev.Type == "chroma" || ev.Type == "shake" && emph || extra++ < 1)
                            plan.Events.Add(ev);
                }
                for (var q = evMark; q < plan.Events.Count; q++) plan.EvOwner[plan.Events[q]] = cut; // 记下每个打点来自哪个切分（锁定用）
            }
            // 长空档里补间奏
            double? nextStart = li < parsed.Lines.Count - 1 ? tm.Starts[li + 1] : null;
            if (nextStart != null && nextStart.Value - visEnd > 1.3 && !parsed.Lines[li + 1].Interlude)
            {
                var r2 = J.Rng(J.H((int)lineSeed, 404));
                var fx1 = CloneFx(fx);
                fx1.Decor = 1;
                plan.Cuts.Add(MakeCut(
                    text: title,
                    lineText: "",
                    line: li,
                    start: visEnd,
                    end: nextStart.Value,
                    layout: "interlude",
                    enter: "blur",
                    exit: "blur",
                    hold: "still",
                    inDur: 0.3,
                    outDur: 0.3,
                    p: Reg.Layouts["interlude"].Plan!(r2, new LayoutCtx(), st) ?? new JObject(),
                    decor: PickDecor(r2, st, en, fx1, "interlude"),
                    scheme: schemeIdx,
                    seed: J.H((int)lineSeed, 405)));
            }
        }
        plan.Cuts = plan.Cuts.OrderBy(c => c.Start).ToList();
        for (var i = 0; i < plan.Cuts.Count; i++)
        {
            var c = plan.Cuts[i];
            c.Index = i;
            if (zones == null || c.Zone != null) continue;
            if (c.Layout == "interlude")
            {
                // 没歌词的占整帧
                var p2 = (JObject)c.Params.DeepClone();
                p2["showTitle"] = false;
                c.Params = p2;
                continue;
            }
            c.Zone = ZoneOf();
        }
        plan.Events = plan.Events.OrderBy(e2 => e2.T).ToList();
        plan.Energy = audio?.Energy;
        plan.EnergyRate = audio?.EnergyRate ?? 0;
        plan.Pitch = audio != null && audio.Pitch != null && audio.Pitch.Length > 0 ? audio.Pitch : null;
        plan.PitchRate = audio?.PitchRate ?? 0;
        return plan;
    }

    /// <summary>ov.decor 数组 → DecorSpec（按注册表过滤，逐 id 消耗 RNG）。</summary>
    private static List<DecorSpec> OvDecor(Rng rng, JArray arr)
    {
        var outList = new List<DecorSpec>();
        foreach (var t in arr)
            if (t is JValue { Value: string id } && Reg.Decor.ContainsKey(id))
                outList.Add(DecorParams(rng, id));
        return outList;
    }

    /// <summary>lockedCuts 可用性（第一道检查：utext 都是新行文本的子串）。</summary>
    private static bool LockedCutsValid(JObject? ov, string lineText)
    {
        if (ov == null || !Truthy(ov["lock"]) || ov["lockedCuts"] is not JArray arr || arr.Count == 0) return false;
        foreach (var t in arr)
        {
            if (t is not JObject c) return false;
            var ut = Str(c, "utext");
            if (ut == null || !lineText.Contains(ut.Trim())) return false;
        }
        return true;
    }

    /// <summary>lockSpecs：锁定行数量与单元一一对应、utext 相同且 layout 有效时返回 lockedCuts。</summary>
    private static JArray? LockSpecsOf(JObject? ov, List<Unit> units)
    {
        if (ov == null || !Truthy(ov["lock"]) || ov["lockedCuts"] is not JArray arr || arr.Count != units.Count) return null;
        for (var i = 0; i < arr.Count; i++)
        {
            if (arr[i] is not JObject c) return null;
            var ut = Str(c, "utext");
            var lk = Str(c, "layout");
            if (ut != units[i].Text || lk == null || !Reg.Layouts.ContainsKey(lk)) return null;
        }
        return arr;
    }

    // ---------------- 統一感（makeUnify） ----------------
    // 手写歌词视频的惯例：空行隔出的每段保持一小套版式/动态/运镜/装饰；
    // 重复出现的行和第一次完全一样；方向性动作左右上下交替，强的动作留给重要的行；
    // キメ：以 ! 结尾的行；行内各部分之间用モーフ，偶尔用太さ（细→粗）。

    private static readonly Dictionary<string, string[][]> DirPairs = new()
    {
        ["enter"] = new[] { new[] { "slideL", "slideR" }, new[] { "riseMask", "dropMask" }, new[] { "trackIn", "trackOut" }, new[] { "flipX", "flipY" } },
        ["exit"] = new[] { new[] { "slideOutL", "slideOutR" }, new[] { "sinkMask", "riseOut" }, new[] { "flipOutX", "flipOutY" } },
        ["cam"] = new[] { new[] { "panL", "panR" }, new[] { "tiltUp", "tiltDown" }, new[] { "dollyIn", "pullOut" } }
    };

    private static readonly Dictionary<string, HashSet<string>> Strong = new()
    {
        ["enter"] = new HashSet<string>
        {
            "bounceBig", "whip", "spin", "slingshot", "crumple", "stamp", "zoom", "glitchIn", "scramble",
            "spiralIn", "rollIn", "shuffle", "windBlown", "matrixRain", "stopMotion", "splitFlap"
        },
        ["cam"] = new HashSet<string>
        {
            "earthquake", "shakeHard", "crashZoom", "barrelRoll", "whipIn", "snapPan", "vertigo", "roll",
            "spiralIn", "jelly", "bounce", "beatPunch", "stepZoom"
        }
    };

    private static readonly Dictionary<string, string[]> KimeTable = new()
    {
        ["layout"] = new[] { "huge", "huge", "huge", "columnsBig", "columnsBig", "halftoneBig", "center" },
        ["enter"] = new[] { "stamp", "zoom", "bounceBig", "overexpose", "slingshot", "blur" },
        ["exit"] = new[] { "zoomThrough", "blur", "shrink", "zoomFar" },
        ["hold"] = new[] { "still", "pulse", "heartbeat" },
        ["cam"] = new[] { "beatPunch", "crashZoom", "dollyIn", "push" }
    };

    private static readonly string[] SoftEnterTable = { "blur", "fadeStagger", "trackIn", "blurStagger", "cut" };

    private static readonly Regex NormRe = new("[\\s、。，．,.!！?？…・「」『』（）()\"“”‘’~〜ー―-]", RegexOptions.Compiled);

    private static Unify MakeUnify(List<ParsedLine> lines, StyleDef st, Dictionary<string, Dictionary<string, bool>> en,
        FxSettings fx, List<Hist> history, string lang) => new(lines, st, en, fx, history, lang);

    private sealed class Unify
    {
        private readonly StyleDef _st;
        private readonly Dictionary<string, Dictionary<string, bool>> _en;
        private readonly FxSettings _fx;
        private readonly List<Hist> _history;
        private readonly string _lang;

        internal readonly HashSet<int> Kime = new();
        private readonly List<int> _sec = new();
        private readonly HashSet<int> _starts = new() { 0 };
        private readonly List<int?> _repeatOf = new();
        private readonly Dictionary<int, Dictionary<string, List<string>>> _pals = new();
        private readonly Dictionary<string, string> _last = new();
        private readonly Dictionary<string, bool> _strongRun = new();
        private readonly Dictionary<int, List<AgainSpec?>> _specs = new();

        internal Unify(List<ParsedLine> lines, StyleDef st, Dictionary<string, Dictionary<string, bool>> en,
            FxSettings fx, List<Hist> history, string lang)
        {
            _st = st;
            _en = en;
            _fx = fx;
            _history = history;
            _lang = lang.Length > 0 ? lang : "ja";
            // 段落：空行或间奏切开的块
            var si = 0;
            for (var i = 0; i < lines.Count; i++)
            {
                var ln = lines[i];
                if (i > 0 && (ln.GapBefore || ln.Interlude || lines[i - 1].Interlude))
                {
                    si++;
                    _starts.Add(i);
                }
                _sec.Add(si);
            }
            // 重复行：归一化文本相同的行
            var first = new Dictionary<string, int>();
            var count = new Dictionary<string, int>();
            foreach (var t in lines)
            {
                var kk = NormRe.Replace(t.Text ?? "", "");
                if (t.Interlude || kk.Length < 2)
                {
                    _repeatOf.Add(null);
                    continue;
                }
                count[kk] = count.GetValueOrDefault(kk) + 1;
                if (first.TryGetValue(kk, out var f)) _repeatOf.Add(f);
                else
                {
                    first[kk] = _repeatOf.Count;
                    _repeatOf.Add(null);
                }
            }
            // キメ 行
            for (var i = 0; i < lines.Count; i++)
                if (lines[i].Impact && !lines[i].Interlude) Kime.Add(i);
            if (Kime.Count == 0)
                for (var i = 0; i < lines.Count; i++)
                    if (_starts.Contains(i) && !lines[i].Interlude && count.GetValueOrDefault(NormRe.Replace(lines[i].Text ?? "", "")) >= 2)
                        Kime.Add(i);
        }

        internal bool SectionStart(int i) => _starts.Contains(i);

        private bool Ok(string g, string? k)
        {
            if (string.IsNullOrEmpty(k)) return false;
            if (_en.TryGetValue(g, out var m) && m.TryGetValue(k, out var v) && !v) return false;
            return g switch
            {
                "layout" => Reg.Layouts.ContainsKey(k!),
                "enter" => Reg.Enter.ContainsKey(k!),
                "exit" => Reg.Exit.ContainsKey(k!),
                "hold" => Reg.Hold.ContainsKey(k!),
                "cam" => Reg.Camera.ContainsKey(k!),
                "treat" => Reg.Treat.ContainsKey(k!),
                "trans" => Reg.Trans.ContainsKey(k!),
                _ => false
            };
        }

        private Dictionary<string, List<string>> Pal(int i)
        {
            var s2 = _sec[i];
            if (!_pals.TryGetValue(s2, out var p))
            {
                p = new Dictionary<string, List<string>>
                {
                    ["layout"] = new(), ["enter"] = new(), ["exit"] = new(), ["hold"] = new(),
                    ["cam"] = new(), ["decor"] = new(), ["treat"] = new(), ["trans"] = new()
                };
                _pals[s2] = p;
            }
            return p;
        }

        // 每段最多存 max 个不同选择，满了之后大多复用
        private string Sticky(int i, string g, string v, int max, Rng rng, Func<string, bool>? fits = null, double reuse = 0.85)
        {
            var P = Pal(i)[g];
            if (P.Count >= max && rng.Chance(reuse))
            {
                var pool = P.Where(k2 => Ok(g, k2) && (fits == null || fits(k2))).ToList();
                if (pool.Count > 0) return rng.Pick(pool);
            }
            if (!P.Contains(v) && P.Count < max) P.Add(v);
            return v;
        }

        // 段内方向交替
        private string Alternate(int i, string g, string v)
        {
            var key = _sec[i] + ":" + g;
            _last.TryGetValue(key, out var prev);
            if (!DirPairs.TryGetValue(g, out var pairs)) pairs = Array.Empty<string[]>();
            foreach (var pr in pairs)
            {
                var j = Array.IndexOf(pr, v);
                if (j >= 0 && prev != null && pr.Contains(prev))
                {
                    var w = pr[1 - Array.IndexOf(pr, prev)];
                    if (Ok(g, w)) v = w;
                }
            }
            _last[key] = v;
            return v;
        }

        private string Calm(int i, string g, string v, bool emph, Func<string> repick, Rng rng)
        {
            var key = _sec[i] + ":" + g;
            _strongRun.TryGetValue(key, out var run);
            if (!emph && Strong.TryGetValue(g, out var strong) && strong.Contains(v) && (run || rng.Chance(0.55)))
                for (var t = 0; t < 4; t++)
                {
                    var w = repick();
                    if (!strong.Contains(w))
                    {
                        v = w;
                        break;
                    }
                }
            _strongRun[key] = Strong.TryGetValue(g, out var strong2) && strong2.Contains(v);
            return v;
        }

        private string KimePick(string g, string v, Rng rng, Func<string, bool>? fits = null)
        {
            var pool = KimeTable[g].Where(k2 => Ok(g, k2) && (fits == null || fits(k2))).ToList();
            return pool.Count > 0 ? rng.Pick(pool) : v;
        }

        internal string Layout(int i, string v, int nn, bool kime, Rng rng, bool portrait)
        {
            bool Fits(string k2) => Reg.Layouts.TryGetValue(k2, out var L2) && L2.Fits != null && L2.Fits(nn)
                                    && (!portrait || L2.Portrait == null || L2.Portrait.Value != 0);
            if (kime) return KimePick("layout", v, rng, k2 => Reg.Layouts.TryGetValue(k2, out var L3) && L3.Fits != null && L3.Fits(nn));
            return Sticky(i, "layout", v, 3, rng, Fits, 0.9);
        }

        internal string Enter(int i, string v, string layout, double dur, bool emph, bool kime, Rng rng, int nn)
        {
            if (kime) return KimePick("enter", v, rng);
            v = Sticky(i, "enter", v, 2, rng);
            v = Calm(i, "enter", v, emph, () => PickEnter(rng, _st, _en, layout, dur, _history, false, nn), rng);
            return Alternate(i, "enter", v);
        }

        internal string Exit(int i, string v, string layout, double dur, bool kime, Rng rng)
        {
            if (kime) return KimePick("exit", v, rng);
            return Alternate(i, "exit", Sticky(i, "exit", v, 2, rng));
        }

        internal string Hold(int i, string v, bool kime, Rng rng)
        {
            if (kime) return KimePick("hold", v, rng);
            return Sticky(i, "hold", v, 1, rng);
        }

        internal string Cam(int i, string v, bool kime, bool emph, Rng rng)
        {
            if (kime) return KimePick("cam", v, rng);
            v = Sticky(i, "cam", v, 2, rng);
            v = Calm(i, "cam", v, emph, () => PickCam(rng, _st, _en, _fx, Reg.Layouts.GetValueOrDefault("center"), false, _history), rng);
            return Alternate(i, "cam", v);
        }

        internal List<DecorSpec> Decor(int i, List<DecorSpec> list, bool kime, Rng rng)
        {
            if (kime) return new List<DecorSpec>();
            var P = Pal(i)["decor"];
            if (P.Count >= 2 && rng.Chance(0.8))
            {
                var id = rng.Pick(P);
                return Reg.Decor.ContainsKey(id) ? new List<DecorSpec> { DecorParams(rng, id) } : list;
            }
            foreach (var d in list)
                if (!P.Contains(d.Id) && P.Count < 2)
                    P.Add(d.Id);
            return list;
        }

        internal string Treat(int i, string v, bool kime, Rng rng)
        {
            if (kime) return "none";
            return Sticky(i, "treat", v, 1, rng, null, 0.75);
        }

        internal string Trans(int i, string v, Rng rng) => Sticky(i, "trans", v, 2, rng);

        internal bool WeightGrow(bool kime, int nn, Rng rng, double dur)
        {
            if (!(_lang is "ja" or "en") || nn > 16 || dur < 0.7) return false;
            return rng.Chance(kime ? 0.45 : 0.14);
        }

        internal string SoftEnter(string v, Rng rng)
        {
            var pool = SoftEnterTable.Where(k2 => Ok("enter", k2)).ToList();
            return pool.Count > 0 ? rng.Pick(pool) : v;
        }

        internal AgainSpec? Again(int i, int k, string txt)
        {
            var f = i < _repeatOf.Count ? _repeatOf[i] : null;
            if (f == null) return null;
            var sp = _specs.TryGetValue(f.Value, out var list) && k < list.Count ? list[k] : null;
            return sp != null && sp.Text == txt ? sp : null;
        }

        internal void Remember(int i, int k, string txt, Cut cut)
        {
            if (!_specs.TryGetValue(i, out var list))
            {
                list = new List<AgainSpec?>();
                _specs[i] = list;
            }
            while (list.Count <= k) list.Add(null);
            list[k] = new AgainSpec
            {
                Text = txt,
                Layout = cut.Layout,
                Enter = cut.Enter,
                Exit = cut.Exit,
                Hold = cut.Hold,
                Params = cut.Params,
                Decor = cut.Decor,
                Treat = cut.Treat,
                TreatP = cut.TreatP,
                Cam = cut.Cam,
                CamP = cut.CamP,
                Scheme = cut.Scheme,
                Seed = cut.Seed,
                Bg = cut.Bg,
                BgP = cut.BgP,
                WeightGrow = cut.WeightGrow,
                Morph = cut.Morph != null,
                Trans = cut.Trans,
                TransP = cut.TransP,
                TransDur = cut.TransDur
            };
        }
    }

    // ---------------- 中央を空ける（splitHalf / SplitCut / SideZones） ----------------

    private static readonly Regex HeadBadRe =
        new("[、。，．,.!?！？…・ーっッゃゅょャュョぁぃぅぇぉァィゥェォをがはにでとのへもやよね」』）)]", RegexOptions.Compiled);
    private static readonly Regex LatinWordRe = new("^[A-Za-z0-9'’-]+$", RegexOptions.Compiled);
    private static readonly Regex HasLatinRe = new("[A-Za-z]", RegexOptions.Compiled);

    /// <summary>splitHalf：一幅画面，歌词拆两半——「花が」在左（上）带，「咲いた」在右（下）带。</summary>
    private static List<string> SplitHalf(string text, string lang)
    {
        var t = (text ?? "").Trim();
        var n = CpLen(WsRe.Replace(t, ""));
        // 尽量从词与词之间、靠近正中处分开（「花が」｜「咲いた」，"Good night," | "see you tomorrow"）
        var words = (lang == "en" ? PhraseChunks(ChunkText(t)) : ChunkText(t));
        if (words.Count >= 2)
        {
            int Lw(string w) => CpLen(WsRe.Replace(w, ""));
            var total = words.Sum(Lw);
            var acc = 0;
            var best = 1;
            var bd = 1e9;
            for (var k = 1; k < words.Count; k++)
            {
                acc += Lw(words[k - 1]);
                var d = Math.Abs(acc - total / 2.0);
                if (d < bd)
                {
                    bd = d;
                    best = k;
                }
            }
            var sep = HasLatinRe.IsMatch(t) ? " " : "";
            return new List<string>
            {
                string.Join(sep, words.Take(best)).Trim(),
                string.Join(sep, words.Skip(best)).Trim()
            };
        }
        // 单个词：短的（和英文单词）两边都放整词，长的在中间附近切
        if (n <= 3 || LatinWordRe.IsMatch(t)) return new List<string> { t, t };
        var two = SplitToCount(new List<string> { t }, 2);
        if (two.Count < 2) return new List<string> { t, t };
        var a = two[0].Trim();
        var b = string.Concat(two.Skip(1)).Trim();
        // 后半不能以助词、标点或小假名开头：「夜明けの色を」｜「覚えてる」，而不是「…色」｜「を…」
        for (var g = 0; g < 3 && b.Length > 1 && HeadBadRe.IsMatch(b[0].ToString()); g++)
        {
            a += b[0];
            b = b[1..];
        }
        return new List<string> { a, b };
    }

    /// <summary>splitCut：把切分拆成左右（上下）孪生，两半用同一套版式/动态/装饰/运镜。</summary>
    private static void SplitCut(Cut cut, List<string> halves, Zone[] zones, StyleDef st, double dur, JObject? LS)
    {
        var LD = Reg.Layouts[cut.Layout];
        var seed = J.H((int)cut.Seed, 23);
        JObject PlanFor(string text, Zone z) =>
            LD.Plan!(J.Rng(seed), new LayoutCtx
            {
                Text = text,
                N = CpLen(WsRe.Replace(text, "")),
                W = z.W,
                H = z.H,
                Dur = dur
            }, st) ?? new JObject();
        cut.Text = halves[0];
        cut.LineText = halves[0];
        cut.Words = ChunkText(halves[0]).ToArray();
        cut.Zone = CloneZone(zones[0]);
        // 锁定行用自己的参数
        cut.Params = LS != null && LS["params"] != null && Truthy(LS["twinParams"])
            ? (JObject)LS["params"]!.DeepClone()
            : PlanFor(halves[0], zones[0]);
        var delay = Math.Min(0.12, dur * 0.08);
        var twin = CloneCutShallow(cut);
        twin.Text = halves[1];
        twin.LineText = halves[1];
        twin.Words = ChunkText(halves[1]).ToArray();
        twin.Zone = CloneZone(zones[1]);
        twin.Params = LS != null && Truthy(LS["twinParams"])
            ? LS["twinParams"] as JObject ?? new JObject()
            : PlanFor(halves[1], zones[1]);
        twin.Start = cut.Start + delay;
        twin.Bg = "none";
        twin.BgP = new JObject();
        twin.Trans = null;
        twin.TransP = new JObject();
        twin.TransDur = 0;
        twin.Morph = cut.Morph;
        twin.Dur = twin.End - twin.Start;
        cut.Companion = twin;
    }

    /// <summary>JS 的 Object.assign({}, cut)：浅拷贝全部字段（decor/params 等共享引用后由调用方覆盖）。</summary>
    private static Cut CloneCutShallow(Cut c) => new()
    {
        Index = c.Index,
        Text = c.Text,
        LineText = c.LineText,
        Note = c.Note,
        Line = c.Line,
        Start = c.Start,
        End = c.End,
        Dur = c.Dur,
        Layout = c.Layout,
        Enter = c.Enter,
        Exit = c.Exit,
        Hold = c.Hold,
        InDur = c.InDur,
        OutDur = c.OutDur,
        Stagger = c.Stagger,
        Params = c.Params,
        Decor = c.Decor,
        Scheme = c.Scheme,
        Seed = c.Seed,
        Emph = c.Emph,
        Recap = c.Recap,
        Words = c.Words,
        Treat = c.Treat,
        TreatP = c.TreatP,
        Bg = c.Bg,
        BgP = c.BgP,
        Cam = c.Cam,
        CamP = c.CamP,
        Trans = c.Trans,
        TransP = c.TransP,
        TransDur = c.TransDur,
        Zone = c.Zone,
        Utext = c.Utext,
        Morph = c.Morph,
        Kime = c.Kime,
        WeightGrow = c.WeightGrow
    };

    /// <summary>
    /// sideZones：中央を空ける的侧边条带（设计像素）。宽帧分左右第三（21:9 再窄一点），
    /// 竖帧分上下，接近方形的算宽帧。gapPct：留白占宽/高百分比（10..70），缺省用固定比例。
    /// zonePos：留白位置 center/left/right/top/bottom。
    /// </summary>
    public static Zone[] SideZones(int W, int H, string? dir, double? gapPct, string? zonePos)
    {
        var gap = !(gapPct > 0) ? 0 : Math.Min(70, Math.Max(10, gapPct.Value)) / 100.0;
        var bw = (int)Math.Round(W * (gap > 0 ? gap : 0.3), MidpointRounding.AwayFromZero);
        var bh = (int)Math.Round(H * (gap > 0 ? gap : 0.33), MidpointRounding.AwayFromZero);
        var halfH = (int)Math.Round(H / 2.0, MidpointRounding.AwayFromZero);
        var halfW = (int)Math.Round(W / 2.0, MidpointRounding.AwayFromZero);
        if (zonePos == "left")
        {
            var w = bw;
            return new[]
            {
                new Zone { X = w, Y = 0, W = W - w, H = halfH, Side = "top" },
                new Zone { X = w, Y = halfH, W = W - w, H = H - halfH, Side = "bottom" }
            };
        }
        if (zonePos == "right")
        {
            var w = bw;
            return new[]
            {
                new Zone { X = 0, Y = 0, W = W - w, H = halfH, Side = "top" },
                new Zone { X = 0, Y = halfH, W = W - w, H = H - halfH, Side = "bottom" }
            };
        }
        if (zonePos == "top")
        {
            var h = bh;
            return new[]
            {
                new Zone { X = 0, Y = h, W = halfW, H = H - h, Side = "left" },
                new Zone { X = halfW, Y = h, W = W - halfW, H = H - h, Side = "right" }
            };
        }
        if (zonePos == "bottom")
        {
            var h = bh;
            return new[]
            {
                new Zone { X = 0, Y = 0, W = halfW, H = H - h, Side = "left" },
                new Zone { X = halfW, Y = 0, W = W - halfW, H = H - h, Side = "right" }
            };
        }
        if (H > W * 1.1 && dir == "lr")
        {
            // 竖长帧也分左右
            var w = (int)Math.Round(W * (gap > 0 ? (1 - gap) / 2 : 0.34), MidpointRounding.AwayFromZero);
            return new[]
            {
                new Zone { X = 0, Y = 0, W = w, H = H, Side = "left" },
                new Zone { X = W - w, Y = 0, W = w, H = H, Side = "right" }
            };
        }
        if (H > W * 1.1)
        {
            var h = (int)Math.Round(H * (gap > 0 ? (1 - gap) / 2 : 0.33), MidpointRounding.AwayFromZero);
            return new[]
            {
                new Zone { X = 0, Y = 0, W = W, H = h, Side = "top" },
                new Zone { X = 0, Y = H - h, W = W, H = h, Side = "bottom" }
            };
        }
        var w2 = (int)Math.Round(W * (gap > 0 ? (1 - gap) / 2 : W / (double)H > 2 ? 0.3 : 0.36), MidpointRounding.AwayFromZero);
        return new[]
        {
            new Zone { X = 0, Y = 0, W = w2, H = H, Side = "left" },
            new Zone { X = W - w2, Y = 0, W = w2, H = H, Side = "right" }
        };
    }

    /// <summary>
    /// 留空区（centerFree）的实际范围：SideZones 两条条带的补集包围盒。
    /// 编辑器预览用它画虚线指引框——直接由 plan.Zones 反推，与引擎真正留出的像素一致
    /// （不再依赖工程里的 CenterDir/CenterGap/CenterZone 复算，避免两处公式漂移）。
    /// </summary>
    public static (double X, double Y, double W, double H)? FreeZone(Plan? plan)
    {
        var z = plan?.Zones;
        if (plan == null || z == null || z.Length < 2) return null;
        var W = plan.W;
        var H = plan.H;
        // 左带：第一条从 X>0 起 → 左侧留空
        if (z[0].X > 0) return (0, 0, z[0].X, H);
        // 右带：最后一条没顶到右边 → 右侧留空
        if (z[1].X + z[1].W < W) return (z[1].X + z[1].W, 0, W - (z[1].X + z[1].W), H);
        // 上带：第一条从 Y>0 起 → 顶部留空
        if (z[0].Y > 0) return (0, 0, W, z[0].Y);
        // 下带：最后一条没顶到下边 → 底部留空
        if (z[1].Y + z[1].H < H) return (0, z[1].Y + z[1].H, W, H - (z[1].Y + z[1].H));
        // 条带是左右两半（横屏默认 / 竖屏 lr）→ 中间竖带
        if (z[0].W < W) return (z[0].X + z[0].W, 0, z[1].X - (z[0].X + z[0].W), H);
        // 条带是上下两条（竖屏默认）→ 中间横带
        if (z[0].H < H) return (0, z[0].Y + z[0].H, W, z[1].Y - (z[0].Y + z[0].H));
        return null;
    }

    // ---------------- makeCut / 切分拆分工具 ----------------

    /// <summary>makeCut：默认值与 JS 一致（hold="still", inDur=0.3, outDur=0.25, stagger=0.04, …），dur = end - start。</summary>
    private static Cut MakeCut(string text, string lineText, int line, double start, double end, string layout,
        string enter, string exit, string hold,
        double inDur = 0.3, double outDur = 0.25, double stagger = 0.04,
        JObject? p = null, List<DecorSpec>? decor = null, int scheme = 0, uint seed = 0,
        bool emph = false, bool recap = false, string[]? words = null, string? note = null,
        string treat = "none", JObject? treatP = null, string bg = "none", JObject? bgP = null,
        string cam = "push", JObject? camP = null,
        string? trans = null, JObject? transP = null, double transDur = 0,
        Zone? zone = null, string? utext = null)
    {
        var c = new Cut
        {
            Text = text,
            LineText = lineText,
            Note = note,
            Line = line,
            Start = start,
            End = end,
            Layout = layout,
            Enter = enter,
            Exit = exit,
            Hold = hold,
            InDur = inDur,
            OutDur = outDur,
            Stagger = stagger,
            Params = p ?? new JObject(),
            Decor = decor ?? new List<DecorSpec>(),
            Scheme = scheme,
            Seed = seed,
            Emph = emph,
            Recap = recap,
            Words = words ?? Array.Empty<string>(),
            Treat = treat,
            TreatP = treatP ?? new JObject(),
            Bg = bg,
            BgP = bgP ?? new JObject(),
            Cam = cam,
            CamP = camP ?? new JObject(),
            Trans = trans,
            TransP = transP ?? new JObject(),
            TransDur = transDur,
            Zone = zone,
            Utext = utext
        };
        c.Dur = c.End - c.Start;
        return c;
    }

    /// <summary>splitToCount：拆词块直到至少 n 块，每次拆最长的（拉丁文按词，其他按字）。</summary>
    private static List<string> SplitToCount(List<string> chunks, int n)
    {
        var outList = chunks.ToList();
        var guard = 0;
        while (outList.Count < n && guard++ < 64)
        {
            var bi = -1;
            var bl = 1;
            for (var i = 0; i < outList.Count; i++)
            {
                var c = outList[i];
                var l = Regex.IsMatch(c.Trim(), @"\s") ? Regex.Split(c.Trim(), @"\s+").Length : CpLen(c);
                if (l > bl)
                {
                    bl = l;
                    bi = i;
                }
            }
            if (bi < 0) break;
            var c0 = outList[bi].Trim();
            string a, b;
            if (Regex.IsMatch(c0, @"\s"))
            {
                var w = Regex.Split(c0, @"\s+");
                var h = (int)Math.Ceiling(w.Length / 2.0);
                a = string.Join(" ", w.Take(h));
                b = string.Join(" ", w.Skip(h));
            }
            else
            {
                // 有分词边界就取最靠近中间的（夜明け|の），否则按字切
                var ch = CodePoints(c0);
                var segs = Segments(c0);
                var cut = (int)Math.Ceiling(ch.Count / 2.0);
                if (segs.Count > 1)
                {
                    var acc2 = 0;
                    var bestC = -1;
                    var bdC = 1e9;
                    for (var k = 0; k < segs.Count - 1; k++)
                    {
                        acc2 += CpLen(segs[k]);
                        var d = Math.Abs(acc2 - ch.Count / 2.0);
                        if (d < bdC)
                        {
                            bdC = d;
                            bestC = acc2;
                        }
                    }
                    if (bestC > 0) cut = bestC;
                }
                a = string.Concat(ch.Take(cut));
                b = string.Concat(ch.Skip(cut));
            }
            outList.RemoveAt(bi);
            outList.Insert(bi, a);
            outList.Insert(bi + 1, b);
        }
        return outList;
    }

    private static List<List<string>> Partition(List<string> chunks, int k)
    {
        var lens = chunks.Select(c => (double)CpLen(c) + 1).ToList();
        var tot = lens.Sum();
        var target = tot / k;
        var groups = new List<List<string>>();
        var cur = new List<string>();
        var acc = 0.0;
        var remainingGroups = k;
        for (var i = 0; i < chunks.Count; i++)
        {
            var remainingChunks = chunks.Count - i;
            if (cur.Count > 0 && (acc + lens[i] / 2 > target || remainingChunks < remainingGroups) && groups.Count < k - 1)
            {
                groups.Add(cur);
                cur = new List<string>();
                acc = 0;
                remainingGroups--;
            }
            cur.Add(chunks[i]);
            acc += lens[i];
        }
        if (cur.Count > 0) groups.Add(cur);
        return groups;
    }

    // ---------------- 抽取（novelty / pick* / decorParams） ----------------

    private static string? HVal(Hist h, string key) => key switch
    {
        "layout" => h.Layout,
        "enter" => h.Enter,
        "exit" => h.Exit,
        "hold" => h.Hold,
        "treat" => h.Treat,
        "cam" => h.Cam,
        "trans" => h.Trans,
        _ => null
    };

    /// <summary>novelty：最近用过的手法降权（d&lt;2 ×0.2，否则 ×0.6，最多回看 6 条）。</summary>
    private static double Novelty(List<Hist> history, string key, string? val)
    {
        var w = 1.0;
        var d = 0;
        for (var i = history.Count - 1; i >= 0 && d < 6; i--, d++)
            if (HVal(history[i], key) == val)
                w *= d < 2 ? 0.2 : 0.6;
        return w;
    }

    /// <summary>PORTRAIT_W：竖帧权重乘数表（layout 未声明 portrait 时用）。</summary>
    private static readonly Dictionary<string, double> PortraitW = new()
    {
        ["vcols"] = 1.9, ["condensed"] = 1.3, ["huge"] = 1.3, ["center"] = 1.2, ["stack"] = 1.1,
        ["mixed"] = 0.7, ["marquee"] = 0.6, ["wave"] = 0.6, ["diag"] = 0.8, ["type"] = 0.8, ["gloss"] = 0.5
    };

    private static string PickLayout(Rng rng, StyleDef st, Dictionary<string, Dictionary<string, bool>> en,
        int n, double dur, List<Hist> history, bool emph, bool recap, bool portrait)
    {
        var cands = new List<(string V, double W)>();
        foreach (var k in Reg.LayoutOrder)
        {
            var L = Reg.Layouts.GetValueOrDefault(k);
            if (L == null || !EnOn(en, "layout", k) || L.Fits?.Invoke(n) != true) continue;
            var w = WKey(BiasOf(st, "layout"), k, L.W ?? 1) * Novelty(history, "layout", k);
            if (portrait) w *= L.Portrait ?? PortraitW.GetValueOrDefault(k, 1);
            if (emph && L.Emph.GetValueOrDefault() != 0) w *= L.Emph.GetValueOrDefault();
            if (emph && k is "huge" or "center" or "tile" or "marquee" or "condensed") w *= 2;
            if (recap && k is "center" or "stack" or "marquee" or "tile" or "mixed" or "type" or "gloss") w *= 1.8;
            if (dur < 0.5 && k is "wave" or "ring" or "labels" or "gloss" or "type" or "tile") w *= 0.3;
            if (dur < 0.5 && k is "center" or "huge" or "condensed" or "vcols") w *= 1.4;
            cands.Add((k, w));
        }
        if (cands.Count == 0) return "center";
        return rng.WPick(cands);
    }

    /// <summary>LAYOUT_ENTER：按版式的 enter 偏置表。</summary>
    private static readonly Dictionary<string, JObject> LayoutEnter = new()
    {
        ["type"] = new JObject { ["type"] = 4, ["scramble"] = 1.5 },
        ["ring"] = new JObject { ["pop"] = 2, ["spin"] = 2, ["cut"] = 1, ["assemble"] = 0.4, ["slice"] = 0.2, ["wipe"] = 0.2 },
        ["labels"] = new JObject { ["cut"] = 3, ["pop"] = 1 },
        ["wave"] = new JObject { ["pop"] = 1.5, ["drop"] = 1.5, ["blur"] = 1, ["slice"] = 0.3 },
        ["tile"] = new JObject { ["assemble"] = 1.3, ["slice"] = 1.4, ["zoom"] = 1.4 },
        ["huge"] = new JObject { ["zoom"] = 1.5, ["wipe"] = 1.5, ["slice"] = 1.4, ["stretch"] = 1.3, ["type"] = 0.2 },
        ["mixed"] = new JObject { ["pop"] = 1.6, ["drop"] = 1.6, ["spin"] = 1.3 },
        ["scatter"] = new JObject { ["pop"] = 1.5, ["spin"] = 1.5, ["drop"] = 1.2, ["assemble"] = 1.3 },
        ["vcols"] = new JObject { ["assemble"] = 1.8, ["type"] = 1.2 },
        ["pill"] = new JObject { ["wipe"] = 1.8, ["type"] = 1.2 },
    };

    private static string PickEnter(Rng rng, StyleDef st, Dictionary<string, Dictionary<string, bool>> en,
        string layout, double dur, List<Hist> history, bool emph, int n)
    {
        var cands = new List<(string V, double W)>();
        foreach (var k in Reg.EnterOrder)
        {
            if (!EnOn(en, "enter", k)) continue;
            var D = Reg.Enter.GetValueOrDefault(k);
            if (D == null) continue;
            var LD = Reg.Layouts.GetValueOrDefault(layout);
            var biasTable = LayoutEnter.TryGetValue(layout, out var leJ) ? leJ : LD?.EnterBias;
            var w = WKey(BiasOf(st, "enter"), k, D.W ?? 1) * Novelty(history, "enter", k) * WKey(biasTable, k, 1);
            if (D.MinDur != null && dur < D.MinDur) w *= 0.15;
            if (D.MaxChars != null && n > D.MaxChars) w *= 0.2;
            if (k == "cut") w *= 0.5;
            if (dur < 0.45 && k is "type" or "assemble" or "drop" or "spin" or "pop" or "flicker") w *= 0.25;
            if (dur < 0.45 && k is "cut" or "slice" or "zoom" or "stretch") w *= 1.8;
            if (k == "type" && n > 18) w *= 0.3;
            if (emph && k is "zoom" or "assemble" or "slice") w *= 1.8;
            cands.Add((k, w));
        }
        return cands.Count > 0 ? rng.WPick(cands) : "cut";
    }

    private static string PickExit(Rng rng, StyleDef st, Dictionary<string, Dictionary<string, bool>> en,
        string layout, double dur, bool lastOfLine, List<Hist> history)
    {
        var cands = new List<(string V, double W)>();
        foreach (var k in Reg.ExitOrder)
        {
            if (!EnOn(en, "exit", k)) continue;
            var D = Reg.Exit.GetValueOrDefault(k);
            if (D == null) continue;
            var w = WKey(BiasOf(st, "exit"), k, D.W ?? 1) * Novelty(history, "exit", k);
            if (D.MinDur != null && dur < D.MinDur) w *= 0.15;
            if (k == "cut") w *= dur < 0.6 ? 4 : lastOfLine ? 1.2 : 2.2;
            if (dur < 0.6 && k != "cut") w *= 0.4;
            if (layout is "labels" or "ring" or "tile" && k is "explode" or "fall" or "drift") w *= 0.3;
            cands.Add((k, w));
        }
        return cands.Count > 0 ? rng.WPick(cands) : "cut";
    }

    private static readonly Dictionary<string, double> HoldW = new()
    {
        ["still"] = 1, ["jitter"] = 1.2, ["drift"] = 1, ["breathe"] = 0.7, ["wave"] = 0.4, ["glitchtick"] = 0.9
    };

    private static string PickHold(Rng rng, Dictionary<string, Dictionary<string, bool>> en, FxSettings fx, List<Hist> history)
    {
        var cands = new List<(string V, double W)>();
        foreach (var k in Reg.HoldOrder)
        {
            if (!EnNotOff(en, "hold", k) || !Reg.Hold.TryGetValue(k, out var D)) continue;
            var w = HoldW.TryGetValue(k, out var hw) ? hw : D.W ?? 0.8;
            if (k == "jitter" || D.Tags.Contains("glitch")) w *= 0.4 + fx.Motion;
            if (k == "glitchtick") w *= fx.Glitch;
            cands.Add((k, w * Novelty(history, "hold", k)));
        }
        return cands.Count > 0 ? rng.WPick(cands) : "still";
    }

    /// <summary>decorParams：一个装饰的全部随机参数（RNG 消耗顺序固定）。</summary>
    private static DecorSpec DecorParams(Rng rng, string k) => new()
    {
        Id = k,
        Seed = rng.Int(1, 1000000000),
        N = rng.Int(1, 3) + (k == "shapes" ? 3 : 0) + (k == "sparks" ? 4 : 0),
        Right = rng.Chance(0.5),
        Low = rng.Chance(0.5),
        Accent = rng.Chance(0.4),
        Corner = rng.Chance(0.5),
        Big = rng.Chance(0.4),
        Mode = rng.Pick(new[] { "count", "index" }),
        From = rng.Int(0, 20),
        To = rng.Int(30, 999),
        V = rng.Int(0, 5),
        R = rng.Next()
    };

    private static List<DecorSpec> PickDecor(Rng rng, StyleDef st, Dictionary<string, Dictionary<string, bool>> en,
        FxSettings fx, string layout, List<Hist>? history = null)
    {
        history ??= new List<Hist>();
        var count = (int)Math.Round(fx.Decor * 2.8 * rng.Range(0.45, 1.15), MidpointRounding.AwayFromZero);
        var recent = new HashSet<string>();
        for (var i = Math.Max(0, history.Count - 2); i < history.Count; i++)
            foreach (var d in history[i].Decor)
                recent.Add(d);
        var LD = Reg.Layouts.GetValueOrDefault(layout);
        var cands = new List<(string V, double W)>();
        foreach (var k in Reg.DecorOrder)
        {
            if (!EnOn(en, "decor", k) || !Reg.Decor.TryGetValue(k, out var dd)) continue;
            if (LD != null && LD.Busy && dd.Layer == "back" && !dd.Subtle) continue;
            var wDef = dd.W != null ? dd.W.Value * 0.5 : 0.35;
            cands.Add((k, WKey(st.Decor, k, wDef) * (recent.Contains(k) ? 0.35 : 1)));
        }
        var outList = new List<DecorSpec>();
        for (var i = 0; i < count && cands.Count > 0; i++)
        {
            var k2 = rng.WPick(cands);
            var idx = cands.FindIndex(c => c.V == k2);
            if (idx >= 0) cands.RemoveAt(idx);
            outList.Add(DecorParams(rng, k2));
        }
        return outList;
    }

    // 文字加工：大多数时候不加，decor 滑块提高使用频率
    private static string PickTreat(Rng rng, StyleDef st, Dictionary<string, Dictionary<string, bool>> en,
        FxSettings fx, LayoutDef LD, bool emph, List<Hist> history)
    {
        if (LD.Treat is bool b && !b) return "none";
        if (!rng.Chance(0.18 + 0.42 * fx.Decor + (emph ? 0.15 : 0))) return "none";
        var safeOnly = LD.Treat is string s2 && s2 == "safe";
        var cands = new List<(string V, double W)>();
        foreach (var k in Reg.TreatOrder)
        {
            if (k == "none" || !EnNotOff(en, "treat", k) || !Reg.Treat.TryGetValue(k, out var td)) continue;
            if (safeOnly && !td.Safe) continue;
            cands.Add((k, WKey(BiasOf(st, "treat"), k, td.W ?? 1) * Novelty(history, "treat", k)));
        }
        return cands.Count > 0 ? rng.WPick(cands) : "none";
    }

    private static string PickBg(Rng rng, StyleDef st, Dictionary<string, Dictionary<string, bool>> en,
        FxSettings fx, List<string> bgHist)
    {
        if (!rng.Chance(0.2 + 0.35 * fx.Decor + 0.2 * fx.BgSwitch)) return "none";
        var last = new List<string>();
        for (var i = Math.Max(0, bgHist.Count - 3); i < bgHist.Count; i++) last.Add(bgHist[i]);
        var cands = new List<(string V, double W)>();
        foreach (var k in Reg.BgOrder)
        {
            if (k == "none" || !EnNotOff(en, "bg", k) || !Reg.Bg.TryGetValue(k, out var bd)) continue;
            cands.Add((k, WKey(BiasOf(st, "bg"), k, bd.W ?? 1) * (last.Contains(k) ? 0.25 : 1)));
        }
        return cands.Count > 0 ? rng.WPick(cands) : "none";
    }

    private static string PickCam(Rng rng, StyleDef st, Dictionary<string, Dictionary<string, bool>> en,
        FxSettings fx, LayoutDef? LD, bool emph, List<Hist> history)
    {
        var cands = new List<(string V, double W)>();
        foreach (var k in Reg.CameraOrder)
        {
            if (!EnNotOff(en, "cam", k) || !Reg.Camera.TryGetValue(k, out var cd)) continue;
            var w = WKey(BiasOf(st, "cam"), k, cd.W ?? 1) * Novelty(history, "cam", k);
            if (cd.Strong) w *= 0.25 + 0.9 * fx.Motion + (emph ? 0.6 : 0);
            if (LD != null && LD.Cam == false && k != "push") w *= 0.05;
            cands.Add((k, w));
        }
        return cands.Count > 0 ? rng.WPick(cands) : "push";
    }

    private static string? PickTrans(Rng rng, StyleDef st, Dictionary<string, Dictionary<string, bool>> en,
        FxSettings fx, bool emph, List<Hist> history)
    {
        if (Reg.TransOrder.Count == 0) return null;
        if (!rng.Chance(0.1 + 0.22 * fx.Motion + (emph ? 0.08 : 0))) return null;
        var cands = new List<(string V, double W)>();
        foreach (var k in Reg.TransOrder)
        {
            if (!EnNotOff(en, "trans", k) || !Reg.Trans.TryGetValue(k, out var td)) continue;
            cands.Add((k, WKey(BiasOf(st, "trans"), k, td.W ?? 1) * Novelty(history, "trans", k)));
        }
        return cands.Count > 0 ? rng.WPick(cands) : null;
    }

    // kind 'edge' = 切分边界上的转场，'mid' = 切分中段的点缀
    private static string? PickFx(Rng rng, StyleDef st, Dictionary<string, Dictionary<string, bool>> en,
        FxSettings fx, bool emph, List<string> fxHist, string kind)
    {
        var g = fx.Glitch;
        var p = kind == "edge"
            ? 0.12 + 0.38 * g + 0.12 * fx.Motion + (emph ? 0.15 : 0)
            : 0.05 + 0.2 * g;
        if (!rng.Chance(p)) return null;
        var last = new List<string>();
        for (var i = Math.Max(0, fxHist.Count - 3); i < fxHist.Count; i++) last.Add(fxHist[i]);
        var cands = new List<(string V, double W)>();
        foreach (var k in Reg.FxeOrder)
        {
            if (!Reg.Fxe.TryGetValue(k, out var fd) || fd.Builtin || !EnNotOff(en, "fx", k)) continue;
            if (kind == "edge" ? !fd.Edge : !fd.Mid) continue;
            var w = WKey(BiasOf(st, "fx"), k, fd.W ?? 1) * (last.Contains(k) ? 0.2 : 1);
            if (fd.Glitchy) w *= 0.3 + g * 1.4;
            cands.Add((k, w));
        }
        return cands.Count > 0 ? rng.WPick(cands) : null;
    }

    // ---------------- 锁定快照（J.lineSnapshot） ----------------

    /// <summary>DecorSpec → JSON（锁定快照契约键：id/seed/n/right/…，与 JS decorParams 同形）。</summary>
    private static JObject DecorToJson(DecorSpec d) => new()
    {
        ["id"] = d.Id,
        ["seed"] = d.Seed,
        ["n"] = d.N,
        ["right"] = d.Right,
        ["low"] = d.Low,
        ["accent"] = d.Accent,
        ["corner"] = d.Corner,
        ["big"] = d.Big,
        ["mode"] = d.Mode,
        ["from"] = d.From,
        ["to"] = d.To,
        ["v"] = d.V,
        ["r"] = d.R
    };

    private static DecorSpec DecorFromJson(JObject? o) => new()
    {
        Id = Str(o, "id") ?? "",
        Seed = (int)(Num(o, "seed") ?? 0),
        N = (int)(Num(o, "n") ?? 0),
        Right = Truthy(o?["right"]),
        Low = Truthy(o?["low"]),
        Accent = Truthy(o?["accent"]),
        Corner = Truthy(o?["corner"]),
        Big = Truthy(o?["big"]),
        Mode = Str(o, "mode") ?? "count",
        From = (int)(Num(o, "from") ?? 0),
        To = (int)(Num(o, "to") ?? 0),
        V = (int)(Num(o, "v") ?? 0),
        R = Num(o, "r") ?? 0
    };

    /// <summary>
    /// J.lineSnapshot：把一行显示的东西存下来，便于原样保留（存进 project.overrides[line].lockedCuts）。
    /// 返回 JArray（对齐 JS 的 JSON 结构，字段名逐字一致）。
    /// </summary>
    public static JArray? LineSnapshot(Plan plan, int li)
    {
        var cuts = plan.Cuts.Where(c => c.Line == li && c.Utext != null).ToList();
        if (cuts.Count == 0) return null;
        var S = new JArray();
        foreach (var c in cuts)
            S.Add(new JObject
            {
                ["utext"] = c.Utext,
                ["layout"] = c.Layout,
                ["enter"] = c.Enter,
                ["exit"] = c.Exit,
                ["hold"] = c.Hold,
                ["inDur"] = c.InDur,
                ["outDur"] = c.OutDur,
                ["params"] = c.Params != null ? c.Params.DeepClone() : new JObject(),
                ["decor"] = new JArray(c.Decor.Select(DecorToJson)),
                ["treat"] = c.Treat,
                ["treatP"] = c.TreatP != null ? c.TreatP.DeepClone() : new JObject(),
                ["bg"] = c.Bg,
                ["bgP"] = c.BgP != null ? c.BgP.DeepClone() : new JObject(),
                ["cam"] = c.Cam,
                ["camP"] = c.CamP != null ? c.CamP.DeepClone() : new JObject(),
                ["scheme"] = c.Scheme,
                ["seed"] = c.Seed,
                ["trans"] = c.Trans != null ? (JToken)c.Trans : JValue.CreateNull(),
                ["transP"] = c.TransP != null ? c.TransP.DeepClone() : new JObject(),
                ["transDur"] = c.TransDur,
                ["morph"] = c.Morph != null ? c.Morph.DeepClone() : JValue.CreateNull(),
                ["weightGrow"] = c.WeightGrow,
                ["kime"] = c.Kime,
                ["recap"] = c.Recap,
                ["twinParams"] = c.Companion != null
                    ? (JToken)(c.Companion.Params?.DeepClone() ?? new JObject())
                    : JValue.CreateNull(),
                ["events"] = new JArray()
            });
        // 每个打点归属它所在的切分（紧贴切分开始前的算给这个切分）
        void PushEvent(int k, PlanEvent e)
        {
            ((JArray)S[k]["events"]!).Add(new JObject
            {
                // JS 的 +(e.t - start).toFixed(4)
                ["dt"] = Math.Round(e.T - cuts[k].Start, 4, MidpointRounding.AwayFromZero),
                ["type"] = e.Type,
                ["amp"] = e.Amp,
                ["dur"] = e.Dur
            });
        }
        foreach (var e in plan.Events)
        {
            // 打点属于制造它的那个切分，包括行尾之后才播放的（收尾特效不再丢失）
            Cut? own = plan.EvOwner.TryGetValue(e, out var oc) ? oc : null;
            if (own != null)
            {
                var k = cuts.IndexOf(own);
                if (k >= 0) PushEvent(k, e);
                continue;
            }
            var kj = -1;
            // [start − 0.25, 下一刀 start − 0.25)：贴着下一刀前面的打点归下一刀
            for (var j = 0; j < cuts.Count; j++)
            {
                var until = j < cuts.Count - 1 ? cuts[j + 1].Start - 0.25 : cuts[j].End - 0.3;
                if (e.T >= cuts[j].Start - 0.25 && e.T < until) kj = j;
            }
            if (kj >= 0) PushEvent(kj, e);
        }
        return S;
    }

    // ---------------- 缩略图预览（J.previewPlan） ----------------

    /// <summary>手法标签页缩略图用的单切分 plan（转场是两切分）。</summary>
    public static Plan PreviewPlan(Project project, string group, string key)
    {
        // JS 的 enUI = document.documentElement.lang === 'en'（UI 语言，引擎内无 DOM——固定日文）
        const bool enUI = false;
        var st = JStyles.ResolveStyle(project);
        var fx = CloneFx(project.Fx);
        fx.Glitch = group == "fx" ? 0.85 : 0;
        fx.Chroma = group == "fx" ? 0.9 : 0.22;
        fx.Flash = false;
        fx.Hud = "off";
        fx.Texture = 0.3;
        fx.BgSwitch = 0;
        fx.Motion = 0.75;
        fx.Decor = group == "decor" ? 1 : 0;
        var (W, H) = DesignSize(string.IsNullOrEmpty(project.Aspect) ? "16:9" : project.Aspect);
        var rng = J.Rng(J.H((int)J.Sid(group + ":" + key), 11, 22));
        var text = enUI ? "Lyric" : "字面";
        if (group == "layout")
        {
            var L0 = Reg.Layouts.GetValueOrDefault(key);
            if (L0 != null && L0.Fits != null && !L0.Fits(CpLen(WsRe.Replace(text, ""))))
                text = enUI ? "color of dawn" : "夜明けの色を";
            if (L0 != null && L0.Fits != null && !L0.Fits(CpLen(WsRe.Replace(text, ""))))
                text = enUI ? "I remember the color of dawn" : "夜明けの色を覚えてる";
        }
        var nn = CpLen(WsRe.Replace(text, ""));
        const double dur = 2.4;
        var layout = group == "layout" ? key : "center";
        if (!Reg.Layouts.TryGetValue(layout, out var LD0) || LD0.Special) layout = "center";
        var enter = group == "enter" ? key : "cut";
        var exit = group == "exit" ? key : "cut";
        var hold = group == "hold" ? key : "still";
        if (!Reg.Enter.ContainsKey(enter)) enter = "cut";
        if (!Reg.Exit.ContainsKey(exit)) exit = "cut";
        if (!Reg.Hold.ContainsKey(hold)) hold = "still";
        if (group is "layout" or "decor" or "treat" or "bg")
        {
            if (enter == "cut" && Reg.Enter.ContainsKey("pop")) enter = "pop";
            if (hold == "still" && Reg.Hold.ContainsKey("breathe")) hold = "breathe";
            else if (hold == "still" && Reg.Hold.ContainsKey("drift")) hold = "drift";
        }
        var LD = Reg.Layouts.GetValueOrDefault(layout)!;
        JObject ps;
        try
        {
            ps = LD.Plan!(rng, new LayoutCtx { Text = text, N = nn, W = W, H = H, Dur = dur }, st) ?? new JObject();
        }
        catch
        {
            ps = new JObject();
        }
        var inDur = enter == "cut" ? 0.12 : 0.5;
        if (Reg.Enter.TryGetValue(enter, out var ed) && ed.InDur != null)
            try
            {
                inDur = ed.InDur(dur, nn);
            }
            catch
            {
            }
        var outDur = exit == "cut" ? 0 : 0.5;
        if (Reg.Exit.TryGetValue(exit, out var xd) && xd.OutDur != null)
            try
            {
                outDur = xd.OutDur(dur, nn);
            }
            catch
            {
            }
        if (inDur + outDur > dur * 0.85)
        {
            var f = dur * 0.85 / Math.Max(0.2, inDur + outDur);
            inDur *= f;
            outDur *= f;
        }
        var decor = group == "decor" && Reg.Decor.ContainsKey(key)
            ? new List<DecorSpec> { DecorParams(rng, key) }
            : new List<DecorSpec>();
        var treat = group == "treat" ? key : "none";
        if (!Reg.Treat.ContainsKey(treat)) treat = "none";
        var treatP = Reg.Treat.TryGetValue(treat, out var tr) && tr.Plan != null
            ? tr.Plan!(rng, st) ?? new JObject()
            : new JObject();
        var bg = group == "bg" ? key : "none";
        if (!Reg.Bg.ContainsKey(bg)) bg = "none";
        var bgP = Reg.Bg.TryGetValue(bg, out var bgd) && bgd.Plan != null
            ? bgd.Plan!(rng, st) ?? new JObject()
            : new JObject();
        var cam = group == "cam" ? key : "push";
        if (!Reg.Camera.ContainsKey(cam)) cam = "push";
        var camP = Reg.Camera.TryGetValue(cam, out var cd) && cd.Plan != null
            ? cd.Plan!(rng, st) ?? new JObject()
            : new JObject();
        var events = new List<PlanEvent>();
        if (group == "fx" && Reg.Fxe.TryGetValue(key, out var fd))
            events.Add(new PlanEvent { T = 0.04, Type = key, Amp = fd.Amp * 1.2, Dur = fd.Dur / 24 });
        var cuts = new List<Cut>();
        if (group == "trans" && Reg.Trans.TryGetValue(key, out var td))
        {
            var transDur = J.Clamp(td.Dur ?? 0.35, 0.18, 0.7);
            var transP = td.Plan != null ? td.Plan!(rng, st) ?? new JObject() : new JObject();
            var tA = enUI ? "BEFORE" : "前のカット";
            var tB = enUI ? "AFTER" : "字面";
            JObject pA, pB;
            try
            {
                pA = Reg.Layouts["center"].Plan!(rng, new LayoutCtx { Text = tA, N = CpLen(tA), W = W, H = H, Dur = 1.2 }, st)
                     ?? new JObject();
            }
            catch
            {
                pA = new JObject();
            }
            try
            {
                pB = Reg.Layouts["center"].Plan!(rng, new LayoutCtx { Text = tB, N = CpLen(tB), W = W, H = H, Dur = 1.2 }, st)
                     ?? new JObject();
            }
            catch
            {
                pB = new JObject();
            }
            cuts.Add(MakeCut(
                text: tA, lineText: tA, line: 0, start: 0, end: 1.2,
                layout: "center", enter: "cut", exit: "cut", hold: "still",
                inDur: 0.12, outDur: 0, p: pA,
                decor: new List<DecorSpec>(), scheme: 0, seed: 1,
                words: ChunkText(tA).ToArray()));
            cuts.Add(MakeCut(
                text: tB, lineText: tB, line: 1, start: 1.2, end: 2.4,
                layout: "center", enter: "cut", exit: "cut", hold: "still",
                inDur: 0.12, outDur: 0, p: pB,
                decor: new List<DecorSpec>(), scheme: Math.Min(1, st.Schemes.Count - 1), seed: 2,
                words: ChunkText(tB).ToArray(),
                trans: key, transP: transP, transDur: transDur));
        }
        else
        {
            cuts.Add(MakeCut(
                text: text, lineText: text, line: 0, start: 0, end: dur,
                layout: layout, enter: enter, exit: exit, hold: hold, inDur: inDur, outDur: outDur,
                p: ps, decor: decor, scheme: 0, seed: J.H((int)J.Sid(key), 9),
                words: ChunkText(text).ToArray(),
                treat: treat, treatP: treatP, bg: bg, bgP: bgP, cam: cam, camP: camP, stagger: 0.04));
        }
        for (var i = 0; i < cuts.Count; i++) cuts[i].Index = i;
        return new Plan
        {
            Version = 1,
            Generator = "JIZURA-preview",
            Title = "",
            Artist = "",
            W = W,
            H = H,
            Fps = 24,
            Duration = cuts[^1].End,
            StyleKey = project.Style,
            Style = st,
            Fx = fx,
            Lines = new List<PlanLine>(),
            Cuts = cuts,
            Events = events,
            Beats = Array.Empty<double>(),
            Hud = false,
            KeyBg = null,
        };
    }

    // ---------------- 画幅（J.designSize / J.outputSize） ----------------

    public static (int W, int H) DesignSize(string aspect) => aspect switch
    {
        "9:16" => (1080, 1920),
        "1:1" => (1440, 1440),
        "4:5" => (1440, 1800),
        "21:9" => (2520, 1080),
        "4:3" => (1440, 1080),
        "3:4" => (1080, 1440),
        _ => (1920, 1080)
    };

    public static (int W, int H) OutputSize(Project project)
    {
        var (W, H) = DesignSize(project.Aspect);
        var k = (project.Res != 0 ? project.Res : 1080) / (double)Math.Min(W, H);
        return ((int)Math.Round(W * k / 2, MidpointRounding.AwayFromZero) * 2,
            (int)Math.Round(H * k / 2, MidpointRounding.AwayFromZero) * 2);
    }

    // ---------------- 当前切分（J.cutAt，09_render.js 开头） ----------------

    /// <summary>J.cutAt：t 所在的切分；不在任何切分内（间隙或首切分之前）返回 null。</summary>
    public static Cut? CutAt(Plan plan, double t)
    {
        var cs = plan.Cuts;
        int lo = 0, hi = cs.Count - 1, ans = -1;
        while (lo <= hi)
        {
            var m = (lo + hi) >> 1;
            if (cs[m].Start <= t)
            {
                ans = m;
                lo = m + 1;
            }
            else hi = m - 1;
        }
        if (ans < 0) return null;
        var c = cs[ans];
        return t < c.End ? c : null;
    }
}
