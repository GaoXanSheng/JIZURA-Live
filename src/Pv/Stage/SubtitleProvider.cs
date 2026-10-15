using JizuraLive.Engine;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JizuraLive.Pv;

/// <summary>
/// 字幕来源：提供当前的字幕时间轴与配置。
/// </summary>
public interface IPvSubtitleSource
{
    IReadOnlyList<KrcLine>? Lines { get; }
    long Version { get; }
}

/// <summary>
/// 纯 GPU 字幕图层：由 Direct2D/DirectWrite 原生排版（阶段三接入），作为图层栈的一层交给舞台。
/// </summary>
public sealed class SubtitleProvider : IPvLayerProvider, IDisposable
{
    private readonly ID2D1DeviceContext _ctx;
    private readonly D3DDevice _gpu;
    private readonly IPvSubtitleSource _source;

    private OffscreenTarget? _target;
    private int _w, _h;
    private long _version = 1;

    public string Id { get; }
    public long Version => _version;

    public SubtitleProvider(string id, D3DDevice gpu, ID2D1DeviceContext ctx, IPvSubtitleSource source)
    {
        Id = id;
        _gpu = gpu;
        _ctx = ctx;
        _source = source;
    }

    public void Render(double time, int width, int height)
    {
        width = Math.Max(16, width);
        height = Math.Max(16, height);

        if (_target is null || width != _w || height != _h)
        {
            _target?.Dispose();
            _target = OffscreenTarget.Create(_gpu, _ctx, width, height, sampleable: true);
            _w = width;
            _h = height;
            _version++;
        }

        // 清成全透明底（后续由 DirectWrite 绘制字幕排版）
        _ctx.Target = _target.Bitmap;
        _ctx.BeginDraw();
        _ctx.Clear(new Color4(0, 0, 0, 0));
        _ctx.EndDraw();
    }

    public PvLayerImage Pump(in PvFrameContext ctx) =>
        _target is null ? PvLayerImage.Empty : new PvLayerImage(_target.Bitmap, null, _w, _h);

    public void Dispose() => _target?.Dispose();
}
