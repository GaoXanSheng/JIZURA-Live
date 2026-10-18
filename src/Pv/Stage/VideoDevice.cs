using JizuraLive.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JizuraLive.Pv;

internal sealed class VideoDevice : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }

    private VideoDevice(ID3D11Device device, ID3D11DeviceContext context)
    {
        Device = device;
        Context = context;
    }

    public static VideoDevice? Create(IDXGIAdapter? adapter = null)
    {
        try
        {
            var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
            var driver = adapter is null ? DriverType.Hardware : DriverType.Unknown;
            var hr = D3D11.D3D11CreateDevice(adapter, driver, DeviceCreationFlags.BgraSupport,
                levels, out var device, out var level, out var context);
            if (hr.Failure || device is null || context is null)
            {
                Log.Warn($"[PV/视频] 独立视频设备创建失败：hr=0x{hr.Code:x}");
                context?.Dispose();
                device?.Dispose();
                return null;
            }

            Log.Info($"[PV/视频] 独立视频设备就绪：feature level={level}");
            using (var multithread = device.QueryInterface<ID3D11Multithread>())
                multithread.SetMultithreadProtected(true);
            return new VideoDevice(device, context);
        }
        catch (Exception ex)
        {
            Log.Error("[PV/视频] 独立视频设备初始化异常：" + ex.Message, ex);
            return null;
        }
    }

    public void Dispose()
    {
        Context.Dispose();
        Device.Dispose();
    }
}
