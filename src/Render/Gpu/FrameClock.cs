using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace JizuraLive.Render.Gpu;

public sealed class FrameClock : IDisposable
{
    private const int WaitTimeoutMs = 1000;

    private readonly IntPtr _waitable;
    private readonly IntPtr _stopHandle;
    private readonly IntPtr[] _handles;
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    private readonly ManualResetEventSlim _stop = new(false);
    private long _lastTicks;
    private double _dtAccum;
    private int _dtCount;
    private double _fpsAccum;
    private int _fpsCount;

    public double Dt { get; private set; }

    public double Fps { get; private set; }

    public long Frame { get; private set; }

    public bool Waitable { get; }

    public FrameClock(IntPtr waitable)
    {
        _waitable = waitable;
        Waitable = waitable != IntPtr.Zero;
        _stopHandle = _stop.WaitHandle.SafeWaitHandle.DangerousGetHandle();
        _handles = [_waitable, _stopHandle];
        _lastTicks = _sw.ElapsedTicks;
    }

    public void WaitNext()
    {
        if (_waitable != IntPtr.Zero)
        {
            WaitForMultipleObjects(2, _handles, false, WaitTimeoutMs);
        }
        else
        {
            WaitForSingleObject(_stopHandle, 16);
        }

        var now = _sw.ElapsedTicks;
        Dt = Frame == 0 ? 0 : (now - _lastTicks) / (double)Stopwatch.Frequency;
        _lastTicks = now;

        if (Frame > 0 && Dt > 0)
        {
            _dtAccum += Dt;
            _dtCount++;
            _fpsAccum += 1.0 / Dt;
            _fpsCount++;
            if (_dtAccum >= 0.5)
            {
                Fps = _fpsCount > 0 ? _fpsAccum / _fpsCount : 0;
                _dtAccum = 0;
                _dtCount = 0;
                _fpsAccum = 0;
                _fpsCount = 0;
            }
        }

        Frame++;
    }

    public void RequestStop() => _stop.Set();

    public void Dispose()
    {
        _stop.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForMultipleObjects(uint nCount, IntPtr[] lpHandles, bool bWaitAll, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}
