using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using EffectBlend = Vortice.Direct2D1.Effects.Blend;
using Vortice.Mathematics;

namespace JizuraLive.Render.Draw;

/// <summary>投影规格（对位 SKImageFilter.CreateDropShadow 的常用参数）。</summary>
public readonly record struct Shadow2D(float OffsetX, float OffsetY, float BlurSigma, Color4 Color);

/// <summary>
/// 可复用的 D2D 效果链：把「模糊 / 色矩阵 / 不透明度 / 投影」按顺序套到一张输入图像上。
///
/// 为什么单独一类：D2D 的效果实例是有状态的（属性 + 输入），每帧新建会持续产生 COM 对象与
/// 显存分配；这里按实例缓存、每帧只改属性与输入，和旧引擎「滤镜来自共享记忆工厂」的做法一致。
///
/// 与 Skia 的语义差异（重要）：
///   * Skia 的 <c>ImageFilter</c> 是**绘制时**的滤镜（画什么就滤什么）；D2D 的效果作用于**图像**，
///     所以必须先把内容渲到离屏面、再对整面套效果——这就是 <see cref="Immediate2D.PushSurface"/> /
///     <see cref="Immediate2D.PopSurface"/> 存在的理由。
///   * D2D 的 <c>Shadow</c> 效果**只输出影子**（不含本体），要「影子 + 本体」得先画影子再画本体。
/// </summary>
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

    /// <summary>是否需要用离屏面（有模糊/色矩阵/半透明才需要，否则可直画）。</summary>
    public static bool NeedsSurface(Paint2D p) =>
        p.BlurSigma > 0.05f || p.ColorMatrix is not null || p.Shadow is not null;

    /// <summary>
    /// 按「模糊 → 色矩阵 → 不透明度」包裹输入图像，返回最终可绘制的图像。
    /// 无效果时原样返回 <paramref name="src"/>。
    /// </summary>
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
            // 关键：D2D 的 ColorMatrix 在 Straight alpha 下工作，而我们的面是**预乘**的。
            // 直接套矩阵会把透明像素 (0,0,0,0) 变成 (1,1,1,0)——RGB 超出 alpha，SourceOver 时
            // 会把整帧加白。所以必须「解预乘 → 套矩阵 → 再预乘」，这也正是 Skia
            // SKColorFilter.CreateColorMatrix 的语义（作用在非预乘颜色上）。
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

    /// <summary>
    /// 取「影子」图像（D2D 的 Shadow 效果只输出影子）。调用方负责先画它、再画本体。
    /// 返回 null 表示 paint 没有投影规格。
    /// </summary>
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

    /// <summary>
    /// 两输入混合：<paramref name="backdrop"/> 为背景（输入 0），<paramref name="layer"/> 为前景（输入 1），
    /// 覆盖 D2D <c>Blend</c> 效果的全部模式。返回可直接绘制的图像。
    ///
    /// 这是补齐 14 项层间混合模式的入口——单张面的 PopSurface 拿不到背景，只有显式两输入才能做。
    /// </summary>
    public ID2D1Image BlendImages(ID2D1Image backdrop, ID2D1Image layer, Blend2D mode)
    {
        _blend ??= new EffectBlend(_ctx);
        _blend.Mode = ToBlendEffect(mode);
        _blend.SetInput(0, backdrop, true);
        _blend.SetInput(1, layer, true);
        return _blend.Output;
    }

    /// <summary>套一层不透明度（返回新图像；alpha≈1 时原样返回）。</summary>
    public ID2D1Image WrapOpacity(ID2D1Image src, float alpha)
    {
        alpha = Math.Clamp(alpha, 0f, 1f);
        if (alpha > 0.999f) return src;
        _opacity ??= new Opacity(_ctx);
        _opacity.Value = alpha;
        _opacity.SetInput(0, src, true);
        return _opacity.Output;
    }

    /// <summary>把 <see cref="Blend2D"/> 映射到 D2D 的 <see cref="CompositeMode"/>（DrawImage 用）。</summary>
    public static CompositeMode ToComposite(Blend2D blend) => blend switch
    {
        Blend2D.Plus => CompositeMode.Plus,
        Blend2D.SrcCopy => CompositeMode.SourceCopy,
        Blend2D.SrcOver => CompositeMode.SourceOver,
        _ => CompositeMode.SourceOver,
    };

    /// <summary>把 <see cref="Blend2D"/> 映射到 D2D 的 <see cref="BlendMode"/>（Blend 效果用）。</summary>
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

    /// <summary>是否能走 D2D 的 DrawImage 合成模式（无需背景图输入）。</summary>
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
