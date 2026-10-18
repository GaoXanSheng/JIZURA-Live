using JizuraLive.Render.Gpu;
using JizuraLive.Render.Text;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.DXGI;
using D2DFactoryType = Vortice.Direct2D1.FactoryType;
using DWFactoryType = Vortice.DirectWrite.FactoryType;

namespace JizuraLive.Render.Draw;

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
        var factory = D2D1.D2D1CreateFactory<ID2D1Factory1>(D2DFactoryType.MultiThreaded, default(FactoryOptions));
        ID2D1Device device;
        using (var dxgiDevice = gpu.Device.QueryInterface<IDXGIDevice>())
        {
            device = factory.CreateDevice(dxgiDevice);
        }

        var ctx = device.CreateDeviceContext(DeviceContextOptions.None);
        // 渲染器全程按像素坐标画；不能让 D2D 再按系统 DPI 把像素当 DIP 放大。
        ctx.UnitMode = UnitMode.Pixels;
        ctx.SetDpi(96f, 96f);
        var dw = DWrite.DWriteCreateFactory<IDWriteFactory>(DWFactoryType.Shared);
        return new D2DHost(factory, device, ctx, dw, new DwFontCache(dw));
    }

    public void Dispose()
    {
        Draw.Dispose();
        Dw.Dispose();
        Ctx.Dispose();
        Device.Dispose();
        Factory.Dispose();
    }
}
