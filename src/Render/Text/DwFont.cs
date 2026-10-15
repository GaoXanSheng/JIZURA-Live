using JizuraLive.Logging;
using Vortice.DirectWrite;

namespace JizuraLive.Render.Text;

/// <summary>
/// 一个 (字族, 字号) 的文本格式：DirectWrite 的 <see cref="IDWriteTextFormat"/> 三种水平对齐各一份，
/// 外加字形度量（ascent / 行高）与**带缓存的逐字宽度测量**。
///
/// 为什么宽度要缓存：旧引擎是逐字排版 + 逐字绘制，每字都要量一次宽；DirectWrite 量宽要建
/// <c>IDWriteTextLayout</c>，每字新建一次会把排版成本抬一个量级。这里按码点缓存宽度，
/// 命中后是纯字典查找。
///
/// 缺字回退交给 DirectWrite 自己做（文本布局阶段逐字回退到系统字体），与旧引擎
/// <c>JFonts.FaceFontFor</c> 的「缺字换脸」语义一致。
/// </summary>
public sealed class DwFont : IDisposable
{
    private readonly IDWriteFactory _dw;
    private readonly Dictionary<int, float> _widthCache = new(256);
    private float _ascent = float.NaN;
    private float _lineHeight = float.NaN;

    public string Family { get; }
    public float Size { get; }

    internal IDWriteTextFormat Leading { get; }
    internal IDWriteTextFormat Centered { get; }
    internal IDWriteTextFormat Trailing { get; }

    internal DwFont(IDWriteFactory dw, string family, float size,
        FontWeight weight, FontStyle style, FontStretch stretch)
    {
        _dw = dw;
        Family = family;
        Size = size;

        Leading = dw.CreateTextFormat(family, null!, weight, style, stretch, size, "zh-Hans");
        Centered = dw.CreateTextFormat(family, null!, weight, style, stretch, size, "zh-Hans");
        Trailing = dw.CreateTextFormat(family, null!, weight, style, stretch, size, "zh-Hans");

        Leading.TextAlignment = TextAlignment.Leading;
        Centered.TextAlignment = TextAlignment.Center;
        Trailing.TextAlignment = TextAlignment.Trailing;

        // 单行、不换行、不裁剪：旧引擎的排版由自己算，DirectWrite 只负责出字形
        foreach (var f in new[] { Leading, Centered, Trailing })
        {
            f.WordWrapping = WordWrapping.NoWrap;
            f.ParagraphAlignment = ParagraphAlignment.Near;
            f.TextAlignment = f == Leading ? TextAlignment.Leading
                : f == Centered ? TextAlignment.Center : TextAlignment.Trailing;
        }
    }

    /// <summary>基线到行顶的距离（像素）。</summary>
    public float Ascent => EnsureMetrics(ref _ascent, m => m.Baseline);

    /// <summary>行高（像素）。</summary>
    public float LineHeight => EnsureMetrics(ref _lineHeight, m => m.Height);

    /// <summary>测一行文本宽度（缓存）。</summary>
    public float MeasureWidth(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        // 单码点走码点缓存（逐字排版是主路径）
        if (text.Length == 1) return MeasureWidth(text[0]);
        using var layout = _dw.CreateTextLayout(text, Leading, float.MaxValue, float.MaxValue);
        return layout.Metrics.Width;
    }

    /// <summary>测单个码点宽度（缓存）。</summary>
    public float MeasureWidth(int codepoint)
    {
        if (_widthCache.TryGetValue(codepoint, out var w)) return w;
        var s = char.ConvertFromUtf32(codepoint);
        using var layout = _dw.CreateTextLayout(s, Leading, float.MaxValue, float.MaxValue);
        w = layout.Metrics.Width;
        _widthCache[codepoint] = w;
        return w;
    }

    private float EnsureMetrics(ref float slot, Func<LineMetrics, float> pick)
    {
        if (!float.IsNaN(slot)) return slot;
        try
        {
            using var layout = _dw.CreateTextLayout("Hg日", Leading, float.MaxValue, float.MaxValue);
            var metrics = layout.LineMetrics;
            slot = metrics.Length > 0 ? pick(metrics[0]) : Size * 1.2f;
        }
        catch (Exception ex)
        {
            Log.Warn($"[DwFont] 取字体度量失败（{Family} {Size}）：{ex.Message}");
            slot = Size * 1.2f; // 兜底：按字号估一个行高
        }

        return slot;
    }

    public void Dispose()
    {
        Leading.Dispose();
        Centered.Dispose();
        Trailing.Dispose();
    }
}

/// <summary>(字族, 字号) → <see cref="DwFont"/> 的缓存（渲染线程内使用，无需加锁）。</summary>
public sealed class DwFontCache : IDisposable
{
    private readonly IDWriteFactory _dw;
    private readonly Dictionary<(string, float), DwFont> _fonts = new();

    public DwFontCache(IDWriteFactory dw) => _dw = dw;

    public DwFont Get(string family, float size,
        FontWeight weight = FontWeight.Normal,
        FontStyle style = FontStyle.Normal,
        FontStretch stretch = FontStretch.Normal)
    {
        family = string.IsNullOrWhiteSpace(family) ? "Microsoft YaHei UI" : family;
        size = MathF.Max(0.1f, size);
        var key = (family, size);
        if (_fonts.TryGetValue(key, out var f)) return f;
        f = new DwFont(_dw, family, size, weight, style, stretch);
        _fonts[key] = f;
        return f;
    }

    public void Dispose()
    {
        foreach (var f in _fonts.Values) f.Dispose();
        _fonts.Clear();
    }
}
