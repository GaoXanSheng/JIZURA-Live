using JizuraLive.Logging;

namespace JizuraLive.Vts;

/// <summary>
/// 追踪驱动：按 ~30Hz 读 <see cref="VtsTrackingProbe"/>，映射成效果驱动量后推给
/// <see cref="IVtsEffectTarget"/>。
///
/// 为什么单独一条线程而不是塞进渲染线程：追踪数据的到达节奏（VTS 的 30–60Hz）与渲染节奏无关，
/// 而且映射本身是纯计算、不需要 GPU 上下文。渲染侧只读「最新一帧驱动量」，读不到就用静息值。
///
/// 映射规则全在 <see cref="VtsTrackingMap.Compute"/> 里（纯函数、可单测），本类只管节拍与生命周期。
/// </summary>
public sealed class VtsTrackingDriver : IDisposable
{
    private const int TickMs = 33;

    private readonly VtsTrackingProbe _probe;
    private readonly IVtsEffectTarget _target;
    private Thread? _thread;
    private volatile bool _running;
    private bool _prevBlink;

    public VtsTrackingOptions Options { get; set; }

    /// <summary>已跑的拍数（诊断用）。</summary>
    public long Ticks { get; private set; }

    /// <summary>最近一次算出的驱动量（诊断/UI 回显）。</summary>
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
        // 收手：把驱动量归位，别让画面卡在最后一帧的姿态上
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
                    // 追踪断了：立刻回到静息，而不是保持最后一帧（保持会让镜头永远偏着）
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

            // 固定节拍（与 VtsPerformanceDriver 同一套粗睡+自旋）
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
