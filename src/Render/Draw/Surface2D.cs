using Vortice.Direct2D1;

namespace JizuraLive.Render.Draw;

public sealed class Surface2D : IDisposable
{
    public int Width { get; }
    public int Height { get; }

    internal ID2D1Bitmap1 Bitmap { get; }

    internal ID2D1Image Image => Bitmap;

    internal Surface2D(ID2D1Bitmap1 bitmap, int width, int height)
    {
        Bitmap = bitmap;
        Width = width;
        Height = height;
    }

    public void Dispose() => Bitmap.Dispose();
}
