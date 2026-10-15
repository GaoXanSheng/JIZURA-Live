using JizuraLive.Logging;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JizuraLive.Render.Gpu;

/// <summary>
/// 渲染核心的 D3D11 设备持有者（P0：GPU 呈现链路的地基）。
///
/// 为什么是 Vortice 而不是 SharpDX：SharpDX 2018 已停更；Vortice.Windows 是同一血统的现役继任
/// （net10 / MIT），D3D11·DXGI·D2D1·DWrite·D3DCompiler 一套齐全。旧的 SharpDX 只留在 src/Spout
/// （SpoutDXWrapper 要它），从本文件起的新渲染链路一律走 Vortice。
///
/// 职责：建硬件设备（feature level 11_1/11_0、BGRA 支持）、挑独显、暴露设备/上下文/DXGI 工厂，
/// 并提供「设备丢失」判定——上层据此重建资源。设备/驱动级失败不重试：一次失败即判定该路线不可用，
/// 上层回退（与 src/Engine/GpuRasterHost 同一纪律）。
///
/// 回退链：独显 → 默认硬件 → WARP（软件光栅）。WARP 让无 GPU 的 CI/虚拟机也能跑通链路与测试。
/// </summary>
public sealed class D3DDevice : IDisposable
{
    private static readonly FeatureLevel[] Levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0];

    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDXGIFactory2 Factory { get; }
    public IDXGIAdapter1? Adapter { get; }
    public FeatureLevel FeatureLevel { get; }

    /// <summary>适配器名（日志/诊断用；WARP 回退时为「WARP 软件光栅」）。</summary>
    public string AdapterName { get; }

    /// <summary>独占显存字节数（挑卡依据；WARP 为 0）。</summary>
    public long DedicatedVideoMemory { get; }

    /// <summary>是否带调试层（需装 D3D SDK 层且 JIZURA_D3D_DEBUG=1）。</summary>
    public bool DebugLayer { get; }

    /// <summary>是否软件光栅（WARP 回退）。</summary>
    public bool IsSoftware { get; }

    /// <summary>JIZURA_D3D_DEBUG=1/true/on 时请求调试层。</summary>
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

    /// <summary>
    /// 建一个可用设备；任何一步失败都返回 null 并给出原因（调用方回退，不重试）。
    /// </summary>
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

            // 1) 独显（挑到的适配器）→ 2) 默认硬件 → 3) WARP。第一档成功即用。
            // D3D11 规定：显式传适配器时 DriverType 必须是 Unknown，否则 E_INVALIDARG。
            var firstWhy = "";
            var pickedDriver = picked != null ? DriverType.Unknown : DriverType.Hardware;
            if (TryCreateOn(picked, pickedDriver, flags,
                    out var device, out var context, out var level, out firstWhy) == false)
            {
                if (TryCreateOn(null, DriverType.Hardware, flags,
                        out device, out context, out level, out var why2) == false)
                {
                    // WARP 不接受 BgraSupport 之外的多余标志也无妨；debug 层对 WARP 亦可用
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

    /// <summary>挑独显：枚举全部适配器，排除软件适配器，取独占显存最大的一块。</summary>
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

    /// <summary>
    /// 设备是否已被移除/重置（驱动升级、TDR、拔卡）。调用方见到 true 应停帧、重建设备与资源。
    /// </summary>
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

    /// <summary>取设备移除原因（日志用）。</summary>
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
        // 上下文先于设备释放（设备释放会带走上下文，但显式顺序更清楚）
        Context.Dispose();
        Device.Dispose();
        Adapter?.Dispose();
        Factory.Dispose();
    }
}
