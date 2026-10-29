using System.IO;
using System.Runtime.InteropServices;
using JizuraLive.Render.Gpu;
using SkiaSharp;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using DCommonPixelFormat = Vortice.DCommon.PixelFormat;

namespace JizuraLive.Pv;

public sealed class ImageLayerProvider : IPvLayerProvider, IDisposable
{
    private readonly ID3D11Texture2D? _texture;
    private readonly ID2D1Bitmap1? _bitmap;
    private readonly int _width;
    private readonly int _height;
    private readonly byte[]? _alpha;
    private readonly int _alphaWidth;
    private readonly int _alphaHeight;

    public string Id { get; }

    public long Version { get; } = 1;

    public ImageLayerProvider(string id, D3DDevice gpu, ID2D1DeviceContext ctx, string path)
    {
        Id = id;

        using var decoded = SKBitmap.Decode(path)
                            ?? throw new InvalidDataException("无法解码图片：" + path);
        int width = decoded.Width;
        int height = decoded.Height;
        if (width <= 0 || height <= 0) throw new InvalidDataException("图片尺寸无效：" + path);

        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var premultiplied = new SKBitmap(info);
        using (var canvas = new SKCanvas(premultiplied))
        {
            canvas.Clear(SKColors.Transparent);
            using var paint = new SKPaint { BlendMode = SKBlendMode.Src };
            canvas.DrawBitmap(decoded, 0, 0, paint);
        }

        var pixels = new byte[width * height * 4];
        Marshal.Copy(premultiplied.GetPixels(), pixels, 0, pixels.Length);

        _texture = gpu.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
            CPUAccessFlags = CpuAccessFlags.None,
            MiscFlags = ResourceOptionFlags.None,
        });
        gpu.Context.UpdateSubresource(pixels, _texture, 0,
            (uint)(width * 4), (uint)(width * 4 * height), null);

        using var surface = _texture.QueryInterface<IDXGISurface>();
        _bitmap = ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.None));

        _width = width;
        _height = height;

        int maxSide = 192;
        double factor = Math.Min(1.0, maxSide / (double)Math.Max(1, Math.Max(width, height)));
        _alphaWidth = Math.Max(1, (int)Math.Round(width * factor));
        _alphaHeight = Math.Max(1, (int)Math.Round(height * factor));
        var alpha = new byte[_alphaWidth * _alphaHeight];
        for (int ay = 0; ay < _alphaHeight; ay++)
        {
            int sy = Math.Min(height - 1, ay * height / _alphaHeight);
            for (int ax = 0; ax < _alphaWidth; ax++)
            {
                int sx = Math.Min(width - 1, ax * width / _alphaWidth);
                alpha[ay * _alphaWidth + ax] = pixels[(sy * width + sx) * 4 + 3];
            }
        }
        _alpha = alpha;
    }

    public PvLayerImage Pump(in PvFrameContext ctx) =>
        _bitmap is null
            ? PvLayerImage.Empty
            : new PvLayerImage(_bitmap, null, _width, _height)
            {
                Alpha = _alpha,
                AlphaWidth = _alphaWidth,
                AlphaHeight = _alphaHeight,
            };

    public void Dispose()
    {
        _bitmap?.Dispose();
        _texture?.Dispose();
    }
}
