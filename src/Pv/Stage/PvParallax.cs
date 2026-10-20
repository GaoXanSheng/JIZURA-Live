using System.Numerics;

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

public readonly record struct PvLayerTransform(Matrix3x2 Matrix, double EffectiveScale, double Overscan, double BlurSigma = 0);

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

    public static PvLayerTransform Compute(float cw, float ch, PvLayerConfig layer, in PvLayerState state,
        PvCamState cam, double shakeX = 0, double shakeY = 0, bool globalParallax = true, bool antiBleed = true)
    {
        var cx = cw / 2f;
        var cy = ch / 2f;

        var depth = globalParallax ? Math.Clamp(layer.Parallax.Depth, 0.0, 3.0) : 1.0;
        var follow = state.CameraOverride is not null
            ? 1.0
            : globalParallax ? Math.Clamp(layer.Parallax.Follow ?? layer.Parallax.Depth, 0.0, 3.0) : 1.0;
        var camUse = state.CameraOverride ?? cam;
        var rotFactor = globalParallax ? Math.Clamp(layer.Parallax.RotationFactor, 0.0, 2.0) : 1.0;
        var scaleFactor = globalParallax ? Math.Clamp(layer.Parallax.ScaleFactor, 0.0, 2.0) : 1.0;

        var defocus = Math.Clamp(layer.Parallax.Defocus, 0.0, 3.0);
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

        return new PvLayerTransform(m, camS * state.Scale, overscan, blur);
    }
}
