using System.Diagnostics;
using System.Runtime.InteropServices;

namespace JizuraLive.Render.Gpu;

/// <summary>
/// 帧节奏：以交换链的帧延迟等待对象为节拍源，取代旧链路里的 Stopwatch 定时器。
///
/// 为什么这样才对：带 FrameLatencyWaitableObject 的 flip 交换链，句柄在「交换链可以再收一帧」
/// 时被置信号——也就是 DWM 已消费上一帧、后缓冲空出来的时刻。渲染线程 Wait 该句柄再画，
/// 画完 Present(1,0)，整条节奏天然锁在显示器刷新上，既不会空转烧 CPU，也不会因软件定时器
/// 抖动而堆积多帧（延迟恒定 1 帧）。
///
/// 无等待对象时（交换链不支持/CPU 回退）：退回 Stopwatch 自计时，dt/FPS 统计口径不变。
/// </summary>
public sealed class FrameClock : IDisposable
{
    private const int WaitTimeoutMs = 1000;

    private readonly IntPtr _waitable;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private long _lastTicks;
    private double _dtAccum;
    private int _dtCount;
    private double _fpsAccum;
    private int _fpsCount;

    /// <summary>距上一帧的秒数（首帧为 0）。</summary>
    public double Dt { get; private set; }

    /// <summary>平滑后的帧率（每 ~0.5s 刷新一次）。</summary>
    public double Fps { get; private set; }

    /// <summary>已呈现帧数。</summary>
    public long Frame { get; private set; }

    /// <summary>本帧是否由等待对象唤醒（false = Stopwatch 回退节拍）。</summary>
    public bool Waitable { get; }

    public FrameClock(IntPtr waitable)
    {
        _waitable = waitable;
        Waitable = waitable != IntPtr.Zero;
        _lastTicks = _sw.ElapsedTicks;
    }

    /// <summary>阻塞到下一帧可以开始时返回，并更新 dt/FPS 统计。</summary>
    public void WaitNext()
    {
        if (_waitable != IntPtr.Zero)
            WaitForSingleObject(_waitable, WaitTimeoutMs);

        var now = _sw.ElapsedTicks;
        // 首帧没有「上一帧」：dt 置 0，否则按 dt 积分的动画会在第一帧跳一下
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

    public void Dispose()
    {
        // 等待对象句柄归交换链所有，这里不关；本类只做节拍，无需释放资源。
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}
