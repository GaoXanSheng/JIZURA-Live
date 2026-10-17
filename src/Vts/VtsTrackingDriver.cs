using JizuraLive.Logging;

namespace JizuraLive.Vts;

public sealed class VtsTrackingDriver : IDisposable
{
    private const int TickMs = 33;

    private readonly VtsTrackingProbe _probe;
    private readonly IVtsEffectTarget _target;
    private Thread? _thread;
    private volatile bool _running;
    private bool _prevBlink;

    public VtsTrackingOptions Options { get; set; }

    public long Ticks { get; private set; }

    public VtsEffectDrive Last { get; private set; } = VtsEffectDrive.Idle;

    public VtsTrackingDriver(VtsTrackingProbe probe, IVtsEffectTarget target, VtsTrackingOptions? options = null)
    {
        _probe = probe;
        _target = target;
        Options = options ?? new VtsTrackingOptions();
    }

    public void Start()
    {
        if (_thread != null) return;
        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "VtsTracking",
        };
        _thread.Start();
    }

    public void Stop()
    {
        _running = false;
        var t = _thread;
        _thread = null;
        if (t is { IsAlive: true } && t != Thread.CurrentThread) t.Join(1000);
        try
        {
            _target.Apply(VtsEffectDrive.Idle);
        }
        catch (Exception ex)
        {
            Log.Debug("[VtsTracking] 归位失败：" + ex.Message);
        }
    }

    public void Dispose() => Stop();

    private void Loop()
    {
        var last = Stopwatch_Timestamp();
        while (_running)
        {
            try
            {
                if (_probe.TryRead(Options.FreshMs, out var state))
                {
                    var drive = VtsTrackingMap.Compute(state, Options, _prevBlink);
                    _prevBlink = drive.Blink;
                    Last = drive;
                    _target.Apply(drive);
                }
                else if (Last.Active)
                {
                    _prevBlink = false;
                    Last = VtsEffectDrive.Idle;
                    _target.Apply(Last);
                }

                Ticks++;
            }
            catch (Exception ex)
            {
                Log.Warn("[VtsTracking] 一拍出错（继续）：" + ex.Message);
            }

            var next = last + TickMs * (long)(System.Diagnostics.Stopwatch.Frequency / 1000.0);
            while (_running)
            {
                var remainMs = (next - Stopwatch_Timestamp()) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                if (remainMs <= 0) break;
                if (remainMs > 2) Thread.Sleep(1);
                else Thread.SpinWait(64);
            }

            last = Stopwatch_Timestamp();
        }
    }

    private static long Stopwatch_Timestamp() => System.Diagnostics.Stopwatch.GetTimestamp();
}
