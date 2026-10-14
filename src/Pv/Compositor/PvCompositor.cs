using JizuraLive.Engine;
using SkiaSharp;

namespace JizuraLive.Pv;

/// <summary>
/// 外部素材帧提供者接口（借用位图，合成器只读不持有）
/// </summary>
public interface IPvAssetFrameProvider
{
    SKBitmap? GetFrameForLayer(string layerId);
}

/// <summary>
/// 专业级多图层 PV 视差与光影合成器
/// 负责将底衬、外部素材/角色、动效歌词、前景特效与字幕深度融合
/// 解决特效单一、人物与画面脱节无配合的痛点
/// </summary>
public sealed class PvCompositor
{
    /// <summary>
    /// 执行整帧合成
    /// </summary>
    /// <param name="outputCanvas">输出目标画布</param>
    /// <param name="scene">PV 场景配置（图层栈、视差与融合开关）</param>
    /// <param name="renderCtx">渲染上下文（时间、BPM、能量、闪光、镜头、环境色等）</param>
    /// <param name="assetProvider">外部素材位图提供者（可选）</param>
    /// <param name="renderLyricLayer">动效歌词层渲染回调 (canvas, scale) -> void</param>
    /// <param name="renderSubtitleLayer">字幕层渲染回调 (canvas) -> void</param>
    public void CompositeFrame(
        SKCanvas outputCanvas,
        PvScene scene,
        PvRenderContext renderCtx,
        IPvAssetFrameProvider? assetProvider = null,
        Action<SKCanvas, double>? renderLyricLayer = null,
        Action<SKCanvas>? renderSubtitleLayer = null)
    {
        var cw = (float)scene.Width;
        var ch = (float)scene.Height;

        // 预先评估本帧人物的动态配合姿态（节拍律动、白闪响应、脉冲轮廓光）
        var actorPose = renderCtx.EnableCharacterBeatBounce
            ? PvCharacterActor.EvaluatePose(
                renderCtx.Time,
                renderCtx.Bpm,
                renderCtx.Energy,
                renderCtx.IsFlashActive && renderCtx.EnableCharacterFlashSync,
                renderCtx.IsBeatHit)
            : new CharacterActorPose(0, 1, 1, 0, 1);

        foreach (var layer in scene.Layers)
        {
            if (!layer.Enabled || layer.Opacity <= 0.001)
                continue;

            outputCanvas.Save();
            try
            {
                // 1. 计算三维视差矩阵（Parallax Matrix）
                var parallax = PvParallaxEngine.ComputeMatrix(
                    cw, ch, layer,
                    renderCtx.Camera,
                    shx: 0, shy: 0,
                    globalParallax: scene.GlobalParallaxEnabled);

                outputCanvas.Concat(parallax.Transform);

                // 2. 根据图层类别进行高质量绘制
                switch (layer.Kind)
                {
                    case PvLayerKind.Background:
                        DrawBackgroundLayer(outputCanvas, cw, ch, layer, renderCtx);
                        break;

                    case PvLayerKind.VisualAsset:
                        if (assetProvider != null)
                        {
                            var bmp = assetProvider.GetFrameForLayer(layer.Id);
                            if (bmp != null)
                            {
                                DrawVisualAssetLayer(outputCanvas, cw, ch, layer, bmp, scene.AmbientBlendEnabled, renderCtx, actorPose);
                            }
                        }
                        break;

                    case PvLayerKind.LyricPv:
                        if (renderLyricLayer != null)
                        {
                            // 歌词动效由核心纯净渲染引擎驱动
                            renderLyricLayer(outputCanvas, 1.0);
                        }
                        break;

                    case PvLayerKind.ForegroundFx:
                        DrawForegroundFxLayer(outputCanvas, cw, ch, layer, renderCtx);
                        break;

                    case PvLayerKind.Subtitle:
                        // 字幕层：若定义在栈顶，则在此绘制（不受晃动影响）
                        renderSubtitleLayer?.Invoke(outputCanvas);
                        break;
                }
            }
            finally
            {
                outputCanvas.Restore();
            }
        }
    }

    /// <summary>
    /// 绘制背景底衬层
    /// </summary>
    private static void DrawBackgroundLayer(
        SKCanvas canvas, float cw, float ch,
        PvLayerConfig layer, PvRenderContext ctx)
    {
        var margin = (float)(layer.VisualBlend.AntiBleedMargin * Math.Max(cw, ch));
        var rect = new SKRect(-margin, -margin, cw + margin, ch + margin);

        using var paint = new SKPaint
        {
            Color = ctx.PrimaryColor.Alpha > 0 ? ctx.PrimaryColor : new SKColor(16, 16, 20),
            Style = SKPaintStyle.Fill,
        };
        canvas.DrawRect(rect, paint);
    }

    /// <summary>
    /// 绘制视觉素材/角色层（深度配合：节拍弹性律动、接触阴影、呼吸脉冲背光泛光、环境光染色）
    /// </summary>
    private static void DrawVisualAssetLayer(
        SKCanvas canvas, float cw, float ch,
        PvLayerConfig layer, SKBitmap bmp,
        bool enableAmbientBlend, PvRenderContext ctx,
        CharacterActorPose pose)
    {
        var sw = bmp.Width;
        var sh = bmp.Height;
        if (sw <= 0 || sh <= 0) return;

        // 计算设计坐标系下的目标矩形
        var dest = LiveLayer.ComputeDest(sw, sh, cw, ch, layer.Scale, layer.OffsetX, layer.OffsetY, layer.Cover);
        if (dest.Width < 1f || dest.Height < 1f) return;

        var cx = (dest.Left + dest.Right) / 2f;
        var bottomY = dest.Bottom;

        canvas.Save();
        try
        {
            // 应用角色节拍弹性律动（Squash & Stretch 在脚底/重心锚点展开）
            if (ctx.EnableCharacterBeatBounce && Math.Abs(pose.BounceOffsetY) > 0.01f)
            {
                canvas.Translate(cx, bottomY);
                canvas.Translate(0, pose.BounceOffsetY);
                canvas.Scale(pose.SquashScaleX, pose.StretchScaleY);
                canvas.Translate(-cx, -bottomY);
            }

            // 1. 绘制接触阴影（当背景偏亮或要求立体感时）
            if (layer.VisualBlend.DropShadow > 0.01 && !ctx.FastMode)
            {
                PvColorGrading.DrawSoftShadow(canvas, dest, layer.VisualBlend.DropShadow, 18.0, layer.VisualBlend.ShadowOffsetY);
            }

            // 2. 绘制呼吸脉冲背光泛光（深色高对比背景，伴随节拍呼吸）
            if (layer.VisualBlend.RimGlow > 0.01 && !ctx.FastMode)
            {
                var glowColor = ctx.AccentColor.Alpha > 0 ? ctx.AccentColor : SKColors.Cyan;
                var dynamicGlow = layer.VisualBlend.RimGlow * (pose.RimGlowPulse > 0 ? pose.RimGlowPulse : 1.0);
                PvColorGrading.DrawRimGlow(canvas, dest, glowColor, dynamicGlow, 22.0);
            }

            // 3. 素材本体绘制（结合环境光调色与白闪过曝响应）
            SKColorFilter? ambientFilter = null;
            if (enableAmbientBlend && !ctx.FastMode && layer.VisualBlend.AmbientTintIntensity > 0.01)
            {
                ambientFilter = PvColorGrading.CreateAmbientColorFilter(
                    ctx.AccentColor.Alpha > 0 ? ctx.AccentColor : ctx.PrimaryColor,
                    layer.VisualBlend.AmbientTintIntensity);
            }

            var (_, actorPaint) = PvCharacterActor.BuildActorTransformAndPaint(
                SKMatrix.Identity, cx, bottomY, pose, ambientFilter, layer.Opacity);

            using (actorPaint)
            {
                var px = bmp.PeekPixels();
                if (px != null)
                {
                    using var img = SKImage.FromPixels(px.Info, px.GetPixels(), px.RowBytes);
                    if (img != null)
                    {
                        var sampling = new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None);
                        canvas.DrawImage(img, dest, sampling, actorPaint);
                    }
                }
            }
        }
        finally
        {
            canvas.Restore();
        }
    }

    /// <summary>
    /// 绘制丰富的前景特效装饰层（节拍冲击波、动漫集中线、横向变形光斑、星尘微粒）
    /// </summary>
    private static void DrawForegroundFxLayer(
        SKCanvas canvas, float cw, float ch,
        PvLayerConfig layer, PvRenderContext ctx)
    {
        var cx = cw / 2f;
        var cy = ch / 2f;
        var color = ctx.AccentColor.Alpha > 0 ? ctx.AccentColor : SKColors.Cyan;

        if (ctx.EnableBeatVisualEffects && !ctx.FastMode)
        {
            // 1. 节拍冲击波（在重音打击时刻产生扩散）
            if (ctx.IsBeatHit)
            {
                PvEffectEngine.DrawBeatShockwave(canvas, cx, cy, 0.35, Math.Min(cw, ch) * 0.45f, color);
            }

            // 2. 高潮动漫集中线（能量充沛或重打击时呈现）
            if (ctx.Energy > 0.75 || ctx.IsBeatHit)
            {
                var burstIntensity = (ctx.Energy - 0.7) * 2.5;
                PvEffectEngine.DrawRadialBurst(canvas, cx, cy, Math.Min(cw, ch) * 0.35f, burstIntensity, color, lineCount: 32);
            }

            // 3. 电影级横向变形光斑（高潮段扫光）
            if (ctx.Energy > 0.6)
            {
                var streakIntensity = (ctx.Energy - 0.6) * 1.5;
                PvEffectEngine.DrawAnamorphicStreak(canvas, cw, cy * 0.85f, streakIntensity, color);
            }
        }

        // 4. 环境浮动微光星尘（随时随音乐轻轻浮动）
        PvEffectEngine.DrawFloatingDust(canvas, cw, ch, ctx.Time, density: 0.6 * layer.Opacity, dustColor: color);
    }
}
