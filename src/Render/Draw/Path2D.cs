using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JizuraLive.Render.Draw;

public sealed class Path2D : IDisposable
{
    private const float Kappa = 0.5522847f;

    private readonly ID2D1Factory _factory;
    private ID2D1PathGeometry? _geo;
    private ID2D1GeometrySink? _sink;
    private Vector2 _cur;
    private bool _figureOpen;
    private bool _finished;

    public Path2D(ID2D1Factory factory) => _factory = factory;

    public bool IsEmpty => _geo is null;

    public FillMode FillMode { get; set; } = FillMode.Winding;

    public Path2D MoveTo(float x, float y)
    {
        EnsureEditable();
        EndFigureIfOpen();
        var s = EnsureSink();
        _cur = new Vector2(x, y);
        s.BeginFigure(_cur, FigureBegin.Filled);
        _figureOpen = true;
        return this;
    }

    public Path2D LineTo(float x, float y)
    {
        var s = EnsureSink();
        if (!_figureOpen) return MoveTo(x, y);
        _cur = new Vector2(x, y);
        s.AddLine(_cur);
        return this;
    }

    public Path2D QuadTo(float cx, float cy, float x, float y)
    {
        var s = EnsureSink();
        if (!_figureOpen) MoveTo(cx, cy);
        var seg = new QuadraticBezierSegment { Point1 = new Vector2(cx, cy), Point2 = new Vector2(x, y) };
        s.AddQuadraticBezier(seg);
        _cur = new Vector2(x, y);
        return this;
    }

    public Path2D CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
    {
        var s = EnsureSink();
        if (!_figureOpen) MoveTo(c1x, c1y);
        var seg = new BezierSegment
        {
            Point1 = new Vector2(c1x, c1y),
            Point2 = new Vector2(c2x, c2y),
            Point3 = new Vector2(x, y),
        };
        s.AddBezier(seg);
        _cur = new Vector2(x, y);
        return this;
    }

    public Path2D ArcToEllipse(float cx, float cy, float rx, float ry, float endAngleDeg, bool clockwise)
    {
        var s = EnsureSink();
        var rad = endAngleDeg * MathF.PI / 180f;
        var end = new Vector2(cx + rx * MathF.Cos(rad), cy + ry * MathF.Sin(rad));
        if (!_figureOpen) MoveTo(cx + rx, cy);

        var start = _cur;
        var startAngle = MathF.Atan2((start.Y - cy) / MathF.Max(ry, 1e-6f), (start.X - cx) / MathF.Max(rx, 1e-6f));
        var sweep = NormalizeSweep(endAngleDeg * MathF.PI / 180f - startAngle, clockwise);

        var steps = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(sweep) / (MathF.PI / 2)));
        for (var i = 1; i <= steps; i++)
        {
            var a1 = startAngle + sweep * i / steps;
            var a0 = startAngle + sweep * (i - 1) / steps;
            var p0 = new Vector2(cx + rx * MathF.Cos(a0), cy + ry * MathF.Sin(a0));
            var p3 = new Vector2(cx + rx * MathF.Cos(a1), cy + ry * MathF.Sin(a1));
            var k = 4f / 3f * MathF.Tan((a1 - a0) / 4f);
            var t0 = new Vector2(-rx * MathF.Sin(a0), ry * MathF.Cos(a0)) * k;
            var t1 = new Vector2(-rx * MathF.Sin(a1), ry * MathF.Cos(a1)) * k;
            if (i == 1 && Vector2.DistanceSquared(p0, _cur) > 1e-4f) s.AddLine(p0);
            var seg = new BezierSegment { Point1 = p0 + t0, Point2 = p3 - t1, Point3 = p3 };
            s.AddBezier(seg);
            _cur = p3;
        }

        return this;
    }

    public Path2D Close()
    {
        if (_figureOpen)
        {
            _sink!.EndFigure(FigureEnd.Closed);
            _figureOpen = false;
        }

        return this;
    }

    public Path2D AddRect(float left, float top, float right, float bottom) =>
        MoveTo(left, top).LineTo(right, top).LineTo(right, bottom).LineTo(left, bottom).Close();

    public Path2D AddOval(float left, float top, float right, float bottom)
    {
        var cx = (left + right) / 2f;
        var cy = (top + bottom) / 2f;
        var rx = (right - left) / 2f;
        var ry = (bottom - top) / 2f;
        var ox = rx * Kappa;
        var oy = ry * Kappa;

        MoveTo(cx, top);
        CubicTo(cx + ox, top, right, cy - oy, right, cy);
        CubicTo(right, cy + oy, cx + ox, bottom, cx, bottom);
        CubicTo(cx - ox, bottom, left, cy + oy, left, cy);
        CubicTo(left, cy - oy, cx - ox, top, cx, top);
        return Close();
    }

    public Path2D AddCircle(float cx, float cy, float radius) =>
        AddOval(cx - radius, cy - radius, cx + radius, cy + radius);

    public Path2D AddRoundRect(float left, float top, float right, float bottom, float rx, float ry)
    {
        rx = MathF.Min(rx, (right - left) / 2f);
        ry = MathF.Min(ry, (bottom - top) / 2f);
        var ox = rx * Kappa;
        var oy = ry * Kappa;

        MoveTo(left + rx, top);
        LineTo(right - rx, top);
        CubicTo(right - rx + ox, top, right, top + ry - oy, right, top + ry);
        LineTo(right, bottom - ry);
        CubicTo(right, bottom - ry + oy, right - rx + ox, bottom, right - rx, bottom);
        LineTo(left + rx, bottom);
        CubicTo(left + rx - ox, bottom, left, bottom - ry + oy, left, bottom - ry);
        LineTo(left, top + ry);
        CubicTo(left, top + ry - oy, left + rx - ox, top, left + rx, top);
        return Close();
    }

    public Path2D Reset()
    {
        DisposeGeometry();
        _finished = false;
        _figureOpen = false;
        _cur = default;
        return this;
    }

    internal ID2D1PathGeometry? Geometry
    {
        get
        {
            Finish();
            return _geo;
        }
    }

    public void Finish()
    {
        if (_finished) return;
        if (_sink is null)
        {
            _finished = true;
            return;
        }

        EndFigureIfOpen();
        _sink.Close();
        _sink.Dispose();
        _sink = null;
        _finished = true;
    }

    private ID2D1GeometrySink EnsureSink()
    {
        if (_sink is not null) return _sink;
        _geo ??= _factory.CreatePathGeometry();
        _sink = _geo.Open();
        _sink.SetFillMode(FillMode);
        return _sink;
    }

    private void EnsureEditable()
    {
        if (_finished && _geo is not null)
            throw new InvalidOperationException("Path2D 已收尾，不能继续追加线段；请先 Reset()");
        _finished = false;
    }

    private void EndFigureIfOpen()
    {
        if (!_figureOpen) return;
        _sink!.EndFigure(FigureEnd.Open);
        _figureOpen = false;
    }

    private static float NormalizeSweep(float sweep, bool clockwise)
    {
        while (sweep <= -MathF.PI) sweep += 2 * MathF.PI;
        while (sweep > MathF.PI) sweep -= 2 * MathF.PI;
        if (clockwise && sweep < 0) sweep += 2 * MathF.PI;
        if (!clockwise && sweep > 0) sweep -= 2 * MathF.PI;
        return sweep;
    }

    private void DisposeGeometry()
    {
        _sink?.Dispose();
        _sink = null;
        _geo?.Dispose();
        _geo = null;
    }

    public void Dispose() => DisposeGeometry();
}
