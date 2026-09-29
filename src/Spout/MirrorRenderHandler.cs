using System;
using System.Diagnostics;
using CefSharp;
using CefSharp.Enums;
using CefSharp.OffScreen;
using CefSharp.Structs;
using SharpDX;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using Device1 = SharpDX.Direct3D11.Device1;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace JizuraLive;

public sealed class MirrorRenderHandler : IRenderHandler
{
    private readonly SpoutPipeline _owner;

    private Device _dxDevice = null!;
    private Device1 _dxDevice1 = null!;

    private SpoutDXWrapper.SpoutSender _spoutSender;

    private Texture2D? _cpuTexture;

    private Texture2D? _sharedTexture;

    private int _cpuTextureWidth;
    private int _cpuTextureHeight;

    private int _sharedTextureWidth;
    private int _sharedTextureHeight;

    private Format _sharedTextureFormat;

    private readonly object _renderLock = new();

    // --------------------------------------------------------------------
    // 调试仪表：出帧节奏与单帧拷贝+发送耗时（每 60 帧汇总一条日志）
    // --------------------------------------------------------------------

    private long _paintLastTicks;
    private int _paintCount;
    private double _paintGapSum;
    private double _paintGapMax;
    private double _paintCostSum;
    private double _paintCostMax;

    private void CountPaint(
        long startTimestamp)
    {
        long end =
            Stopwatch.GetTimestamp();

        double costMs =
            (end - startTimestamp) *
            1000.0 / Stopwatch.Frequency;

        if (_paintLastTicks != 0)
        {
            double gapMs =
                (end - _paintLastTicks) *
                1000.0 / Stopwatch.Frequency;

            _paintGapSum += gapMs;

            if (gapMs > _paintGapMax)
                _paintGapMax = gapMs;
        }

        _paintLastTicks = end;
        _paintCostSum += costMs;

        if (costMs > _paintCostMax)
            _paintCostMax = costMs;

        if (++_paintCount < 60)
            return;

        Log.Info(
            $"[SpoutMirror] 帧输出：间隔 " +
            $"avg={_paintGapSum / Math.Max(1, _paintCount - 1):F1}ms, " +
            $"max={_paintGapMax:F1}ms；" +
            $"拷贝+Spout发送 avg={_paintCostSum / _paintCount:F2}ms, " +
            $"max={_paintCostMax:F2}ms");

        _paintCount = 0;
        _paintLastTicks = 0;
        _paintGapSum = 0;
        _paintGapMax = 0;
        _paintCostSum = 0;
        _paintCostMax = 0;
    }

    private bool _disposed;

    public MirrorRenderHandler(
        SpoutPipeline owner)
    {
        _owner = owner;

        InitializeDirectX(
            _owner.SenderName ?? "JIZURA-live");
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
            new SpoutDXWrapper.SpoutSender();

        _spoutSender.SetSenderName(
            spoutId);

        _spoutSender.OpenDirectX(
            _dxDevice.NativePointer);

        Log.Info($"[SpoutMirror] D3D11 初始化完成：");
    }

    // ====================================================================
    // IRenderHandler
    // ====================================================================

    public ScreenInfo? GetScreenInfo()
    {
        return null;
    }

    public Rect GetViewRect()
    {
        var browser = _owner._browser;
        if (browser == null)
        {
            return new Rect(0, 0, 128, 128);
        }

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
        CefSharp.Structs.Range selectedRange,
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

    // ====================================================================
    // CPU Fallback
    // ====================================================================

    private void EnsureCpuTexture(int width, int height)
    {
        if (_cpuTexture != null && _cpuTextureWidth == width && _cpuTextureHeight == height)
        {
            return;
        }

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

    public void OnPaint(PaintElementType type, Rect dirtyRect, IntPtr buffer, int width, int height)
    {
        if (_disposed)
            return;

        if (type != PaintElementType.View)
            return;
        if (buffer == IntPtr.Zero || width <= 0 || height <= 0)
        {
            return;
        }

        lock (_renderLock)
        {
            long t0 =
                Stopwatch.GetTimestamp();

            try
            {
                EnsureCpuTexture(width, height);
                if (_cpuTexture == null)
                    return;
                var context = _dxDevice.ImmediateContext;
                var mapped = context.MapSubresource(_cpuTexture, 0, MapMode.WriteDiscard, MapFlags.None);
                try
                {
                    int srcStride = checked(width * 4);
                    int dstStride = mapped.RowPitch;
                    if (dstStride == srcStride)
                    {
                        Utilities.CopyMemory(mapped.DataPointer, buffer, checked(height * srcStride));
                    }
                    else
                    {
                        for (int row = 0; row < height; row++)
                        {
                            IntPtr src = IntPtr.Add(buffer, row * srcStride);
                            IntPtr dst = IntPtr.Add(mapped.DataPointer, row * dstStride);
                            Utilities.CopyMemory(dst, src, srcStride);
                        }
                    }
                }
                finally
                {
                    context.UnmapSubresource(_cpuTexture, 0);
                }

                _spoutSender.SendTexture(_cpuTexture.NativePointer);

                CountPaint(t0);
            }
            catch (Exception ex)
            {
                Log.Error("[SpoutMirror] OnPaint 失败", ex);
            }
        }
    }

    // ====================================================================
    // GPU Shared Texture
    // ====================================================================

    private void EnsureSharedTexture(Texture2D source)
    {
        var desc = source.Description;
        if (_sharedTexture != null && _sharedTextureWidth == desc.Width && _sharedTextureHeight == desc.Height && _sharedTextureFormat == desc.Format)
        {
            return;
        }

        _sharedTexture?.Dispose();

        var targetDesc = new Texture2DDescription
            {
                Width = desc.Width,
                Height = desc.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = desc.Format,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            };

        _sharedTexture = new Texture2D(_dxDevice, targetDesc);
        _sharedTextureWidth = desc.Width;
        _sharedTextureHeight = desc.Height;
        _sharedTextureFormat = desc.Format;

        Log.Info($"[SpoutMirror] 创建输出纹理：" + $"{desc.Width}x{desc.Height}, " + $"Format={desc.Format}");
    }

    public void OnAcceleratedPaint(PaintElementType type, Rect dirtyRect, AcceleratedPaintInfo info)
    {
        if (_disposed)
            return;
        if (type != PaintElementType.View)
            return;
        if (info.SharedTextureHandle == IntPtr.Zero)
        {
            return;
        }
        lock (_renderLock)
        {
            long t0 =
                Stopwatch.GetTimestamp();

            try
            {
                using var cefTexture = _dxDevice1.OpenSharedResource1<Texture2D>(info.SharedTextureHandle);
                if (cefTexture == null) return;
                EnsureSharedTexture(cefTexture);
                if (_sharedTexture == null)
                    return;
                var context = _dxDevice.ImmediateContext;
                context.CopyResource(cefTexture, _sharedTexture);
                _spoutSender.SendTexture(_sharedTexture.NativePointer);

                CountPaint(t0);
            }
            catch (Exception ex)
            {
                Log.Error("[SpoutMirror] OnAcceleratedPaint 失败", ex);
            }
        }
    }

    // ====================================================================
    // Dispose
    // ====================================================================

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        lock (_renderLock)
        {
            try
            {
                _cpuTexture?.Dispose();
            }
            catch
            {
            }

            _cpuTexture = null;

            try
            {
                _sharedTexture?.Dispose();
            }
            catch
            {
            }

            _sharedTexture = null;

            try
            {
                _dxDevice1?.Dispose();
            }
            catch
            {
            }

            _dxDevice1 = null!;

            try
            {
                _dxDevice?.Dispose();
            }
            catch
            {
            }

            _dxDevice = null!;
        }
    }
}