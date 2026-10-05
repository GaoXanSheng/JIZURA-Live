using System.Diagnostics;
using CefSharp;
using CefSharp.Enums;
using CefSharp.OffScreen;
using CefSharp.Structs;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SpoutDXWrapper;
using Device = SharpDX.Direct3D11.Device;
using Device1 = SharpDX.Direct3D11.Device1;
using MapFlags = SharpDX.Direct3D11.MapFlags;
using Range = CefSharp.Structs.Range;

namespace JizuraLive;

// 帧间隔直方图统计：1ms 桶（0..63ms，>=64ms 落尾桶），周期性汇报 avg/p95/max。
// 回调按帧发生，观测零成本；汇报窗口自带门控，避免逐帧日志写满滚动日志
internal sealed class FrameIntervalStats
{
    private const int BucketCount = 64;

    private readonly object _gate = new();
    private readonly ulong[] _buckets = new ulong[BucketCount];

    private long _lastTs;
    private long _windowStartTs;
    private ulong _count;
    private double _sumMs;
    private double _maxMs;

    public void Observe()
    {
        var now = Stopwatch.GetTimestamp();

        lock (_gate)
        {
            if (_lastTs != 0)
            {
                var ms = (now - _lastTs) * 1000.0 / Stopwatch.Frequency;
                _count++;
                _sumMs += ms;
                if (ms > _maxMs)
                    _maxMs = ms;
                _buckets[ms >= BucketCount ? BucketCount - 1 : (int)ms]++;
            }
            else
            {
                _windowStartTs = now;
            }

            _lastTs = now;
        }
    }

    // 回调处理耗时（OpenSharedResource + Spout 发送）：与帧间隔同窗口累计，
    // 帧间隔行汇报时一并带走并清零
    private ulong _costCount;
    private double _costSumMs;
    private double _costMaxMs;

    public void AddCost(double ms)
    {
        lock (_gate)
        {
            _costCount++;
            _costSumMs += ms;
            if (ms > _costMaxMs)
                _costMaxMs = ms;
        }
    }

    // 自上一次汇报累计满 windowMs 且有样本才返回统计文本并清零窗口，否则 null
    public string? ReportIfDue(int windowMs = 5000)
    {
        var now = Stopwatch.GetTimestamp();

        lock (_gate)
        {
            if (_lastTs == 0 || _count == 0)
                return null;

            var elapsed = (now - _windowStartTs) * 1000.0 / Stopwatch.Frequency;
            if (elapsed < windowMs)
                return null;

            var avg = _sumMs / _count;
            var p95Target = (ulong)Math.Ceiling(_count * 0.95);
            ulong acc = 0;
            var p95 = BucketCount - 1;
            for (var i = 0; i < BucketCount; i++)
            {
                acc += _buckets[i];
                if (acc >= p95Target)
                {
                    p95 = i;
                    break;
                }
            }

            var text =
                $"帧间隔（{elapsed / 1000.0:F1}s, n={_count}）：" +
                $"avg={avg:F1}ms, p95={p95}ms, max={_maxMs:F1}ms";

            if (_costCount > 0)
                text += $", 回调耗时 avg={_costSumMs / _costCount:F2}ms, max={_costMaxMs:F1}ms (n={_costCount})";

            _count = 0;
            _sumMs = 0;
            _maxMs = 0;
            _costCount = 0;
            _costSumMs = 0;
            _costMaxMs = 0;
            Array.Clear(_buckets);
            _windowStartTs = now;

            return text;
        }
    }
}

public sealed class MirrorRenderHandler : IRenderHandler
{
    private readonly SpoutPipeline _owner;

    // GPU 共享纹理路径 / CPU 回退路径分开统计，掉出 GPU 路径时一眼可见
    private readonly FrameIntervalStats _gpuStats = new();
    private readonly FrameIntervalStats _cpuStats = new();

    private bool _accelFirstSeen;
    private bool _cpuFirstSeen;

    private Texture2D? _cpuTexture;
    private int _cpuTextureHeight;

    private int _cpuTextureWidth;

    private bool _disposed;

    private Device _dxDevice = null!;
    private Device1 _dxDevice1 = null!;

    private SpoutSender _spoutSender;
    private bool _zeroHandleWarned;

    // 渲染回调失败按帧发生（设备移除等持续性故障），逐帧记 Error 几分钟就写满滚动日志：
    // 首条立即记（初值非零保证这一点），之后每 30 秒汇总一条
    private long _lastRenderErrorTick = -31_000;
    private int _renderErrorsSinceLastLog;

    public MirrorRenderHandler(SpoutPipeline owner)
    {
        _owner = owner;

        InitializeDirectX(_owner.SenderName ?? "JIZURA-live");
    }

    public ScreenInfo? GetScreenInfo()
    {
        // 缩放系数必须钉死 1：返回 null 时 CEF 按显示器 DPI 推导（实测推得 0.9），
        // CSS 视口(2133x1200)≠出图纹理(1920x1080)，canvas 缩在左上角、右侧/底部留出
        // alpha=0 透明带，OBS 合成到黑底就是大黑边。=1 保证 CSS 像素 = 纹理像素
        var size = GetViewRect();
        return new ScreenInfo
        {
            DeviceScaleFactor = 1f,
            Rect = size,
            AvailableRect = size
        };
    }

    public Rect GetViewRect()
    {
        var browser = _owner._browser;
        if (browser == null) return new Rect(0, 0, 128, 128);

        var size = browser.Size;

        return new Rect(0, 0, Math.Max(1, size.Width), Math.Max(1, size.Height));
    }

    public bool GetScreenPoint(int viewX, int viewY, out int screenX, out int screenY)
    {
        screenX = viewX;
        screenY = viewY;

        return false;
    }

    public void OnCursorChange(
        IntPtr cursor,
        CursorType type,
        CursorInfo customCursorInfo)
    {
    }

    public void OnImeCompositionRangeChanged(
        Range selectedRange,
        Rect[] characterBounds)
    {
    }

    public void OnPopupShow(
        bool show)
    {
    }

    public void OnPopupSize(
        Rect rect)
    {
    }

    public void OnVirtualKeyboardRequested(
        IBrowser browser,
        TextInputMode inputMode)
    {
    }

    public bool StartDragging(IDragData dragData, DragOperationsMask mask, int x, int y)
    {
        return false;
    }

    public void UpdateDragCursor(DragOperationsMask operation)
    {
    }

    public void OnPaint(PaintElementType type, Rect dirtyRect, IntPtr buffer, int width, int height)
    {
        if (_disposed)
            return;

        if (type != PaintElementType.View)
            return;
        if (buffer == IntPtr.Zero || width <= 0 || height <= 0) return;

        _cpuStats.Observe();
        ReportFrameStats();

        var cpuFirstSeenWarn = false;
        Exception? logEx = null;
        try
        {
            EnsureCpuTexture(width, height);
            if (_cpuTexture == null)
                return;
            var context = _dxDevice.ImmediateContext;
            var mapped = context.MapSubresource(_cpuTexture, 0, MapMode.WriteDiscard, MapFlags.None);
            try
            {
                var srcStride = checked(width * 4);
                var dstStride = mapped.RowPitch;
                if (dstStride == srcStride)
                    Utilities.CopyMemory(mapped.DataPointer, buffer, checked(height * srcStride));
                else
                    for (var row = 0; row < height; row++)
                    {
                        var src = IntPtr.Add(buffer, row * srcStride);
                        var dst = IntPtr.Add(mapped.DataPointer, row * dstStride);
                        Utilities.CopyMemory(dst, src, srcStride);
                    }
            }
            finally
            {
                context.UnmapSubresource(_cpuTexture, 0);
            }

            _spoutSender.SendTexture(_cpuTexture.NativePointer);

            if (!_cpuFirstSeen)
            {
                _cpuFirstSeen = true;
                cpuFirstSeenWarn = true;
            }
        }
        catch (Exception ex)
        {
            logEx = ex;
        }


        if (cpuFirstSeenWarn)
            Log.Warn(
                "[SpoutMirror] CPU 回退路径首次触发" +
                "（OnPaint：GPU 共享纹理未生效）");

        if (logEx != null) LogRenderError("OnPaint", logEx);
    }

    public void OnAcceleratedPaint(PaintElementType type, Rect dirtyRect, AcceleratedPaintInfo info)
    {
        if (_disposed)
            return;
        if (type != PaintElementType.View)
            return;
        if (info.SharedTextureHandle == IntPtr.Zero)
        {
            if (!_zeroHandleWarned)
            {
                _zeroHandleWarned = true;

                Log.Warn(
                    "[SpoutMirror] OnAcceleratedPaint " +
                    "收到空共享纹理句柄");
            }

            return;
        }

        _gpuStats.Observe();
        ReportFrameStats();

        if (!_accelFirstSeen)
        {
            _accelFirstSeen = true;

            Log.Info(
                "[SpoutMirror] GPU 共享纹理路径已激活" +
                "（OnAcceleratedPaint）");
        }

        Exception? logEx = null;

        try
        {
            var sw = Stopwatch.StartNew();
            using var cefTexture = _dxDevice1.OpenSharedResource1<Texture2D>(info.SharedTextureHandle);
            if (cefTexture == null) return;
            _spoutSender.SendTexture(cefTexture.NativePointer);
            _gpuStats.AddCost(sw.Elapsed.TotalMilliseconds);
        }
        catch (Exception ex)
        {
            logEx = ex;
        }

        if (logEx != null) LogRenderError("OnAcceleratedPaint", logEx);
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        var logExs = new List<Exception>();

        try
        {
            _spoutSender.Dispose();
        }
        catch (Exception ex)
        {
            logExs.Add(ex);
        }

        try
        {
            _cpuTexture?.Dispose();
        }
        catch (Exception ex)
        {
            logExs.Add(ex);
        }

        _cpuTexture = null;

        try
        {
            _dxDevice1?.Dispose();
        }
        catch (Exception ex)
        {
            logExs.Add(ex);
        }

        _dxDevice1 = null!;

        try
        {
            _dxDevice?.Dispose();
        }
        catch (Exception ex)
        {
            logExs.Add(ex);
        }

        _dxDevice = null!;


        foreach (var ex in logExs) Log.Debug("Dispose object failed: " + ex);
    }

    private void InitializeDirectX(
        string spoutId)
    {
        _dxDevice = new Device(
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport);

        _dxDevice1 =
            _dxDevice.QueryInterface<Device1>();

        _spoutSender =
            new SpoutSender();

        _spoutSender.SetSenderName(
            spoutId);

        _spoutSender.OpenDirectX(
            _dxDevice.NativePointer);
    }

    // 两条路径各自 5s 一个统计窗口，谁有帧谁报；GPU/CPU 都没帧时静默。
    // 行首带通道名：主/歌词两条管线都往同一份镜像日志写，不带名字分不清谁在掉帧
    private void ReportFrameStats()
    {
        var tag = _owner.SenderName ?? "未知通道";

        var gpu = _gpuStats.ReportIfDue();
        if (gpu != null)
            Log.Info($"[SpoutMirror][{tag}] GPU 共享纹理路径{gpu}");

        var cpu = _cpuStats.ReportIfDue();
        if (cpu != null)
            Log.Warn($"[SpoutMirror][{tag}] CPU 回退路径{cpu}");
    }

    private void LogRenderError(string where, Exception ex)
    {
        _renderErrorsSinceLastLog++;
        var now = Environment.TickCount64;
        if (now - _lastRenderErrorTick < 30_000) return;

        var suppressed = _renderErrorsSinceLastLog - 1;
        _renderErrorsSinceLastLog = 0;
        _lastRenderErrorTick = now;
        Log.Error(
            $"[SpoutMirror] {where} 失败" +
            (suppressed > 0 ? $"（节流：此前 {suppressed} 条同类错误未逐条记录）" : ""),
            ex);
    }

    private void EnsureCpuTexture(int width, int height)
    {
        if (_cpuTexture != null && _cpuTextureWidth == width && _cpuTextureHeight == height) return;

        _cpuTexture?.Dispose();
        var desc = new Texture2DDescription
        {
            Width = width, Height = height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = ResourceUsage.Dynamic,
            BindFlags = BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.Write,
            OptionFlags = ResourceOptionFlags.None
        };
        _cpuTexture = new Texture2D(_dxDevice, desc);
        _cpuTextureWidth = width;
        _cpuTextureHeight = height;
    }
}