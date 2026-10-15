namespace JizuraLive.Vts;

/// <summary>
/// VTS 的实时追踪快照（TrackingDataRequest 的投影）。
///
/// 为什么需要「读」：现有集成只**写** VTS（取景/参数注入/热键），所以它是个木偶——
/// 真人做什么动作、表情，画面里没有任何反应。要「用 VTS 做效果展示」，必须先能读回追踪数据：
/// 头部位移可以驱动视差运镜、口型可以驱动频谱/歌词强调、眨眼可以触发闪光、手位可以做光条/粒子原点。
/// </summary>
public readonly record struct VtsTrackingState(
    bool FaceFound,
    double HeadX,
    double HeadY,
    double HeadZ,
    double HeadAngleX,
    double HeadAngleY,
    double HeadAngleZ,
    double BodyAngleX,
    double BodyAngleZ,
    double EyeOpenLeft,
    double EyeOpenRight,
    double MouthOpen,
    double MouthSmile,
    bool HandLeftFound,
    bool HandRightFound,
    double HandLeftX,
    double HandLeftY,
    double HandRightX,
    double HandRightY,
    long AtMs)
{
    /// <summary>空快照（未连上/未开始追踪）。</summary>
    public static VtsTrackingState Empty => new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, false, 0, 0, 0, 0, 0);
}

/// <summary>
/// 追踪快照的无锁单写单读盒（写 = VTS 工作线程，读 = 渲染/驱动线程）。
/// <see cref="TryRead"/> 带新鲜度检查，停更时不喂过期姿态。
/// </summary>
public sealed class VtsTrackingProbe
{
    private VtsTrackingState _state;
    private long _seq;

    public void Set(in VtsTrackingState state)
    {
        _state = state;
        Interlocked.Increment(ref _seq);
    }

    /// <summary>取最新快照；<paramref name="freshMs"/> 内没更新过则返回 false。</summary>
    public bool TryRead(int freshMs, out VtsTrackingState state)
    {
        state = _state;
        if (state.AtMs == 0) return false;
        return freshMs <= 0 || Environment.TickCount64 - state.AtMs <= freshMs;
    }

    public long Seq => Interlocked.Read(ref _seq);
}

/// <summary>追踪 → 效果 的映射参数（面板可调）。</summary>
public sealed class VtsTrackingOptions
{
    /// <summary>头部位移驱动视差运镜的增益（0 = 不跟随）。</summary>
    public double CameraGain { get; set; } = 0.6;

    /// <summary>头部左右/上下移动的行程（VTS 归一化单位；超出按 1 截断）。</summary>
    public double HeadRange { get; set; } = 0.35;

    /// <summary>头部前后位移驱动缩放（0 = 不跟随）。</summary>
    public double ZoomGain { get; set; } = 0.25;

    /// <summary>口型驱动「能量」的增益。</summary>
    public double MouthGain { get; set; } = 1.0;

    /// <summary>眨眼触发闪光的阈值（眼开度低于它算闭眼）与强度。</summary>
    public double BlinkThreshold { get; set; } = 0.25;
    public double BlinkFlash { get; set; } = 0.8;

    /// <summary>手位驱动光条/粒子原点的增益。</summary>
    public double HandGain { get; set; } = 1.0;

    /// <summary>追踪数据的新鲜度上限（毫秒）：超过就当作「没有脸」。</summary>
    public int FreshMs { get; set; } = 500;
}

/// <summary>
/// 追踪 → 效果 的映射结果（渲染侧每帧读）。所有量都归一化到便于直接喂效果参数的范围。
/// </summary>
public readonly record struct VtsEffectDrive(
    bool Active,
    double CameraX,
    double CameraY,
    double Zoom,
    double MouthEnergy,
    double Flash,
    double HandX,
    double HandY,
    bool Blink,
    long AtMs)
{
    public static VtsEffectDrive Idle => new(false, 0, 0, 1, 0, 0, 0, 0, false, 0);
}

/// <summary>效果驱动的输出口：<see cref="VtsTrackingDriver"/> 把结果推给它（真实现刷新渲染侧状态，测试用替身）。</summary>
public interface IVtsEffectTarget
{
    void Apply(in VtsEffectDrive drive);
}

/// <summary>
/// 追踪驱动的**纯函数**核心：把一帧追踪快照映射成效果驱动量。
///
/// 单独抽出来是为了可单测——映射规则（头部死区、口型平滑、眨眼边沿触发）是本模块最容易写错、
/// 也最难在真机上复现的部分。线程/定时只是外面薄薄一层。
/// </summary>
public static class VtsTrackingMap
{
    /// <summary>把 -range..range 的位移映射到 -1..1 并带死区（手抖不该让镜头一直飘）。</summary>
    private static double Normalize(double v, double range, double deadZone)
    {
        if (range <= 1e-6) return 0;
        var n = v / range;
        var sign = n < 0 ? -1 : 1;
        var mag = Math.Abs(n);
        if (mag <= deadZone) return 0;
        // 死区外重新归一化，保证过死区后从 0 连续起步
        mag = (mag - deadZone) / (1 - deadZone);
        return Math.Clamp(sign * mag, -1, 1);
    }

    /// <summary>
    /// 计算本帧的驱动量。<paramref name="previousBlink"/> 用于眨眼**边沿**判定
    /// （持续闭眼只触发一次闪光，而不是每帧都闪）。
    /// </summary>
    public static VtsEffectDrive Compute(in VtsTrackingState s, VtsTrackingOptions opt,
        bool previousBlink, double deadZone = 0.06)
    {
        if (!s.FaceFound) return VtsEffectDrive.Idle with { AtMs = s.AtMs };

        var camX = Normalize(s.HeadX, opt.HeadRange, deadZone) * opt.CameraGain;
        // 屏幕 y 向下为正，头部上抬（VTS 的 y 向上为正）应当让镜头向上 → 取负
        var camY = -Normalize(s.HeadY, opt.HeadRange, deadZone) * opt.CameraGain;
        var zoom = 1.0 + Math.Clamp(s.HeadZ / Math.Max(opt.HeadRange, 1e-6), -1, 1) * opt.ZoomGain;

        var mouth = Math.Clamp(s.MouthOpen * opt.MouthGain, 0, 1);

        var eyeOpen = Math.Min(s.EyeOpenLeft, s.EyeOpenRight);
        var blink = eyeOpen < opt.BlinkThreshold;
        // 边沿触发：从「睁」到「闭」的那一帧给闪光，之后保持闭眼不再重复
        var flash = blink && !previousBlink ? opt.BlinkFlash : 0.0;

        var handX = 0.0;
        var handY = 0.0;
        if (s.HandLeftFound || s.HandRightFound)
        {
            var (hx, hy) = s.HandRightFound ? (s.HandRightX, s.HandRightY) : (s.HandLeftX, s.HandLeftY);
            handX = Math.Clamp(hx * opt.HandGain, -1, 1);
            handY = Math.Clamp(hy * opt.HandGain, -1, 1);
        }

        return new VtsEffectDrive(true, camX, camY, zoom, mouth, flash, handX, handY, blink, s.AtMs);
    }
}
