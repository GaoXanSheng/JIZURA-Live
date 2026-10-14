using JizuraLive.Engine;
using SkiaSharp;

namespace JizuraLive.Pv;

/// <summary>
/// 角色动态表演与画面协同状态
/// </summary>
public readonly record struct CharacterActorPose(
    float BounceOffsetY,
    float SquashScaleX,
    float StretchScaleY,
    float FlashBoost,
    float RimGlowPulse
);

/// <summary>
/// 角色动作与画面协同控制器（解决"不会操作人物和画面打配合"的核心引擎）
/// 负责将节拍冲击、音乐能量、闪光、故障等事件转化为人物的弹性动作、高光响应与呼吸律动
/// </summary>
public static class PvCharacterActor
{
    /// <summary>
    /// 根据当前时间、BPM、小节位置与音乐能量，计算角色的实时表演姿态与画面协同参数
    /// </summary>
    /// <param name="t">当前时间（秒）</param>
    /// <param name="bpm">歌曲 BPM（如 128, 160 等，默认 120）</param>
    /// <param name="energy">当前时刻的音频能量 0.0 ~ 1.0</param>
    /// <param name="isFlashActive">画面当前是否处于白闪/闪电高潮态</param>
    /// <param name="isBeatHit">当前帧是否触发了重音打击点</param>
    public static CharacterActorPose EvaluatePose(
        double t,
        double bpm = 120.0,
        double energy = 0.5,
        bool isFlashActive = false,
        bool isBeatHit = false)
    {
        var safeBpm = Math.Clamp(bpm > 10 ? bpm : 120.0, 40.0, 280.0);
        var beatDuration = 60.0 / safeBpm;
        var beatPhase = (t % beatDuration) / beatDuration; // 0.0 ~ 1.0 在一拍内的归一化相位

        // 1. 节拍弹性律动（Beat Bounce / Squash & Stretch）
        // 在拍头（beatPhase < 0.25）快速下沉并横向微展，随后如弹簧般阻尼回弹，人物获得生命般的律动感
        float bounceY = 0f;
        float squashX = 1f;
        float stretchY = 1f;

        var bounceIntensity = (float)Math.Clamp(0.5 + energy * 0.8, 0.4, 1.4);
        if (isBeatHit)
            bounceIntensity *= 1.5f;

        if (beatPhase < 0.22)
        {
            // 拍头冲击下沉
            var p = (float)(beatPhase / 0.22);
            var sink = (float)Math.Sin(p * Math.PI) * 14f * bounceIntensity;
            bounceY = sink;
            squashX = 1.0f + (sink / 320f); // 挤压横向膨胀
            stretchY = 1.0f - (sink / 320f);
        }
        else
        {
            // 弹性回弹缓冲（带轻微过冲）
            var p = (float)((beatPhase - 0.22) / 0.78);
            var decay = (float)Math.Exp(-p * 4.0);
            var osc = (float)Math.Cos(p * Math.PI * 3.0) * decay * 5f * bounceIntensity;
            bounceY = -osc;
            squashX = 1.0f - (osc / 400f);
            stretchY = 1.0f + (osc / 400f);
        }

        // 2. 闪光协同响应（Flash Overexposure Response）
        // 画面白闪时，角色瞬间过曝高亮，与全局背景闪电完全同频
        var flashBoost = isFlashActive ? 0.65f : 0.0f;

        // 3. 脉冲边缘轮廓光（Pulsing Rim Glow）
        // 随 BPM 与能量有节律地呼吸闪耀
        var pulse = (float)((Math.Sin(t * (safeBpm / 60.0) * Math.PI) * 0.4 + 0.6) * Math.Clamp(energy * 1.2, 0.2, 1.0));

        return new CharacterActorPose(bounceY, squashX, stretchY, flashBoost, pulse);
    }

    /// <summary>
    /// 为视觉素材图层生成融合了角色节拍动作（Beat Pose）的绘制变换与高光 Paint
    /// </summary>
    public static (SKMatrix Matrix, SKPaint Paint) BuildActorTransformAndPaint(
        SKMatrix baseMatrix,
        float destCenterX, float destBottomY,
        CharacterActorPose pose,
        SKColorFilter? ambientFilter,
        double baseAlpha = 1.0)
    {
        // 围绕角色脚底/重心做 Squash & Stretch
        var actorMatrix = baseMatrix;
        actorMatrix = SKMatrix.Concat(actorMatrix, SKMatrix.CreateTranslation(destCenterX, destBottomY));
        actorMatrix = SKMatrix.Concat(actorMatrix, SKMatrix.CreateTranslation(0, pose.BounceOffsetY));
        actorMatrix = SKMatrix.Concat(actorMatrix, SKMatrix.CreateScale(pose.SquashScaleX, pose.StretchScaleY));
        actorMatrix = SKMatrix.Concat(actorMatrix, SKMatrix.CreateTranslation(-destCenterX, -destBottomY));

        var alphaByte = (byte)(Math.Clamp(baseAlpha, 0, 1) * 255);
        var paint = new SKPaint
        {
            Color = new SKColor(255, 255, 255, alphaByte),
            IsAntialias = true,
        };

        if (pose.FlashBoost > 0.05f)
        {
            // 白闪高光叠加：提升亮度通道
            var boost = pose.FlashBoost;
            var flashMatrix = new float[]
            {
                1, 0, 0, 0, boost * 255,
                0, 1, 0, 0, boost * 255,
                0, 0, 1, 0, boost * 255,
                0, 0, 0, 1, 0
            };
            using var flashFilter = SKColorFilter.CreateColorMatrix(flashMatrix);
            paint.ColorFilter = ambientFilter != null
                ? SKColorFilter.CreateCompose(flashFilter, ambientFilter)
                : flashFilter;
        }
        else if (ambientFilter != null)
        {
            paint.ColorFilter = ambientFilter;
        }

        return (actorMatrix, paint);
    }
}
