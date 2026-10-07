using System.IO;
using System.IO.Compression;
using SkiaSharp;

namespace JizuraLive.Export;

/// <summary>
/// PNG 序列 zip（对位 11_export.js 的 exportPNGZip）：
/// 平铺模式帧名 frame_0001.png；分层模式 back/frame_0001.png + front/frame_0001.png
/// （front 恒透明底，供后期叠加）。ZipArchive 直写磁盘，大序列不占内存。
/// </summary>
public sealed class PngSequenceWriter : IDisposable
{
    private readonly ZipArchive _zip;

    public PngSequenceWriter(string zipPath, bool layered)
    {
        Layered = layered;
        _zip = new ZipArchive(new FileStream(zipPath, FileMode.Create, FileAccess.Write), ZipArchiveMode.Create);
    }

    public bool Layered { get; }

    /// <summary>layer: null=平铺；"back"/"front"=分层目录。</summary>
    public void AddFrame(string? layer, int index, SKBitmap bmp)
    {
        var name = layer == null ? $"frame_{index:D4}.png" : $"{layer}/frame_{index:D4}.png";
        var entry = _zip.CreateEntry(name, CompressionLevel.NoCompression); // PNG 已压缩
        using var es = entry.Open();
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 90);
        data.SaveTo(es);
    }

    public void Finish()
    {
        _zip.Dispose();
    }

    public void Dispose() => _zip.Dispose();
}
