using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using EffectBlend = Vortice.Direct2D1.Effects.Blend;
using Vortice.Mathematics;

namespace JizuraLive.Render.Draw;

public readonly record struct Shadow2D(float OffsetX, float OffsetY, float BlurSigma, Color4 Color);

public sealed class EffectChain : IDisposable
{
    private readonly ID2D1DeviceContext _ctx;
    private GaussianBlur? _blur;
    private Shadow? _shadow;
    private ColorMatrix? _cm;
    private Unpremultiply? _unpremul;
    private Premultiply? _premul;
    private Opacity? _opacity;
    private EffectBlend? _blend;

    public EffectChain(ID2D1DeviceContext ctx) => _ctx = ctx;

    public static bool NeedsSurface(Paint2D p) =>
        p.BlurSigma > 0.05f || p.ColorMatrix is not null || p.Shadow is not null;

    public ID2D1Image Wrap(ID2D1Image src, Paint2D p)
    {
        var cur = src;

        if (p.BlurSigma > 0.05f)
        {
            _blur ??= new GaussianBlur(_ctx);
            _blur.StandardDeviation = p.BlurSigma;
            _blur.BorderMode = BorderMode.Soft;
            _blur.SetInput(0, cur, true);
            cur = _blur.Output;
        }

        if (p.ColorMatrix is { } m)
        {
            _unpremul ??= new Unpremultiply(_ctx);
            _unpremul.SetInput(0, cur, true);

            _cm ??= new ColorMatrix(_ctx);
            _cm.Matrix = m;
            _cm.AlphaMode = ColorMatrixAlphaMode.Straight;
            _cm.SetInput(0, _unpremul.Output, true);

            _premul ??= new Premultiply(_ctx);
            _premul.SetInput(0, _cm.Output, true);
            cur = _premul.Output;
        }

        var alpha = Math.Clamp(p.Alpha, 0f, 1f);
        if (alpha < 0.999f)
        {
            _opacity ??= new Opacity(_ctx);
            _opacity.Value = alpha;
            _opacity.SetInput(0, cur, true);
            cur = _opacity.Output;
        }

        return cur;
    }

    public ID2D1Image? ShadowImage(ID2D1Image src, Paint2D p)
    {
        if (p.Shadow is not { } sh) return null;
        _shadow ??= new Shadow(_ctx);
        _shadow.BlurStandardDeviation = sh.BlurSigma;
        _shadow.Color = new Vector4(sh.Color.R, sh.Color.G, sh.Color.B, sh.Color.A);
        _shadow.Optimization = ShadowOptimization.Speed;
        _shadow.SetInput(0, src, true);
        return _shadow.Output;
    }

    public ID2D1Image BlendImages(ID2D1Image backdrop, ID2D1Image layer, Blend2D mode)
    {
        _blend ??= new EffectBlend(_ctx);
        _blend.Mode = ToBlendEffect(mode);
        _blend.SetInput(0, backdrop, true);
        _blend.SetInput(1, layer, true);
        return _blend.Output;
    }

    public ID2D1Image WrapOpacity(ID2D1Image src, float alpha)
    {
        alpha = Math.Clamp(alpha, 0f, 1f);
        if (alpha > 0.999f) return src;
        _opacity ??= new Opacity(_ctx);
        _opacity.Value = alpha;
        _opacity.SetInput(0, src, true);
        return _opacity.Output;
    }

    public static CompositeMode ToComposite(Blend2D blend) => blend switch
    {
        Blend2D.Plus => CompositeMode.Plus,
        Blend2D.SrcCopy => CompositeMode.SourceCopy,
        Blend2D.SrcOver => CompositeMode.SourceOver,
        _ => CompositeMode.SourceOver,
    };

    public static BlendMode ToBlendEffect(Blend2D blend) => blend switch
    {
        Blend2D.Multiply => BlendMode.Multiply,
        Blend2D.Screen => BlendMode.Screen,
        Blend2D.Overlay => BlendMode.Overlay,
        Blend2D.Lighten => BlendMode.Lighten,
        Blend2D.Darken => BlendMode.Darken,
        Blend2D.ColorDodge => BlendMode.ColorDodge,
        Blend2D.ColorBurn => BlendMode.ColorBurn,
        Blend2D.HardLight => BlendMode.HardLight,
        Blend2D.SoftLight => BlendMode.SoftLight,
        Blend2D.Difference => BlendMode.Difference,
        Blend2D.Exclusion => BlendMode.Exclusion,
        Blend2D.Hue => BlendMode.Hue,
        Blend2D.Saturation => BlendMode.Saturation,
        Blend2D.Color => BlendMode.Color,
        Blend2D.Luminosity => BlendMode.Luminosity,
        _ => BlendMode.Multiply,
    };

    public static bool IsDirectComposite(Blend2D blend) =>
        blend is Blend2D.SrcOver or Blend2D.Plus or Blend2D.SrcCopy;

    public void Dispose()
    {
        _blur?.Dispose();
        _shadow?.Dispose();
        _cm?.Dispose();
        _unpremul?.Dispose();
        _premul?.Dispose();
        _opacity?.Dispose();
        _blend?.Dispose();
    }
}
