using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JizuraLive.Render.Gpu;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using DCommonPixelFormat = Vortice.DCommon.PixelFormat;

namespace JizuraLive.Render.Draw;

/// <summary>
/// 一张可被绘制的位图（包住 D2D 的 <see cref="ID2D1Bitmap"/>）。
/// 解码基于 Windows 内置 WIC（BitmapDecoder），完全独立于外部渲染引擎。
/// </summary>
public sealed class Image2D : IDisposable
{
    public ID2D1Bitmap Bitmap { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>随位图一起释放的附属资源（共享纹理与其 DXGI 表面——D2D 位图引用着它们）。</summary>
    private readonly IDisposable?[] _owned;

    internal Image2D(ID2D1Bitmap bitmap, int width, int height, params IDisposable?[] owned)
    {
        Bitmap = bitmap;
        Width = width;
        Height = height;
        _owned = owned;
    }

    /// <summary>从原始 BGRA8888 像素创建 D2D 位图。</summary>
    public static Image2D FromBgraPixels(ID2D1DeviceContext ctx, byte[] bgraPixels, int width, int height)
    {
        var format = new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied);
        var props = new BitmapProperties1(format, 96f, 96f);
        unsafe
        {
            fixed (byte* ptr = bgraPixels)
            {
                var bmp2d = ctx.CreateBitmap(new SizeI(width, height), (IntPtr)ptr, (uint)(width * 4), props);
                return new Image2D(bmp2d, width, height);
            }
        }
    }

    /// <summary>
    /// 在**另一台 D3D11 设备**（新渲染核心的 Vortice 设备）上打开一块共享纹理，包成可绘制位图。
    /// </summary>
    public static Image2D FromSharedHandle(D3DDevice gpu, ID2D1DeviceContext ctx,
        IntPtr shareHandle, int width, int height)
    {
        var texture = gpu.Device.OpenSharedResource<ID3D11Texture2D>(shareHandle);
        var surface = texture.QueryInterface<IDXGISurface>();
        var bitmap = ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.None));
        return new Image2D(bitmap, width, height, surface, texture);
    }

    /// <summary>从 PNG/JPEG/BMP/WebP 等编码字节解码并上传至 D2D。</summary>
    public static Image2D FromEncodedBytes(ID2D1DeviceContext ctx, byte[] data)
    {
        using var ms = new MemoryStream(data);
        var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];

        var converted = new FormatConvertedBitmap();
        converted.BeginInit();
        converted.Source = frame;
        converted.DestinationFormat = PixelFormats.Pbgra32;
        converted.EndInit();
        converted.Freeze();

        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[height * stride];
        converted.CopyPixels(pixels, stride, 0);

        return FromBgraPixels(ctx, pixels, width, height);
    }

    public static Image2D FromFile(ID2D1DeviceContext ctx, string path) =>
        FromEncodedBytes(ctx, File.ReadAllBytes(path));

    public void Dispose()
    {
        Bitmap.Dispose();
        foreach (var d in _owned) d?.Dispose();
    }
}
