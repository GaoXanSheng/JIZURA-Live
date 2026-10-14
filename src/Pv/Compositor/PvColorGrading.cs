using SkiaSharp;

namespace JizuraLive.Pv;

/// <summary>
/// 环境光调色与光影融合工具（使外部素材与 PV 场景自然融为一体，告别生硬贴片感）
/// </summary>
public static class PvColorGrading
{
    /// <summary>
    /// 生成环境光融入颜色矩阵滤镜（ColorMatrix）
    /// 将场景主色调（Ambient Color）按强度混合进素材中，统一色调倾向。
    /// </summary>
    /// <param name="ambientColor">场景主色调（来自当前工程 Scheme 的 Accent 或 Background）</param>
    /// <param name="intensity">融合强度 0.0 ~ 1.0（建议 0.15 ~ 0.25）</param>
    public static SKColorFilter? CreateAmbientColorFilter(SKColor ambientColor, double intensity)
    {
        var k = (float)Math.Clamp(intensity, 0.0, 1.0);
        if (k < 0.01f) return null;

        var rNorm = ambientColor.Red / 255f;
        var gNorm = ambientColor.Green / 255f;
        var bNorm = ambientColor.Blue / 255f;

        // 柔和染色矩阵：主对角线保留 (1 - k)，同时引入环境色贡献
        var inv = 1.0f - k;
        var m = new float[]
        {
            inv + k * rNorm, 0, 0, 0, 0,
            0, inv + k * gNorm, 0, 0, 0,
            0, 0, inv + k * bNorm, 0, 0,
            0, 0, 0, 1, 0,
        };

        return SKColorFilter.CreateColorMatrix(m);
    }

    /// <summary>
    /// 绘制接触软阴影（在浅色或丰富背景下提供物理落地感）
    /// </summary>
    public static void DrawSoftShadow(
        SKCanvas canvas,
        SKRect destRect,
        double opacity,
        double blurRadius = 16.0,
        double offsetY = 12.0)
    {
        var a = (byte)(Math.Clamp(opacity, 0.0, 1.0) * 255);
        if (a < 2) return;

        var shadowRect = new SKRect(
            destRect.Left + 8f,
            (float)(destRect.Top + offsetY),
            destRect.Right - 8f,
            (float)(destRect.Bottom + offsetY));

        using var shadowPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, a),
            ImageFilter = SKImageFilter.CreateBlur((float)blurRadius / 2f, (float)blurRadius / 2f),
            IsAntialias = true,
        };

        canvas.DrawRoundRect(shadowRect, 16f, 16f, shadowPaint);
    }

    /// <summary>
    /// 绘制背光泛光（深色背景下增强主体边缘轮廓感）
    /// </summary>
    public static void DrawRimGlow(
        SKCanvas canvas,
        SKRect destRect,
        SKColor glowColor,
        double glowIntensity,
        double blurRadius = 24.0)
    {
        var a = (byte)(Math.Clamp(glowIntensity, 0.0, 1.0) * 180);
        if (a < 2) return;

        using var glowPaint = new SKPaint
        {
            Color = new SKColor(glowColor.Red, glowColor.Green, glowColor.Blue, a),
            ImageFilter = SKImageFilter.CreateBlur((float)blurRadius / 2f, (float)blurRadius / 2f),
            BlendMode = SKBlendMode.Screen,
            IsAntialias = true,
        };

        canvas.DrawRoundRect(destRect, 12f, 12f, glowPaint);
    }
}
