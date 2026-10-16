using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JizuraLive.Pv;

/// <summary>
/// 前景粒子层：把 <see cref="ParticleField"/> 求出的粒子按精灵画到本层离屏面。
///
/// 精灵几何（花瓣/星光）只建一次；每帧复用同一份 <see cref="ParticleState"/> 缓冲与画笔，
/// 稳态不分配。<see cref="PvParticleConfig"/> 是**逐帧读的引用**，改参数下一帧就生效。
/// </summary>
public sealed class ParticleLayerProvider : IPvLayerProvider, IDisposable
{
    /// <summary>单位精灵的基准像素尺寸（再乘 ParticleState.Scale）。</summary>
    private const float PetalPx = 26f;
    private const float StarPx = 22f;
    private const float BokehPx = 60f;

    private readonly D3DDevice _gpu;
    private readonly D2DHost _host;
    private readonly PvParticleConfig _cfg;
    private readonly ParticleState[] _buffer = new ParticleState[ParticleField.MaxParticles];
    private readonly Paint2D _paint = new();
    private readonly Path2D _petal;
    private readonly Path2D _star;

    private OffscreenTarget? _target;
    private Color4 _brushColor;
    private string? _brushHex;
    private bool _brushReady;
    private double _renderedTime = double.NaN;
    private int _w, _h;
    private long _version = 1;

    public string Id { get; }
    public long Version => _version;
    public int Width => _w;
    public int Height => _h;
    public int DesignWidth { get; }
    public int DesignHeight { get; }

    public ParticleLayerProvider(string id, D3DDevice gpu, D2DHost host, PvParticleConfig cfg,
        int designWidth, int designHeight)
    {
        Id = id;
        _gpu = gpu;
        _host = host;
        _cfg = cfg;
        DesignWidth = designWidth;
        DesignHeight = designHeight;
        _petal = BuildPetal(host.Factory);
        _star = BuildStar(host.Factory);
    }

    public PvLayerImage Pump(in PvFrameContext ctx)
    {
        // 管线每帧在合成之前先调一次 Render（见 PvPipeline.Prepare）；直接 Pump（测试/诊断）时在这里补画
        if (ctx.Time != _renderedTime) Render(ctx.Time);
        return _target is null ? PvLayerImage.Empty : new PvLayerImage(_target.Bitmap, null, _w, _h);
    }

    /// <summary>
    /// 求值并画好本时刻的内容。**必须在帧合成之外调用**：本层要用共享的 Immediate2D 切到自己的离屏面
    /// （SetTarget + Begin/End），在合成器的绘制批次里做这件事会打断那一批，整帧都贴不出去。
    /// </summary>
    public void Render(double time)
    {
        var w = Math.Max(16, DesignWidth);
        var h = Math.Max(16, DesignHeight);
        if (_target is null || w != _w || h != _h)
        {
            _target?.Dispose();
            _target = OffscreenTarget.Create(_gpu, _host.Ctx, w, h, sampleable: true);
            _w = w;
            _h = h;
            _version++;
        }

        var count = ParticleField.Compute(_cfg, time, w, h, _buffer);
        var ambient = Math.Min(ParticleField.AmbientCount(_cfg), count);

        // ParseHex 每次都会分配字符串：颜色串没换就不重新解析
        if (!_brushReady || !ReferenceEquals(_cfg.Color, _brushHex))
        {
            _brushReady = true;
            _brushHex = _cfg.Color;
            var color = PvScene.ParseHex(_cfg.Color);
            if (!SameColor(color, _brushColor))
            {
                _brushColor = color;
                _paint.Brush = new SolidBrush2D(color);
            }
        }

        var kind = _cfg.Kind;
        var sparkle = string.Equals(kind, "sparkle", StringComparison.Ordinal);
        var bokeh = string.Equals(kind, "bokeh", StringComparison.Ordinal);

        var d = _host.Draw;
        d.SetTarget(_target.Bitmap, w, h);
        d.Begin();
        d.Clear(new Color4(0f, 0f, 0f, 0f));

        for (var i = 0; i < count; i++)
        {
            var s = _buffer[i];
            if (s.Alpha <= 0.002f) continue;

            _paint.Alpha = Math.Clamp(s.Alpha, 0f, 1f);
            var px = s.Scale * BaseSize(i < ambient, sparkle, bokeh);

            // 顺序必须是 Scale → Rotate → Translate：Immediate2D 的矩阵按行向量右乘，
            // 先 Translate 再 Scale 会把平移量一起放大，精灵会飞出画面。
            d.Save();
            d.Scale(px, px);
            d.Rotate(s.Rotation);
            d.Translate(s.X, s.Y);

            if (i < ambient)
            {
                if (sparkle) DrawStar(d);
                else if (bokeh) DrawBokeh(d);
                else d.FillPath(_petal, _paint);
            }
            else
            {
                DrawStar(d); // 爆发粒子一律星光
            }

            d.Restore();
        }

        d.End();
        _renderedTime = time;
    }

    /// <summary>
    /// 回读本层离屏面的像素（**仅测试/诊断用**：会阻塞到 GPU 完成，逐帧调用会拖垮帧率）。
    /// </summary>
    public ReadbackImage? ReadbackTarget() => _target?.Readback();

    /// <summary>四角星 + 小光晕圆（单位几何，实际大小由外层 Scale 决定）。</summary>
    private void DrawStar(Immediate2D d)
    {
        var alpha = _paint.Alpha;
        _paint.Alpha = alpha * 0.35f;
        d.FillCircle(0f, 0f, 0.32f, _paint);
        _paint.Alpha = alpha;
        d.FillPath(_star, _paint);
    }

    /// <summary>同心圆叠出的软圆盘：由内向外逐层加大、逐层降低 alpha，中心最亮、边缘化开。</summary>
    private void DrawBokeh(Immediate2D d)
    {
        var alpha = _paint.Alpha;
        _paint.Alpha = alpha * 0.35f;
        d.FillCircle(0f, 0f, 0.25f, _paint);
        _paint.Alpha = alpha * 0.24f;
        d.FillCircle(0f, 0f, 0.45f, _paint);
        _paint.Alpha = alpha * 0.16f;
        d.FillCircle(0f, 0f, 0.70f, _paint);
        _paint.Alpha = alpha * 0.10f;
        d.FillCircle(0f, 0f, 1.00f, _paint);
        _paint.Alpha = alpha;
    }

    /// <summary>环境粒子按 Kind 取精灵尺寸，爆发星光固定用星光尺寸。</summary>
    private static float BaseSize(bool ambient, bool sparkle, bool bokeh) =>
        ambient ? sparkle ? StarPx : bokeh ? BokehPx : PetalPx : StarPx;

    /// <summary>带缺口的椭圆花瓣（单位几何，尖端朝上、底部内凹）。</summary>
    private static Path2D BuildPetal(ID2D1Factory factory)
    {
        var p = new Path2D(factory);
        p.MoveTo(0f, -1f);
        p.CubicTo(0.62f, -0.74f, 0.78f, 0.22f, 0.20f, 0.88f);
        p.CubicTo(0.10f, 0.70f, -0.10f, 0.70f, -0.20f, 0.88f);
        p.CubicTo(-0.78f, 0.22f, -0.62f, -0.74f, 0f, -1f);
        return p.Close();
    }

    /// <summary>四角星（单位几何，凹边由二次贝塞尔给出）。</summary>
    private static Path2D BuildStar(ID2D1Factory factory)
    {
        var p = new Path2D(factory);
        p.MoveTo(0f, -1f);
        p.QuadTo(0.16f, -0.16f, 1f, 0f);
        p.QuadTo(0.16f, 0.16f, 0f, 1f);
        p.QuadTo(-0.16f, 0.16f, -1f, 0f);
        p.QuadTo(-0.16f, -0.16f, 0f, -1f);
        return p.Close();
    }

    private static bool SameColor(Color4 a, Color4 b) =>
        a.R == b.R && a.G == b.G && a.B == b.B && a.A == b.A;

    public void Dispose()
    {
        _target?.Dispose();
        _petal.Dispose();
        _star.Dispose();
    }
}
