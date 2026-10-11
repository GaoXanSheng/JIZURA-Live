// 移植自 frontend/src/cloud/subtitle.ts + subtitle-draw.ts（git 历史 20c717e^，M7 已删 TS 参照）。
//
// 独立字幕渲染层，纯渲染模块：不依赖 Env/Plan/渲染器/宿主状态，输入只有
// KRC 时间轴（Krc.Parse 产物的 KrcLine）+ 每行注音（JIZURA LRC 的 "roma　trans" 串），
// Render(t) 直画到任意 SKCanvas——预览叠加（subtitle.ts 的 overlay 语义）与导出烤入
// （SubPayload/makeSubtitleOverlay 语义）以后都接这一个类（接线是下一个任务）。
//
// 与 TS 的有意差异（其余排版常数逐条对位 subtitle-draw.ts）：
//   1. TS 字幕层只画注音（主歌词由 JIZURA 引擎本体画）；C# 独立字幕通道要自带主行，
//      所以画「主行 + 注音行」整块，注音按 roma/trans 拆行（TS 是 '　' join 后一起 wrap）。
//      mainLine=false 可退回 TS 语义（只画注音行）——预览叠加与导出烤入用：主歌词由引擎画，
//      字幕层只叠翻译/音译，防止主行重影。
//   2. 行间空隙默认不画（TS lineAt 语义）；HoldLastLine=true 时保持上一句到下一句开始。
//   3. localStorage/BroadcastChannel/worker 载荷等宿主逻辑不移植——配置就是纯 POCO。
//
// 字幕整行单色：不做已唱/未唱逐字高亮（KRC 逐字时间轴只用来选行与取词文本，不参与配色）。

using System.Collections;
using System.Globalization;
using JizuraLive.App.Cloud;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using SkiaSharp;

namespace JizuraLive.Engine;

/// <summary>字幕内容模式：对位 SubConfig.content（Off = 整层不画）。</summary>
[JsonConverter(typeof(StringEnumConverter))] // 落盘（settings.json）字符串，可读可手改
public enum SubContent
{
    Off,
    Trans,
    Roma,
    Both
}

/// <summary>字幕位置：对位 SubConfig.pos。</summary>
[JsonConverter(typeof(StringEnumConverter))]
public enum SubPos
{
    Bottom,
    Top
}

/// <summary>
/// 字幕配置：字段与默认值逐条对位 subtitle.ts 的 SubConfig/SUB_DEFAULT
/// （Content 默认 Off 是 TS 原样——字幕面板初始就是关闭的）；字体默认黑体无衬线。
/// </summary>
public sealed class SubtitleConfig
{
    /// <summary>画什么注音：off / 只翻译 / 只音译 / 双行。</summary>
    public SubContent Content { get; set; } = SubContent.Off;

    /// <summary>底部 / 顶部。</summary>
    public SubPos Pos { get; set; } = SubPos.Bottom;

    /// <summary>字号（画面高的百分比；fontPx = max(8, H*size/100)）。</summary>
    public double Size { get; set; } = 4.5;

    /// <summary>描边宽（1080p 像素，按 H/1080 缩放；0 = 不描边）。</summary>
    public double Stroke { get; set; } = 4;

    /// <summary>填充色（#rgb/#rrggbb）。</summary>
    public string Color { get; set; } = "#ffffff";

    /// <summary>描边色。</summary>
    public string StrokeColor { get; set; } = "#000000";

    /// <summary>字体族：默认 'sans'（黑体无衬线）/ 'serif'（宋体）；旧设置里的 '' 也按无衬线解析（family 链对位 subFont()）。</summary>
    public string Font { get; set; } = "sans";

    /// <summary>半透明黑底板条（rgba(0,0,0,0.45)，对位 cfg.bar）。</summary>
    public bool Bar { get; set; }

    /// <summary>水平偏移（画面宽的百分比，±50 封顶）。</summary>
    public double OffX { get; set; }

    /// <summary>垂直偏移（画面高的百分比，±50 封顶）。</summary>
    public double OffY { get; set; }

    // ---- C# 扩展（TS 字幕层没有行间保持） ----

    /// <summary>行间空隙/间奏时保持上一句直到下一句开始；false = 空隙不画（TS 语义）。</summary>
    public bool HoldLastLine { get; set; }

    /// <summary>字段逐项拷贝（启动时从 settings.json 灌入、设置面板存盘取快照）——
    /// 不换实例：预览/导出/Spout 字幕通道共用同一份配置对象。</summary>
    public void CopyFrom(SubtitleConfig src)
    {
        Content = src.Content;
        Pos = src.Pos;
        Size = src.Size;
        Stroke = src.Stroke;
        Color = src.Color;
        StrokeColor = src.StrokeColor;
        Font = src.Font;
        Bar = src.Bar;
        OffX = src.OffX;
        OffY = src.OffY;
        HoldLastLine = src.HoldLastLine;
    }
}

/// <summary>
/// 字幕层：KRC 时间轴 + 配置 → 逐帧 SkiaSharp 直画。输入不可变、输出是输入的纯函数，可完全离线测试。
/// 渲染整体过一把静态锁：FontJobs 字体缓存与 SKFont 测量/绘制不能并发（Spout 字幕通道在泵线程、
/// 导出烤入在后台线程、预览叠加入口在预览泵线程，三方可同时调 Render）。
/// </summary>
public sealed class SubtitleLayer
{
    private readonly IReadOnlyList<KrcLine> _lines;
    private readonly List<int> _order; // 按 StartMs 升序的行索引（OrderBy 稳定，输入乱序也能二分）
    private readonly SubtitleConfig _cfg;
    private readonly IReadOnlyList<string>? _notes;
    private readonly bool _mainLine;

    // 渲染串行锁：FontJobs 静态缓存与 SKFont 测量/绘制不线程安全，而本类的调用方分布在三条线程上
    // （Spout 字幕通道的泵线程、导出烤入的后台线程、预览叠加的预览泵线程）
    private static readonly object Gate = new();

    /// <summary>
    /// lines：KRC 时间轴（Krc.Parse 的产物，时间单位毫秒）。
    /// notes：每行注音（JIZURA LRC 的 "roma　trans" 串，Krc.KrcToLrc 生成；缺省 = 无注音行）。
    /// cfg：缺省 = SUB_DEFAULT（Content=Off，什么都不画——要出画面得显式开）。
    /// mainLine：是否画主行（KRC 正文）。独立 Spout 字幕通道 = true（整块自带主行）；
    ///   预览叠加与导出烤入 = false（主歌词由引擎自己画，只叠注音/翻译行，防重影）。
    /// </summary>
    public SubtitleLayer(IReadOnlyList<KrcLine> lines, SubtitleConfig? cfg = null, IReadOnlyList<string>? notes = null,
        bool mainLine = true)
    {
        _lines = lines ?? throw new ArgumentNullException(nameof(lines));
        _cfg = cfg ?? new SubtitleConfig();
        _notes = notes;
        _mainLine = mainLine;
        _order = Enumerable.Range(0, _lines.Count).OrderBy(i => _lines[i].StartMs).ToList();
    }

    /// <summary>Render 的一帧统计（纯数值，测试与上层接线用；不关心可直接忽略返回值）。</summary>
    public sealed record FrameStats(
        int? LineIndex, // 当前行索引（无 → null；空隙/间奏下 HoldLastLine 给的是被保持的行）
        int Words, // 当前行 KrcWord 数
        int MainRows, // 主行折行数
        int SubRows, // 翻译/音译行数
        bool Drawn) // 本帧有笔画落盘
    {
        public static FrameStats None { get; } = new(null, 0, 0, 0, false);
    }

    /// <summary>
    /// 从 plan 的 cut 注音回填字幕层的注音行（KRC 行序）：工程 LRC 的 "|注音" 经 planner 挂到
    /// cut.LineText/Note，这里按整行文本对齐。KrcToLrc 写 LRC 时把 '/' 换成全角，匹配同样归一化。
    /// 对不上的行留空；全都没对上返回 null（退化为只画主行）。
    /// </summary>
    public static IReadOnlyList<string>? NotesFromPlan(IReadOnlyList<KrcLine> lines, Plan plan)
    {
        Dictionary<string, string>? map = null;
        foreach (var c in plan.Cuts)
        {
            if (string.IsNullOrEmpty(c.Note) || c.LineText.Length == 0) continue;
            map ??= new Dictionary<string, string>(StringComparer.Ordinal);
            map.TryAdd(c.LineText, c.Note!);
        }
        if (map == null) return null;

        var notes = new List<string>(lines.Count);
        var hit = false;
        foreach (var l in lines)
        {
            var note = map.TryGetValue(l.Text.Replace("/", "／"), out var v) ? v : "";
            if (note.Length > 0) hit = true;
            notes.Add(note);
        }
        return hit ? notes : null;
    }

    /// <summary>
    /// 没有 KRC 逐字数据时（网易云 LRC / 手动粘贴 / 重启恢复工程——KRC 不随工程落盘）从 plan 的行与
    /// 注音合成字幕行：非间奏行整行合成一个「词」（无逐字时间），这样 mainLine=true
    /// 的 Spout 字幕通道与纯字幕导出也能画出主行；注音取 PlanLine.Note。
    /// 全行都没有注音内容返回 null（调用方退化为不画字幕）。
    /// 预览叠加、导出烤入、Spout 字幕通道三条路共用这一份，避免各处行为漂移。
    /// </summary>
    public static (List<KrcLine> Lines, IReadOnlyList<string> Notes)? FromPlan(Plan? plan)
    {
        if (plan?.Lines is not { Count: > 0 }) return null;
        var lines = new List<KrcLine>(plan.Lines.Count);
        var notes = new List<string>(plan.Lines.Count);
        var hasAnyNote = false;
        foreach (var l in plan.Lines)
        {
            var startMs = (long)(l.Start * 1000);
            if (l.Interlude)
            {
                lines.Add(new KrcLine(startMs, (long)(Math.Max(0, l.End - l.Start) * 1000), "", Array.Empty<KrcWord>()));
                notes.Add("");
                continue;
            }

            var dur = Math.Max(0.1, (l.VisEnd > l.Start ? l.VisEnd : l.End) - l.Start);
            var durMs = (long)(dur * 1000);
            IReadOnlyList<KrcWord> words = l.Text.Length > 0
                ? new[] { new KrcWord(startMs, durMs, l.Text) }
                : Array.Empty<KrcWord>();
            lines.Add(new KrcLine(startMs, durMs, l.Text, words));

            var note = l.Note ?? "";
            if (note.Length > 0) hasAnyNote = true;
            notes.Add(note);
        }

        return hasAnyNote ? (lines, notes) : null;
    }

    /// <summary>
    /// 字幕层是否有可画内容：内容模式非 off，且「有 KRC 逐字 或 能从 plan 行合成出注音」。
    /// 导出面板的 subOn、MCP 导出的 hasSubs、导出引擎的取源判据统一走这里——否则重启恢复工程
    /// （KRC 不落盘）后会出现「通道/预览有字幕、导出面板却报没字幕」这类判据打架。
    /// </summary>
    public static bool HasContent(IReadOnlyList<KrcLine>? krc, Plan? plan, SubtitleConfig cfg)
        => cfg.Content != SubContent.Off && (krc is { Count: > 0 } || FromPlan(plan) != null);

    /// <summary>
    /// 画一帧字幕。t 是曲内秒（KRC 是毫秒，内部 ×1000）；w/h 是画布尺寸（设计像素，
    /// 与 TS 的 plan.W/H 同位——排版常数全部由 h/w 派生，见各注释）。
    /// 线程安全：整体过静态锁串行，任何线程可调。
    /// </summary>
    public FrameStats Render(SKCanvas ctx, double t, int w, int h)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        if (_cfg.Content == SubContent.Off || w <= 0 || h <= 0) return FrameStats.None;
        lock (Gate)
        {
            return RenderCore(ctx, t, w, h);
        }
    }

    private FrameStats RenderCore(SKCanvas ctx, double t, int w, int h)
    {
        var tMs = t * 1000.0;
        var at = LineAt(tMs);
        if (at == null) return FrameStats.None;
        var idx = at.Value.Idx;
        var line = _lines[idx];

        // 注音行：notes[idx] 按 roma/trans 分类后按 Content 模式取（TS fromNoteOf/subTextFor 同款）
        var (roma, trans) = NoteParts(idx);
        var subTexts = new List<string>();
        if (_cfg.Content == SubContent.Trans)
        {
            if (trans.Length > 0) subTexts.Add(trans);
        }
        else if (_cfg.Content == SubContent.Roma)
        {
            if (roma.Length > 0) subTexts.Add(roma);
        }
        else if (_cfg.Content == SubContent.Both)
        {
            if (roma.Length > 0) subTexts.Add(roma);
            if (trans.Length > 0) subTexts.Add(trans);
        }

        // 主行码点（主文本 = 词文本按序 concat，与 Krc.Parse 的 Text 同源）
        var cps = new List<string>(64);
        for (var wi = 0; wi < line.Words.Count; wi++)
            foreach (var cp in CodePoints(line.Words[wi].Text))
                cps.Add(cp);

        // ---------------- 排版（subtitle-draw.ts 的常数逐条对位） ----------------
        var fontPx = Math.Max(8, h * _cfg.Size / 100);
        var strokePx = h / 1080.0 * _cfg.Stroke;
        var maxW = w * 0.86;
        var lh = fontPx * 1.32;
        var font = SubFont(fontPx, _cfg.Font);

        var cpW = new double[cps.Count];
        if (_mainLine) // 只画注音的模式（预览叠加/导出烤入）不做主行测量与折行
            for (var i = 0; i < cps.Count; i++) cpW[i] = font.MeasureText(cps[i]);
        var mainRows = _mainLine ? Wrap(cps, cpW, maxW) : new List<(int Start, int End)>();

        var subRows = new List<(string Text, double Width)>();
        foreach (var st in subTexts)
        {
            var scps = CodePoints(st);
            if (scps.Count == 0) continue;
            var sw = new double[scps.Count];
            for (var i = 0; i < scps.Count; i++) sw[i] = font.MeasureText(scps[i]);
            foreach (var (s, e) in Wrap(scps, sw, maxW))
            {
                var width = 0.0;
                for (var i = s; i < e; i++) width += sw[i];
                subRows.Add((string.Concat(scps.Skip(s).Take(e - s)), width));
            }
        }

        var mainRowW = new double[mainRows.Count];
        for (var r = 0; r < mainRows.Count; r++)
        {
            var width = 0.0;
            for (var i = mainRows[r].Start; i < mainRows[r].End; i++) width += cpW[i];
            mainRowW[r] = width;
        }

        var stats = new FrameStats(idx, line.Words.Count,
            mainRows.Count, subRows.Count, mainRows.Count + subRows.Count > 0);
        if (!stats.Drawn) return stats;

        // offX/offY 是画面宽/高的百分比偏移（±50 封顶；TS parseFloat(..)||0 的 NaN 兜底 = 0）
        var centerX = w * (0.5 + ClampPct(_cfg.OffX) / 100);
        var blockH = (mainRows.Count + subRows.Count) * lh;
        var baseY = (_cfg.Pos == SubPos.Top ? h * 0.055 + fontPx : h - h * 0.055 - blockH + fontPx)
                    + h * ClampPct(_cfg.OffY) / 100;

        ctx.Save();
        var fillCol = SubColor(_cfg.Color);
        using var fillP = new SKPaint { IsAntialias = true, Color = fillCol };
        using var strokeP = new SKPaint
        {
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = (float)strokePx,
            StrokeJoin = SKStrokeJoin.Round, // TS lineJoin='round'
            Color = SubColor(_cfg.StrokeColor)
        };

        // 底板条：先于文字画（TS 同序），宽度取最宽行
        if (_cfg.Bar)
        {
            var wmax = mainRowW.DefaultIfEmpty(0).Max();
            foreach (var sr in subRows) wmax = Math.Max(wmax, sr.Width);
            if (wmax > 0)
            {
                var padX = fontPx * 0.5;
                var padY = fontPx * 0.3;
                using var bp = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, (byte)Math.Round(0.45 * 255)) };
                ctx.DrawRect(new SKRect(
                    (float)(centerX - wmax / 2 - padX),
                    (float)(baseY - fontPx * 0.82 - padY),
                    (float)(centerX + wmax / 2 + padX),
                    (float)(baseY - fontPx * 0.82 - padY + blockH + padY * 2)), bp);
            }
        }

        // 主行：整行单色（先描边一遍再填色；基线 = baseY + r*lh，TS textAlign=center 的
        // 整行描边 → 这里逐码点描边，同色不透明下视觉等价）
        var y = baseY;
        for (var r = 0; r < mainRows.Count; r++)
        {
            var (s, e) = mainRows[r];
            var x = centerX - mainRowW[r] / 2;
            if (strokePx > 0)
            {
                var cx = x;
                for (var i = s; i < e; i++)
                {
                    ctx.DrawText(cps[i], (float)cx, (float)y, SKTextAlign.Left, GlyphFont(font, cps[i], fontPx, _cfg.Font), strokeP);
                    cx += cpW[i];
                }
            }

            var fx = x;
            for (var i = s; i < e; i++)
            {
                ctx.DrawText(cps[i], (float)fx, (float)y, SKTextAlign.Left, GlyphFont(font, cps[i], fontPx, _cfg.Font), fillP);
                fx += cpW[i];
            }

            y += lh;
        }

        // 注音行：单色整行（描边 + 填充，居中，TS strokeText/fillText 同序）；
        // 逐字取脸（主脸缺字时换脸），起点按主脸量出的行宽居中、推进也用主脸宽，与 Wrap 结果对齐
        foreach (var (text, width) in subRows)
        {
            var x0 = centerX - width / 2;
            foreach (var cp in CodePoints(text))
            {
                var gf = GlyphFont(font, cp, fontPx, _cfg.Font);
                if (strokePx > 0) ctx.DrawText(cp, (float)x0, (float)y, SKTextAlign.Left, gf, strokeP);
                ctx.DrawText(cp, (float)x0, (float)y, SKTextAlign.Left, gf, fillP);
                x0 += font.MeasureText(cp);
            }

            y += lh;
        }

        ctx.Restore();
        return stats;
    }

    // ---------------- 当前行查找（二分） ----------------

    /// <summary>
    /// 最后一个 StartMs &lt;= tMs 的行；活动 = t 在 [start, start+dur)。
    /// 空隙/间奏：默认不画（TS lineAt 的 ok 判据）；HoldLastLine 时保持最近一句非空行。
    /// </summary>
    private (int Idx, bool Active)? LineAt(double tMs)
    {
        int lo = 0, hi = _order.Count - 1, k = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_lines[_order[mid]].StartMs <= tMs)
            {
                k = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (k < 0) return null; // 首行之前
        var idx = _order[k];
        var active = tMs < _lines[idx].StartMs + Math.Max(0, _lines[idx].DurMs);
        if (!_cfg.HoldLastLine)
            return active ? (idx, true) : ((int, bool)?)null;
        if (active && _lines[idx].Text.Length > 0) return (idx, true);
        // 间奏空行不吃上一句：往回找最近一句非空行（被保持的上一句照常整行画）
        while (k >= 0 && _lines[_order[k]].Text.Length == 0) k--;
        return k >= 0 ? (Idx: _order[k], Active: false) : ((int, bool)?)null;
    }

    // ---------------- 注音解析（TS fromNoteOf 对位） ----------------

    /// <summary>notes[idx]（JIZURA LRC 注音串 "roma　trans"）→ (roma, trans)。
    /// 按 '　' 拆段逐段用 Krc.ClassifyLangRows 按脚本分类（TS 同款）；段内空格 join 保留。</summary>
    private (string Roma, string Trans) NoteParts(int idx)
    {
        var note = _notes != null && idx < _notes.Count ? _notes[idx] : "";
        if (string.IsNullOrWhiteSpace(note)) return ("", "");
        var roma = new List<string>();
        var trans = new List<string>();
        foreach (var p in note.Split('　').Select(s => s.Trim()).Where(s => s.Length > 0))
            (Krc.ClassifyLangRows(new[] { p }) == "roma" ? roma : trans).Add(p);
        return (string.Join(" ", roma), string.Join(" ", trans));
    }

    // ---------------- 折行（TS wrapSubtitle 对位） ----------------

    /// <summary>按全角空格分段，整段不超宽整段成行，超宽段再逐码点贪心折行。
    /// 返回 [Start,End) 码点区间（全角空格分隔符不入行）。</summary>
    private static List<(int Start, int End)> Wrap(IReadOnlyList<string> cps, double[] w, double maxW)
    {
        var rows = new List<(int, int)>();
        var i = 0;
        while (i < cps.Count)
        {
            if (cps[i] == "　")
            {
                i++;
                continue;
            }

            var j = i;
            while (j < cps.Count && cps[j] != "　") j++;
            if (maxW <= 0)
            {
                rows.Add((i, j)); // 没有可用宽度：一段一行兜底
                i = j;
                continue;
            }

            double segW = 0;
            for (var k = i; k < j; k++) segW += w[k];
            if (segW <= maxW)
            {
                rows.Add((i, j));
            }
            else
            {
                var s = i;
                double cur = 0;
                for (var k = i; k < j; k++)
                {
                    if (cur > 0 && cur + w[k] > maxW)
                    {
                        rows.Add((s, k));
                        s = k;
                        cur = w[k];
                    }
                    else
                    {
                        cur += w[k];
                    }
                }

                if (cur > 0) rows.Add((s, j));
            }

            i = j;
        }

        return rows;
    }

    // ---------------- 字体（TS subFont 对位） ----------------

    // (kind,px) → SKFont 缓存；typeface 解析结果随之复用。Hashtable 读多写少，与 JText.FontJobs 同款。
    private static readonly Hashtable FontJobs = new();

    /// <summary>subFont(px, kind)：700 字重、三条 family 链逐字对位；本机缺 family 时沿链下探到
    /// 语言×气质的系统字体栈（JFonts 统一解析，含逐字兜底），不再落 Skia 默认体。</summary>
    private static SKFont SubFont(double px, string kind)
    {
        var key = string.Create(CultureInfo.InvariantCulture,
            $"{kind}\u0001{px.ToString("R", CultureInfo.InvariantCulture)}");
        if (FontJobs[key] is SKFont hit) return hit;
        var families = kind == "serif"
            ? new[] { "Noto Serif JP", "SimSun" }
            : kind == "sans"
                ? new[] { "Microsoft YaHei", "Noto Sans JP" }
                : new[] { "Noto Sans JP", "Microsoft YaHei" };
        var tf = JFonts.ResolveChain(families.Concat(JFonts.StackFor(kind == "serif" ? "mincho" : "gothic")), 700);

        var f = new SKFont(tf, (float)px)
        {
            Edging = SKFontEdging.Antialias,
            Subpixel = true
        };
        if (FontJobs.Count > 256) ClearFontJobs(); // 容量护栏（同 JText.FaceFont 模式）
        FontJobs[key] = f;
        return f;
    }

    /// <summary>逐字取脸：字幕主脸缺这个字时（如韩文主脸渲染简体汉字）问 JFonts 要一张能画的
    /// （气质栈/系统逐字匹配）；kind 映射与 SubFont 一致。缓存复用同一张 FontJobs 表（\u0002 前缀区分）。</summary>
    private static SKFont GlyphFont(SKFont baseFont, string ch, double px, string kind)
    {
        if (ch.Length == 0) return baseFont;
        var cp = char.ConvertToUtf32(ch, 0);
        if (baseFont.Typeface.ContainsGlyph(cp)) return baseFont;
        var tf = JFonts.CoverFace(baseFont.Typeface, cp, 700, kind == "serif" ? "mincho" : "gothic");
        if (ReferenceEquals(tf, baseFont.Typeface)) return baseFont;
        var key = string.Create(CultureInfo.InvariantCulture,
            $"\u0002{tf.FamilyName}\u0001{(int)tf.FontStyle.Weight}\u0001{px.ToString("R", CultureInfo.InvariantCulture)}");
        if (FontJobs[key] is SKFont hit) return hit;
        var nf = new SKFont(tf, (float)px)
        {
            Edging = SKFontEdging.Antialias,
            Subpixel = true
        };
        if (FontJobs.Count > 256) ClearFontJobs();
        FontJobs[key] = nf;
        return nf;
    }

    /// <summary>清空并释放字体缓存：SKFont 是原生对象，光 Clear 会让句柄攒到 GC 终结器才还
    ///（JText.ClearFontJobs 同款）。SubFont 每次 Render 只调一次、且清空发生在取新字之前，
    /// 不会释放本帧正在用的那张。</summary>
    private static void ClearFontJobs()
    {
        foreach (DictionaryEntry kv in FontJobs)
            if (kv.Value is IDisposable d) d.Dispose();
        FontJobs.Clear();
    }

    // ---------------- 小助手 ----------------

    private static double ClampPct(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) v = 0; // TS parseFloat(..)||0 的 NaN 兜底
        return Math.Max(-50, Math.Min(50, v));
    }

    private static SKColor SubColor(string hex)
    {
        var (r, g, b) = J.Hex(hex);
        return new SKColor((byte)r, (byte)g, (byte)b);
    }

    /// <summary>[...String(s)]：按码点切（含增补平面）。</summary>
    private static List<string> CodePoints(string s)
    {
        var l = new List<string>(s.Length);
        var i = 0;
        while (i < s.Length)
        {
            if (i + 1 < s.Length && char.IsHighSurrogate(s[i]) && char.IsLowSurrogate(s[i + 1]))
            {
                l.Add(s.Substring(i, 2));
                i += 2;
            }
            else
            {
                l.Add(s[i].ToString());
                i++;
            }
        }

        return l;
    }
}
