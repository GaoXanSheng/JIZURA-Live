using JizuraLive.Render.Gpu;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using DCommonPixelFormat = Vortice.DCommon.PixelFormat;

namespace JizuraLive.Render.Draw;

public sealed class OffscreenTarget : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public ID3D11Texture2D Texture { get; }
    public ID2D1Bitmap1 Bitmap { get; }

    private readonly D3DDevice _gpu;
    private readonly IDXGISurface _surface;

    private OffscreenTarget(D3DDevice gpu, int w, int h, ID3D11Texture2D texture,
        IDXGISurface surface, ID2D1Bitmap1 bitmap)
    {
        _gpu = gpu;
        Width = w;
        Height = h;
        Texture = texture;
        _surface = surface;
        Bitmap = bitmap;
    }

    public static OffscreenTarget Create(D3DDevice gpu, ID2D1DeviceContext ctx, int width, int height,
        bool sampleable = false)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        var desc = new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1u, 0u),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | (sampleable ? BindFlags.ShaderResource : 0),
            CPUAccessFlags = CpuAccessFlags.None,
        };
        var texture = gpu.Device.CreateTexture2D(desc);
        var surface = texture.QueryInterface<IDXGISurface>();
        var options = BitmapOptions.Target | (sampleable ? BitmapOptions.None : BitmapOptions.CannotDraw);
        var bitmap = ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
            96f, 96f, options));

        return new OffscreenTarget(gpu, width, height, texture, surface, bitmap);
    }

    public ReadbackImage Readback() => GpuReadback.ToImage(_gpu, Texture, Width, Height);

    public void Dispose()
    {
        Bitmap.Dispose();
        _surface.Dispose();
        Texture.Dispose();
    }
}
