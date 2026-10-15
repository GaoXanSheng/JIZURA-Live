using System.IO;
using JizuraLive.Logging;
using JizuraLive.Render.Draw;
using Vortice.Direct2D1;

namespace JizuraLive.Pv;

/// <summary>
/// 磁盘图片图层：把一张图片文件（PNG/JPEG/BMP/WebP…）当图层交给 PV 舞台，文件改了自动重载。
///
/// 取景不在这里做：本层只交出"位图 + 原尺寸"，Cover/Contain 的缩放与裁切由既有的
/// <see cref="PvStageCompositor"/> 按 <see cref="PvLayerConfig.Cover"/> 统一算。
/// 两处各算一次取景，迟早会出现"改了图层配置、画面不动"。
///
/// 解码走 <see cref="Image2D"/>（WIC）。D2D 位图必须与绘制在同一线程上建，所以加载与重载都发生在
/// <see cref="Pump"/> 里——与 <see cref="SpoutSourceProvider"/> 建 surface 位图是同一个时机、同一条理由。
/// </summary>
public sealed class FileImageProvider : IPvLayerProvider, IDisposable
{
    private readonly ID2D1DeviceContext _ctx;
    private readonly string _path;

    private Image2D? _image;
    private long _version = 1;

    /// <summary>
    /// 上一次核对的文件戳（最后写入时刻的 Ticks；&lt; 0 = 当时文件不存在）。
    /// 一个长整型字段而不是可空结构：帧内比较不做装箱，也不依赖可空结构的相等语义。
    /// </summary>
    private long _stampTicks = -1;

    /// <summary>这一戳是否已经加载/试过（成败都算）：同一个戳不重试，文件再改一次才重来。</summary>
    private bool _stampTried;

    public string Id { get; }
    public long Version => _version;

    /// <summary>最近一次加载失败的原因（null = 正常）。与 Spout 层同款：失败要留下可读原因，不静默吞。</summary>
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

    /// <summary>
    /// 每帧核对一次文件戳（一次 stat），戳变了才重新解码。
    ///
    /// 这一处读盘是刻意的：本层唯一的 IO 就是"读这张图"，而它的**解码**必须留在渲染线程
    /// （D2D 位图只能与绘制同线程建，见类注释），所以判龄也只能在同一处。真去起一条
    /// FileSystemWatcher，就是为本线加一条监听线程 + 跨线程回调，代价比一次 stat 大得多；
    /// 而一次 stat 的开销远小于本帧的任何一次取帧。写盘仍然只发生在 UI 线程（工程存盘）。
    /// </summary>
    private void Sync()
    {
        var ticks = Stamp();
        if (_stampTried && ticks == _stampTicks) return;

        // 先记戳再加载：同一个坏文件不每帧重试（异常很贵，每帧抛一次会把渲染线程拖垮），
        // 文件下次改动带来新戳，自然重来。
        _stampTried = true;
        _stampTicks = ticks;

        try
        {
            // 先建新的再换旧的：解码失败（改文件改到一半）时上一张还在，图层不闪黑
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
            // 同一条原因只报一次：每帧刷日志等于没日志
            if (!string.Equals(ex.Message, LastError, StringComparison.Ordinal))
                Log.Warn($"[PV/图片] 载入失败 {_path}：{ex.Message}；本层保持上一张已载入的图（文件再改动会重试）");

            LastError = ex.Message;
        }
    }

    /// <summary>
    /// 文件的最后写入时刻（Ticks）；不存在或读不到返回 -1。
    /// 只认 mtime 不叠长度：换文件时 mtime 一定会变，而叠长度要多建一个 FileInfo（帧路径的托管分配），不值。
    /// </summary>
    private long Stamp()
    {
        try
        {
            // 静态 API 返回结构体，帧内不产生托管分配；文件不存在时它给的是 1601 哨兵值，映射成 -1
            var ticks = File.GetLastWriteTimeUtc(_path).Ticks;
            return ticks <= 0 ? -1L : ticks;
        }
        catch (Exception)
        {
            // 路径非法/权限一类：按"这次读不到"处理，已加载的那张继续用
            return -1L;
        }
    }

    public void Dispose()
    {
        _image?.Dispose();
        _image = null;
    }
}
