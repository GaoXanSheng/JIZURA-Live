using JizuraLive.Render.Draw;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JizuraLive.Pv;

public readonly record struct PvFrameContext(
    double Time,
    double Beat,
    double Energy,
    int Width,
    int Height);

public interface IPvLayerProvider : IDisposable
{
    string Id { get; }

    long Version { get; }

    PvLayerImage Pump(in PvFrameContext ctx);
}

public sealed class ColorProvider : IPvLayerProvider
{
    private Color4 _color;
    private long _version = 1;

    public string Id { get; }

    public ColorProvider(string id, Color4 color)
    {
        Id = id;
        _color = color;
    }

    public long Version => _version;

    public Color4 Color
    {
        get => _color;
        set
        {
            _color = value;
            Interlocked.Increment(ref _version);
        }
    }

    public PvLayerImage Pump(in PvFrameContext ctx) =>
        PvLayerImage.FromSolid(_color, ctx.Width, ctx.Height);

    public void Dispose()
    {
    }
}

public sealed class TextureProvider : IPvLayerProvider
{
    private ID2D1Bitmap _bitmap;
    private readonly int _width;
    private readonly int _height;

    public string Id { get; }
    public long Version { get; private set; } = 1;

    public TextureProvider(string id, ID2D1Bitmap bitmap, int width, int height)
    {
        Id = id;
        _bitmap = bitmap;
        _width = width;
        _height = height;
    }

    public PvLayerImage Pump(in PvFrameContext ctx) => new(_bitmap, null, _width, _height);

    public void Swap(ID2D1Bitmap bitmap)
    {
        _bitmap = bitmap;
        Version++;
    }

    public void Dispose()
    {
    }
}
