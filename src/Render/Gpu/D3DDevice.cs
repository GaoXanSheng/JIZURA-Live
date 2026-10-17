using JizuraLive.Logging;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JizuraLive.Render.Gpu;

public sealed class D3DDevice : IDisposable
{
    private static readonly FeatureLevel[] Levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDXGIFactory2 Factory { get; }
    public IDXGIAdapter1? Adapter { get; }
    public FeatureLevel FeatureLevel { get; }

    public string AdapterName { get; }

    public long DedicatedVideoMemory { get; }

    public bool DebugLayer { get; }

    public bool IsSoftware { get; }

    public static bool DebugRequested =>
        Environment.GetEnvironmentVariable("JIZURA_D3D_DEBUG") is "1" or "true" or "on";

    private D3DDevice(ID3D11Device device, ID3D11DeviceContext context, IDXGIFactory2 factory,
        IDXGIAdapter1? adapter, FeatureLevel level, string adapterName, long vram, bool debug, bool software)
    {
        Device = device;
        Context = context;
        Factory = factory;
        Adapter = adapter;
        FeatureLevel = level;
        AdapterName = adapterName;
        DedicatedVideoMemory = vram;
        DebugLayer = debug;
        IsSoftware = software;
    }

    public static D3DDevice? TryCreate(out string error)
    {
        error = "";
        IDXGIFactory2? factory = null;
        IDXGIAdapter1? picked = null;
        try
        {
            factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
            picked = PickAdapter(factory);

            var debug = DebugRequested && D3D11.SdkLayersAvailable();
            var flags = DeviceCreationFlags.BgraSupport
                        | (debug ? DeviceCreationFlags.Debug : DeviceCreationFlags.None);

            var firstWhy = "";
            var pickedDriver = picked != null ? DriverType.Unknown : DriverType.Hardware;
            if (TryCreateOn(picked, pickedDriver, flags,
                    out var device, out var context, out var level, out firstWhy) == false)
            {
                if (TryCreateOn(null, DriverType.Hardware, flags,
                        out device, out context, out level, out var why2) == false)
                {
                    if (TryCreateOn(null, DriverType.Warp, flags,
                            out device, out context, out level, out var why3) == false)
                    {
                        error = $"D3D11 设备创建失败（独显/默认硬件/WARP 均不可用）：{firstWhy}｜{why2}｜{why3}";
                        picked?.Dispose();
                        factory.Dispose();
                        return null;
                    }

                    Log.Warn($"[D3DDevice] 硬件设备不可用，回退 WARP 软件光栅：{firstWhy}｜{why2}");
                    return new D3DDevice(device!, context!, factory, null, level, "WARP 软件光栅", 0, debug, true);
                }

                Log.Warn($"[D3DDevice] 挑中的适配器不可用，回退默认硬件设备：{firstWhy}");
            }

            var name = picked != null ? DescribeAdapter(picked) : "(默认硬件适配器)";
            var vram = picked != null ? (long)(ulong)picked.Description1.DedicatedVideoMemory : 0L;
            var dev = new D3DDevice(device!, context!, factory, picked, level, name, vram, debug, false);
            Log.Info($"[D3DDevice] 就绪：{name}，feature level={level}，"
                     + $"显存 {vram / (1024 * 1024)}MB，调试层={(debug ? "开" : "关")}");
            return dev;
        }
        catch (Exception ex)
        {
            error = ex.ToString();
            picked?.Dispose();
            factory?.Dispose();
            Log.Error("[D3DDevice] 初始化异常：" + ex.Message, ex);
            return null;
        }
    }

    private static bool TryCreateOn(IDXGIAdapter? adapter, DriverType driverType, DeviceCreationFlags flags,
        out ID3D11Device? device, out ID3D11DeviceContext? context, out FeatureLevel level, out string why)
    {
        device = null;
        context = null;
        level = FeatureLevel.Level_11_0;
        why = "";
        try
        {
            var hr = D3D11.D3D11CreateDevice(adapter, driverType, flags, Levels,
                out device, out level, out context);
            if (hr.Failure || device is null || context is null)
            {
                why = $"hr=0x{hr.Code:x}（{hr.Description}）";
                device?.Dispose();
                context?.Dispose();
                device = null;
                context = null;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            why = string.IsNullOrEmpty(ex.Message) ? ex.GetType().Name : ex.Message;
            return false;
        }
    }

    private static IDXGIAdapter1? PickAdapter(IDXGIFactory2 factory)
    {
        IDXGIAdapter1? best = null;
        ulong bestVram = 0;
        for (uint i = 0; ; i++)
        {
            Result hr;
            IDXGIAdapter1? ad;
            try
            {
                hr = factory.EnumAdapters1(i, out ad);
            }
            catch
            {
                break;
            }

            if (hr.Failure || ad is null) break;

            var desc = ad.Description1;
            var software = (desc.Flags & AdapterFlags.Software) != 0;
            var vram = (ulong)desc.DedicatedVideoMemory;
            if (!software && vram > bestVram)
            {
                best?.Dispose();
                best = ad;
                bestVram = vram;
            }
            else
            {
                ad.Dispose();
            }
        }

        return best;
    }

    private static string DescribeAdapter(IDXGIAdapter1 ad)
    {
        var d = ad.Description1;
        return $"{d.Description.Trim()} (Luid={d.Luid}, vendor=0x{d.VendorId:x})";
    }

    public bool IsDeviceRemoved
    {
        get
        {
            try
            {
                var reason = Device.DeviceRemovedReason;
                return reason.Failure && reason.Code != 0;
            }
            catch
            {
                return true;
            }
        }
    }

    public string RemovedReasonText()
    {
        try
        {
            var r = Device.DeviceRemovedReason;
            return r.Success ? "正常" : $"0x{(int)r.Code:x}（{r.Description}）";
        }
        catch (Exception ex)
        {
            return "不可得：" + ex.Message;
        }
    }

    public void Dispose()
    {
        Context.Dispose();
        Device.Dispose();
        Adapter?.Dispose();
        Factory.Dispose();
    }
}
