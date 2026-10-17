using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D11;
using Vortice.DXGI;
using D3DMapFlags = Vortice.Direct3D11.MapFlags;

namespace JizuraLive.Render.Gpu;

public readonly record struct ReadbackPixel(byte Red, byte Green, byte Blue, byte Alpha);

public readonly record struct ReadbackImage(int Width, int Height, byte[] BgraPixels) : IDisposable
{
    public ReadbackPixel GetPixel(int x, int y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
            return default;
        var idx = (y * Width + x) * 4;
        return new ReadbackPixel(BgraPixels[idx + 2], BgraPixels[idx + 1], BgraPixels[idx], BgraPixels[idx + 3]);
    }

    public void SavePng(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var stride = Width * 4;
        var bmp = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Pbgra32, null, BgraPixels, stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var fs = File.Create(path);
        encoder.Save(fs);
    }

    public static ReadbackImage LoadPng(string path)
    {
        using var fs = File.OpenRead(path);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
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

        return new ReadbackImage(width, height, pixels);
    }

    public void Dispose() { }
}

public static class GpuReadback
{
    public static ReadbackImage ToImage(D3DDevice gpu, ID3D11Texture2D texture, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        using var staging = gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1u, 0u),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });

        var ctx = gpu.Context;
        ctx.CopyResource(staging, texture);
        ctx.Flush();

        var pixels = new byte[width * height * 4];
        var mapped = ctx.Map(staging, 0, MapMode.Read, D3DMapFlags.None);
        try
        {
            var rowBytes = width * 4;
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(IntPtr.Add(mapped.DataPointer, (int)(y * mapped.RowPitch)), pixels, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            ctx.Unmap(staging, 0);
        }

        return new ReadbackImage(width, height, pixels);
    }
}
