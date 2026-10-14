using JizuraLive.Engine;
using SkiaSharp;

namespace JizuraLive.Pv;

/// <summary>
/// 视差矩阵计算结果
/// </summary>
public readonly record struct ParallaxMatrixResult(
    SKMatrix Transform,
    double EffectiveScale,
    double OverscanScale,
    float CenterX,
    float CenterY
);

/// <summary>
/// 专业三维视差运镜与防露底数学引擎
/// </summary>
public static class PvParallaxEngine
{
    private const double DegToRad = Math.PI / 180.0;

    /// <summary>
    /// 为指定图层计算包含视差深度、几何偏移与防露底扩展的完整投影矩阵
    /// </summary>
    /// <param name="cw">画布设计宽度</param>
    /// <param name="ch">画布设计高度</param>
    /// <param name="layer">图层配置</param>
    /// <param name="cam">当前镜头运镜状态（若无镜头则传 null）</param>
    /// <param name="shx">镜头抖动偏移 X</param>
    /// <param name="shy">镜头抖动偏移 Y</param>
    /// <param name="globalParallax">是否启用全局视差</param>
    public static ParallaxMatrixResult ComputeMatrix(
        float cw, float ch,
        PvLayerConfig layer,
        CamState? cam,
        double shx = 0, double shy = 0,
        bool globalParallax = true)
    {
        var cx = cw / 2f;
        var cy = ch / 2f;

        var depth = globalParallax ? Math.Clamp(layer.Parallax.Depth, 0.0, 3.0) : 1.0;
        var rotFactor = globalParallax ? Math.Clamp(layer.Parallax.RotationFactor, 0.0, 2.0) : 1.0;
        var scaleFactor = globalParallax ? Math.Clamp(layer.Parallax.ScaleFactor, 0.0, 2.0) : 1.0;

        // 1. 镜头分量（乘以该层的深度与阻尼系数）
        var camX = 0.0;
        var camY = 0.0;
        var camRot = 0.0;
        var camS = 1.0;
        var camSx = 1.0;
        var camSy = 1.0;
        var camSkx = 0.0;

        if (cam != null)
        {
            camX = (cam.X + shx) * depth;
            camY = (cam.Y + shy) * depth;
            camRot = cam.Rot * rotFactor * depth;
            camS = 1.0 + (cam.S - 1.0) * scaleFactor * depth;
            camSx = 1.0 + ((cam.Sx ?? 1.0) - 1.0) * depth;
            camSy = 1.0 + ((cam.Sy ?? 1.0) - 1.0) * depth;
            camSkx = cam.Skx * depth;
        }

        // 2. 防露底超取景扩展（Anti-Bleed Overscan Guard）
        // 当镜头位移或旋转剧烈时，背景/主体层自动叠加平滑的安全放大系数，彻底消除黑边穿帮
        var overscan = 1.0;
        if (layer.VisualBlend.AntiBleedMargin > 0.001)
        {
            var moveRatio = Math.Max(Math.Abs(camX) / Math.Max(1f, cw), Math.Abs(camY) / Math.Max(1f, ch));
            var rotRad = Math.Abs(camRot) * DegToRad;
            var rotRatio = Math.Sin(rotRad) * 0.45;
            var zoomRatio = Math.Max(0.0, 1.0 - camS); // 缩小时更易露底

            var neededMargin = (moveRatio + rotRatio + zoomRatio) * 1.25;
            var clampedMargin = Math.Min(layer.VisualBlend.AntiBleedMargin, neededMargin);
            overscan = 1.0 + clampedMargin;
        }

        // 3. 构建复合变换矩阵（中心对齐机制）
        // 顺序：画布中心平移 -> 镜头位移+图层偏移 -> 旋转 -> 斜切 -> 缩放 -> 镜像 -> 原点复位
        var matrix = SKMatrix.CreateTranslation(
            (float)(cx + camX + layer.OffsetX * cw),
            (float)(cy + camY + layer.OffsetY * ch));

        var totalRot = (float)(camRot + layer.Rotation);
        if (Math.Abs(totalRot) > 0.01f)
            matrix = SKMatrix.Concat(matrix, SKMatrix.CreateRotationDegrees(totalRot));

        if (Math.Abs(camSkx) > 0.01)
            matrix = SKMatrix.Concat(matrix, SKMatrix.CreateSkew((float)Math.Tan(camSkx * DegToRad), 0));

        var totalSx = (float)(camS * camSx * layer.Scale * overscan);
        var totalSy = (float)(camS * camSy * layer.Scale * overscan);

        if (layer.MirrorX)
            totalSx = -totalSx;

        matrix = SKMatrix.Concat(matrix, SKMatrix.CreateScale(totalSx, totalSy));
        matrix = SKMatrix.Concat(matrix, SKMatrix.CreateTranslation(-cx, -cy));

        return new ParallaxMatrixResult(matrix, camS * layer.Scale, overscan, cx, cy);
    }
}
