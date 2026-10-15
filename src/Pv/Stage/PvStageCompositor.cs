using System.Numerics;
using JizuraLive.Render.Draw;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JizuraLive.Pv;

/// <summary>一层本帧要画的内容：位图或纯色（两者都不存在 = 本帧该层缺席）。</summary>
public readonly record struct PvLayerImage(ID2D1Bitmap? Bitmap, Color4? Solid, int Width, int Height)
{
    public bool HasContent => Bitmap is not null || Solid is not null;

    public static PvLayerImage FromSolid(Color4 color, int width, int height) =>
        new(null, color, width, height);

    public static PvLayerImage Empty => new(null, null, 0, 0);
}

/// <summary>
/// 环境光融合（解决「角色/素材像粗制滥造的贴片」）。
///
/// 三件事：**环境光染色**（让素材吃场景主色调，而不是白惨惨地贴上去）、
/// **边缘光**（深色背景下从背后透出一层模糊亮拷贝，把主体从背景里"分"出来）、
/// **接触阴影**（浅色背景下给主体立足感）。
/// </summary>
public static class PvAmbient
{
    /// <summary>
    /// 环境光染色的 5×4 颜色矩阵：<c>out.rgb = rgb * lerp(1, tint.rgb * 2, k)</c>。
    /// 与效果库的 <c>colorGrade.tint</c> 同一套语义（乘 2 让中性色不受影响）。
    /// </summary>
    public static Matrix5x4 TintMatrix(Color4 tint, double strength)
    {
        var k = (float)Math.Clamp(strength, 0, 1);
        float L(float c) => 1f + (c * 2f - 1f) * k;

        return new Matrix5x4(
            L(tint.R), 0, 0, 0,
            0, L(tint.G), 0, 0,
            0, 0, L(tint.B), 0,
            0, 0, 0, 1,
            0, 0, 0, 0);
    }

    /// <summary>该层是否需要走离屏面（有染色/边缘光/阴影才需要；否则可以直接画，省一次面切换）。</summary>
    public static bool NeedsSurface(PvLayerConfig layer, bool ambientEnabled)
    {
        if (!ambientEnabled) return false;
        var b = layer.VisualBlend;
        return b.AmbientTintIntensity > 0.001 || b.RimGlow > 0.001 || b.DropShadow > 0.001;
    }
}

/// <summary>
/// PV 舞台合成器：按图层栈顺序把各层的图合到当前目标上。
///
/// 与旧 Skia 版合成器的区别：跑在新渲染核心（D2D/D3D11）上，所以每层的
/// 环境光染色/边缘光/阴影直接用 <see cref="Immediate2D.PushSurface"/> 的中间面 + 效果链，
/// 与用户手搭的效果走同一条路径，不再有一套独立的 Skia 滤镜代码。
/// </summary>
public sealed class PvStageCompositor
{
    /// <summary>边缘光模糊半径（像素，按 1080p 设计）。</summary>
    private const float RimBlurSigma = 26f;

    /// <summary>接触阴影模糊半径（像素）。</summary>
    private const float ShadowBlurSigma = 14f;

    private readonly Immediate2D _draw;

    public PvStageCompositor(Immediate2D draw) => _draw = draw;

    /// <summary>
    /// 合成一帧。图层栈顺序 = 绘制顺序（先画在下）；相机按每层深度加权。
    /// </summary>
    public void Composite(PvScene scene, PvCamState cam,
        IReadOnlyDictionary<string, PvLayerImage> images, int width, int height)
    {
        foreach (var layer in scene.Layers)
        {
            if (!layer.Enabled) continue;
            if (!images.TryGetValue(layer.Id, out var image) || !image.HasContent) continue;
            DrawLayer(scene, layer, image, cam, width, height);
        }
    }

    private void DrawLayer(PvScene scene, PvLayerConfig layer, PvLayerImage image,
        PvCamState cam, int width, int height)
    {
        var tf = PvParallax.Compute(width, height, layer, cam,
            globalParallax: scene.GlobalParallaxEnabled,
            antiBleed: scene.AntiBleedGuardEnabled);

        if (!PvAmbient.NeedsSurface(layer, scene.AmbientBlendEnabled))
        {
            DrawContent(layer, image, tf, width, height, layer.Opacity);
            return;
        }

        var blend = layer.VisualBlend;
        _draw.PushSurface(width, height, clear: true);

        if (blend.RimGlow > 0.001)
        {
            // 边缘光：同一内容先模糊加色画在底下，主体画在上面 → 轮廓外透出一圈光
            _draw.PushSurface(width, height, clear: true);
            DrawContent(layer, image, tf, width, height, 1.0);
            _draw.PopSurface(new Paint2D
            {
                BlurSigma = RimBlurSigma,
                Alpha = (float)Math.Clamp(blend.RimGlow, 0, 1),
                Blend = Blend2D.Plus,
            });
        }

        DrawContent(layer, image, tf, width, height, 1.0);

        _draw.PopSurface(new Paint2D
        {
            Alpha = (float)Math.Clamp(layer.Opacity, 0, 1),
            ColorMatrix = blend.AmbientTintIntensity > 0.001
                ? PvAmbient.TintMatrix(scene.AmbientTintColor, blend.AmbientTintIntensity)
                : null,
            Shadow = blend.DropShadow > 0.001
                ? new Shadow2D(0f, (float)blend.ShadowOffsetY, ShadowBlurSigma,
                    new Color4(0f, 0f, 0f, (float)Math.Clamp(blend.DropShadow, 0, 1)))
                : null,
        });
    }

    private void DrawContent(PvLayerConfig layer, PvLayerImage image, PvLayerTransform tf,
        int width, int height, double alpha)
    {
        _draw.Save();
        _draw.SetMatrix(tf.Matrix);

        // 取景：Cover 铺满裁切 / Contain 完整可见；源尺寸未知时按整幅拉伸
        var dest = FitRect(image, layer.Cover, width, height);
        if (image.Bitmap is not null)
            _draw.DrawBitmap(image.Bitmap, dest, (float)Math.Clamp(alpha, 0, 1));
        else if (image.Solid is { } c)
            _draw.FillRect(dest, new Paint2D { Brush = new SolidBrush2D(c), Alpha = (float)Math.Clamp(alpha, 0, 1) });

        _draw.Restore();
    }

    private static Rect FitRect(PvLayerImage image, bool cover, int width, int height)
    {
        if (image.Width <= 0 || image.Height <= 0)
            return new Rect(0, 0, width, height);

        var sx = width / (float)image.Width;
        var sy = height / (float)image.Height;
        var s = cover ? Math.Max(sx, sy) : Math.Min(sx, sy);
        var w = image.Width * s;
        var h = image.Height * s;
        return new Rect((width - w) / 2f, (height - h) / 2f, w, h);
    }
}
