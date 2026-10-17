using System.Numerics;
using JizuraLive.Render.Draw;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JizuraLive.Pv;

public readonly record struct PvLayerImage(ID2D1Bitmap? Bitmap, Color4? Solid, int Width, int Height)
{
    public bool HasContent => Bitmap is not null || Solid is not null;

    public static PvLayerImage FromSolid(Color4 color, int width, int height) =>
        new(null, color, width, height);

    public static PvLayerImage Empty => new(null, null, 0, 0);
}

public static class PvAmbient
{
    public static Matrix5x4 TintMatrix(Color4 tint, double strength) => BlendMatrix(tint, strength, 0);

    public static double AtmosphereFar(PvParallaxConfig parallax, bool globalParallax) =>
        globalParallax ? Math.Clamp((0.9 - parallax.Depth) / 0.9, 0.0, 1.0) : 0.0;

    public static Matrix5x4 BlendMatrix(Color4 tint, double tintStrength, double atmos)
    {
        var far = Math.Clamp(atmos, 0, 1);
        var sat = (float)(1.0 - 0.35 * far);
        var mix = 1f - sat;
        var bright = (float)(1.0 - 0.12 * far);
        var pull = (float)(0.28 * far);
        var keep = 1f - pull;

        float Gain(float c) => bright * (1f + (c * 2f - 1f) * (float)Math.Clamp(tintStrength, 0, 1));

        const float Wr = 0.299f, Wg = 0.587f, Wb = 0.114f;
        var gr = Gain(tint.R);
        var gg = Gain(tint.G);
        var gb = Gain(tint.B);

        return new Matrix5x4(
            keep * gr * (sat + mix * Wr), keep * gr * mix * Wg, keep * gr * mix * Wb, 0,
            keep * gg * mix * Wr, keep * gg * (sat + mix * Wg), keep * gg * mix * Wb, 0,
            keep * gb * mix * Wr, keep * gb * mix * Wg, keep * gb * (sat + mix * Wb), 0,
            0, 0, 0, 1,
            pull * tint.R, pull * tint.G, pull * tint.B, 0);
    }

    public static bool NeedsSurface(PvLayerConfig layer, bool ambientEnabled)
    {
        if (!ambientEnabled) return false;
        var b = layer.VisualBlend;
        return b.AmbientTintIntensity > 0.001 || b.RimGlow > 0.001 || b.DropShadow > 0.001;
    }
}

public sealed class PvStageCompositor
{
    private const float RimBlurSigma = 26f;

    private const float ShadowBlurSigma = 14f;

    private readonly Immediate2D _draw;

    public PvStageCompositor(Immediate2D draw) => _draw = draw;

    public static readonly string[] FixedZones = { "lyrics_pv", "subtitle_top" };

    public void Composite(PvScene scene, PvCamState cam,
        IReadOnlyDictionary<string, PvLayerImage> images, int width, int height)
    {
        foreach (var layer in scene.Layers)
        {
            if (!layer.Enabled) continue;
            if (!images.TryGetValue(layer.Id, out var image) || !image.HasContent) continue;

            var state = scene.Frame.TryGetLayer(layer.Id, out var frame) ? frame : PvLayerState.From(layer);
            CompositeLayer(scene, layer, state, image, cam, width, height);
        }

        DrawBrighten(cam, width, height);

        foreach (var id in FixedZones)
            CompositeFixedZone(id, images, width, height);
    }

    public void CompositeLayer(PvScene scene, PvLayerConfig layer, in PvLayerState state, PvLayerImage image,
        PvCamState cam, int width, int height)
    {
        var tf = PvParallax.Compute(width, height, layer, state, cam,
            globalParallax: scene.GlobalParallaxEnabled,
            antiBleed: scene.AntiBleedGuardEnabled);

        var atmos = scene.AmbientBlendEnabled
            ? Math.Clamp(scene.Atmosphere, 0, 1) * PvAmbient.AtmosphereFar(layer.Parallax, scene.GlobalParallaxEnabled)
            : 0;

        if (!PvAmbient.NeedsSurface(layer, scene.AmbientBlendEnabled)
            && tf.BlurSigma <= 0.05 && atmos <= 0.001)
        {
            DrawContent(layer, image, tf, width, height, state.Opacity);
            return;
        }

        var blend = layer.VisualBlend;
        _draw.PushSurface(width, height, clear: true);

        if (blend.RimGlow > 0.001)
        {
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
            Alpha = (float)Math.Clamp(state.Opacity, 0, 1),
            BlurSigma = (float)tf.BlurSigma,
            ColorMatrix = blend.AmbientTintIntensity > 0.001 || atmos > 0.001
                ? PvAmbient.BlendMatrix(scene.AmbientTintColor, blend.AmbientTintIntensity, atmos)
                : null,
            Shadow = blend.DropShadow > 0.001
                ? new Shadow2D(0f, (float)blend.ShadowOffsetY, ShadowBlurSigma,
                    new Color4(0f, 0f, 0f, (float)Math.Clamp(blend.DropShadow, 0, 1)))
                : null,
        });
    }

    public void DrawBrighten(PvCamState cam, int width, int height)
    {
        var lift = Math.Clamp(cam.Brighten, 0, 1);
        if (lift <= 0.002) return;

        _draw.Save();
        _draw.SetMatrix(Matrix3x2.Identity);
        _draw.FillRect(new Rect(0, 0, width, height), new Paint2D
        {
            Brush = new SolidBrush2D(new Color4(1f, 1f, 1f, 1f)),
            Alpha = (float)lift,
            Blend = Blend2D.Plus,
        });
        _draw.Restore();
    }

    public void CompositeFixedZone(string id, IReadOnlyDictionary<string, PvLayerImage> images,
        int width, int height)
    {
        if (string.Equals(id, "lyrics_pv", StringComparison.Ordinal)) return;
        if (!images.TryGetValue(id, out var zone) || !zone.HasContent) return;
        var dest = FitRect(zone, false, width, height);
        if (zone.Bitmap is not null) _draw.DrawBitmap(zone.Bitmap, dest, 1f);
    }

    private void DrawContent(PvLayerConfig layer, PvLayerImage image, PvLayerTransform tf,
        int width, int height, double alpha)
    {
        _draw.Save();
        _draw.SetMatrix(tf.Matrix);

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
