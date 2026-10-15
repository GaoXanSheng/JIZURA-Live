using JizuraLive.Render.Text;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 歌词样式：只钉排版输入（字号/颜色/预设解析），不钉像素——像素那条线在 TypographyTests 里。
/// </summary>
public class LyricStyleTests
{
    private static readonly LyricStyle[] Presets =
        [LyricStyle.Classic, LyricStyle.Neon, LyricStyle.Outline, LyricStyle.Minimal];

    [Fact]
    public void PresetsAreDistinctAndWithinRange()
    {
        for (var i = 0; i < Presets.Length; i++)
        {
            var s = Presets[i];
            Assert.InRange(s.FontSize, 8f, 400f);
            Assert.InRange(s.GlowSize, 0f, 200f);
            Assert.InRange(s.StrokeWidth, 0f, 100f);
            Assert.InRange(s.ColorValue.A, 0f, 1f);
            Assert.InRange(s.GlowColorValue.A, 0f, 1f);
            Assert.InRange(s.StrokeColorValue.A, 0f, 1f);

            for (var j = i + 1; j < Presets.Length; j++)
                Assert.NotEqual(Presets[i], Presets[j]);
        }
    }

    /// <summary>Classic 必须还原替换掉的那套硬编码值，否则等于偷偷改了默认外观。</summary>
    [Fact]
    public void ClassicKeepsPreviousHardcodedLayoutInputs()
    {
        var c = LyricStyle.Classic;

        Assert.Equal(52f, c.FontSize);
        Assert.Equal(2f, c.GlowSize);
        Assert.Equal(0f, c.StrokeWidth);

        Assert.Equal(1f, c.ColorValue.R);
        Assert.Equal(1f, c.ColorValue.G);
        Assert.Equal(1f, c.ColorValue.B);
        Assert.Equal(1f, c.ColorValue.A);

        // 旧硬编码发光 (0, 0.05, 0.15) × 0.6 → 8bit 量化后 0 / 13 / 38
        Assert.Equal(0f, c.GlowColorValue.R);
        Assert.Equal(13f / 255f, c.GlowColorValue.G);
        Assert.Equal(38f / 255f, c.GlowColorValue.B);
        Assert.Equal(0.6f, c.GlowColorValue.A);

        Assert.Equal(52f, c.FontSizePx(1.0));
        Assert.Equal(26f, c.FontSizePx(0.5));
    }

    [Fact]
    public void FromNameResolvesKnownPresetsCaseInsensitively()
    {
        Assert.Equal(LyricStyle.Classic, LyricStyle.FromName("Classic"));
        Assert.Equal(LyricStyle.Classic, LyricStyle.FromName("classic"));
        Assert.Equal(LyricStyle.Neon, LyricStyle.FromName("Neon"));
        Assert.Equal(LyricStyle.Outline, LyricStyle.FromName(" outline "));
        Assert.Equal(LyricStyle.Minimal, LyricStyle.FromName("Minimal"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Glitch")]
    [InlineData("neon-ish")]
    public void FromNameFallsBackToClassicForUnknown(string? name) =>
        Assert.Equal(LyricStyle.Classic, LyricStyle.FromName(name));

    /// <summary>
    /// SetStyle 换的是字号/颜色这两项排版输入，不是像素：断言本层生效的样式与由它推出的值。
    /// 构造只存引用、不碰设备（见 TypographyLayerProvider 构造函数），所以这里不需要 GPU。
    /// </summary>
    [Fact]
    public void SetStyleFeedsLayoutInputs()
    {
        using var provider = new TypographyLayerProvider("style_test", null!, null!);

        Assert.Equal(LyricStyle.Classic, provider.EffectiveStyle);
        Assert.Equal(52f, provider.EffectiveStyle.FontSizePx(1.0));

        provider.SetStyle(LyricStyle.Neon);
        Assert.Equal(LyricStyle.Neon, provider.EffectiveStyle);
        Assert.Equal(LyricStyle.Neon.FontSize, provider.EffectiveStyle.FontSizePx(1.0));
        Assert.Equal(LyricStyle.Neon.FontSizePx(0.5), provider.EffectiveStyle.FontSizePx(0.5));
        Assert.Equal(234f / 255f, provider.EffectiveStyle.ColorValue.R);
        Assert.Equal(252f / 255f, provider.EffectiveStyle.ColorValue.G);
        Assert.Equal(1f, provider.EffectiveStyle.ColorValue.B);

        // 显式 null = 回到预设，不是「没有样式」的空白样式
        provider.SetStyle(null);
        Assert.Equal(LyricStyle.Classic, provider.EffectiveStyle);
    }
}
