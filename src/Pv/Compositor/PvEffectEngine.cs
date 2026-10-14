using SkiaSharp;

namespace JizuraLive.Pv;

/// <summary>
/// 专业级 PV 视觉特效库（解决特效单一问题：节拍冲击波、RGB色散、电影级变形光斑、动漫集中线、Glitch切片）
/// </summary>
public static class PvEffectEngine
{
    private const double DegToRad = Math.PI / 180.0;

    /// <summary>
    /// 绘制节拍冲击波（Beat Shockwave）：在重音或鼓点处从中心向外扩散的高光能量环
    /// </summary>
    /// <param name="canvas">画布</param>
    /// <param name="cx">冲击中心 X</param>
    /// <param name="cy">冲击中心 Y</param>
    /// <param name="progress">冲击波生命周期 0.0 ~ 1.0（0=爆发瞬间, 1=消散）</param>
    /// <param name="maxRadius">最大扩散半径</param>
    /// <param name="shockColor">冲击光环颜色</param>
    public static void DrawBeatShockwave(
        SKCanvas canvas,
        float cx, float cy,
        double progress,
        float maxRadius,
        SKColor shockColor)
    {
        var p = (float)Math.Clamp(progress, 0.0, 1.0);
        if (p <= 0.001f || p >= 0.999f) return;

        // 缓动膨胀曲线（OutCubic）
        var easeRadius = (float)(1.0 - Math.Pow(1.0 - p, 3)) * maxRadius;
        // 强度衰减（前半段亮，后半段迅速透明）
        var alpha = (byte)((1.0f - p) * Math.Sin(p * Math.PI) * 220);
        if (alpha < 3) return;

        var ringThickness = Math.Max(2f, (1.0f - p) * 14f);

        using var ringPaint = new SKPaint
        {
            Color = new SKColor(shockColor.Red, shockColor.Green, shockColor.Blue, alpha),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = ringThickness,
            IsAntialias = true,
            BlendMode = SKBlendMode.Screen,
        };

        canvas.DrawCircle(cx, cy, easeRadius, ringPaint);
    }

    /// <summary>
    /// 绘制动漫高潮集中线（Radial Burst / Speedlines）：营造强烈的视觉冲力与情绪聚焦
    /// </summary>
    public static void DrawRadialBurst(
        SKCanvas canvas,
        float cx, float cy,
        float radius,
        double intensity,
        SKColor burstColor,
        int lineCount = 36)
    {
        var k = (float)Math.Clamp(intensity, 0.0, 1.0);
        if (k < 0.02f) return;

        var a = (byte)(k * 160);
        using var linePaint = new SKPaint
        {
            Color = new SKColor(burstColor.Red, burstColor.Green, burstColor.Blue, a),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
            BlendMode = SKBlendMode.Plus,
        };

        var angleStep = 360.0 / lineCount;
        for (var i = 0; i < lineCount; i++)
        {
            // 随机长度与宽度，产生有机爆发感
            var pseudoRng = Math.Sin(i * 12.9898) * 43758.5453;
            var frac = (float)(pseudoRng - Math.Floor(pseudoRng));
            if (frac < 0.35f) continue; // 随机疏密

            var baseAngle = i * angleStep;
            var rad1 = (baseAngle - 0.8) * DegToRad;
            var rad2 = (baseAngle + 0.8) * DegToRad;

            var rInner = radius * (0.35f + frac * 0.2f);
            var rOuter = radius * 1.5f;

            using var path = new SKPath();
            path.MoveTo((float)(cx + Math.Cos(rad1) * rInner), (float)(cy + Math.Sin(rad1) * rInner));
            path.LineTo((float)(cx + Math.Cos(rad1) * rOuter), (float)(cy + Math.Sin(rad1) * rOuter));
            path.LineTo((float)(cx + Math.Cos(rad2) * rOuter), (float)(cy + Math.Sin(rad2) * rOuter));
            path.LineTo((float)(cx + Math.Cos(rad2) * rInner), (float)(cy + Math.Sin(rad2) * rInner));
            path.Close();

            canvas.DrawPath(path, linePaint);
        }
    }

    /// <summary>
    /// 绘制电影级横向变形镜头光晕（Anamorphic Streak / Flare）：赛博朋克与科幻抒情必备
    /// </summary>
    public static void DrawAnamorphicStreak(
        SKCanvas canvas,
        float cw, float cy,
        double intensity,
        SKColor flareColor)
    {
        var k = (float)Math.Clamp(intensity, 0.0, 1.0);
        if (k < 0.02f) return;

        var a = (byte)(k * 180);
        var halfHeight = Math.Max(2f, k * 28f);

        var rect = new SKRect(0, cy - halfHeight, cw, cy + halfHeight);

        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(0, cy - halfHeight),
            new SKPoint(0, cy + halfHeight),
            new[] { SKColors.Transparent, new SKColor(flareColor.Red, flareColor.Green, flareColor.Blue, a), SKColors.Transparent },
            new[] { 0.0f, 0.5f, 1.0f },
            SKShaderTileMode.Clamp);

        using var paint = new SKPaint
        {
            Shader = shader,
            BlendMode = SKBlendMode.Plus,
            IsAntialias = true,
        };

        canvas.DrawRect(rect, paint);
    }

    /// <summary>
    /// 绘制浮游氛围微光粒子（Floating Dust / Bokeh）：随时间优雅浮动的粒子星尘
    /// </summary>
    public static void DrawFloatingDust(
        SKCanvas canvas,
        float cw, float ch,
        double t,
        double density,
        SKColor dustColor,
        int count = 28)
    {
        var d = Math.Clamp(density, 0.0, 1.0);
        if (d < 0.05) return;

        var total = Math.Max(4, (int)(count * d));
        using var paint = new SKPaint
        {
            Color = dustColor,
            IsAntialias = true,
            BlendMode = SKBlendMode.Screen,
        };

        for (var i = 0; i < total; i++)
        {
            var seed = i * 19.37;
            var px = (float)(((Math.Sin(t * 0.3 + seed) * 0.5 + 0.5) * cw + seed * 47) % cw);
            var py = (float)(((Math.Cos(t * 0.2 + seed * 1.5) * 0.5 + 0.5) * ch + seed * 31) % ch);
            var pAlpha = (byte)((Math.Sin(t * 1.2 + seed) * 0.4 + 0.6) * 140 * d);

            paint.Color = new SKColor(dustColor.Red, dustColor.Green, dustColor.Blue, pAlpha);
            var size = (float)(2.0 + Math.Sin(seed) * 2.0);
            canvas.DrawCircle(px, py, size, paint);
        }
    }

    /// <summary>
    /// RGB 色散分离偏移矩阵（用于鼓点爆炸瞬间的色差抽搐）
    /// </summary>
    public static (float RedOffsetX, float BlueOffsetX) CalculateRgbSplit(double beatHitIntensity, double maxOffset = 18.0)
    {
        var k = (float)Math.Clamp(beatHitIntensity, 0.0, 1.0);
        var off = (float)(k * maxOffset);
        return (-off, off);
    }
}
