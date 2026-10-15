using Vortice.Direct2D1;

namespace JizuraLive.Render.Draw;

/// <summary>
/// 一张**调用方持有**的离屏面，可作为合成/转场的输入。
///
/// 与 <see cref="Immediate2D.PushSurface"/>（内部池化、用完即还）的区别：这里的面由调用方长期持有，
/// 典型用法是转场——把「上一个 cut 的末帧」与「当前 cut 的首帧」各渲一张面，再交给
/// <see cref="Immediate2D.DrawComposite"/> 按混合模式合起来（对位旧引擎的 <c>_transA</c>/<c>_transB</c>）。
/// </summary>
public sealed class Surface2D : IDisposable
{
    public int Width { get; }
    public int Height { get; }

    internal ID2D1Bitmap1 Bitmap { get; }

    /// <summary>作为效果输入/绘制源的图像视图。</summary>
    internal ID2D1Image Image => Bitmap;

    internal Surface2D(ID2D1Bitmap1 bitmap, int width, int height)
    {
        Bitmap = bitmap;
        Width = width;
        Height = height;
    }

    public void Dispose() => Bitmap.Dispose();
}
