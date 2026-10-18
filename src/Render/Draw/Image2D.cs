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

public sealed class Image2D : IDisposable
{
    public ID2D1Bitmap Bitmap { get; }
    public int Width { get; }
    public int Height { get; }

    private readonly IDisposable?[] _owned;

    internal Image2D(ID2D1Bitmap bitmap, int width, int height, params IDisposable?[] owned)
    {
        Bitmap = bitmap;
        Width = width;
        Height = height;
        _owned = owned;
    }

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
