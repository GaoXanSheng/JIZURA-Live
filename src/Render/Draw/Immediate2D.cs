using System.Numerics;
using JizuraLive.Logging;
using JizuraLive.Render.Text;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace JizuraLive.Render.Draw;

/// <summary>
/// GPU 即时模式 2D 绘制面（D2D 之上）：旧引擎绘制层的**兼容面**。
///
/// 设计原则：方法名与语义尽量贴着旧代码里用到的 SKCanvas/SKPaint 调用面（见
/// <c>DOC/P1-DRAW-PRIMITIVES.md</c> 的实测频次表），让「移植」退化成改类型名而不是改结构。
///
/// 状态模型：D2D 的 SetTransform 是「替换」而不是「叠加」，也没有可回滚的裁剪栈，
/// 所以变换与裁剪由本类自己维护（<c>_stack</c> + <c>_clips</c>）：
///   Save() 记下 (矩阵, 裁剪深度)；Restore() 把裁剪弹回该深度并还原矩阵。
///   这一点与 SKCanvas 的 save/restore 语义一致。
///
/// 已知与 Skia 的差异（P1 缺口，详见文档）：
///   * 旋转状态下的 ClipRect 会退化成轴对齐包围盒（D2D 的轴对齐裁剪限制）；
///   * 只有 SourceOver / Plus / SrcCopy 等少数混合模式能走 D2D 原始混合，其余需 D2D 效果图；
///   * ImageFilter（blur/dropshadow）与 ColorFilter（colormatrix）尚未接入（下一批）。
/// </summary>
public sealed class Immediate2D : IDisposable
{
    public ID2D1DeviceContext Ctx { get; }
    public ID2D1RenderTarget Rt { get; }
    public ID2D1Factory1 Factory { get; }
    public DwFontCache Fonts { get; }

    /// <summary>当前目标的像素尺寸（裁剪/图层尺寸用）。</summary>
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>当前变换矩阵（局部 → 目标像素）。</summary>
    public Matrix3x2 Matrix => _matrix;

    /// <summary>当前 Save 层数（对位 SKCanvas.SaveCount）。</summary>
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

    // 离屏中间面池 + 效果链（PushSurface/PopSurface 用）
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

    /// <summary>当前是否有未弹出的离屏中间面。</summary>
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

    // ---------------- 目标与帧 ----------------

    /// <summary>换绘制目标（交换链后缓冲 / 离屏面），并重置全部状态。</summary>
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

    // ---------------- 状态栈 ----------------

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

    /// <summary>弹到只剩 <paramref name="count"/> 层（对位 SKCanvas.RestoreToCount）。</summary>
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

    /// <summary>旋转（角度制，顺时针为正，与 SKCanvas.RotateDegrees 一致）。</summary>
    public void Rotate(float degrees)
    {
        _matrix = Matrix3x2.Multiply(_matrix, Matrix3x2.CreateRotation(degrees * MathF.PI / 180f));
        Rt.Transform = _matrix;
    }

    /// <summary>旋转（弧度制，对位 SKCanvas.RotateRadians）。</summary>
    public void RotateRadians(float radians)
    {
        _matrix = Matrix3x2.Multiply(_matrix, Matrix3x2.CreateRotation(radians));
        Rt.Transform = _matrix;
    }

    /// <summary>绕某点旋转（对位 SKCanvas.RotateDegrees(deg, px, py)）。</summary>
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

    /// <summary>叠加变换（对位 SKCanvas.Concat：先当前后 m）。</summary>
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

    // ---------------- 裁剪 ----------------

    /// <summary>
    /// 轴对齐矩形裁剪。注意：D2D 的轴对齐裁剪在**当前变换含旋转**时会退化成包围盒——
    /// 旧引擎里旋转态下的 ClipRect 需要改用 <see cref="ClipPath"/>（走图层掩码）。
    /// </summary>
    public void ClipRect(Rect rect, bool antialias = true)
    {
        Rt.PushAxisAlignedClip(rect, antialias ? AntialiasMode.PerPrimitive : AntialiasMode.Aliased);
        _clips.Add((false, null));
    }

    /// <summary>任意路径裁剪（走 D2D 图层 + 几何掩码，可跟随旋转/斜切）。</summary>
    public void ClipPath(Path2D path, bool antialias = true)
    {
        var geo = path.Geometry;
        if (geo is null)
        {
            // 空路径 = 全裁掉；用零面积矩形表达
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

    // ---------------- 绘制 ----------------
    //
    // 每个原语都是「BeginDraw → 画 → EndDraw」而不是把绘制收进一个回调：
    // 回调写法（`DrawCore(p, b => …)`）每笔都要分配一个闭包 + 委托（实测 104 字节/笔，
    // 一帧几十笔就是每帧几 KB 托管垃圾，见 tests 的 Immediate2DAllocationTests）。
    // BeginDraw 返回 null = 本笔完全透明，调用方直接返回。

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

    /// <summary>按 paint 的 IsStroke 自动分派（对位 SKCanvas.DrawRect）。</summary>
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

        // 显式分派，不是 suppression：Vortice 3.8.3 的 4 参
        // DrawRoundedRectangle(RoundedRectangle, ID2D1Brush, float, ID2D1StrokeStyle) 的 strokeStyle
        // 是**非空**引用类型（CS8604 的成因，编译器报错串里点名的就是这个签名），而 3 参重载等价于
        // 传 null。StrokeStyle(p) 返回 null 的条件（无虚线 && Miter && Flat && MiterLimit≈10）正好就是
        // D2D 的内建默认样式，所以走 3 参重载语义等价。
        // 收窄成局部 s：把「此处 style 一定非空」这个事实落在调用点上，而不是依赖外层变量的
        // 流分析状态（本会话最小编译实测：直接用已收窄的 style 同样 0 警告——收窄是为了让非空事实
        // 显式可见，不是 suppress）。
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

    /// <summary>
    /// 画一个点（对位 SKCanvas.DrawPoint）：大小取 StrokeWidth，圆端点画圆、其余画方。
    /// D2D 没有点图元，按 Skia 的语义用最小图元模拟。
    /// </summary>
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

    /// <summary>按 paint 的 IsStroke 自动分派（对位 SKCanvas.DrawPath）。</summary>
    public void DrawPath(Path2D path, Paint2D p)
    {
        if (p.IsStroke) StrokePath(path, p);
        else FillPath(path, p);
    }

    /// <summary>把位图画进目标矩形（当前变换生效）。</summary>
    public void DrawImage(Image2D img, Rect dest, float alpha = 1f,
        BitmapInterpolationMode interp = BitmapInterpolationMode.Linear)
    {
        if (alpha <= 0.001f) return;
        if (BeginDraw(new Paint2D { Alpha = alpha }) is null) return;
        try { Rt.DrawBitmap(img.Bitmap, dest, Math.Clamp(alpha, 0f, 1f), interp, null); }
        finally { EndDraw(); }
    }

    /// <summary>
    /// 直接画一张 D2D 位图（不经过 <see cref="Image2D"/> 包装）。
    /// 用途：效果链的输出面（D3D11 纹理的 D2D 视图）需要合成回目标，而它的所有权不在本类手里。
    /// </summary>
    public void DrawBitmap(ID2D1Bitmap bitmap, Rect dest, float alpha = 1f,
        BitmapInterpolationMode interp = BitmapInterpolationMode.Linear)
    {
        if (alpha <= 0.001f) return;
        if (BeginDraw(new Paint2D { Alpha = alpha }) is null) return;
        try { Rt.DrawBitmap(bitmap, dest, Math.Clamp(alpha, 0f, 1f), interp, null); }
        finally { EndDraw(); }
    }

    /// <summary>
    /// 画一行文本（对位 SKCanvas.DrawText 的 x/y + 对齐语义）：<paramref name="x"/> 按
    /// <paramref name="align"/> 解释，<paramref name="y"/> 是**基线**。
    /// </summary>
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

    // ---------------- 离屏中间面（对位 Renderer 的 scratch/small/tiny/trans/morph 复用面） ----------------

    /// <summary>
    /// 把后续绘制重定向到一张离屏面（尺寸默认 = 当前目标）。进入时**重置变换**为恒等，
    /// 与旧引擎「scratch 面上重新排版」的用法一致；<paramref name="clear"/> 默认清成全透明。
    /// </summary>
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

    /// <summary>
    /// 结束离屏面并把内容按 <paramref name="p"/> 合成回上一层目标：支持模糊 / 色矩阵 / 不透明度 /
    /// 投影，以及 SrcOver / Plus / SrcCopy 三种直接合成模式。
    ///
    /// 其余混合模式（Screen/Multiply/Overlay/…）需要**背景图作为第二输入**（D2D 的 Blend 效果），
    /// 单靠 PopSurface 拿不到背景——本批按 SourceOver 处理并告警一次。
    /// </summary>
    public void PopSurface(Paint2D p)
    {
        if (_surfaceStack.Count == 0) return;
        var frame = _surfaceStack[^1];
        _surfaceStack.RemoveAt(_surfaceStack.Count - 1);

        Ctx.Target = frame.PrevTarget;
        _matrix = frame.PrevMatrix;
        Rt.Transform = _matrix;

        var chain = _effects ??= new EffectChain(Ctx);

        // 投影：D2D 的 Shadow 只出影子，所以先画影子（按偏移平移），再画本体
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

    // ---------------- 调用方持有的面 + 两输入合成（转场 / 层间混合） ----------------

    /// <summary>建一张调用方持有的离屏面（用完自己 Dispose）。</summary>
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

    /// <summary>把后续绘制重定向到调用方持有的面（进入时重置变换；<paramref name="clear"/> 默认清空）。</summary>
    public void PushSurface(Surface2D surface, bool clear = true)
    {
        _surfaceStack.Add(new SurfaceFrame(surface.Bitmap, Ctx.Target, _matrix, surface.Width, surface.Height));
        Ctx.Target = surface.Bitmap;
        _matrix = Matrix3x2.Identity;
        Rt.Transform = _matrix;
        if (clear) Ctx.Clear(new Color4(0f, 0f, 0f, 0f));
    }

    /// <summary>结束当前面、恢复上一层目标——**不做合成**（面留给调用方后续当输入用）。</summary>
    public void PopSurface()
    {
        if (_surfaceStack.Count == 0) return;
        var frame = _surfaceStack[^1];
        _surfaceStack.RemoveAt(_surfaceStack.Count - 1);
        Ctx.Target = frame.PrevTarget;
        _matrix = frame.PrevMatrix;
        Rt.Transform = _matrix;
    }

    /// <summary>
    /// 把两张面按 <paramref name="p"/> 的混合模式合起来画到当前目标：覆盖**全部 17 项**
    /// <see cref="Blend2D"/>（走 D2D 的 Blend 效果，两输入齐全）。转场与图层混合走这条路。
    /// <paramref name="dest"/> 为空时按面的原始尺寸 1:1 落位。
    /// </summary>
    public void DrawComposite(Paint2D p, Surface2D backdrop, Surface2D layer, Rect? dest = null)
    {
        var chain = _effects ??= new EffectChain(Ctx);

        // SrcOver / Plus / SrcCopy 不需要 Blend 效果——直接用合成模式画前景即可（更快，也避免
        // 被 Blend 效果的默认模式带偏）。其余 14 项走两输入 Blend 效果。
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

    // ---------------- 内部：paint → 画刷 + 混合 ----------------

    /// <summary>
    /// 起一笔绘制：解析画刷并设好混合/抗锯齿。返回 null = 本笔完全透明，调用方直接跳过。
    /// 与 <see cref="EndDraw"/> 必须成对（EndDraw 放 finally）。
    /// </summary>
    private ID2D1Brush? BeginDraw(Paint2D p)
    {
        var alpha = Math.Clamp(p.Alpha, 0f, 1f);
        if (alpha <= 0.002f) return null;

        var brush = ResolveBrush(p.Brush, alpha);
        ApplyBlend(p.Blend);
        ApplyAntialias(p.IsAntialias);
        return brush;
    }

    /// <summary>收一笔绘制：混合与抗锯齿复位（原 DrawCore 的 finally 段）。</summary>
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
            return null; // 默认样式：交给 D2D 内建

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
