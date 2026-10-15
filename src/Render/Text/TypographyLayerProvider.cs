using System.Numerics;
using JizuraLive.Engine;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JizuraLive.Render.Text;

/// <summary>
/// 纯 Direct2D / DirectWrite 打造的现代实时 PV 歌词排版层。
/// 支持毫秒级时间轴匹配、字级动态入场与高级感阴影发光。
/// </summary>
public sealed class TypographyLayerProvider : IPvLayerProvider, IDisposable
{
    private readonly D3DDevice _gpu;
    private readonly D2DHost _host;
    private OffscreenTarget? _target;
    private IReadOnlyList<KrcLine> _lines = Array.Empty<KrcLine>();

    private int _w, _h;
    private double _scale = 1.0;
    private long _version = 1;

    public string Id { get; }
    public long Version => _version;
    public int Width => _w;
    public int Height => _h;
    public int DesignWidth { get; }
    public int DesignHeight { get; }

    public TypographyLayerProvider(string id, D3DDevice gpu, D2DHost host,
        int designWidth = 1920, int designHeight = 1080)
    {
        Id = id;
        _gpu = gpu;
        _host = host;
        DesignWidth = designWidth;
        DesignHeight = designHeight;
    }

    public void SetLyrics(IReadOnlyList<KrcLine>? lines)
    {
        _lines = lines ?? Array.Empty<KrcLine>();
        _version++;
    }

    public void SetScale(double scale)
    {
        scale = Math.Clamp(scale, 0.05, 1.0);
        if (Math.Abs(scale - _scale) < 1e-4) return;
        _scale = scale;
        _version++;
    }

    public void Render(double time)
    {
        var w = Math.Max(16, (int)Math.Round(DesignWidth * _scale));
        var h = Math.Max(16, (int)Math.Round(DesignHeight * _scale));

        if (_target is null || w != _w || h != _h)
        {
            _target?.Dispose();
            _target = OffscreenTarget.Create(_gpu, _host.Ctx, w, h, sampleable: true);
            _w = w;
            _h = h;
            _version++;
        }

        var d = _host.Draw;
        d.SetTarget(_target.Bitmap, w, h);
        d.Begin();
        d.Clear(new Color4(0f, 0f, 0f, 0f));

        if (_lines.Count > 0)
        {
            var idx = LrcParser.FindLineIndex(_lines, time);
            if (idx >= 0)
            {
                var cur = _lines[idx];
                var tMs = (long)(time * 1000.0);
                var elapsed = Math.Max(0, tMs - cur.StartMs);
                var progress = cur.DurMs > 0 ? Math.Clamp((float)elapsed / cur.DurMs, 0f, 1f) : 1f;

                // 进出场曲线计算
                float alpha = 1f;
                float offsetY = 0f;
                if (progress < 0.15f)
                {
                    // 进场前 15%：上浮 + 渐现
                    var inP = progress / 0.15f;
                    alpha = inP;
                    offsetY = (1f - inP) * 16f * (float)_scale;
                }
                else if (progress > 0.85f)
                {
                    // 退场后 15%：微沉 + 渐隐
                    var outP = (progress - 0.85f) / 0.15f;
                    alpha = 1f - outP;
                    offsetY = -outP * 10f * (float)_scale;
                }

                var font = d.Fonts.Get("Microsoft YaHei UI", 52f * (float)_scale, weight: Vortice.DirectWrite.FontWeight.Bold);
                var cx = w / 2f;
                var cy = h * 0.75f + offsetY;

                // 1) 柔和外发光底影
                d.DrawText(cur.Text, cx, cy + 2f, TextAlign2D.Center, font, new Paint2D
                {
                    Brush = new SolidBrush2D(new Color4(0f, 0.05f, 0.15f, alpha * 0.6f)),
                });

                // 2) 主高亮正文
                d.DrawText(cur.Text, cx, cy, TextAlign2D.Center, font, new Paint2D
                {
                    Brush = new SolidBrush2D(new Color4(1f, 1f, 1f, alpha)),
                });
            }
        }

        d.End();
    }

    public PvLayerImage Pump(in PvFrameContext ctx) =>
        _target is null ? PvLayerImage.Empty : new PvLayerImage(_target.Bitmap, null, _w, _h);

    public void Dispose()
    {
        _target?.Dispose();
    }
}
