using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JizuraLive.Render.Draw;

public enum StrokeJoin2D
{
    Miter,
    Round,
    Bevel,
}

public enum StrokeCap2D
{
    Flat,
    Round,
    Square,
}

public enum TextAlign2D
{
    Left,
    Center,
    Right,
}

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

public readonly record struct GradientStop2D(float Offset, Color4 Color);

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

public sealed class Paint2D
{
    public Brush2D Brush { get; set; } = new SolidBrush2D(new Color4(1f, 1f, 1f, 1f));

    public float Alpha { get; set; } = 1f;

    public bool IsStroke { get; set; }

    public float StrokeWidth { get; set; } = 1f;
    public StrokeJoin2D StrokeJoin { get; set; } = StrokeJoin2D.Miter;
    public StrokeCap2D StrokeCap { get; set; } = StrokeCap2D.Flat;
    public float StrokeMiter { get; set; } = 10f;

    public float[]? DashPattern { get; set; }

    public float DashOffset { get; set; }

    public bool IsAntialias { get; set; } = true;

    public Blend2D Blend { get; set; } = Blend2D.SrcOver;

    public float BlurSigma { get; set; }

    public Shadow2D? Shadow { get; set; }

    public Matrix5x4? ColorMatrix { get; set; }

}
