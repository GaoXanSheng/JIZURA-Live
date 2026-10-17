namespace JizuraLive.Pv;

public enum LayoutAlign
{
    Center,
    Left,
    Right,
}

public enum LayoutBlockKind
{
    Single,
    SplitVertical,
    SplitDiagonal,
    HugeBreak,
    VerticalColumns,
}

public readonly record struct LayoutAnchor
{
    public float X { get; init; }
    public float Y { get; init; }

    public static LayoutAnchor At(float x, float y) => new() { X = x, Y = y };
}

public readonly record struct LayoutBlock
{
    public LayoutBlockKind Kind { get; init; }
    public LayoutAlign Align { get; init; }
    public float Scale { get; init; }
    public float OffsetX { get; init; }
    public float OffsetY { get; init; }
    public float RotationRad { get; init; }
    public float AlphaScale { get; init; }

    public static LayoutBlock Default { get; } = new()
    {
        Kind = LayoutBlockKind.Single,
        Align = LayoutAlign.Center,
        Scale = 1f,
        OffsetX = 0f,
        OffsetY = 0f,
        RotationRad = 0f,
        AlphaScale = 1f,
    };
}

public sealed class LayoutTemplate
{
    public string Key { get; init; } = "";
    public string Name { get; init; } = "";
    public string Family { get; init; } = "";
    public int MinGlyphs { get; init; }
    public int MaxGlyphs { get; init; } = int.MaxValue;
    public LayoutAnchor Anchor { get; init; } = LayoutAnchor.At(0.5f, 0.75f);
    public LayoutBlock[] Blocks { get; init; } = [LayoutBlock.Default];

    public bool Fits(int glyphCount) => glyphCount >= MinGlyphs && glyphCount <= MaxGlyphs;

    public LayoutBlock BlockFor(int glyphCount)
    {
        if (Blocks.Length == 0) return LayoutBlock.Default;
        if (Blocks.Length == 1) return Blocks[0];

        var lo = Math.Max(MinGlyphs, 0);
        var span = Math.Max(1, MaxGlyphs - lo + 1);
        var band = (glyphCount - lo) * Blocks.Length / span;
        return Blocks[Math.Clamp(band, 0, Blocks.Length - 1)];
    }
}

public static class LayoutTemplates
{
    public const string DefaultKey = "center";

    public static IReadOnlyList<LayoutTemplate> All => _all;

    private static readonly LayoutTemplate[] _all =
    [
        Center(),
        CenterLeft(),
        CenterRight(),
        SplitTop(),
        SplitBottom(),
        DiagonalBand(),
        DiagonalReverse(),
        HugeBreak(),
        HugeBreakSmall(),
        VerticalColumns(),
        VerticalSingle(),
        LowerThird(),
        CornerTopLeft(),
        CornerTopRight(),
        Pill(),
        Stack(),
        MirrorPair(),
        TypeSpecimen(),
        Quote(),
        Ruler(),
    ];

    public static LayoutTemplate Default => Get(DefaultKey) ?? _all[0];

    public static LayoutTemplate? Get(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        for (var i = 0; i < _all.Length; i++)
            if (string.Equals(_all[i].Key, key, StringComparison.OrdinalIgnoreCase))
                return _all[i];
        return null;
    }

    public static IReadOnlyList<LayoutTemplate> Fits(int glyphCount)
    {
        var list = new List<LayoutTemplate>(_all.Length);
        for (var i = 0; i < _all.Length; i++)
            if (_all[i].Fits(glyphCount))
                list.Add(_all[i]);
        return list;
    }

    public static LayoutTemplate Pick(int glyphCount)
    {
        LayoutTemplate? best = null;
        var bestSpan = int.MaxValue;
        for (var i = 0; i < _all.Length; i++)
        {
            if (!_all[i].Fits(glyphCount)) continue;
            var span = _all[i].MaxGlyphs == int.MaxValue
                ? int.MaxValue
                : _all[i].MaxGlyphs - Math.Max(0, _all[i].MinGlyphs);
            if (span < bestSpan)
            {
                best = _all[i];
                bestSpan = span;
            }
        }

        return best ?? Default;
    }

    private static LayoutBlock Block(LayoutBlockKind kind, LayoutAlign align, float scale,
        float offsetX, float offsetY, float rotationRad, float alphaScale)
    {
        return LayoutBlock.Default with
        {
            Kind = kind,
            Align = align,
            Scale = scale,
            OffsetX = offsetX,
            OffsetY = offsetY,
            RotationRad = rotationRad,
            AlphaScale = alphaScale,
        };
    }

    private static LayoutTemplate Make(string key, string name, string family, LayoutAnchor anchor,
        int minGlyphs, int maxGlyphs, params LayoutBlock[] blocks) => new()
    {
        Key = key,
        Name = name,
        Family = family,
        Anchor = anchor,
        MinGlyphs = minGlyphs,
        MaxGlyphs = maxGlyphs,
        Blocks = blocks,
    };

    private static LayoutTemplate Center() =>
        Make("center", "中央", "center", LayoutAnchor.At(0.5f, 0.75f), 0, int.MaxValue,
            Block(LayoutBlockKind.Single, LayoutAlign.Center, 1f, 0f, 0f, 0f, 1f));

    private static LayoutTemplate CenterLeft() =>
        Make("centerLeft", "中央・左", "center", LayoutAnchor.At(0.5f, 0.75f), 0, int.MaxValue,
            Block(LayoutBlockKind.Single, LayoutAlign.Left, 1f, 0f, 0f, 0f, 1f));

    private static LayoutTemplate CenterRight() =>
        Make("centerRight", "中央・右", "center", LayoutAnchor.At(0.5f, 0.75f), 0, int.MaxValue,
            Block(LayoutBlockKind.Single, LayoutAlign.Right, 1f, 0f, 0f, 0f, 1f));

    private static LayoutTemplate SplitTop() =>
        Make("splitTop", "二行割り", "split", LayoutAnchor.At(0.5f, 0.6f), 4, 40,
            Block(LayoutBlockKind.SplitVertical, LayoutAlign.Center, 1f, 0f, -0.06f, 0f, 1f));

    private static LayoutTemplate SplitBottom() =>
        Make("splitBottom", "二段・下", "split", LayoutAnchor.At(0.5f, 0.72f), 4, 40,
            Block(LayoutBlockKind.SplitVertical, LayoutAlign.Center, 0.8f, 0f, 0.1f, 0f, 1f));

    private static LayoutTemplate DiagonalBand() =>
        Make("diagonalBand", "斜め帯", "diag", LayoutAnchor.At(0.5f, 0.5f), 1, 24,
            Block(LayoutBlockKind.SplitDiagonal, LayoutAlign.Center, 1f, 0f, 0f, 0.24f, 1f));

    private static LayoutTemplate DiagonalReverse() =>
        Make("diagonalReverse", "斜め帯・逆", "diag", LayoutAnchor.At(0.5f, 0.5f), 1, 24,
            Block(LayoutBlockKind.SplitDiagonal, LayoutAlign.Center, 1f, 0f, 0f, -0.22f, 1f));

    private static LayoutTemplate HugeBreak() =>
        Make("hugeBreak", "画面突き抜け", "huge", LayoutAnchor.At(0.5f, 0.52f), 1, 8,
            Block(LayoutBlockKind.HugeBreak, LayoutAlign.Center, 1f, 0f, 0f, 0f, 1f));

    private static LayoutTemplate HugeBreakSmall() =>
        Make("hugeBreakSmall", "突き抜け・小", "huge", LayoutAnchor.At(0.5f, 0.58f), 1, 12,
            Block(LayoutBlockKind.HugeBreak, LayoutAlign.Center, 0.7f, 0f, 0f, 0f, 1f));

    private static LayoutTemplate VerticalColumns() =>
        Make("verticalColumns", "縦書き格", "vertical", LayoutAnchor.At(0.5f, 0.5f), 2, 40,
            Block(LayoutBlockKind.VerticalColumns, LayoutAlign.Center, 1f, 0f, 0f, 0f, 1f));

    private static LayoutTemplate VerticalSingle() =>
        Make("verticalSingle", "縦書き一列", "vertical", LayoutAnchor.At(0.5f, 0.5f), 1, 20,
            Block(LayoutBlockKind.VerticalColumns, LayoutAlign.Center, 1f, 0f, 0f, 0f, 1f));

    private static LayoutTemplate LowerThird() =>
        Make("lowerThird", "ロアーサード", "band", LayoutAnchor.At(0.5f, 0.86f), 2, 30,
            Block(LayoutBlockKind.Single, LayoutAlign.Center, 0.9f, 0f, 0f, 0f, 1f));

    private static LayoutTemplate CornerTopLeft() =>
        Make("cornerTopLeft", "左上コーナー", "corner", LayoutAnchor.At(0.18f, 0.16f), 1, 20,
            Block(LayoutBlockKind.Single, LayoutAlign.Left, 0.75f, 0f, 0f, 0f, 1f));

    private static LayoutTemplate CornerTopRight() =>
        Make("cornerTopRight", "右上コーナー", "corner", LayoutAnchor.At(0.82f, 0.16f), 1, 20,
            Block(LayoutBlockKind.Single, LayoutAlign.Right, 0.75f, 0f, 0f, 0f, 1f));

    private static LayoutTemplate Pill() =>
        Make("pill", "カプセル", "box", LayoutAnchor.At(0.5f, 0.75f), 1, 24,
            Block(LayoutBlockKind.Single, LayoutAlign.Center, 0.85f, 0f, 0f, 0f, 1f));

    private static LayoutTemplate Stack() =>
        Make("stack", "残像スタック", "stack", LayoutAnchor.At(0.5f, 0.5f), 1, 24,
            Block(LayoutBlockKind.SplitVertical, LayoutAlign.Center, 0.9f, 0f, 0f, 0f, 1f));

    private static LayoutTemplate MirrorPair() =>
        Make("mirrorPair", "鏡像", "mirror", LayoutAnchor.At(0.5f, 0.75f), 2, 30,
            Block(LayoutBlockKind.Single, LayoutAlign.Center, 1f, 0f, 0f, 0f, 1f));

    private static LayoutTemplate TypeSpecimen() =>
        Make("typeSpecimen", "見本帳", "type", LayoutAnchor.At(0.5f, 0.5f), 1, 30,
            Block(LayoutBlockKind.SplitVertical, LayoutAlign.Left, 0.8f, -0.08f, 0f, 0f, 1f),
            Block(LayoutBlockKind.Single, LayoutAlign.Center, 0.5f, 0f, 0.2f, 0f, 0.7f));

    private static LayoutTemplate Quote() =>
        Make("quote", "引用", "type", LayoutAnchor.At(0.5f, 0.62f), 4, 40,
            Block(LayoutBlockKind.Single, LayoutAlign.Center, 0.9f, 0f, 0f, 0f, 1f),
            Block(LayoutBlockKind.Single, LayoutAlign.Left, 0.45f, -0.06f, 0.18f, 0f, 0.6f));

    private static LayoutTemplate Ruler() =>
        Make("ruler", "物差し", "type", LayoutAnchor.At(0.5f, 0.5f), 1, 20,
            Block(LayoutBlockKind.Single, LayoutAlign.Left, 0.7f, -0.06f, 0f, 0f, 1f),
            Block(LayoutBlockKind.Single, LayoutAlign.Center, 0.55f, 0f, 0.16f, 0f, 0.75f));
}
