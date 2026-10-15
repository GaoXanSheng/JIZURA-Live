using JizuraLive.Render.Draw;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JizuraLive.Pv;

/// <summary>
/// 一帧的输入快照（设计像素 + 时间 + 音频能量/节拍）。
/// 由帧入口在边界处读一次时钟后构造，整帧复用同一份；任何环节都不许回头再读。
/// </summary>
public readonly record struct PvFrameContext(
    double Time,
    double Beat,
    double Energy,
    int Width,
    int Height);

/// <summary>
/// 图层提供者：PV 舞台只认「给我这一帧要画的图」。
/// 时钟不归它管——帧入口在边界处读一次，随 <see cref="PvFrameContext"/> 整帧传进来，
/// 图层自己再读钟就会出现同一帧里前后不一的时间口径。
/// 它不知道也不该知道图是从哪来的（引擎歌词 / Spout 角色 / 字幕 / 纯色）。
/// </summary>
public interface IPvLayerProvider : IDisposable
{
    /// <summary>稳定标识（图层配置里按它绑定）。</summary>
    string Id { get; }

    /// <summary>内容版本：换源/改参数时变化，用于脏判断。</summary>
    long Version { get; }

    /// <summary>取本帧内容（没有内容返回 <see cref="PvLayerImage.Empty"/>）。</summary>
    PvLayerImage Pump(in PvFrameContext ctx);
}

/// <summary>纯色/底色层（背景衬底用）。</summary>
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

/// <summary>固定位图层（测试与演示用；真实场景由歌词层/Spout 层实现同一接口）。</summary>
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

    /// <summary>换一张图（演示里用来模拟"源在动"）。</summary>
    public void Swap(ID2D1Bitmap bitmap)
    {
        _bitmap = bitmap;
        Version++;
    }

    public void Dispose()
    {
    }
}
