using System.Numerics;
using JizuraLive.Logging;
using JizuraLive.Render.Text;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace JizuraLive.Render.Draw;

public sealed class Immediate2D : IDisposable
{
    public ID2D1DeviceContext Ctx { get; }
    public ID2D1RenderTarget Rt { get; }
    public ID2D1Factory1 Factory { get; }
    public DwFontCache Fonts { get; }

    public int Width { get; private set; }
    public int Height { get; private set; }

    public Matrix3x2 Matrix => _matrix;

    public int SaveCount => _stack.Count;

    private readonly struct StateEntry
    {
        public readonly Matrix3x2 Matrix;
        public readonly int ClipDepth;
        public readonly int DrawCount;
        public readonly Blend2D Blend;
        public readonly bool Antialias;

        public StateEntry(Matrix3x2 matrix, int clipDepth, int drawCount, Blend2D blend, bool antialias)
        {
            Matrix = matrix;
            ClipDepth = clipDepth;
            DrawCount = drawCount;
            Blend = blend;
            Antialias = antialias;
        }
    }

    private readonly List<StateEntry> _stack = new();
    private readonly List<(bool IsLayer, ID2D1Layer? Layer)> _clips = new();
    private readonly Dictionary<uint, ID2D1SolidColorBrush> _solid = new();
    private readonly Dictionary<string, ID2D1Brush> _brushes = new();
    private readonly Dictionary<string, ID2D1StrokeStyle> _strokes = new();
    private readonly HashSet<Blend2D> _warnedBlend = new();

    private readonly Dictionary<(int W, int H), Stack<ID2D1Bitmap1>> _surfacePool = new();
    private readonly List<SurfaceFrame> _surfaceStack = new();
    private EffectChain? _effects;

    private readonly struct SurfaceFrame
    {
        public readonly ID2D1Bitmap1 Surface;
        public readonly ID2D1Image? PrevTarget;
        public readonly Matrix3x2 PrevMatrix;
        public readonly int W;
        public readonly int H;

        public SurfaceFrame(ID2D1Bitmap1 surface, ID2D1Image? prevTarget, Matrix3x2 prevMatrix, int w, int h)
        {
            Surface = surface;
            PrevTarget = prevTarget;
            PrevMatrix = prevMatrix;
            W = w;
            H = h;
        }
    }

    public bool InSurface => _surfaceStack.Count > 0;

    private Matrix3x2 _matrix = Matrix3x2.Identity;
    private Blend2D _blend = Blend2D.SrcOver;
    private bool _antialias = true;
    private bool _drawing;

    public Immediate2D(ID2D1Factory1 factory, ID2D1DeviceContext ctx, DwFontCache fonts)
    {
        Factory = factory;
        Ctx = ctx;
        Rt = (ID2D1RenderTarget)ctx;
        Fonts = fonts;
    }

    public void SetTarget(ID2D1Bitmap1? target, int width, int height)
    {
        ResetState();
        Ctx.Target = target;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
    }

    public void Begin()
    {
        ResetState();
        Rt.BeginDraw();
        _drawing = true;
    }

    public void End()
    {
        if (!_drawing) return;
        Rt.EndDraw();
        _drawing = false;
    }

    private void ResetState()
    {
        PopAllClips();
        _stack.Clear();
        _matrix = Matrix3x2.Identity;
        _blend = Blend2D.SrcOver;
        _antialias = true;
        Rt.Transform = Matrix3x2.Identity;
        Ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;
    }

    public void Clear(Color4 color) => Rt.Clear(color);

    public void Save()
    {
        _stack.Add(new StateEntry(_matrix, _clips.Count, _stack.Count, _blend, _antialias));
    }

    public void Restore()
    {
        if (_stack.Count == 0) return;
        var e = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        PopClipsTo(e.ClipDepth);
        _matrix = e.Matrix;
        Rt.Transform = _matrix;
        _blend = e.Blend;
        _antialias = e.Antialias;
    }

    public void RestoreToCount(int count)
    {
        count = Math.Clamp(count, 0, _stack.Count);
        while (_stack.Count > count) Restore();
    }

    public void Translate(float x, float y)
    {
        _matrix = Matrix3x2.Multiply(_matrix, Matrix3x2.CreateTranslation(x, y));
        Rt.Transform = _matrix;
    }

    public void Scale(float sx, float sy)
    {
        _matrix = Matrix3x2.Multiply(_matrix, Matrix3x2.CreateScale(sx, sy));
        Rt.Transform = _matrix;
    }

    public void Rotate(float degrees)
    {
        _matrix = Matrix3x2.Multiply(_matrix, Matrix3x2.CreateRotation(degrees * MathF.PI / 180f));
        Rt.Transform = _matrix;
    }

    public void RotateRadians(float radians)
    {
        _matrix = Matrix3x2.Multiply(_matrix, Matrix3x2.CreateRotation(radians));
        Rt.Transform = _matrix;
    }

    public void Rotate(float degrees, float px, float py)
    {
        Translate(px, py);
        Rotate(degrees);
        Translate(-px, -py);
    }

    public void Skew(float kx, float ky)
    {
        var m = new Matrix3x2(1, ky, kx, 1, 0, 0);
        _matrix = Matrix3x2.Multiply(_matrix, m);
        Rt.Transform = _matrix;
    }

    public void Concat(Matrix3x2 m)
    {
        _matrix = Matrix3x2.Multiply(_matrix, m);
        Rt.Transform = _matrix;
    }

    public void SetMatrix(Matrix3x2 m)
    {
        _matrix = m;
        Rt.Transform = _matrix;
    }

    public void ClipRect(Rect rect, bool antialias = true)
    {
        Rt.PushAxisAlignedClip(rect, antialias ? AntialiasMode.PerPrimitive : AntialiasMode.Aliased);
        _clips.Add((false, null));
    }

    public void ClipPath(Path2D path, bool antialias = true)
    {
        var geo = path.Geometry;
        if (geo is null)
        {
            ClipRect(new Rect(0, 0, 0, 0), antialias);
            return;
        }

        var layer = Rt.CreateLayer(new Size(Width, Height));
        var lp = new LayerParameters
        {
            ContentBounds = new Vortice.RawRectF(0, 0, Width, Height),
            GeometricMask = geo,
            MaskAntialiasMode = antialias ? AntialiasMode.PerPrimitive : AntialiasMode.Aliased,
            MaskTransform = Matrix3x2.Identity,
            Opacity = 1f,
            LayerOptions = LayerOptions.None,
        };
        Rt.PushLayer(lp, layer);
        _clips.Add((true, layer));
    }

    private void PopClipsTo(int depth)
    {
        while (_clips.Count > depth)
        {
            var (isLayer, layer) = _clips[^1];
            _clips.RemoveAt(_clips.Count - 1);
            if (isLayer)
            {
                Rt.PopLayer();
                layer?.Dispose();
            }
            else
            {
                Rt.PopAxisAlignedClip();
            }
        }
    }

    private void PopAllClips() => PopClipsTo(0);

    public void FillRect(Rect r, Paint2D p)
    {
        if (BeginDraw(p) is not { } b) return;
        try { Rt.FillRectangle(r, b); }
        finally { EndDraw(); }
    }

    public void StrokeRect(Rect r, Paint2D p)
    {
        if (BeginDraw(p) is not { } b) return;
        try { Rt.DrawRectangle(r, b, MathF.Max(0.01f, p.StrokeWidth), StrokeStyle(p)); }
        finally { EndDraw(); }
    }

    public void DrawRect(Rect r, Paint2D p)
    {
        if (p.IsStroke) StrokeRect(r, p);
        else FillRect(r, p);
    }

    public void FillRoundRect(Rect r, float rx, float ry, Paint2D p)
    {
        var rr = new RoundedRectangle(new Vortice.RawRectF(r.Left, r.Top, r.Right, r.Bottom), rx, ry);
        if (BeginDraw(p) is not { } b) return;
        try { Rt.FillRoundedRectangle(rr, b); }
        finally { EndDraw(); }
    }

    public void StrokeRoundRect(Rect r, float rx, float ry, Paint2D p)
    {
        var rr = new RoundedRectangle(new Vortice.RawRectF(r.Left, r.Top, r.Right, r.Bottom), rx, ry);
        var w = MathF.Max(0.01f, p.StrokeWidth);

        var style = StrokeStyle(p);
        if (BeginDraw(p) is not { } b) return;
        try
        {
            if (style is null) Rt.DrawRoundedRectangle(rr, b, w);
            else
            {
                var s = style;
                Rt.DrawRoundedRectangle(rr, b, w, s);
            }
        }
        finally { EndDraw(); }
    }

    public void DrawRoundRect(Rect r, float rx, float ry, Paint2D p)
    {
        if (p.IsStroke) StrokeRoundRect(r, rx, ry, p);
        else FillRoundRect(r, rx, ry, p);
    }

    public void DrawLine(float x0, float y0, float x1, float y1, Paint2D p)
    {
        var a = new Vector2(x0, y0);
        var b2 = new Vector2(x1, y1);
        if (BeginDraw(p) is not { } b) return;
        try { Rt.DrawLine(a, b2, b, MathF.Max(0.01f, p.StrokeWidth), StrokeStyle(p)); }
        finally { EndDraw(); }
    }

    public void DrawPoint(float x, float y, Paint2D p)
    {
        var s = MathF.Max(0.5f, p.StrokeWidth);
        if (p.StrokeCap == StrokeCap2D.Round) FillCircle(x, y, s / 2f, p);
        else FillRect(new Rect(x - s / 2f, y - s / 2f, s, s), p);
    }

    public void FillCircle(float cx, float cy, float radius, Paint2D p)
    {
        var e = new Ellipse(new Vector2(cx, cy), radius, radius);
        if (BeginDraw(p) is not { } b) return;
        try { Rt.FillEllipse(e, b); }
        finally { EndDraw(); }
    }

    public void StrokeCircle(float cx, float cy, float radius, Paint2D p)
    {
        var e = new Ellipse(new Vector2(cx, cy), radius, radius);
        if (BeginDraw(p) is not { } b) return;
        try { Rt.DrawEllipse(e, b, MathF.Max(0.01f, p.StrokeWidth), StrokeStyle(p)); }
        finally { EndDraw(); }
    }

    public void DrawCircle(float cx, float cy, float radius, Paint2D p)
    {
        if (p.IsStroke) StrokeCircle(cx, cy, radius, p);
        else FillCircle(cx, cy, radius, p);
    }

    public void DrawOval(Rect r, Paint2D p)
    {
        var e = new Ellipse(new Vector2((r.Left + r.Right) / 2f, (r.Top + r.Bottom) / 2f),
            (r.Right - r.Left) / 2f, (r.Bottom - r.Top) / 2f);
        if (BeginDraw(p) is not { } b) return;
        try
        {
            if (p.IsStroke) Rt.DrawEllipse(e, b, MathF.Max(0.01f, p.StrokeWidth), StrokeStyle(p));
            else Rt.FillEllipse(e, b);
        }
        finally { EndDraw(); }
    }

    public void FillPath(Path2D path, Paint2D p)
    {
        var geo = path.Geometry;
        if (geo is null) return;
        if (BeginDraw(p) is not { } b) return;
        try { Rt.FillGeometry(geo, b); }
        finally { EndDraw(); }
    }

    public void StrokePath(Path2D path, Paint2D p)
    {
        var geo = path.Geometry;
        if (geo is null) return;
        if (BeginDraw(p) is not { } b) return;
        try { Rt.DrawGeometry(geo, b, MathF.Max(0.01f, p.StrokeWidth), StrokeStyle(p)); }
        finally { EndDraw(); }
    }

    public void DrawPath(Path2D path, Paint2D p)
    {
        if (p.IsStroke) StrokePath(path, p);
        else FillPath(path, p);
    }

    public void DrawImage(Image2D img, Rect dest, float alpha = 1f,
        BitmapInterpolationMode interp = BitmapInterpolationMode.Linear)
    {
        if (alpha <= 0.001f) return;
        if (BeginDraw(new Paint2D { Alpha = alpha }) is null) return;
        try { Rt.DrawBitmap(img.Bitmap, dest, Math.Clamp(alpha, 0f, 1f), interp, null); }
        finally { EndDraw(); }
    }

    public void DrawBitmap(ID2D1Bitmap bitmap, Rect dest, float alpha = 1f,
        BitmapInterpolationMode interp = BitmapInterpolationMode.Linear)
    {
        if (alpha <= 0.001f) return;
        if (BeginDraw(new Paint2D { Alpha = alpha }) is null) return;
        try { Rt.DrawBitmap(bitmap, dest, Math.Clamp(alpha, 0f, 1f), interp, null); }
        finally { EndDraw(); }
    }

    public void DrawText(string text, float x, float y, TextAlign2D align, DwFont font, Paint2D p)
    {
        if (string.IsNullOrEmpty(text) || p.Alpha <= 0.002f) return;
        var w = font.MeasureWidth(text);
        var left = align switch
        {
            TextAlign2D.Center => x - w / 2f,
            TextAlign2D.Right => x - w,
            _ => x,
        };
        var rect = new Rect(left, y - font.Ascent, MathF.Max(w, 1f), MathF.Max(font.LineHeight, 1f));
        var fmt = align switch
        {
            TextAlign2D.Center => font.Centered,
            TextAlign2D.Right => font.Trailing,
            _ => font.Leading,
        };
        if (BeginDraw(p) is not { } b) return;
        try { Rt.DrawText(text, fmt, rect, b, DrawTextOptions.None, MeasuringMode.Natural); }
        finally { EndDraw(); }
    }

    public void PushSurface(int? width = null, int? height = null, bool clear = true)
    {
        var w = Math.Max(1, width ?? Width);
        var h = Math.Max(1, height ?? Height);
        var surf = RentSurface(w, h);

        _surfaceStack.Add(new SurfaceFrame(surf, Ctx.Target, _matrix, w, h));
        Ctx.Target = surf;
        _matrix = Matrix3x2.Identity;
        Rt.Transform = _matrix;
        if (clear) Ctx.Clear(new Color4(0f, 0f, 0f, 0f));
    }

    public void PopSurface(Paint2D p)
    {
        if (_surfaceStack.Count == 0) return;
        var frame = _surfaceStack[^1];
        _surfaceStack.RemoveAt(_surfaceStack.Count - 1);

        Ctx.Target = frame.PrevTarget;
        _matrix = frame.PrevMatrix;
        Rt.Transform = _matrix;

        var chain = _effects ??= new EffectChain(Ctx);

        var shadow = chain.ShadowImage(frame.Surface, p);
        if (shadow is not null && p.Shadow is { } sh)
        {
            Rt.Transform = Matrix3x2.Multiply(_matrix, Matrix3x2.CreateTranslation(sh.OffsetX, sh.OffsetY));
            Ctx.DrawImage(shadow, InterpolationMode.Linear, CompositeMode.SourceOver);
            Rt.Transform = _matrix;
        }

        if (!EffectChain.IsDirectComposite(p.Blend) && _warnedBlend.Add(p.Blend))
            Log.Warn($"[Immediate2D] 层间混合 {p.Blend} 需要背景图作为 Blend 效果输入，"
                     + "PopSurface 拿不到背景，本帧按 SourceOver 处理");

        var image = chain.Wrap(frame.Surface, p);
        Ctx.DrawImage(image, InterpolationMode.Linear, EffectChain.ToComposite(p.Blend));

        ReturnSurface(frame.Surface, frame.W, frame.H);
    }

    public Surface2D CreateSurface(int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var props = new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm,
                Vortice.DCommon.AlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.Target);
        return new Surface2D(Ctx.CreateBitmap(new SizeI(width, height), props), width, height);
    }

    public void PushSurface(Surface2D surface, bool clear = true)
    {
        _surfaceStack.Add(new SurfaceFrame(surface.Bitmap, Ctx.Target, _matrix, surface.Width, surface.Height));
        Ctx.Target = surface.Bitmap;
        _matrix = Matrix3x2.Identity;
        Rt.Transform = _matrix;
        if (clear) Ctx.Clear(new Color4(0f, 0f, 0f, 0f));
    }

    public void PopSurface()
    {
        if (_surfaceStack.Count == 0) return;
        var frame = _surfaceStack[^1];
        _surfaceStack.RemoveAt(_surfaceStack.Count - 1);
        Ctx.Target = frame.PrevTarget;
        _matrix = frame.PrevMatrix;
        Rt.Transform = _matrix;
    }

    public void DrawComposite(Paint2D p, Surface2D backdrop, Surface2D layer, Rect? dest = null)
    {
        var chain = _effects ??= new EffectChain(Ctx);

        ID2D1Image image;
        CompositeMode composite;
        if (EffectChain.IsDirectComposite(p.Blend))
        {
            image = chain.WrapOpacity(layer.Image, p.Alpha);
            composite = EffectChain.ToComposite(p.Blend);
        }
        else
        {
            image = chain.BlendImages(backdrop.Image, layer.Image, p.Blend);
            image = chain.WrapOpacity(image, p.Alpha);
            composite = CompositeMode.SourceOver;
        }

        if (dest is { } r)
        {
            Save();
            Translate(r.Left, r.Top);
            Scale(r.Width / MathF.Max(1f, backdrop.Width), r.Height / MathF.Max(1f, backdrop.Height));
            Ctx.DrawImage(image, InterpolationMode.Linear, composite);
            Restore();
        }
        else
        {
            Ctx.DrawImage(image, InterpolationMode.Linear, composite);
        }
    }

    private ID2D1Bitmap1 RentSurface(int w, int h)
    {
        if (_surfacePool.TryGetValue((w, h), out var pool) && pool.Count > 0)            return pool.Pop();

        var props = new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm,
                Vortice.DCommon.AlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.Target);
        return Ctx.CreateBitmap(new SizeI(w, h), props);
    }

    private void ReturnSurface(ID2D1Bitmap1 surface, int w, int h)
    {
        if (!_surfacePool.TryGetValue((w, h), out var pool))
        {
            pool = new Stack<ID2D1Bitmap1>();
            _surfacePool[(w, h)] = pool;
        }

        pool.Push(surface);
    }

    private ID2D1Brush? BeginDraw(Paint2D p)
    {
        var alpha = Math.Clamp(p.Alpha, 0f, 1f);
        if (alpha <= 0.002f) return null;

        var brush = ResolveBrush(p.Brush, alpha);
        ApplyBlend(p.Blend);
        ApplyAntialias(p.IsAntialias);
        return brush;
    }

    private void EndDraw()
    {
        Ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;
        ApplyAntialias(true);
    }

    private void ApplyBlend(Blend2D blend)
    {
        var pb = blend switch
        {
            Blend2D.SrcOver => PrimitiveBlend.SourceOver,
            Blend2D.Plus => PrimitiveBlend.Add,
            Blend2D.SrcCopy => PrimitiveBlend.Copy,
            Blend2D.Darken => PrimitiveBlend.Min,
            Blend2D.Lighten => PrimitiveBlend.Max,
            _ => PrimitiveBlend.SourceOver,
        };
        if (pb == PrimitiveBlend.SourceOver && blend != Blend2D.SrcOver && _warnedBlend.Add(blend))
            Log.Warn($"[Immediate2D] 混合模式 {blend} 需 D2D 效果图（Blend 效果），本帧按 SourceOver 处理");
        Ctx.PrimitiveBlend = pb;
    }

    private void ApplyAntialias(bool on)
    {
        if (on == _antialias) return;
        _antialias = on;
        Rt.AntialiasMode = on ? AntialiasMode.PerPrimitive : AntialiasMode.Aliased;
    }

    private ID2D1Brush ResolveBrush(Brush2D brush, float alpha)
    {
        switch (brush)
        {
            case SolidBrush2D s:
            {
                var key = Pack(s.Color);
                if (!_solid.TryGetValue(key, out var b))
                {
                    b = Rt.CreateSolidColorBrush(s.Color);
                    _solid[key] = b;
                }

                b.Opacity = alpha;
                return b;
            }

            case LinearGradient2D g:
            {
                var key = "L|" + GradientKey(g.Stops, g.Extend) + $"|{g.Start.X},{g.Start.Y},{g.End.X},{g.End.Y}";
                if (!_brushes.TryGetValue(key, out var b))
                {
                    using var coll = Rt.CreateGradientStopCollection(ToStops(g.Stops), Gamma.StandardRgb, g.Extend);
                    b = Rt.CreateLinearGradientBrush(new LinearGradientBrushProperties
                    {
                        StartPoint = g.Start,
                        EndPoint = g.End,
                    }, coll);
                    _brushes[key] = b;
                }

                b.Opacity = alpha;
                return b;
            }

            case RadialGradient2D g:
            {
                var key = "R|" + GradientKey(g.Stops, g.Extend)
                          + $"|{g.Center.X},{g.Center.Y},{g.RadiusX},{g.RadiusY}";
                if (!_brushes.TryGetValue(key, out var b))
                {
                    using var coll = Rt.CreateGradientStopCollection(ToStops(g.Stops), Gamma.StandardRgb, g.Extend);
                    b = Rt.CreateRadialGradientBrush(new RadialGradientBrushProperties
                    {
                        Center = g.Center,
                        GradientOriginOffset = Vector2.Zero,
                        RadiusX = g.RadiusX,
                        RadiusY = g.RadiusY,
                    }, coll);
                    _brushes[key] = b;
                }

                b.Opacity = alpha;
                return b;
            }

            case BitmapBrush2D bm:
            {
                var key = $"B|{bm.Image.GetHashCode()}|{bm.ExtendX}|{bm.ExtendY}|{bm.Interp}";
                if (!_brushes.TryGetValue(key, out var b))
                {
                    var props = new BitmapBrushProperties(bm.ExtendX, bm.ExtendY, bm.Interp);
                    b = Rt.CreateBitmapBrush(bm.Image.Bitmap, props, null);
                    _brushes[key] = b;
                }

                b.Opacity = alpha;
                return b;
            }

            default:
                throw new NotSupportedException($"未知画刷类型 {brush.GetType().Name}");
        }
    }

    private ID2D1StrokeStyle? StrokeStyle(Paint2D p)
    {
        if (p.DashPattern is null && p.StrokeJoin == StrokeJoin2D.Miter
            && p.StrokeCap == StrokeCap2D.Flat && Math.Abs(p.StrokeMiter - 10f) < 0.001f)
            return null;

        var dashKey = p.DashPattern is null ? "solid" : string.Join(',', p.DashPattern);
        var key = $"{p.StrokeJoin}|{p.StrokeCap}|{p.StrokeMiter}|{dashKey}|{p.DashOffset}";
        if (_strokes.TryGetValue(key, out var s)) return s;

        var props = new StrokeStyleProperties
        {
            StartCap = ToCap(p.StrokeCap),
            EndCap = ToCap(p.StrokeCap),
            DashCap = CapStyle.Flat,
            LineJoin = ToJoin(p.StrokeJoin),
            MiterLimit = p.StrokeMiter,
            DashStyle = p.DashPattern is null ? DashStyle.Solid : DashStyle.Custom,
            DashOffset = p.DashOffset,
        };
        s = p.DashPattern is null
            ? Factory.CreateStrokeStyle(props)
            : Factory.CreateStrokeStyle(props, p.DashPattern);
        _strokes[key] = s;
        return s;
    }

    private static CapStyle ToCap(StrokeCap2D c) => c switch
    {
        StrokeCap2D.Round => CapStyle.Round,
        StrokeCap2D.Square => CapStyle.Square,
        _ => CapStyle.Flat,
    };

    private static LineJoin ToJoin(StrokeJoin2D j) => j switch
    {
        StrokeJoin2D.Round => LineJoin.Round,
        StrokeJoin2D.Bevel => LineJoin.Bevel,
        _ => LineJoin.Miter,
    };

    private static GradientStop[] ToStops(GradientStop2D[] stops) =>
        Array.ConvertAll(stops, s => new GradientStop(s.Offset, s.Color));

    private static string GradientKey(GradientStop2D[] stops, ExtendMode extend)
    {
        var sb = new System.Text.StringBuilder(64);
        sb.Append((int)extend).Append(':');
        foreach (var s in stops) sb.Append(s.Offset).Append(',').Append(Pack(s.Color)).Append(';');
        return sb.ToString();
    }

    private static uint Pack(Color4 c) =>
        ((uint)(Math.Clamp(c.R, 0f, 1f) * 255) << 24)
        | ((uint)(Math.Clamp(c.G, 0f, 1f) * 255) << 16)
        | ((uint)(Math.Clamp(c.B, 0f, 1f) * 255) << 8)
        | (uint)(Math.Clamp(c.A, 0f, 1f) * 255);

    public void Dispose()
    {
        PopAllClips();
        foreach (var pool in _surfacePool.Values)
            foreach (var s in pool)
                s.Dispose();
        _surfacePool.Clear();
        _effects?.Dispose();
        foreach (var b in _solid.Values) b.Dispose();
        foreach (var b in _brushes.Values) b.Dispose();
        foreach (var s in _strokes.Values) s.Dispose();
        _solid.Clear();
        _brushes.Clear();
        _strokes.Clear();
        Fonts.Dispose();
    }
}
