using System.IO;
using JizuraLive.Logging;
using JizuraLive.Render.Draw;
using Vortice.Direct2D1;

namespace JizuraLive.Pv;

public sealed class FileImageProvider : IPvLayerProvider, IDisposable
{
    private readonly ID2D1DeviceContext _ctx;
    private readonly string _path;

    private Image2D? _image;
    private long _version = 1;

    private long _stampTicks = -1;

    private bool _stampTried;

    public string Id { get; }
    public long Version => _version;

    public string? LastError { get; private set; }

    public FileImageProvider(string id, ID2D1DeviceContext ctx, string path)
    {
        Id = id;
        _ctx = ctx;
        _path = path;
    }

    public PvLayerImage Pump(in PvFrameContext ctx)
    {
        Sync();

        return _image is null
            ? PvLayerImage.Empty
            : new PvLayerImage(_image.Bitmap, null, _image.Width, _image.Height);
    }

    private void Sync()
    {
        var ticks = Stamp();
        if (_stampTried && ticks == _stampTicks) return;

        _stampTried = true;
        _stampTicks = ticks;

        try
        {
            var fresh = Image2D.FromFile(_ctx, _path);
            var stale = _image;
            _image = fresh;
            stale?.Dispose();
            _version++;
            LastError = null;
            Log.Info($"[PV/图片] 已载入 {_path}（{fresh.Width}×{fresh.Height}）");
        }
        catch (Exception ex)
        {
            if (!string.Equals(ex.Message, LastError, StringComparison.Ordinal))
                Log.Warn($"[PV/图片] 载入失败 {_path}：{ex.Message}；本层保持上一张已载入的图（文件再改动会重试）");

            LastError = ex.Message;
        }
    }

    private long Stamp()
    {
        try
        {
            var ticks = File.GetLastWriteTimeUtc(_path).Ticks;
            return ticks <= 0 ? -1L : ticks;
        }
        catch (Exception)
        {
            return -1L;
        }
    }

    public void Dispose()
    {
        _image?.Dispose();
        _image = null;
    }
}
