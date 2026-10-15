using System.Numerics;

namespace JizuraLive.Pv;

/// <summary>镜头状态（PV 舞台自己的运镜，与引擎 cut 镜头解耦）。</summary>
public readonly record struct PvCamState(
    double X = 0,
    double Y = 0,
    double Scale = 1,
    double Rotation = 0,
    double SkewX = 0,
    double? ScaleX = null,
    double? ScaleY = null)
{
    /// <summary>
    /// 静息镜头。**必须显式构造**：对 record struct 而言 <c>new()</c> 产生的是零值
    /// （Scale 会是 0 而不是主构造函数的默认 1），那会让每一层的缩放都塌成 0、整幅画面空白。
    /// </summary>
    public static PvCamState Identity => new(0, 0, 1, 0, 0, null, null);
}

/// <summary>一层的最终投影：矩阵 + 有效缩放 + 实际用到的防露底系数。</summary>
public readonly record struct PvLayerTransform(Matrix3x2 Matrix, double EffectiveScale, double Overscan);

/// <summary>
/// 视差运镜与防露底数学（<see cref="System.Numerics.Matrix3x2"/> 版）。
///
/// 与旧的 Skia 版（<c>PvParallaxEngine</c>）是同一套公式，换成新渲染核心的矩阵类型——
/// 混合路线下合成器跑在 D2D/D3D11 上，不该再拖一个 Skia 矩阵进来。
///
/// 视差的意义：真实镜头里远景几乎不动、近景飞速掠过。给每层一个 <c>Depth</c>，
/// 镜头位移/旋转/缩放都按深度加权，画面才有纵深而不是"一块铁板一起晃"。
/// </summary>
public static class PvParallax
{
    private const double DegToRad = Math.PI / 180.0;

    /// <summary>
    /// 计算一层的完整投影矩阵。
    /// </summary>
    /// <param name="cw">画布宽（设计像素）</param>
    /// <param name="ch">画布高（设计像素）</param>
    /// <param name="layer">图层配置</param>
    /// <param name="cam">镜头状态</param>
    /// <param name="shakeX">镜头抖动偏移 X（像素）</param>
    /// <param name="shakeY">镜头抖动偏移 Y（像素）</param>
    /// <param name="globalParallax">false = 所有层按深度 1 处理（关掉视差）</param>
    /// <param name="antiBleed">是否启用防露底超取景</param>
    public static PvLayerTransform Compute(float cw, float ch, PvLayerConfig layer, PvCamState cam,
        double shakeX = 0, double shakeY = 0, bool globalParallax = true, bool antiBleed = true)
    {
        var cx = cw / 2f;
        var cy = ch / 2f;

        var depth = globalParallax ? Math.Clamp(layer.Parallax.Depth, 0.0, 3.0) : 1.0;
        var rotFactor = globalParallax ? Math.Clamp(layer.Parallax.RotationFactor, 0.0, 2.0) : 1.0;
        var scaleFactor = globalParallax ? Math.Clamp(layer.Parallax.ScaleFactor, 0.0, 2.0) : 1.0;

        // 镜头分量按该层深度与阻尼系数加权
        var camX = (cam.X + shakeX) * depth;
        var camY = (cam.Y + shakeY) * depth;
        var camRot = cam.Rotation * rotFactor * depth;
        var camS = 1.0 + (cam.Scale - 1.0) * scaleFactor * depth;
        var camSx = 1.0 + ((cam.ScaleX ?? 1.0) - 1.0) * depth;
        var camSy = 1.0 + ((cam.ScaleY ?? 1.0) - 1.0) * depth;
        var camSkx = cam.SkewX * depth;

        // 防露底：位移/旋转/缩小越剧烈，需要越多超取景
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

        // 顺序：中心平移 → 镜头位移 + 图层偏移 → 旋转 → 斜切 → 缩放 → 原点复位
        var m = Matrix3x2.CreateTranslation(
            (float)(cx + camX + layer.OffsetX * cw),
            (float)(cy + camY + layer.OffsetY * ch));

        var totalRot = (float)(camRot + layer.Rotation);
        if (Math.Abs(totalRot) > 0.01f)
            m = Matrix3x2.Multiply(m, Matrix3x2.CreateRotation(totalRot * (float)DegToRad));

        if (Math.Abs(camSkx) > 0.01)
            m = Matrix3x2.Multiply(m, Matrix3x2.CreateSkew((float)Math.Tan(camSkx * DegToRad), 0));

        var sx = (float)(camS * camSx * layer.Scale * overscan);
        var sy = (float)(camS * camSy * layer.Scale * overscan);
        if (layer.MirrorX) sx = -sx;

        m = Matrix3x2.Multiply(m, Matrix3x2.CreateScale(sx, sy));
        m = Matrix3x2.Multiply(m, Matrix3x2.CreateTranslation(-cx, -cy));

        return new PvLayerTransform(m, camS * layer.Scale, overscan);
    }
}
