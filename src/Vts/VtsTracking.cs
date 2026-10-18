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

    private VtsTrackingState _state;
    private long _seq;

    public void Set(in VtsTrackingState state)
    {
        _state = state;
        Interlocked.Increment(ref _seq);
    }

}

public static class VtsTrackingMap
{
    public const double AngleRangeDeg = 30.0;

}
