using JizuraLive.Render.Gpu;
using JizuraLive.Render.Text;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;
using DWFactoryType = Vortice.DirectWrite.FactoryType;

namespace JizuraLive.Render.Draw;

/// <summary>
/// Direct2D 运行环境的一次性组装：工厂 → 设备 → 设备上下文 → 字体缓存 → 即时绘制面。
///
/// 单独抽出来是因为它要被三处复用：验证程序（RenderProbe）、离屏对拍测试、以及后续接入主程序。
/// 组装顺序是硬性的（D2D 设备必须从 D3D11 设备的 IDXGIDevice 创建），集中在一处避免各处抄错。
/// </summary>
public sealed class D2DHost : IDisposable
{
    public ID2D1Factory1 Factory { get; }
    public ID2D1Device Device { get; }
    public ID2D1DeviceContext Ctx { get; }
    public IDWriteFactory Dw { get; }
    public DwFontCache Fonts { get; }
    public Immediate2D Draw { get; }

    private D2DHost(ID2D1Factory1 factory, ID2D1Device device, ID2D1DeviceContext ctx,
        IDWriteFactory dw, DwFontCache fonts)
    {
        Factory = factory;
        Device = device;
        Ctx = ctx;
        Dw = dw;
        Fonts = fonts;
        Draw = new Immediate2D(factory, ctx, fonts);
    }

    public static D2DHost Create(D3DDevice gpu)
    {
        var factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(D2DFactoryType.SingleThreaded, default(FactoryOptions));
        ID2D1Device device;
        using (var dxgiDevice = gpu.Device.QueryInterface<IDXGIDevice>())
        {
            device = factory.CreateDevice(dxgiDevice);
        }

        var ctx = device.CreateDeviceContext(DeviceContextOptions.None);
        var dw = DWrite.DWriteCreateFactory<IDWriteFactory>(DWFactoryType.Shared);
        return new D2DHost(factory, device, ctx, dw, new DwFontCache(dw));
    }

    public void Dispose()
    {
        Draw.Dispose(); // 内含 Fonts.Dispose
        Dw.Dispose();
        Ctx.Dispose();
        Device.Dispose();
        Factory.Dispose();
    }
}
