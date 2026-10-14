using JizuraLive.Engine;
using SkiaSharp;

namespace JizuraLive.Pv;

/// <summary>
/// PV 场景单帧合成上下文（承载音画、节拍、运镜与全局动效状态）
/// </summary>
public sealed class PvRenderContext
{
    /// <summary>设计画布宽度</summary>
    public int Width { get; set; } = 1920;

    /// <summary>设计画布高度</summary>
    public int Height { get; set; } = 1080;

    /// <summary>当前时间轴时间（秒）</summary>
    public double Time { get; set; }

    /// <summary>当前歌曲的 BPM（节拍速度，用于驱动角色律动与特效周期）</summary>
    public double Bpm { get; set; } = 120.0;

    /// <summary>当前时刻的音频能量 0.0 ~ 1.0（由 JAudio 能量包或分析得出）</summary>
    public double Energy { get; set; } = 0.5;

    /// <summary>当前帧是否处于全局闪光/白闪态（画面与角色同频过曝）</summary>
    public bool IsFlashActive { get; set; }

    /// <summary>当前帧是否触发了重大鼓点打击（用于爆发冲击波、RGB色散与重击下沉）</summary>
    public bool IsBeatHit { get; set; }

    /// <summary>当前 cut 的主摄像机运镜状态</summary>
    public CamState? Camera { get; set; }

    /// <summary>场景主底色（由当前 Scheme 定义）</summary>
    public SKColor PrimaryColor { get; set; } = new SKColor(16, 16, 20);

    /// <summary>场景强调色/氛围色（用于边缘光与泛光，由当前 Scheme 定义）</summary>
    public SKColor AccentColor { get; set; } = SKColors.Cyan;

    /// <summary>快速模式（降级阴影/泛光，保障低配机器帧率）</summary>
    public bool FastMode { get; set; }

    /// <summary>是否启用角色节拍弹性律动（Squash & Stretch）</summary>
    public bool EnableCharacterBeatBounce { get; set; } = true;

    /// <summary>是否启用角色与画面的闪光/光影协同</summary>
    public bool EnableCharacterFlashSync { get; set; } = true;

    /// <summary>是否启用节拍冲击波与集中线特效</summary>
    public bool EnableBeatVisualEffects { get; set; } = true;
}
