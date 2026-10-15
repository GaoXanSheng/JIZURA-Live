using JizuraLive.Engine;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using JizuraLive.Render.Text;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using DWFactoryType = Vortice.DirectWrite.FactoryType;

namespace JizuraLive.Pv;

/// <summary>
/// 字幕来源：提供当前的字幕时间轴与配置。
/// </summary>
public interface IPvSubtitleSource
{
    IReadOnlyList<KrcLine>? Lines { get; }

    /// <summary>字幕配置（可空 = 未配置，等价于不画）。生产侧目前不传，接线归界面阶段。</summary>
    SubtitleConfig? Config { get; }

    long Version { get; }
}

/// <summary>
/// 纯 GPU 字幕图层：DirectWrite 排版 + 描边绘字，作为图层栈的一层交给舞台。
///
/// SubtitleConfig 单位约定（界面阶段核对用，依据都是默认值量级）：
///   * <c>Size</c>（默认 4.5）、<c>MarginTop/MarginBottom</c>（默认 4.0）是**设计高度百分比**：
///     4.5 → 1080 下 48.6px（与动效歌词层 TypographyLayerProvider 的 52px 同量级）；
///     4.0 → 1080 下 43.2px 边距。
///   * <c>Stroke</c>（默认 4.0）是**设计像素**，不随画布高度缩放：4.0 → 4px 描边宽。
///   * 颜色串：<c>Color/StrokeColor</c> 为 #rgb|#rrggbb，<c>BgColor</c> 为 rgba(r,g,b,a)。
/// 排版规则全在 <see cref="SubtitleLayout"/>（纯函数、可脱设备测试），这里只负责把规划落到 D2D。
/// </summary>
public sealed class SubtitleProvider : IPvLayerProvider, IDisposable
{
    /// <summary>默认字族：SubtitleConfig 没有族字段，沿用 DwFontCache 的兜底同名族（DwFont.cs:122）。</summary>
    private const string FontFamily = "Microsoft YaHei UI";

    private readonly ID2D1DeviceContext _ctx;
    private readonly D3DDevice _gpu;
    private readonly IPvSubtitleSource _source;

    // 排版要用的 DWrite 工厂与字体缓存**自带一套**：传进来的 ctx 归调用方（D2DHost）所有，
    // 但它绑的工厂/字体缓存不在我们手里，字幕借不到（D2DHost 只暴露 Ctx）。
    // Immediate2D.Dispose 会连带释放它持有的 Fonts，所以这里不再单独 Dispose _fonts。
    private readonly IDWriteFactory _dw;
    private readonly ID2D1Factory1 _factory;
    private readonly Immediate2D _draw;

    private OffscreenTarget? _target;
    private int _w, _h;
    private long _version = 1;

    private long _sourceVersion = -1;
    private SubtitleConfig? _seenConfig;

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
        // 配置改了也要脏：源版本号只覆盖歌词文本，界面改字号/颜色时它不会动
        if (_source.Version != _sourceVersion || !SameConfig(cfg, _seenConfig))
        {
            _sourceVersion = _source.Version;
            _seenConfig = Clone(cfg);
            _version++;
        }

        // 字号是纯公式（与 SubtitleLayout 同一处实现）：先算出来才能建字体、拿到 ascent/行高
        var font = _draw.Fonts.Get(FontFamily, SubtitleLayout.FontSizeOf(cfg, height),
            weight: FontWeight.Bold);
        // ToClips 直接读 Count，源没给歌词时得给空表（空表 → Plan 返回 null → 只清屏）
        var clips = LrcParser.ToClips(_source.Lines ?? Array.Empty<KrcLine>());
        var plan = SubtitleLayout.Plan(cfg, clips, time, width, height,
            font.Ascent, font.LineHeight);

        _draw.SetTarget(_target!.Bitmap, width, height);
        _draw.Begin();
        // 清成全透明底：文字离开区间必须清屏，绝不能把上一帧位图继续当新句（契约 PV-DIRECTOR-SYSTEM.md:397）
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

            // 描边在前、填充在后：中心那笔由填充覆盖，圆周偏移那圈留在字外沿（DrawText 的 y 是基线）
            foreach (var offset in plan.StrokeOffsets)
                _draw.DrawText(plan.Lines[i], plan.CenterX + offset.X, baseline + offset.Y,
                    TextAlign2D.Center, font, strokePaint);

            _draw.DrawText(plan.Lines[i], plan.CenterX, baseline, TextAlign2D.Center, font, fillPaint);
        }
    }

    public PvLayerImage Pump(in PvFrameContext ctx) =>
        _target is null ? PvLayerImage.Empty : new PvLayerImage(_target.Bitmap, null, _w, _h);

    /// <summary>
    /// 回读当前字幕面（测试 / 诊断用）。**会阻塞到 GPU 完成**，绝不能在渲染循环里调。
    /// </summary>
    public ReadbackImage? ReadbackTarget() => _target?.Readback();

    /// <summary>配置是活对象（界面直接改 Settings.Subtitle），得跟快照比，不然改参数不会脏。</summary>
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
        // 只释放自己建的：_draw 连带释放它持有的 DwFontCache，_factory 是本类 QueryInterface 出来的引用。
        // 传进来的 ctx/gpu 归调用方（D2DHost/PvPipeline）所有，这里绝不 Dispose。
        _draw.Dispose();
        _factory.Dispose();
        _dw.Dispose();
        _target?.Dispose();
        _target = null;
    }
}
