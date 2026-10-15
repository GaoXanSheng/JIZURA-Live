using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

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
    /// <summary>
    /// 帧等待切片：停机握手的最坏可见延迟，也是渲染线程空转节拍的粒度下限。
    /// 取 64ms 是为了压住停机时延又不至于把等待切得太碎（每次切片都有一次内核往返）。
    /// </summary>
    private const int StopSliceMs = 64;

    private const int WaitTimeoutMs = 1000;

    private readonly IntPtr _waitable;
    private readonly Stopwatch _sw = Stopwatch.StartNew();

    // 停机握手用：WaitNext 不能只盯帧等待对象，否则停机会被按在超时上等满一个节拍才被看见
    private readonly ManualResetEventSlim _stop = new(false);
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
    /// <remarks>
    /// 停机握手：<see cref="RequestStop"/> 后本方法最迟在 <see cref="StopSliceMs"/> 内返回，
    /// 不等帧等待对象的 1s 超时——否则 <see cref="JizuraLive.Render.Gpu.PresentHost.StopRender"/>
    /// 的 Join 预算会被白等一整段时间。
    /// </remarks>
    public void WaitNext()
    {
        // 两段式等待：先零超时问一次帧信号（停时机它多半早就置位了，省掉一次切片等待），
        // 没置位再以短切片等，好让停止请求及时插进来说「不用再等这一帧」
        if (_waitable != IntPtr.Zero
            && WaitForSingleObject(_waitable, 0) != 0)
            _stop.Wait(TimeSpan.FromMilliseconds(StopSliceMs));

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

    /// <summary>
    /// 请求本时钟立即结束等待（停机握手用，可重复调用）。
    /// 只影响 <see cref="WaitNext"/> 的阻塞时长，dt/FPS 统计口径不变。
    /// </summary>
    public void RequestStop() => _stop.Set();

    public void Dispose()
    {
        // 等待对象句柄归交换链所有，这里不关；只放自己的停止信号
        _stop.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}
