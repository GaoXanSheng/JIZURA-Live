using System.Numerics;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JizuraLive.Render.Text;

/// <summary>
/// 纯 Direct2D / DirectWrite 打造的现代实时 PV 歌词排版层。
/// 支持毫秒级时间轴匹配、字级动态入场与高级感阴影发光。
/// </summary>
public sealed class TypographyLayerProvider : IPvLayerProvider, IDisposable
{
    private readonly D3DDevice _gpu;
    private readonly D2DHost _host;
    private OffscreenTarget? _target;
    private IReadOnlyList<LyricClip> _clips = Array.Empty<LyricClip>();

    /// <summary>同刻合并后的多行文本按行拆开画：DwFont 的文本格式是单行不换行的，'\n' 得自己分层。</summary>
    private static readonly string[] LineSeparators = ["\r\n", "\r", "\n"];

    /// <summary>描边方向数：与 SubtitleLayout 同量级，16 向在数 px 描边下看不出缺口。</summary>
    private const int StrokeDirections = 16;

    /// <summary>pop 的入场总时长与 typewriter 的淡入时长（毫秒）。</summary>
    private const float PopMs = 180f;
    private const float TypewriterMs = 80f;

    private LyricStyle? _style;

    /// <summary>逐字位置缓存（单槽）：一次只渲染一句，命中句不变时每帧零分配。</summary>
    private SentenceLayout? _layout;
    private string? _layoutClipId;
    private float _layoutFontSize = -1f;

    /// <summary>逐字时间与整句文本对不上的句 Id：这些句回落整句静态绘制。</summary>
    private readonly HashSet<string> _wordsMismatch = new();

    /// <summary>已经报过「逐字时间对不上」的句 Id（SetClips 会被反复调用，日志只报一次）。</summary>
    private readonly HashSet<string> _mismatchWarned = new();

    private int _w, _h;
    private double _scale = 1.0;
    private long _version = 1;

    public string Id { get; }
    public long Version => _version;
    public int Width => _w;
    public int Height => _h;
    public int DesignWidth { get; }
    public int DesignHeight { get; }

    public TypographyLayerProvider(string id, D3DDevice gpu, D2DHost host,
        int designWidth = 1920, int designHeight = 1080)
    {
        Id = id;
        _gpu = gpu;
        _host = host;
        DesignWidth = designWidth;
        DesignHeight = designHeight;
    }

    /// <summary>
    /// 旧入口（PvPipeline.SetLyrics 转发 KrcLine）：转成 Clip 后走同一条查找路径，
    /// 不再让动效歌词自己算一套「当前句」。
    /// </summary>
    public void SetLyrics(IReadOnlyList<KrcLine>? lines) =>
        SetClips(lines is null ? Array.Empty<LyricClip>() : LrcParser.ToClips(lines));

    /// <summary>直接接收已冻结的 Clip 序列（歌词轨编辑后的产物）。要求按 StartMs 升序。</summary>
    public void SetClips(IReadOnlyList<LyricClip>? clips)
    {
        // 没有逐字时间的句在这里按字合成一次，渲染路径直接用（每帧不再做这件事）
        _clips = clips is null ? Array.Empty<LyricClip>() : LyricTiming.EnsureWords(clips);

        _wordsMismatch.Clear();
        foreach (var clip in _clips)
        {
            // 没有词表（空文本 / 零时长句）本来就整句绘制，谈不上「不一致」
            if (clip.Words is not { Count: > 0 } || WordsMatchText(clip)) continue;
            _wordsMismatch.Add(clip.Id);
            if (_mismatchWarned.Add(clip.Id))
                Log.Warn($"[Typography] 歌词 {clip.Id} 的逐字时间与整句文本不一致，该句按整句绘制");
        }

        _layout = null;
        _layoutClipId = null;
        _version++;
    }

    public void SetScale(double scale)
    {
        scale = Math.Clamp(scale, 0.05, 1.0);
        if (Math.Abs(scale - _scale) < 1e-4) return;
        _scale = scale;
        _version++;
    }

    /// <summary>设定排版样式；<c>null</c> = 回到 <see cref="LyricStyle.Classic"/> 预设。</summary>
    public void SetStyle(LyricStyle? style)
    {
        _style = style;
        _version++;
    }

    /// <summary>本层实际生效的样式（没设过就用 Classic）。</summary>
    public LyricStyle EffectiveStyle => _style ?? LyricStyle.Classic;

    public void Render(double time)
    {
        var w = Math.Max(16, (int)Math.Round(DesignWidth * _scale));
        var h = Math.Max(16, (int)Math.Round(DesignHeight * _scale));

        if (_target is null || w != _w || h != _h)
        {
            _target?.Dispose();
            _target = OffscreenTarget.Create(_gpu, _host.Ctx, w, h, sampleable: true);
            _w = w;
            _h = h;
            _version++;
        }

        var d = _host.Draw;
        d.SetTarget(_target.Bitmap, w, h);
        d.Begin();
        d.Clear(new Color4(0f, 0f, 0f, 0f));

        if (_clips.Count > 0)
        {
            // 唯一真源：命中不了样本（空档 / t == EndMs / 越界）就什么都不画，等清屏
            var sample = LrcParser.FindSample(_clips, time);
            if (sample is { } cur)
            {
                var progress = (float)cur.ProgressAt(time);

                // 进出场曲线计算
                float alpha = 1f;
                float offsetY = 0f;
                if (progress < 0.15f)
                {
                    // 进场前 15%：上浮 + 渐现
                    var inP = progress / 0.15f;
                    alpha = inP;
                    offsetY = (1f - inP) * 16f * (float)_scale;
                }
                else if (progress > 0.85f)
                {
                    // 退场后 15%：微沉 + 渐隐
                    var outP = (progress - 0.85f) / 0.15f;
                    alpha = 1f - outP;
                    offsetY = -outP * 10f * (float)_scale;
                }

                var clip = FindClip(cur.ClipId);
                var style = ResolveStyle(clip);
                var font = d.Fonts.Get("Microsoft YaHei UI", style.FontSizePx(_scale), weight: Vortice.DirectWrite.FontWeight.Bold);
                var cx = w / 2f;
                var cy = h * 0.75f + offsetY;

                var glow = style.GlowColorValue;
                var fill = style.ColorValue;
                var stroke = style.StrokeColorValue;
                var strokeOffsets = style.StrokeWidth > 0f ? BuildStrokeOffsets(style.StrokeWidth) : null;

                var anim = NormalizeAnim(style.Anim);
                var layout = clip is null ? null : GetLayout(clip, font);

                if (anim is not null && layout is not null)
                {
                    DrawAnimated(d, layout, anim, time, cx, cy, alpha, font, fill, glow, style.GlowSize,
                        stroke, strokeOffsets, style.HighlightColorValue);
                }
                else
                {
                    // 整句静态路径（Anim=none / 逐字时间对不上）：同刻合并的多行各自排版，整块以 cy 为中心
                    // （单行时与旧路径完全一致，多行不会被裁掉半截）
                    var blockLines = cur.Text.Split(LineSeparators, StringSplitOptions.None);
                    var lineHeight = font.LineHeight;
                    var firstBaseline = cy - (blockLines.Length - 1) * lineHeight / 2f;
                    var scratch = new Paint2D();

                    for (var i = 0; i < blockLines.Length; i++)
                    {
                        var line = blockLines[i];
                        if (line.Length == 0) continue; // 合并文本里的空行只占排版高度，没有字形

                        DrawRun(d, line, cx, firstBaseline + i * lineHeight, TextAlign2D.Center, font,
                            fill, glow, style.GlowSize, stroke, strokeOffsets, alpha, scratch);
                    }
                }
            }
        }

        d.End();
    }

    public PvLayerImage Pump(in PvFrameContext ctx) =>
        _target is null ? PvLayerImage.Empty : new PvLayerImage(_target.Bitmap, null, _w, _h);

    /// <summary>
    /// 回读本层离屏面的像素（**仅测试/诊断用**：会阻塞到 GPU 完成，逐帧调用会拖垮帧率）。
    /// 空档清屏这类断言必须看像素——只查 HasContent 只说明「有目标面」，是假绿。
    /// </summary>
    public ReadbackImage? ReadbackTarget() => _target?.Readback();

    // ---------------- 逐字动画 ----------------

    /// <summary>命中句的样式：句上带样式名就用它（导演按段落设的名字），否则用本层全局样式。</summary>
    private LyricStyle ResolveStyle(LyricClip? clip) =>
        string.IsNullOrWhiteSpace(clip?.Style) ? EffectiveStyle : LyricStyle.FromName(clip!.Style);

    /// <summary>按 Id 找命中句（要拿它的 Words/Style；样本本身只带文本与区间）。</summary>
    private LyricClip? FindClip(string id)
    {
        for (var i = 0; i < _clips.Count; i++)
            if (string.Equals(_clips[i].Id, id, StringComparison.Ordinal)) return _clips[i];

        return null;
    }

    /// <summary>一帧只渲染一句，所以逐字位置缓存一格就够；键是 (句 Id, 字号)，变了才重建。</summary>
    private SentenceLayout? GetLayout(LyricClip clip, DwFont font)
    {
        if (clip.Words is not { Count: > 0 } || _wordsMismatch.Contains(clip.Id)) return null;

        if (_layout is not null && _layoutClipId == clip.Id && Math.Abs(_layoutFontSize - font.Size) < 1e-3f)
            return _layout;

        _layout = BuildLayout(clip, font);
        _layoutClipId = clip.Id;
        _layoutFontSize = font.Size;
        return _layout;
    }

    /// <summary>样式里的动画名归一化；none/未知 → null（走整句静态路径）。</summary>
    private static string? NormalizeAnim(string? anim) => anim?.Trim().ToLowerInvariant() switch
    {
        "karaoke" => "karaoke",
        "pop" => "pop",
        "typewriter" => "typewriter",
        _ => null,
    };

    /// <summary>
    /// 逐字动画：按缓存的逐字位置排版，每个词按自己的起点决定可见性与高亮进度。
    /// 整句的进出场淡入淡出（<paramref name="alpha"/>）对所有路径一律照乘。
    /// </summary>
    private void DrawAnimated(Immediate2D d, SentenceLayout layout, string anim, double time,
        float cx, float cy, float alpha, DwFont font, in Color4 fill, in Color4 glow, float glowDy,
        in Color4 stroke, Vector2[]? strokeOffsets, in Color4 highlight)
    {
        var tMs = (long)(time * 1000.0);
        var lineHeight = layout.LineHeight;
        var firstBaseline = cy - (layout.Lines.Length - 1) * lineHeight / 2f;
        var pad = MathF.Max(glowDy, strokeOffsets is null ? 0f : strokeOffsets[0].Length()) + 4f;
        var scratch = new Paint2D();

        if (anim == "karaoke")
        {
            // 1) 整句先用正文字色铺底（发光/描边照旧）
            for (var i = 0; i < layout.Lines.Length; i++)
            {
                var line = layout.Lines[i];
                if (line.Text.Length == 0) continue;
                DrawRun(d, line.Text, cx, firstBaseline + i * lineHeight, TextAlign2D.Center, font,
                    fill, glow, glowDy, stroke, strokeOffsets, alpha, scratch);
            }

            // 2) 已唱到的词用高亮色重画；正在唱的那个词按进度横向擦除
            for (var i = 0; i < layout.Lines.Length; i++)
            {
                var line = layout.Lines[i];
                var baseline = firstBaseline + i * lineHeight;

                for (var s = 0; s < line.Spans.Length; s++)
                {
                    var span = line.Spans[s];
                    var wordStart = layout.WordStartMs[span.Word];
                    if (tMs < wordStart) continue;

                    var dur = layout.WordDurMs[span.Word];
                    var x0 = cx + line.X[span.First];
                    var x1 = cx + line.X[span.Last] + line.W[span.Last];
                    var left = tMs >= wordStart + dur || dur <= 0
                        ? x1
                        : x0 + (x1 - x0) * ((tMs - wordStart) / (float)dur);

                    d.Save();
                    if (left < x1) d.ClipRect(SweepBand(x0, left, baseline, font, pad));
                    scratch.Brush = new SolidBrush2D(WithAlpha(highlight, alpha));
                    for (var t = span.First; t <= span.Last; t++)
                        d.DrawText(line.Tokens[t], cx + line.X[t], baseline, TextAlign2D.Left, font, scratch);
                    d.Restore();
                }
            }

            return;
        }

        for (var i = 0; i < layout.Lines.Length; i++)
        {
            var line = layout.Lines[i];
            var baseline = firstBaseline + i * lineHeight;

            for (var s = 0; s < line.Spans.Length; s++)
            {
                var span = line.Spans[s];
                var wordStart = layout.WordStartMs[span.Word];
                if (tMs < wordStart) continue; // 词开始前不可见

                var e = Math.Min(1f, (tMs - wordStart) / (anim == "pop" ? PopMs : TypewriterMs));
                var a = alpha * e;
                if (a <= 0.002f) continue;

                if (anim == "pop")
                {
                    // 缓出回弹：0.4 → 1.15（前 60%）→ 1.0，同时上移 10px 归位
                    var scale = e < 0.6f
                        ? 0.4f + 0.75f * (e / 0.6f)
                        : 1.15f - 0.15f * EaseOut((e - 0.6f) / 0.4f);
                    var yc = baseline + (1f - e) * 10f * (float)_scale;
                    var mcx = cx + (line.X[span.First] + line.X[span.Last] + line.W[span.Last]) / 2f;

                    // 直接给矩阵：字词按自己的中心缩放（用 Translate/Scale 拼要靠合成顺序，
                    // 那种写法在本层的语义得靠猜，这里把不动点写死）
                    d.Save();
                    d.SetMatrix(new Matrix3x2(scale, 0f, 0f, scale, mcx * (1f - scale), yc * (1f - scale)));
                    DrawTokens(d, line, span, cx, yc, font, fill, glow, glowDy, stroke, strokeOffsets, a, scratch);
                    d.Restore();
                }
                else
                {
                    DrawTokens(d, line, span, cx, baseline, font, fill, glow, glowDy, stroke, strokeOffsets, a, scratch);
                }
            }
        }
    }

    private static void DrawTokens(Immediate2D d, LineLayout line, WordSpan span, float cx, float baseline,
        DwFont font, in Color4 fill, in Color4 glow, float glowDy, in Color4 stroke, Vector2[]? strokeOffsets,
        float alpha, Paint2D scratch)
    {
        for (var t = span.First; t <= span.Last; t++)
            DrawRun(d, line.Tokens[t], cx + line.X[t], baseline, TextAlign2D.Left, font,
                fill, glow, glowDy, stroke, strokeOffsets, alpha, scratch);
    }

    /// <summary>
    /// 一段文本的完整笔法：发光底影 → 描边 → 正文（与整句路径同一套笔顺与偏移）。
    /// 三笔共用 <paramref name="scratch"/>：画刷按颜色缓存，反复改色不会重建 D2D 资源。
    /// </summary>
    private static void DrawRun(Immediate2D d, string text, float x, float baseline, TextAlign2D align, DwFont font,
        in Color4 fill, in Color4 glow, float glowDy, in Color4 stroke, Vector2[]? strokeOffsets, float alpha,
        Paint2D scratch)
    {
        if (text.Length == 0 || alpha <= 0.002f) return;

        if (glowDy != 0f && glow.A > 0f)
        {
            scratch.Brush = new SolidBrush2D(new Color4(glow.R, glow.G, glow.B, glow.A * alpha));
            d.DrawText(text, x, baseline + glowDy, align, font, scratch);
        }

        // D2D 的 DrawText 没有描边笔，只能沿圆周偏移重画模拟
        if (strokeOffsets is not null && stroke.A > 0f)
        {
            scratch.Brush = new SolidBrush2D(new Color4(stroke.R, stroke.G, stroke.B, stroke.A * alpha));
            foreach (var offset in strokeOffsets)
                d.DrawText(text, x + offset.X, baseline + offset.Y, align, font, scratch);
        }

        scratch.Brush = new SolidBrush2D(new Color4(fill.R, fill.G, fill.B, fill.A * alpha));
        d.DrawText(text, x, baseline, align, font, scratch);
    }

    /// <summary>擦除用竖向带宽：盖住基线上下一个行高，外加描边/发光的外扩余量。</summary>
    private static Rect SweepBand(float x0, float x1, float baseline, DwFont font, float pad) =>
        new(x0, baseline - font.Ascent - pad, MathF.Max(x1 - x0, 0.01f), font.LineHeight + 2f * pad);

    private static Color4 WithAlpha(in Color4 c, float alpha) => new(c.R, c.G, c.B, c.A * alpha);

    /// <summary>缓出 1-(1-p)²：回弹第二段收在 1.0 上。</summary>
    private static float EaseOut(float p) => 1f - (1f - p) * (1f - p);

    /// <summary>整句文本折成逐词序列对应的线性文本（词拼接对齐的就是它）。</summary>
    private static string FlatText(string text) =>
        text.IndexOf('\r') < 0 ? text.Replace("\n", "") : text.Replace("\r", "").Replace("\n", "");

    /// <summary>逐字时间的拼接是否就等于整句文本（契约保证；对不上说明数据脏，该句退回整句绘制）。</summary>
    private static bool WordsMatchText(LyricClip clip)
    {
        if (clip.Words is not { Count: > 0 } words) return false;

        var flat = FlatText(clip.Text);
        var g = 0;
        foreach (var word in words)
        {
            if (word.Text.Length == 0) continue; // 空词不占字形，跳过而不是整句判脏
            if (g + word.Text.Length > flat.Length) return false;
            if (string.CompareOrdinal(word.Text, 0, flat, g, word.Text.Length) != 0) return false;
            g += word.Text.Length;
        }

        return g == flat.Length;
    }

    /// <summary>
    /// 逐字位置只算一次并存下来（每帧零分配：字串、宽度、词区间全在这里定好）。
    /// 横向按 DwFont.MeasureWidth 逐字累加、整行以中心对齐；词首映射到线性文本的下标，
    /// 因此跨行的词会拆成两段（各自带同一个词下标）。
    /// </summary>
    private static SentenceLayout BuildLayout(LyricClip clip, DwFont font)
    {
        var words = clip.Words ?? Array.Empty<KrcWord>();
        var wordStartMs = new long[words.Count];
        var wordDurMs = new long[words.Count];
        var wordOffset = new int[words.Count];
        var offset = 0;
        for (var i = 0; i < words.Count; i++)
        {
            wordStartMs[i] = words[i].StartMs;
            wordDurMs[i] = words[i].DurMs;
            wordOffset[i] = offset;
            offset += words[i].Text.Length;
        }

        var source = clip.Text.Split(LineSeparators, StringSplitOptions.None);
        var lines = new LineLayout[source.Length];
        var w = 0;
        var g = 0;

        for (var li = 0; li < source.Length; li++)
        {
            var text = source[li];
            var tokens = new List<string>(text.Length);
            var xs = new List<float>(text.Length);
            var ws = new List<float>(text.Length);
            var spans = new List<WordSpan>();
            var x = 0f;

            for (var c = 0; c < text.Length;)
            {
                // 代理对当一个字：DwFont 的码点测量对孤立代理是坏输入
                var len = char.IsHighSurrogate(text[c]) && c + 1 < text.Length && char.IsLowSurrogate(text[c + 1])
                    ? 2 : 1;
                var token = text.Substring(c, len);

                while (w < words.Count && g >= wordOffset[w] + words[w].Text.Length) w++;
                if (w < words.Count && g >= wordOffset[w])
                {
                    if (spans.Count > 0 && spans[^1].Word == w) spans[^1] = spans[^1] with { Last = tokens.Count };
                    else spans.Add(new WordSpan(w, tokens.Count, tokens.Count));
                }

                tokens.Add(token);
                xs.Add(x);
                ws.Add(font.MeasureWidth(token));
                x += ws[^1];
                g += len;
                c += len;
            }

            // 逐字路径用 cx + X[t] 左对齐画字，所以 X 必须是「相对行中心的左缘偏移」：
            // 上面从 0 起累加得到的是左缘到行首的距离，整行统一左移行宽/2 才绕 cx 居中。
            var halfLine = x / 2f;
            for (var k = 0; k < xs.Count; k++) xs[k] -= halfLine;

            lines[li] = new LineLayout
            {
                Text = text,
                Tokens = [.. tokens],
                X = [.. xs],
                W = [.. ws],
                Spans = [.. spans],
            };
        }

        return new SentenceLayout
        {
            Lines = lines,
            WordStartMs = wordStartMs,
            WordDurMs = wordDurMs,
            LineHeight = font.LineHeight,
        };
    }

    /// <summary>一句歌词的逐字排版（按句 Id + 字号缓存）：每行的逐字位置 + 词的绝对起点/时长。</summary>
    private sealed class SentenceLayout
    {
        public LineLayout[] Lines = [];
        public long[] WordStartMs = [];
        public long[] WordDurMs = [];
        public float LineHeight;
    }

    /// <summary>一行的逐字布局：<see cref="X"/> 是相对行中心的左缘偏移，<see cref="W"/> 是该字宽度。</summary>
    private sealed class LineLayout
    {
        public string Text = "";
        public string[] Tokens = [];
        public float[] X = [];
        public float[] W = [];
        public WordSpan[] Spans = [];
    }

    /// <summary>词在本行占的字区间 [First, Last]（跨行的词会有两段）。</summary>
    private readonly record struct WordSpan(int Word, int First, int Last);

    /// <summary>描边方向：半径 StrokeWidth/2 的圆周偏移（不含 (0,0)，中心那笔由填充覆盖）。</summary>
    private static Vector2[] BuildStrokeOffsets(float strokeWidth)
    {
        var radius = strokeWidth / 2f;
        var offsets = new Vector2[StrokeDirections];
        for (var i = 0; i < StrokeDirections; i++)
        {
            var angle = i * 2f * MathF.PI / StrokeDirections;
            offsets[i] = new Vector2(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius);
        }

        return offsets;
    }

    public void Dispose()
    {
        _target?.Dispose();
    }
}
