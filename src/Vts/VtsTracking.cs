namespace JizuraLive.Vts;

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
    public static VtsTrackingState Empty => new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, false, 0, 0, 0, 0, 0);
}

public sealed class VtsTrackingProbe
{
    public const int StaleMs = 250;

    private VtsTrackingState _state;
    private long _seq;

    public void Set(in VtsTrackingState state)
    {
        _state = state;
        Interlocked.Increment(ref _seq);
    }

    public bool TryRead(int freshMs, out VtsTrackingState state)
    {
        state = _state;
        if (state.AtMs == 0) return false;
        return freshMs <= 0 || Environment.TickCount64 - state.AtMs <= freshMs;
    }

    public long Seq => Interlocked.Read(ref _seq);
}

public sealed class VtsTrackingOptions
{
    public double CameraGain { get; set; } = 0.6;

    public double HeadRange { get; set; } = 0.35;

    public double ZoomGain { get; set; } = 0.25;

    public double TiltDegrees { get; set; } = 6.0;

    public double MouthGain { get; set; } = 1.0;

    public double BlinkThreshold { get; set; } = 0.25;
    public double BlinkFlash { get; set; } = 0.8;

    public double HandGain { get; set; } = 1.0;

    public int FreshMs { get; set; } = VtsTrackingProbe.StaleMs;
}

public readonly record struct VtsEffectDrive(
    bool Active,
    double CameraX,
    double CameraY,
    double Zoom,
    double Rotation,
    double MouthEnergy,
    double Flash,
    double HandX,
    double HandY,
    bool Blink,
    long AtMs)
{
    public static VtsEffectDrive Idle => new(false, 0, 0, 1, 0, 0, 0, 0, 0, false, 0);
}

public interface IVtsEffectTarget
{
    void Apply(in VtsEffectDrive drive);
}

public static class VtsTrackingMap
{
    public const double AngleRangeDeg = 30.0;

    private static double Normalize(double v, double range, double deadZone)
    {
        if (range <= 1e-6) return 0;
        var n = v / range;
        var sign = n < 0 ? -1 : 1;
        var mag = Math.Abs(n);
        if (mag <= deadZone) return 0;
        mag = (mag - deadZone) / (1 - deadZone);
        return Math.Clamp(sign * mag, -1, 1);
    }

    public static VtsEffectDrive Compute(in VtsTrackingState s, VtsTrackingOptions opt,
        bool previousBlink, double deadZone = 0.06)
    {
        if (!s.FaceFound) return VtsEffectDrive.Idle with { AtMs = s.AtMs };

        var camX = Normalize(s.HeadX, opt.HeadRange, deadZone) * opt.CameraGain;
        var camY = -Normalize(s.HeadY, opt.HeadRange, deadZone) * opt.CameraGain;
        var zoom = 1.0 + Math.Clamp(s.HeadZ / Math.Max(opt.HeadRange, 1e-6), -1, 1) * opt.ZoomGain;

        var tilt = Normalize(s.HeadAngleZ / AngleRangeDeg, 1.0, deadZone) * opt.TiltDegrees;

        var mouth = Math.Clamp(s.MouthOpen * opt.MouthGain, 0, 1);

        var eyeOpen = Math.Min(s.EyeOpenLeft, s.EyeOpenRight);
        var blink = eyeOpen < opt.BlinkThreshold;
        var flash = blink && !previousBlink ? opt.BlinkFlash : 0.0;

        var handX = 0.0;
        var handY = 0.0;
        if (s.HandLeftFound || s.HandRightFound)
        {
            var (hx, hy) = s.HandRightFound ? (s.HandRightX, s.HandRightY) : (s.HandLeftX, s.HandLeftY);
            handX = Math.Clamp(hx * opt.HandGain, -1, 1);
            handY = Math.Clamp(hy * opt.HandGain, -1, 1);
        }

        return new VtsEffectDrive(true, camX, camY, zoom, tilt, mouth, flash, handX, handY, blink, s.AtMs);
    }
}
