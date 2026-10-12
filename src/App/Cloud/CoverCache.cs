using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media.Imaging;
using JizuraLive.Logging;

namespace JizuraLive.App.Cloud;

/// <summary>
/// 歌单/歌曲封面缩略图（只存内存，不落盘）：按 URL 去重 + 限量并发，失败也记一份（免得每次重渲染都重试）。
/// 解码时按显示尺寸缩（DecodePixelWidth），63 张卡片也就几百 KB，不占 LOH。
/// </summary>
public static class CoverCache
{
    private sealed class Entry
    {
        public BitmapImage? Image;
        public readonly List<Action<BitmapImage?>> Callbacks = [];
        public bool Failed;
    }

    private const int DecodeWidth = 96;   // 卡片 42px、播放条 40px，2x 屏也够
    private const int MaxParallel = 4;
    private const int MaxFiles = 500;     // 磁盘缓存张数上限（约 15–30MB；LRU 删最旧）

    /// <summary>封面磁盘缓存目录（在 cache\ 下，设置页「清理缓存」会自动带上）。</summary>
    private static string CoverDir => Path.Combine(AppDirs.CacheDir, "covers");

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim Gate = new(MaxParallel);
    private static readonly Dictionary<string, Entry> Cache = new(StringComparer.Ordinal);
    private static readonly object Lock = new();

    /// <summary>取封面（命中立即返回；未命中返回 null 并在后台拉，拉完回调 <paramref name="onLoaded"/>）。
    /// 同一 URL 的并发请求挂到同一次下载上，拉完每个调用方都收到回调。</summary>
    public static BitmapImage? Get(string url, Action<BitmapImage?> onLoaded)
    {
        if (string.IsNullOrEmpty(url)) return null;
        Entry entry;
        var start = false;
        lock (Lock)
        {
            if (!Cache.TryGetValue(url, out entry!))
            {
                entry = new Entry();
                Cache[url] = entry;
                start = true;
            }
            if (entry.Image != null) return entry.Image;
            if (entry.Failed) return null; // 拉过且失败：不再重试
            entry.Callbacks.Add(onLoaded);
        }
        if (start) _ = LoadAsync(url, entry);
        return null;
    }

    private static async Task LoadAsync(string url, Entry entry)
    {
        BitmapImage? bmp = null;
        try
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                bmp = await LoadFromDiskOrNetAsync(url).ConfigureAwait(false);
            }
            finally
            {
                Gate.Release();
            }
        }
        catch (Exception ex)
        {
            Log.Debug("[Cloud] 封面拉取失败：" + ex.Message);
        }

        Action<BitmapImage?>[] callbacks;
        lock (Lock)
        {
            entry.Image = bmp;
            entry.Failed = bmp == null;
            callbacks = [.. entry.Callbacks];
            entry.Callbacks.Clear();
        }
        // 回 UI 线程填图；面板可能已经关掉（回调里自己判活）
        void Fire()
        {
            foreach (var cb in callbacks) cb(bmp);
        }

        var d = System.Windows.Application.Current?.Dispatcher;
        if (d != null && !d.HasShutdownStarted) d.BeginInvoke(Fire);
        else Fire();
    }

    /// <summary>先看磁盘（重启后也命中），没有才联网并落盘；坏文件就地删掉重下。</summary>
    private static async Task<BitmapImage?> LoadFromDiskOrNetAsync(string url)
    {
        var file = PathOf(url);
        if (File.Exists(file))
        {
            try
            {
                var bytes = await File.ReadAllBytesAsync(file).ConfigureAwait(false);
                var hit = Decode(bytes);
                if (hit != null)
                {
                    Touch(file);
                    return hit;
                }
                File.Delete(file); // 半截/坏图：删了重下
            }
            catch (Exception ex)
            {
                Log.Debug("[Cloud] 封面缓存读取失败（重下）：" + ex.Message);
            }
        }

        var net = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
        var bmp = Decode(net);
        if (bmp != null) await SaveAsync(file, net).ConfigureAwait(false);
        return bmp;
    }

    private static async Task SaveAsync(string file, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(CoverDir);
            var tmp = file + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            File.Move(tmp, file, true);
            TrimIfNeeded();
        }
        catch (Exception ex)
        {
            Log.Debug("[Cloud] 封面缓存落盘失败：" + ex.Message);
        }
    }

    private static string PathOf(string url) => Path.Combine(CoverDir,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..16].ToLowerInvariant() + ".img");

    /// <summary>摸一下修改时间当作 LRU 的访问时间（Windows 的"最后访问时间"常被关掉）。</summary>
    private static void Touch(string file)
    {
        try { File.SetLastWriteTimeUtc(file, DateTime.UtcNow); }
        catch { /* 摸不动就算了 */ }
    }

    /// <summary>张数超上限就按最后写入时间删最旧（一次清到 90%，不是每张都扫目录）。</summary>
    private static void TrimIfNeeded()
    {
        try
        {
            var files = new DirectoryInfo(CoverDir).GetFiles("*.img");
            if (files.Length <= MaxFiles) return;
            foreach (var f in files.OrderBy(f => f.LastWriteTimeUtc).Take(files.Length - (int)(MaxFiles * 0.9)))
            {
                try { f.Delete(); } catch { /* 正被读着 */ }
            }
        }
        catch (Exception ex)
        {
            Log.Debug("[Cloud] 封面缓存清理失败：" + ex.Message);
        }
    }

    private static BitmapImage? Decode(byte[] bytes)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad; // 一次读完，文件流可以立刻关
            bmp.DecodePixelWidth = DecodeWidth;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze(); // 跨线程共享
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Debug("[Cloud] 封面解码失败：" + ex.Message);
            return null;
        }
    }
}
