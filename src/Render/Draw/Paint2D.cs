using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JizuraLive.Render.Draw;

/// <summary>描边拐角（对位 SKStrokeJoin）。</summary>
public enum StrokeJoin2D
{
    Miter,
    Round,
    Bevel,
}

/// <summary>描边端点（对位 SKStrokeCap）。</summary>
public enum StrokeCap2D
{
    Flat,
    Round,
    Square,
}

/// <summary>文本水平对齐（对位 SKTextAlign）。</summary>
public enum TextAlign2D
{
    Left,
    Center,
    Right,
}

/// <summary>
/// 混合模式：**完整对位旧引擎的 SKBlendMode 子集**（见 JText.BlendMap 的 17 项）。
/// 注意 Direct2D 的原始混合（<see cref="PrimitiveBlend"/>）只有 5 种，超出的模式必须走
/// D2D 效果图（Blend 效果）才能做到像素级一致——见 <c>DOC/P1-DRAW-PRIMITIVES.md</c> 的缺口清单。
/// </summary>
public enum Blend2D
{
    SrcOver,
    Plus,
    Screen,
    Overlay,
    Multiply,
    Lighten,
    Darken,
    ColorDodge,
    ColorBurn,
    HardLight,
    SoftLight,
    Difference,
    Exclusion,
    Hue,
    Saturation,
    Color,
    Luminosity,
    SrcCopy,
}

/// <summary>渐变停止点。</summary>
public readonly record struct GradientStop2D(float Offset, Color4 Color);

/// <summary>
/// 画刷描述（不可变记录）：描述「怎么填」，由 <see cref="Immediate2D"/> 物化成 D2D 画刷并缓存。
///
/// 为什么是描述而不是 D2D 画刷对象：旧引擎的绘制代码里 paint 是「每帧改属性、复用同一个对象」，
/// 描述式让缓存键天然稳定（同色/同渐变只建一次画刷），也避免了调用方管理 D2D 资源生命周期。
/// </summary>
public abstract record Brush2D;

public sealed record SolidBrush2D(Color4 Color) : Brush2D;

public sealed record LinearGradient2D(
    Vector2 Start,
    Vector2 End,
    GradientStop2D[] Stops,
    ExtendMode Extend = ExtendMode.Clamp) : Brush2D;

public sealed record RadialGradient2D(
    Vector2 Center,
    float RadiusX,
    float RadiusY,
    GradientStop2D[] Stops,
    ExtendMode Extend = ExtendMode.Clamp) : Brush2D;

public sealed record BitmapBrush2D(
    Image2D Image,
    ExtendMode ExtendX = ExtendMode.Wrap,
    ExtendMode ExtendY = ExtendMode.Wrap,
    BitmapInterpolationMode Interp = BitmapInterpolationMode.Linear) : Brush2D;

/// <summary>
/// 绘制参数（对位 SKPaint 的常用面）。可变对象、可复用——与旧引擎「一个 paint 改属性画一堆」的
/// 写法保持一致，移植时改的是类型名，不是结构。
/// </summary>
public sealed class Paint2D
{
    public Brush2D Brush { get; set; } = new SolidBrush2D(new Color4(1f, 1f, 1f, 1f));

    /// <summary>整体不透明度 0..1，乘在画刷之上（对位 globalAlpha / paint alpha）。</summary>
    public float Alpha { get; set; } = 1f;

    /// <summary>true = 描边，false = 填充。</summary>
    public bool IsStroke { get; set; }

    public float StrokeWidth { get; set; } = 1f;
    public StrokeJoin2D StrokeJoin { get; set; } = StrokeJoin2D.Miter;
    public StrokeCap2D StrokeCap { get; set; } = StrokeCap2D.Flat;
    public float StrokeMiter { get; set; } = 10f;

    /// <summary>虚线模式（对位 SKPathEffect.CreateDash）：交替「实长/空长」，null = 实线。</summary>
    public float[]? DashPattern { get; set; }

    public float DashOffset { get; set; }

    public bool IsAntialias { get; set; } = true;

    public Blend2D Blend { get; set; } = Blend2D.SrcOver;

    // ---------------- 需要离屏面的效果（对位 SKImageFilter / SKColorFilter） ----------------
    // 这几个属性只有在 PushSurface/PopSurface 的整面合成里生效：D2D 的效果作用于图像，
    // 不能像 Skia 的 paint.ImageFilter 那样直接挂在单次绘制上。

    /// <summary>高斯模糊 sigma（对位 SKImageFilter.CreateBlur，σ = 半径/2）。</summary>
    public float BlurSigma { get; set; }

    /// <summary>投影（对位 SKImageFilter.CreateDropShadow）。</summary>
    public Shadow2D? Shadow { get; set; }

    /// <summary>颜色矩阵（对位 SKColorFilter.CreateColorMatrix）。</summary>
    public Matrix5x4? ColorMatrix { get; set; }

    public Paint2D Clone() => (Paint2D)MemberwiseClone();

    /// <summary>改色便捷方法（移植期高频：`p.Color = ...`）。</summary>
    public Paint2D WithColor(Color4 c)
    {
        Brush = new SolidBrush2D(c);
        return this;
    }
}
