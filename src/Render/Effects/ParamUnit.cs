namespace JizuraLive.Render.Effects;

/// <summary>
/// 参数单位（量纲）：「同一个值、不同含义」不误接靠它，而不是靠数值范围——
/// 0.5 是比例还是像素，只有声明说得清（契约 §13.6「单位」、L11「同值不同单位不误接」）。
///
/// 放在 <c>JizuraLive.Render.Effects</c> 而不是 Pv 层：参数 schema 的真相在 <see cref="EffectCatalog"/>，
/// 而 Pv.Director 已经引用本命名空间（DirectorProject.cs 的 using），反向放会造出 Render → Pv 的依赖，成环。
/// </summary>
public enum ParamUnit
{
    /// <summary>
    /// 未注册 / 未验证：这个参数的语义量纲还没有被标注过（首批只标了 M6 闸门与解锁路径用到的键）。
    /// 它**不是一种量纲**，所以只能作为 Remap 的输入（显式换算会给出输出单位）；
    /// 其余位置（Add/Multiply/Clamp/绑定目标）一律拒绝——宁可拒绝接线，也不静默按错单位算。
    /// </summary>
    Unspecified = 0,

    /// <summary>无量纲纯数：加成、倍率因子这类"乘上去/加上去"的数（如 LFO 振幅、bloom intensity 的倍率）。</summary>
    Dimensionless = 1,

    /// <summary>
    /// 比例（归一化到 0..1 的占比）：音频频带能量、亮度阈值这类"占多少"。
    /// 与 <see cref="Dimensionless"/> 分开：两个比例相加的含义，与"两个倍率相加"不是一回事。
    /// </summary>
    Ratio = 2,

    /// <summary>设计画布像素：半径、位移、块大小这类与分辨率绑定的长度。</summary>
    Pixels = 3,

    /// <summary>角度（度）：旋转、方向这类。</summary>
    Degrees = 4,

    /// <summary>秒：时间量（与全链路 double 秒同一口径），Time 节点的输出单位。</summary>
    Seconds = 5,
}
