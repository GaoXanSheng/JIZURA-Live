using System.Windows;
using System.Windows.Interop;
using JizuraLive.Logging;
using SharpDX.Direct3D9;

namespace JizuraLive.App;

/// <summary>
/// WPF D3DImage 与 Direct3D 11 共享纹理桥接器：
/// 通过 Direct3D 9Ex 驱动 WPF 的 D3DImage 硬件合成表面，
/// 利用 OS 共享句柄直接映射 D3D11 显存纹理，实现零拷贝、零 CPU 介入的纯 GPU 预览直显。
/// </summary>
public sealed class D3D11ImageSource : D3DImage, IDisposable
{
    private static Direct3DEx? _d3dEx;
    private static DeviceEx? _deviceEx;
    private static readonly object DeviceLock = new();

    private Texture? _texture;
    private Surface? _surface;
    private IntPtr _lastShareHandle = IntPtr.Zero;
    private int _width, _height;

    public D3D11ImageSource()
    {
        EnsureDevice();
        IsFrontBufferAvailableChanged += OnIsFrontBufferAvailableChanged;
    }

    private static void EnsureDevice()
    {
        lock (DeviceLock)
        {
            if (_deviceEx != null) return;
            try
            {
                _d3dEx = new Direct3DEx();
                var pp = new PresentParameters
                {
                    Windowed = true,
                    SwapEffect = SwapEffect.Discard,
                    DeviceWindowHandle = IntPtr.Zero,
                    PresentationInterval = PresentInterval.Default,
                    BackBufferFormat = Format.Unknown,
                    BackBufferWidth = 1,
                    BackBufferHeight = 1
                };

                _deviceEx = new DeviceEx(_d3dEx, 0, DeviceType.Hardware, IntPtr.Zero,
                    CreateFlags.HardwareVertexProcessing | CreateFlags.Multithreaded | CreateFlags.FpuPreserve,
                    pp);
                Log.Info("[D3D11ImageSource] Direct3D9Ex 硬件互操作设备创建成功");
            }
            catch (Exception ex)
            {
                Log.Error("[D3D11ImageSource] Direct3D9Ex 设备初始化失败：" + ex.Message, ex);
            }
        }
    }

    /// <summary>绑定新的 D3D11 共享纹理句柄（只在 UI 线程调用）。</summary>
    public bool BindSharedHandle(IntPtr shareHandle, int width, int height)
    {
        if (shareHandle == IntPtr.Zero || width <= 0 || height <= 0) return false;
        if (_deviceEx == null) EnsureDevice();
        if (_deviceEx == null) return false;

        if (_lastShareHandle == shareHandle && _width == width && _height == height && _surface != null)
            return true;

        try
        {
            CleanupTexture();

            IntPtr pShared = shareHandle;
            // D3D11 的 BGRA8 映射到 D3D9 的 A8R8G8B8
            _texture = new Texture(_deviceEx, width, height, 1, Usage.RenderTarget,
                Format.A8R8G8B8, Pool.Default, ref pShared);
            _surface = _texture.GetSurfaceLevel(0);
            _lastShareHandle = shareHandle;
            _width = width;
            _height = height;

            Lock();
            SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface.NativePointer);
            Unlock();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"[D3D11ImageSource] 打开共享句柄 0x{shareHandle:x} ({width}x{height}) 失败: {ex.Message}", ex);
            CleanupTexture();
            return false;
        }
    }

    /// <summary>通知 WPF 合成器有新帧绘制完成并提交脏区。</summary>
    public void Invalidate()
    {
        if (!IsFrontBufferAvailable || _surface == null || _width <= 0 || _height <= 0) return;
        Lock();
        AddDirtyRect(new Int32Rect(0, 0, _width, _height));
        Unlock();
    }

    private void OnIsFrontBufferAvailableChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsFrontBufferAvailable && _surface != null)
        {
            try
            {
                Lock();
                SetBackBuffer(D3DResourceType.IDirect3DSurface9, _surface.NativePointer);
                Unlock();
            }
            catch (Exception ex)
            {
                Log.Warn("[D3D11ImageSource] 恢复前缓冲区失败：" + ex.Message);
            }
        }
    }

    private void CleanupTexture()
    {
        Lock();
        SetBackBuffer(D3DResourceType.IDirect3DSurface9, IntPtr.Zero);
        Unlock();

        _surface?.Dispose();
        _surface = null;
        _texture?.Dispose();
        _texture = null;
        _lastShareHandle = IntPtr.Zero;
    }

    public void Dispose()
    {
        CleanupTexture();
        IsFrontBufferAvailableChanged -= OnIsFrontBufferAvailableChanged;
    }
}
