using System.Numerics;
using Vortice.Mathematics;

namespace JizuraLive.Pv;

public readonly record struct PvCamState(
    double X = 0,
    double Y = 0,
    double Scale = 1,
    double Rotation = 0,
    double SkewX = 0,
    double? ScaleX = null,
    double? ScaleY = null,
    double BlurSigma = 0,
    double Brighten = 0)
{
    public static PvCamState Identity => new(0, 0, 1, 0, 0, null, null, 0, 0);
}

public readonly record struct PvLayerTransform(Matrix3x2 Matrix, double BlurSigma = 0);

public readonly record struct PvLayerState(
    double OffsetX,
    double OffsetY,
    double Scale,
    double Opacity,
    double Rotation,
    PvCamState? CameraOverride = null)
{
    public static PvLayerState From(PvLayerConfig layer) =>
        new(layer.OffsetX, layer.OffsetY, layer.Scale, layer.Opacity, layer.Rotation);
}

public sealed class PvFrameState
{
    private readonly Dictionary<string, PvLayerState> _layers = new(StringComparer.Ordinal);

    public PvCamState Camera { get; set; } = PvCamState.Identity;

    public void SetLayer(string id, in PvLayerState state) => _layers[id] = state;

    public bool TryGetLayer(string id, out PvLayerState state) => _layers.TryGetValue(id, out state);
}

public static class PvParallax
{
    private const double DegToRad = Math.PI / 180.0;

    private static readonly PvParallaxConfig CameraFollow = new()
    {
        Depth = 1,
        Follow = 1,
        RotationFactor = 1,
        ScaleFactor = 1,
        Defocus = 1,
    };

    public static PvParallaxConfig? OverrideFor(PvScene scene, PvLayerConfig layer) =>
        scene.CameraAffectsCharacter && scene.IsCharacterLayer(layer) ? CameraFollow : null;

    public static PvLayerTransform Compute(float cw, float ch, PvLayerConfig layer, in PvLayerState state,
        PvCamState cam, double shakeX = 0, double shakeY = 0, bool globalParallax = true, bool antiBleed = true,
        PvParallaxConfig? parallaxOverride = null)
    {
        var cx = cw / 2f;
        var cy = ch / 2f;
        var px = parallaxOverride ?? layer.Parallax;

        var depth = globalParallax ? Math.Clamp(px.Depth, 0.0, 3.0) : 1.0;
        var follow = state.CameraOverride is not null
            ? 1.0
            : globalParallax ? Math.Clamp(px.Follow ?? px.Depth, 0.0, 3.0) : 1.0;
        var camUse = state.CameraOverride ?? cam;
        var rotFactor = globalParallax ? Math.Clamp(px.RotationFactor, 0.0, 2.0) : 1.0;
        var scaleFactor = globalParallax ? Math.Clamp(px.ScaleFactor, 0.0, 2.0) : 1.0;

        var defocus = Math.Clamp(px.Defocus, 0.0, 3.0);
        var depthBlur = depth <= 0.001 ? 1.0 : 1.0 + Math.Max(0.0, 0.7 - depth) * 2.0;
        var blur = Math.Clamp(camUse.BlurSigma, 0.0, 40.0) * follow * depthBlur * defocus;

        var camX = (camUse.X + shakeX) * follow;
        var camY = (camUse.Y + shakeY) * follow;
        var camRot = camUse.Rotation * rotFactor * follow;
        var camS = 1.0 + (camUse.Scale - 1.0) * scaleFactor * follow;
        var camSx = 1.0 + ((camUse.ScaleX ?? 1.0) - 1.0) * follow;
        var camSy = 1.0 + ((camUse.ScaleY ?? 1.0) - 1.0) * follow;
        var camSkx = camUse.SkewX * follow;

        var overscan = 1.0;
        var margin = layer.VisualBlend.AntiBleedMargin;
        if (antiBleed && margin > 0.001)
        {
            var moveRatio = Math.Max(Math.Abs(camX) / Math.Max(1f, cw), Math.Abs(camY) / Math.Max(1f, ch));
            var rotRatio = Math.Sin(Math.Abs(camRot) * DegToRad) * 0.45;
            var zoomRatio = Math.Max(0.0, 1.0 - camS);
            var needed = (moveRatio + rotRatio + zoomRatio) * 1.25;
            overscan = 1.0 + Math.Min(margin, needed);
        }

        var m = Matrix3x2.CreateTranslation(
            (float)(camX + state.OffsetX * cw - cx),
            (float)(camY + state.OffsetY * ch - cy));

        var totalRot = (float)(camRot + state.Rotation);
        if (Math.Abs(totalRot) > 0.01f)
            m = Matrix3x2.Multiply(m, Matrix3x2.CreateRotation(totalRot * (float)DegToRad));

        if (Math.Abs(camSkx) > 0.01)
            m = Matrix3x2.Multiply(m, Matrix3x2.CreateSkew((float)Math.Tan(camSkx * DegToRad), 0));

        var sx = (float)(camS * camSx * state.Scale * overscan);
        var sy = (float)(camS * camSy * state.Scale * overscan);
        if (layer.MirrorX) sx = -sx;

        m = Matrix3x2.Multiply(m, Matrix3x2.CreateScale(sx, sy));
        m = Matrix3x2.Multiply(m, Matrix3x2.CreateTranslation(cx, cy));

        return new PvLayerTransform(m, blur);
    }

    public static PvCamState LimitToLayer(float cw, float ch, PvLayerConfig layer, in PvLayerState state,
        PvCamState cam, PvLayerImage image, bool globalParallax)
    {
        if (!image.HasContent) return cam;

        var follow = globalParallax
            ? Math.Clamp(layer.Parallax.Follow ?? layer.Parallax.Depth, 0.0, 3.0)
            : 1.0;
        if (follow <= 0.001) return cam;

        var rect = PvStageCompositor.FitRect(image, layer.Cover, (int)cw, (int)ch);
        var centered = cam with { X = 0, Y = 0 };
        if (!Covers(cw, ch, layer, state, centered, rect, globalParallax))
        {
            if (!Covers(cw, ch, layer, state, centered with { Scale = 2 }, rect, globalParallax))
                return cam;

            double lo = Math.Min(2, centered.Scale), hi = 2;
            for (var i = 0; i < 14; i++)
            {
                var mid = (lo + hi) * 0.5;
                if (Covers(cw, ch, layer, state, centered with { Scale = mid }, rect, globalParallax)) hi = mid;
                else lo = mid;
            }

            centered = centered with { Scale = hi };
        }

        var full = centered with { X = cam.X, Y = cam.Y };
        if (Covers(cw, ch, layer, state, full, rect, globalParallax)) return full;

        double ok = 0, over = 1;
        for (var i = 0; i < 14; i++)
        {
            var mid = (ok + over) * 0.5;
            var test = centered with { X = cam.X * mid, Y = cam.Y * mid };
            if (Covers(cw, ch, layer, state, test, rect, globalParallax)) ok = mid;
            else over = mid;
        }

        return centered with { X = cam.X * ok, Y = cam.Y * ok };
    }

    private static bool Covers(float cw, float ch, PvLayerConfig layer, in PvLayerState state, PvCamState cam,
        Rect rect, bool globalParallax)
    {
        var tf = Compute(cw, ch, layer, state, cam, globalParallax: globalParallax, antiBleed: false);
        if (!Matrix3x2.Invert(tf.Matrix, out var inv)) return false;

        return Inside(inv, rect, 0, 0)
               && Inside(inv, rect, cw, 0)
               && Inside(inv, rect, 0, ch)
               && Inside(inv, rect, cw, ch);
    }

    private static bool Inside(Matrix3x2 inv, Rect rect, float x, float y)
    {
        var p = Vector2.Transform(new Vector2(x, y), inv);
        return p.X >= rect.X && p.X <= rect.X + rect.Width
               && p.Y >= rect.Y && p.Y <= rect.Y + rect.Height;
    }
}
