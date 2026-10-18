using JizuraLive.Engine;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using JizuraLive.Render.Text;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using DWFactoryType = Vortice.DirectWrite.FactoryType;

namespace JizuraLive.Pv;

public interface IPvSubtitleSource
{
    IReadOnlyList<KrcLine>? Lines { get; }

    SubtitleConfig? Config { get; }

    long Version { get; }
}

public sealed class SubtitleProvider : IPvLayerProvider, IDisposable
{
    private const string FontFamily = "Microsoft YaHei UI";

    private readonly ID2D1DeviceContext _ctx;
    private readonly D3DDevice _gpu;
    private readonly IPvSubtitleSource _source;

    private readonly IDWriteFactory _dw;
    private readonly ID2D1Factory1 _factory;
    private readonly Immediate2D _draw;

    private OffscreenTarget? _target;
    private int _w, _h;
    private long _version = 1;

    private long _sourceVersion = -1;
    private SubtitleConfig? _seenConfig;
    private IReadOnlyList<LyricClip> _clips = Array.Empty<LyricClip>();

    public string Id { get; }
    public long Version => _version;

    public SubtitleProvider(string id, D3DDevice gpu, ID2D1DeviceContext ctx, IPvSubtitleSource source)
    {
        Id = id;
        _gpu = gpu;
        _ctx = ctx;
        _source = source;

        _dw = DWrite.DWriteCreateFactory<IDWriteFactory>(DWFactoryType.Shared);
        _factory = ctx.Factory.QueryInterface<ID2D1Factory1>();
        _draw = new Immediate2D(_factory, ctx, new DwFontCache(_dw));
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

        var cfg = _source.Config;
        if (_source.Version != _sourceVersion || !SameConfig(cfg, _seenConfig))
        {
            _sourceVersion = _source.Version;
            _seenConfig = Clone(cfg);
            _clips = LrcParser.ToClips(_source.Lines ?? Array.Empty<KrcLine>());
            _version++;
        }

        var font = _draw.Fonts.Get(FontFamily, SubtitleLayout.FontSizeOf(cfg, height),
            weight: FontWeight.Bold);
        var plan = SubtitleLayout.Plan(cfg, _clips, time, width, height,
            font.Ascent, font.LineHeight);

        _draw.SetTarget(_target!.Bitmap, width, height);
        _draw.Begin();
        _draw.Clear(new Color4(0f, 0f, 0f, 0f));

        if (plan is { } p) Draw(p, font);

        _draw.End();
    }

    private void Draw(in SubtitlePlan plan, DwFont font)
    {
        if (plan.Background is { } bg)
            _draw.FillRect(bg, new Paint2D
            {
                Brush = new SolidBrush2D(SubtitleLayout.ParseColor(_source.Config?.BgColor,
                    new Color4(0f, 0f, 0f, 0f))),
            });

        var strokePaint = new Paint2D { Brush = new SolidBrush2D(plan.Stroke) };
        var fillPaint = new Paint2D { Brush = new SolidBrush2D(plan.Fill) };

        for (var i = 0; i < plan.Lines.Length; i++)
        {
            var baseline = plan.FirstBaselineY + i * plan.LineHeight;

            foreach (var offset in plan.StrokeOffsets)
                _draw.DrawText(plan.Lines[i], plan.CenterX + offset.X, baseline + offset.Y,
                    TextAlign2D.Center, font, strokePaint);

            _draw.DrawText(plan.Lines[i], plan.CenterX, baseline, TextAlign2D.Center, font, fillPaint);
        }
    }

    public PvLayerImage Pump(in PvFrameContext ctx) =>
        _target is null ? PvLayerImage.Empty : new PvLayerImage(_target.Bitmap, null, _w, _h);

    public ReadbackImage? ReadbackTarget() => _target?.Readback();

    private static bool SameConfig(SubtitleConfig? a, SubtitleConfig? b) =>
        a is null || b is null
            ? ReferenceEquals(a, b)
            : a.Content == b.Content && a.Pos == b.Pos && a.Size == b.Size && a.Stroke == b.Stroke
              && a.Color == b.Color && a.StrokeColor == b.StrokeColor && a.BgColor == b.BgColor
              && a.MarginBottom == b.MarginBottom && a.MarginTop == b.MarginTop
              && a.HoldLastLine == b.HoldLastLine;

    private static SubtitleConfig? Clone(SubtitleConfig? c) => c is null
        ? null
        : new SubtitleConfig
        {
            Content = c.Content,
            Pos = c.Pos,
            Size = c.Size,
            Stroke = c.Stroke,
            Color = c.Color,
            StrokeColor = c.StrokeColor,
            BgColor = c.BgColor,
            MarginBottom = c.MarginBottom,
            MarginTop = c.MarginTop,
            HoldLastLine = c.HoldLastLine,
        };

    public void Dispose()
    {
        _draw.Dispose();
        _factory.Dispose();
        _dw.Dispose();
        _target?.Dispose();
        _target = null;
    }
}
