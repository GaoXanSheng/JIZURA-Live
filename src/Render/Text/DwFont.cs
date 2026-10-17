using JizuraLive.Logging;
using Vortice.DirectWrite;

namespace JizuraLive.Render.Text;

public sealed class DwFont : IDisposable
{
    private readonly IDWriteFactory _dw;
    private readonly Dictionary<int, float> _widthCache = new(256);
    private float _ascent = float.NaN;
    private float _lineHeight = float.NaN;
    private float _spaceWidth = float.NaN;

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

        foreach (var f in new[] { Leading, Centered, Trailing })
        {
            f.WordWrapping = WordWrapping.NoWrap;
            f.ParagraphAlignment = ParagraphAlignment.Near;
            f.TextAlignment = f == Leading ? TextAlignment.Leading
                : f == Centered ? TextAlignment.Center : TextAlignment.Trailing;
        }
    }

    public float Ascent => EnsureMetrics(ref _ascent, m => m.Baseline);

    public float LineHeight => EnsureMetrics(ref _lineHeight, m => m.Height);

    public float MeasureWidth(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (text.Length == 1) return MeasureWidth(text[0]);
        using var layout = _dw.CreateTextLayout(text, Leading, float.MaxValue, float.MaxValue);
        return layout.Metrics.Width;
    }

    public float MeasureWidth(int codepoint)
    {
        var s = char.ConvertFromUtf32(codepoint);
        if (string.IsNullOrWhiteSpace(s)) return SpaceWidth;
        if (_widthCache.TryGetValue(codepoint, out var w)) return w;
        using var layout = _dw.CreateTextLayout(s, Leading, float.MaxValue, float.MaxValue);
        w = layout.Metrics.Width;
        _widthCache[codepoint] = w;
        return w;
    }

    private float SpaceWidth
    {
        get
        {
            if (!float.IsNaN(_spaceWidth)) return _spaceWidth;
            try
            {
                var n = MeasureWidth('n');
                using var layout = _dw.CreateTextLayout("n n", Leading, float.MaxValue, float.MaxValue);
                var w = layout.Metrics.Width - 2f * n;
                _spaceWidth = w > 0.01f ? w : Size * 0.3f;
            }
            catch
            {
                _spaceWidth = Size * 0.3f;
            }

            return _spaceWidth;
        }
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
            slot = Size * 1.2f;
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
