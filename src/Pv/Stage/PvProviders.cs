using JizuraLive.Render.Draw;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JizuraLive.Pv;

/// <summary>PV 舞台一帧的上下文（设计像素 + 时间 + 音频能量/节拍）。</summary>
public readonly record struct PvFrameContext(
    double Time,
    double Beat,
    double Energy,
    int Width,
    int Height);

/// <summary>
/// 图层提供者：PV 舞台只认「给我这一帧要画的图 + 它自己的时钟」。
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

/// <summary>
/// 驱动器：把 PV 舞台与「音乐从哪来」解耦（云歌单 / 本地文件 / 编辑器实拍 / 手动）。
/// PV 舞台只管展示与切换，不依赖任何具体音乐来源。
/// </summary>
public interface IPvDriver : IDisposable
{
    string Name { get; }
    double Time { get; }
    double Duration { get; }
    bool IsPlaying { get; }

    void Play();
    void Pause();
    void Seek(double time);

    /// <summary>换歌/时钟来源变化。</summary>
    event Action? Changed;
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

/// <summary>
/// 手动驱动器：时间由外部推（编辑器实拍 / 测试 / 手动拖时间轴）。
/// 这是最"笨"的驱动器，但正因为笨，它让 PV 舞台可以完全脱离云歌单与音频跑起来。
/// </summary>
public sealed class ManualDriver : IPvDriver
{
    private double _time;
    private double _duration = 180;
    private bool _playing;

    public string Name => "手动";
    public double Time => _time;
    public double Duration => _duration;
    public bool IsPlaying => _playing;

    public event Action? Changed;

    public void SetDuration(double seconds)
    {
        _duration = Math.Max(0.01, seconds);
        Changed?.Invoke();
    }

    public void Play()
    {
        _playing = true;
        Changed?.Invoke();
    }

    public void Pause()
    {
        _playing = false;
        Changed?.Invoke();
    }

    public void Seek(double time)
    {
        _time = Math.Clamp(time, 0, _duration);
        Changed?.Invoke();
    }

    /// <summary>按墙钟推进（宿主每帧调用）。</summary>
    public void Advance(double seconds)
    {
        if (!_playing) return;
        _time = Math.Clamp(_time + seconds, 0, _duration);
    }

    public void Dispose()
    {
    }
}
