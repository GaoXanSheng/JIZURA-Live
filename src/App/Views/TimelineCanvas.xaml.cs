using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JizuraLive.Engine;

namespace JizuraLive.App.Views;

// 时间轴（对位 TimelineCanvas.vue）：上条视图滑动 / 中条播放头 / 下条时间轴本体。
//
// 实现选型：WPF DrawingContext + FormattedText 自绘（不用 SKElement）——
// 这条时间轴只有几百个矩形 + 十几段 10px 小字，矢量直绘在任何 DPI 下都锐利，
// 无需 Skia 光栅面与像素拷贝；SKElement 的价值在逐像素特效（舞台预览那种），这里用不上。
//
// 交互清单（逐项对齐 Vue 版）：
//   上条：点按=视窗块中心瞬移；拖动=块跟手 1:1 平移；Shift/横向滚轮=平移，滚轮=缩放
//   中条：点按/拖动=自由 seek（BeginScrub/EndScrub，音频静默跟随）
//   下条：点按/拖动=seek；行界刻度悬停出 ew-resize，拖动改行时刻（吸附最近节拍 0.12s，
//         Shift 自由；钳在相邻行 ±0.2s；一次手势一条历史）；滚轮缩放（Ctrl 加速），Shift 平移
//   播放头跟随：放大且播放中，播放头越过视窗 92% 即自动平移跟走
//
// 与 Vue 的取舍：导出范围底色（exportRange）M3 未接导出模型，不画；循环区间 overlay 的
// cut/line 语义在本文件以同名私有方法实现（引擎 M3 只有整首 Loop bool，模式 UI 后续接）。
public partial class TimelineCanvas : UserControl
{
    private readonly DispatcherTimer _clock;

    public TimelineCanvas()
    {
        InitializeComponent();
        // 10Hz 跟随 Transport 的刷新思路：只驱动 Surface 重绘播放头，逻辑全在 Surface 里
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _clock.Tick += (_, _) => Surface.OnClock();
        Loaded += (_, _) =>
        {
            var st = App.MainInstance?.State;
            if (st != null)
            {
                st.StateChanged += Surface.NotifyPlanChanged; // plan 更新重算布局
                st.AudioChanged += Surface.NotifyPlanChanged; // 波形投入/卸载
            }
            Surface.NotifyPlanChanged();
            _clock.Start();
        };
    }

    private void OnUndo(object sender, RoutedEventArgs e) => App.MainInstance?.State.Undo();
    private void OnZoomIn(object sender, RoutedEventArgs e) => Surface.ZoomBy(1.6);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Surface.ZoomBy(1 / 1.6);
    private void OnZoomAll(object sender, RoutedEventArgs e) => Surface.ZoomAll();
}

/// <summary>三带合一的时间轴表面：绘制、命中与拖拽都在这一个元素里，状态不用跨元素同步。</summary>
public sealed class TimelineSurface : FrameworkElement
{
    // ---------------- 三带几何（对位 Vue 三块 canvas 的高度） ----------------

    private const double PanH = 16;              // 上条：视图滑动
    private const double PhH = 20;               // 中条：播放头
    internal const double BodyH = 60;            // 下条：时间轴本体
    private double BodyTop => PanH + PhH;

    // ---------------- 视图状态（对位 Vue 的 TL） ----------------

    private double _zoom = 1;                    // 缩放倍率（1 = 整首）
    private double _off;                         // 可视窗起点（秒）
    private int _dragLine = -1;                  // 正在拖的行界
    private int _hoverLine = -1;                 // 指针下的行界
    private bool _panDrag, _phScrub, _bodyScrub, _lineDrag;
    private double _panLastX;

    // 行界拖动节流（Vue 用 rAF 合并；这里 30ms DispatcherTimer 合并，Replan 一次全量规划别按指针频率跑）
    private readonly DispatcherTimer _dragTimer;
    private int _pendingLine = -1;
    private double _pendingX;
    private bool _pendingShift;

    /// <summary>循环显示模式：all / line / cut（对位 S.loop；引擎 M3 只有整首 Loop，这里管 overlay 显示）。</summary>
    public string LoopMode { get; set; } = "all";

    private int _planVersion;                    // plan/音频变化时 +1，参与静态层缓存键
    private string? _cacheKey;                   // 静态层缓存键（对位 tlCacheKey）
    private double _lastT = double.NaN;          // 上次时钟读数：没动且静态层没变就跳过重绘
    private double _ppd = 1;                     // PixelsPerDip
    private readonly Dictionary<int, FormattedText> _cutLabels = new();
    private readonly Dictionary<long, FormattedText> _lineLabels = new(); // key = index*2 + (高亮?1:0)

    public TimelineSurface()
    {
        _dragTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(30) };
        _dragTimer.Tick += (_, _) =>
        {
            if (_pendingLine < 0) return;
            DragTo(_pendingLine, _pendingX, _pendingShift);
            _pendingLine = -1;
        };
    }

    // ---------------- 外部驱动 ----------------

    /// <summary>plan/音频/工程变化：清缓存键，下帧整幅重建（State.StateChanged / AudioChanged 调）。</summary>
    public void NotifyPlanChanged()
    {
        _planVersion++;
        _cacheKey = null;
        InvalidateVisual();
    }

    /// <summary>10Hz 时钟：放大且播放中，播放头出可视区就跟着平移；其余情况只重绘播放头。</summary>
    public void OnClock()
    {
        if (App.MainInstance == null) return;
        var preview = App.MainInstance.Preview;
        if (_zoom > 1 && preview.IsPlaying && _dragLine < 0 && !_lineDrag)
        {
            var t = preview.CurrentTime;
            var vd = ViewDur;
            if (t < _off || t > _off + vd * 0.92)
            {
                _off = Math.Max(0, t - vd * 0.1);
                NormalizeOff();
                _cacheKey = null;
            }
        }
        // 暂停且静态层没动：播放头位置不变，重绘纯浪费
        var now = preview.CurrentTime;
        if (_cacheKey == null || !now.Equals(_lastT))
        {
            _lastT = now;
            InvalidateVisual();
        }
    }

    public void ZoomBy(double f) => ZoomAt(f, null);
    public void ZoomAll()
    {
        _zoom = 1;
        _off = 0;
        _cacheKey = null;
        InvalidateVisual();
    }

    // ---------------- 数据入口 ----------------

    private static EditorState? State => App.MainInstance?.State;
    private static PreviewEngine? Preview => App.MainInstance?.Preview;

    private double Dur => State?.Plan?.Duration ?? 0;
    private double ViewDur => Math.Max(0.001, Dur / _zoom);
    private double XOf(double t) => (t - _off) / ViewDur * ActualWidth;
    private double TimeOfX(double x) => _off + Math.Clamp(x / Math.Max(1, ActualWidth), 0, 1) * ViewDur;

    private void NormalizeOff() => _off = Math.Clamp(_off, 0, Math.Max(0, Dur - ViewDur));

    /// <summary>缩放（对位 tlZoom）：绕锚点时刻 tc 缩放，视图跟随指针/播放头。</summary>
    private void ZoomAt(double f, double? tc)
    {
        var d = Math.Max(0.001, Dur);
        var vd = d / _zoom;
        var z = Math.Clamp(_zoom * f, 1, Math.Max(1, d / 2));
        var c = tc ?? Preview?.CurrentTime ?? 0;
        var frac = (c - _off) / vd;
        _zoom = z;
        _off = c - frac * (d / z);
        NormalizeOff();
        _cacheKey = null;
        InvalidateVisual();
    }

    // ---------------- 命中与拖拽 ----------------

    /// <summary>指针下的行界手柄（对位 tlHandleAt）：上带 40% 内、距行界 8px 内最近者。</summary>
    private int HandleAt(double x, double y)
    {
        var plan = State?.Plan;
        if (plan == null) return -1;
        if (y - BodyTop > BodyH * 0.4) return -1;
        var best = -1;
        var bd = 8.0;
        foreach (var ln in plan.Lines)
        {
            var d = Math.Abs(XOf(ln.Start) - x);
            if (d < bd)
            {
                bd = d;
                best = ln.Index;
            }
        }
        return best;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (App.MainInstance == null) return;
        var p = e.GetPosition(this);
        if (p.Y < PanH)
        {
            // 音量条语义：点哪，视窗块中心瞬移到哪，之后块跟手 1:1 拖
            CaptureMouse();
            _panDrag = true;
            _panLastX = p.X;
            var frac = Math.Clamp(p.X / Math.Max(1, ActualWidth), 0, 1);
            _off = frac * Dur - ViewDur / 2;
            NormalizeOff();
            Cursor = Cursors.Hand;
        }
        else if (p.Y < BodyTop)
        {
            // 播放头条：自由定位、不吸附；拖动中音频静默跟随
            CaptureMouse();
            _phScrub = true;
            Preview!.BeginScrub();
            Preview.Seek(TimeOfX(p.X));
            Cursor = Cursors.SizeWE;
        }
        else
        {
            var h = HandleAt(p.X, p.Y);
            if (h >= 0)
            {
                // 行界拖动：一次手势压一条历史（对位 pushEdit 后直接改 + replan 的模式）
                CaptureMouse();
                _dragLine = h;
                _lineDrag = true;
                Preview!.Pause();
                BeginLineMutate(h);
                Cursor = Cursors.SizeWE;
            }
            else
            {
                // 时间轴本体：点击/拖动 seek（对位 Vue：这里不 beginScrub，音频照常跟 seek 重启）
                CaptureMouse();
                _bodyScrub = true;
                Preview!.Seek(TimeOfX(p.X));
                Cursor = Cursors.SizeWE;
            }
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (App.MainInstance == null) return;
        var p = e.GetPosition(this);
        if (_panDrag)
        {
            // 滚动条语义：握住视窗块直接拖，块跟手 1:1（对位 onPanMove 的注释取舍）
            _off += (p.X - _panLastX) / Math.Max(1, ActualWidth) * Dur;
            _panLastX = p.X;
            NormalizeOff();
            _cacheKey = null;
        }
        else if (_phScrub || _bodyScrub)
        {
            Preview!.Seek(TimeOfX(p.X));
        }
        else if (_lineDrag)
        {
            _pendingLine = _dragLine;
            _pendingX = p.X;
            _pendingShift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        }
        else
        {
            var hv = HandleAt(p.X, p.Y);
            if (hv != _hoverLine)
            {
                _hoverLine = hv;
                _cacheKey = null;
            }
            Cursor = hv >= 0 ? Cursors.SizeWE : p.Y >= BodyTop ? Cursors.Arrow : Cursors.Hand;
        }
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_lineDrag)
        {
            if (_pendingLine >= 0) DragTo(_pendingLine, _pendingX, _pendingShift); // 兜掉 timer 前松手的最后一跳
            _pendingLine = -1;
            _dragTimer.Stop();
            _dragLine = -1;
            _lineDrag = false;
            if (State != null) ProjectIO.Autosave(State.Project); // 直接改过的行时刻补一次防抖存盘
        }
        _panDrag = false;
        if (_phScrub)
        {
            _phScrub = false;
            Preview?.EndScrub();
        }
        _bodyScrub = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        Cursor = Cursors.Arrow;
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverLine != -1 && _dragLine < 0)
        {
            _hoverLine = -1;
            _cacheKey = null;
            InvalidateVisual();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var p = e.GetPosition(this);
        // WPF Delta 与浏览器 deltaY 反号（下滚 Delta<0）：取负后与 Vue 同向——下滚=缩放/平移朝后
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            _off -= e.Delta / Math.Max(1, ActualWidth) * ViewDur;
            NormalizeOff();
            _cacheKey = null;
            InvalidateVisual();
        }
        else
        {
            ZoomAt(Math.Exp(e.Delta * (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 0.01 : 0.0025)),
                TimeOfX(p.X));
        }
        e.Handled = true;
    }

    /// <summary>拖动开始的事务：把全部行界钉进 LineTimes + 压一条历史（对位 tlDragTo 的 pin 一并做掉）。</summary>
    private void BeginLineMutate(int i)
    {
        var plan = State?.Plan;
        if (State == null || plan == null) return;
        State.Mutate(p =>
        {
            var lt = p.Timing.LineTimes;
            foreach (var ln in plan.Lines)
                lt.TryAdd(ln.Index.ToString(), Math.Round(ln.Start, 3));
        }, "行界拖动");
        _ = i; // 锚定行由 DragTo 逐跳写入
        _dragTimer.Start();
    }

    /// <summary>行界拖到某处（对位 tlDragTo）：吸附节拍 → 钳位 → 直改 + 重规划 + seek（画面实时跟手）。</summary>
    private void DragTo(int i, double x, bool shift)
    {
        var state = State;
        var plan = state?.Plan;
        var preview = Preview;
        if (state == null || plan == null || preview == null) return;
        var t0 = TimeOfX(x);
        var t = t0;
        if (!shift && plan.Beats is { Length: > 0 })
        {
            double? bt = null;
            var bd = 0.12;
            foreach (var b in plan.Beats)
            {
                var d = Math.Abs(b - t0);
                if (d < bd)
                {
                    bd = d;
                    bt = b;
                }
                if (b > t0 + 0.2) break;
            }
            if (bt != null) t = bt.Value;
        }
        var lines = plan.Lines;
        var lo = i > 0 ? lines[i - 1].Start + 0.2 : 0;
        var hi = i < lines.Count - 1 ? lines[i + 1].Start - 0.2 : plan.Duration - 0.2;
        t = Math.Round(Math.Clamp(t, lo, Math.Max(lo, hi)), 3);
        // Vue 模式：拖动手势内直改工程（历史已在 BeginLineMutate 压栈），replan + seek 实时预览
        state.Project.Timing.LineTimes[i.ToString()] = t;
        App.MainInstance!.State.ReplanAsync(); // 拖动改行界：后台重规划（last-wins 合并高频请求）
        preview.Seek(t + 0.001);
        _cacheKey = null;
    }

    // ---------------- 循环区间（对位 state.ts 的 cutAround / loopRange 同名语义） ----------------

    private Cut? CutAround(double t)
    {
        var plan = State?.Plan;
        if (plan == null) return null;
        var c = Planner.CutAt(plan, t);
        if (c != null) return c;
        Cut? ans = null;
        foreach (var cut in plan.Cuts)
        {
            if (cut.Start <= t) ans = cut;
            else break;
        }
        return ans;
    }

    private (double Start, double End) LoopRange(double t)
    {
        var plan = State?.Plan;
        var endAll = plan?.Duration ?? 0;
        if (LoopMode == "cut")
        {
            var c = CutAround(t);
            return c != null ? (c.Start, c.End) : (0, endAll);
        }
        if (LoopMode == "line")
        {
            var c = CutAround(t);
            if (c == null) return (0, endAll);
            if (c.Line < 0) return (c.Start, c.End);
            var same = new List<Cut>();
            foreach (var x in plan!.Cuts)
                if (x.Line == c.Line && x.Layout != "interlude" && x.Layout != "title")
                    same.Add(x);
            if (same.Count == 0) return (c.Start, c.End);
            return (same[0].Start, same[^1].End);
        }
        return (0, endAll);
    }

    // ---------------- 绘制 ----------------

    private static SolidColorBrush _bPanel = null!, _bAmber = null!, _bMut = null!, _bFaint = null!, _bFg = null!;

    private static SolidColorBrush BrushOf(FrameworkElement fe, string key, Color fb)
    {
        var b = (fe.TryFindResource(key) as SolidColorBrush)?.Clone() ?? new SolidColorBrush(fb);
        if (b.CanFreeze) b.Freeze();
        return b;
    }

    private static SolidColorBrush Alpha(Color c, byte a)
    {
        var b = new SolidColorBrush(Color.FromArgb(a, c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    private static Color Hsla(double h, double s, double l, double a)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return Color.FromArgb((byte)Math.Round(a * 255), Hc(p, q, h + 120), Hc(p, q, h), Hc(p, q, h - 120));
    }

    private static byte Hc(double p, double q, double h)
    {
        h = ((h % 360) + 360) % 360 / 360;
        var v = h < 1.0 / 6 ? p + (q - p) * 6 * h
            : h < 0.5 ? q
            : h < 2.0 / 3 ? p + (q - p) * (2.0 / 3 - h) * 6
            : p;
        return (byte)Math.Round(v * 255);
    }

    private FormattedText Ft(string s, double size, Brush brush, double ppd)
        => new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Consolas, Microsoft YaHei UI, Meiryo"), FontStyles.Normal,
                FontWeights.Normal, FontStretches.Normal),
            size, brush, ppd);

    /// <summary>layout → 色相（对位 layoutHue：(J.LAYOUT_ORDER 序号 * 37 + 30) % 360，未知 layout 落 353）。</summary>
    private static double LayoutHue(string layout)
    {
        var idx = Array.IndexOf(JLayouts.LayoutOrder, layout);
        return ((idx * 37 + 30) % 360 + 360) % 360;
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (App.MainInstance == null) return;
        var state = State!;
        var preview = Preview!;
        var plan = state.Plan;

        var w = Math.Max(1, ActualWidth);
        var h = Math.Max(1, ActualHeight);
        var d = Math.Max(0.001, plan?.Duration ?? 0);
        var vd = ViewDur;
        var t = preview.CurrentTime;

        if (_bPanel == null)
        {
            _bPanel = BrushOf(this, "Panel", Color.FromRgb(0x13, 0x13, 0x16));
            _bAmber = BrushOf(this, "Amber", Color.FromRgb(0xF5, 0xA5, 0x0C));
            _bMut = BrushOf(this, "Mut", Color.FromRgb(0x8E, 0x8A, 0x94));
            _bFaint = BrushOf(this, "Faint", Color.FromRgb(0x5D, 0x5A, 0x63));
            _bFg = BrushOf(this, "Fg", Color.FromRgb(0xEC, 0xE7, 0xE1));
        }
        var white = Color.FromRgb(255, 255, 255);
        var amber = _bAmber.Color;
        _ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 静态层缓存键（对位 tlCacheKey）：视图/悬停/plan/音频变了才重建文字缓存，播放头走零分配路径
        var key = $"{w:F0}|{h:F0}|{_off:F3}|{vd:F3}|{_zoom:F4}|{_dragLine}|{_hoverLine}|{LoopMode}|{_planVersion}|{(state.Audio?.Duration ?? 0):F2}|{plan?.Cuts.Count ?? 0}|{plan?.Lines.Count ?? 0}";
        if (key != _cacheKey)
        {
            _cacheKey = key;
            _cutLabels.Clear();
            _lineLabels.Clear();
        }

        var bodyTop = BodyTop;
        var top = bodyTop + 16;   // 行界带占本体顶部 16px
        var bot = h - 8;

        // 底
        dc.DrawRectangle(_bPanel, null, new Rect(0, 0, w, h));

        // ---- 上条：视图滑动 ----
        dc.DrawRectangle(Alpha(white, 12), null, new Rect(0, 0, w, PanH)); // white 4.5%
        if (_zoom > 1)
            dc.DrawRectangle(Alpha(amber, 115), null,
                new Rect(_off / d * w, 1.5, Math.Max(6, vd / d * w), PanH - 3));
        else
            dc.DrawRectangle(Alpha(white, 23), null, new Rect(0, 1.5, w, PanH - 3)); // white 9%

        // ---- 中条：播放头（背景 + 中线；三角/竖线在最后叠） ----
        dc.DrawRectangle(Alpha(white, 5), null, new Rect(0, PanH, w, PhH)); // white 2%
        dc.DrawRectangle(Alpha(white, 18), null, new Rect(0, PanH + PhH / 2 - 0.5, w, 1)); // white 7%

        if (plan != null)
        {
            var lineTimes = state.Project.Timing.LineTimes;

            // ---- 行界：刻度线 + 三角手柄 + 行号 ----
            foreach (var ln in plan.Lines)
            {
                var lx = XOf(ln.Start);
                if (lx < -20 || lx > w + 2) continue;
                var on = ln.Index == _dragLine || ln.Index == _hoverLine;
                var man = lineTimes.ContainsKey(ln.Index.ToString());
                var brush = on ? _bAmber : man ? ManualBrush : _bFaint;
                dc.DrawRectangle(brush, null, new Rect(Math.Round(lx) - (on ? 1 : 0), bodyTop, on ? 2 : 1, 16));
                var tri = new StreamGeometry();
                using (var g = tri.Open())
                {
                    g.BeginFigure(new Point(lx - 5, bodyTop), true, true);
                    g.LineTo(new Point(lx + 5, bodyTop), true, false);
                    g.LineTo(new Point(lx, bodyTop + 7), true, false);
                }
                tri.Freeze();
                dc.DrawGeometry(brush, null, tri);
                var lkey = (long)ln.Index * 2 + (on ? 1 : 0);
                if (!_lineLabels.TryGetValue(lkey, out var lft))
                {
                    lft = Ft((ln.Index + 1).ToString("00") + (ln.Interlude ? " 间奏" : ""), 10, on ? _bAmber : _bMut, _ppd);
                    _lineLabels[lkey] = lft;
                }
                dc.DrawText(lft, new Point(lx + 4, bodyTop + 2));
            }

            // ---- 波形（垫底）----
            if (state.Audio?.Peaks is { Length: > 0 } pk)
            {
                var n = pk.Length;
                var sd = Math.Max(0.001, state.Audio.Duration);
                var wf = WaveBrush;
                var mid = top + (bot - top) / 2;
                for (var i = 0; i < w; i += 2)
                {
                    var st = TimeOfX(i);
                    if (st > sd) break;
                    if (st < 0) continue;
                    var v = pk[Math.Min(n - 1, (int)(st / sd * n))];
                    var hh = v * (bot - top) * 0.85;
                    if (hh < 0.5) continue;
                    dc.DrawRectangle(wf, null, new Rect(i, mid - hh / 2, 1.5, hh));
                }
            }

            // ---- 节拍刻度（底缘）----
            var bt2 = BeatBrush;
            foreach (var b in plan.Beats)
            {
                if (b < _off) continue;
                if (b > _off + vd) break;
                dc.DrawRectangle(bt2, null, new Rect(Math.Round(XOf(b)), h - 6, 1, 6));
            }

            // ---- 切分块（layout 决定色相；interlude/title 不在 LAYOUT_ORDER，同落 353 区分色）----
            foreach (var cut in plan.Cuts)
            {
                var x0 = XOf(cut.Start);
                var x1 = XOf(cut.End);
                if (x1 < 0 || x0 > w) continue;
                var hue = LayoutHue(cut.Layout);
                var bw = Math.Max(1, x1 - x0 - 1);
                dc.DrawRectangle(Hb(hue, 70, 58, 0.28), null, new Rect(x0, top, bw, bot - top));
                dc.DrawRectangle(Hb(hue, 80, 62, 0.95), null, new Rect(x0, top, Math.Max(1, 2), bot - top));
                if (x1 - x0 > 34)
                {
                    var text = _zoom > 3 && !string.IsNullOrEmpty(cut.Text)
                        ? cut.Text
                        : Reg.Layouts.GetValueOrDefault(cut.Layout)?.Name ?? cut.Layout;
                    if (!_cutLabels.TryGetValue(cut.Index, out var cft) || cft.Text != text)
                    {
                        cft = Ft(text, 10, Alpha(_bFg.Color, 217), _ppd);
                        _cutLabels[cut.Index] = cft;
                    }
                    dc.PushClip(new RectangleGeometry(new Rect(x0, top, Math.Max(1, x1 - x0 - 3), bot - top)));
                    dc.DrawText(cft, new Point(x0 + 5, top + 2));
                    dc.Pop();
                }
            }

            // ---- 循环区间（cut/line 模式下画琥珀罩；all 模式由整首循环表达，不画）----
            if (LoopMode is "line" or "cut")
            {
                var r = LoopRange(t);
                var rx0 = XOf(r.Start);
                dc.DrawRectangle(Alpha(amber, 41), null,
                    new Rect(rx0, bodyTop, Math.Max(2, XOf(r.End) - rx0), BodyH));
            }
        }

        // ---- 缩放范围条（底缘，对位 Vue 的底部黄条）----
        if (_zoom > 1)
        {
            dc.DrawRectangle(Alpha(white, 46), null, new Rect(0, h - 2, w, 2)); // white 18%
            dc.DrawRectangle(_bAmber, null, new Rect(_off / d * w, h - 2, Math.Max(4, vd / d * w), 2));
        }

        // ---- 播放头（最后叠：中条竖线 + 三角 + 本体竖线）----
        var px = Math.Round(XOf(t));
        dc.DrawRectangle(_bAmber, null, new Rect(px - 1, PanH, 2, PhH));
        var ptri = new StreamGeometry();
        using (var g2 = ptri.Open())
        {
            g2.BeginFigure(new Point(px - 5, PanH), true, true);
            g2.LineTo(new Point(px + 5, PanH), true, false);
            g2.LineTo(new Point(px, PanH + PhH * 0.55), true, false);
        }
        ptri.Freeze();
        dc.DrawGeometry(_bAmber, null, ptri);
        dc.DrawRectangle(_bAmber, null, new Rect(px - 1, bodyTop, 2, BodyH));
    }

    // 懒建的一次性小色刷（Vue 特有配色，token 里没有）
    private SolidColorBrush? _manual, _wave, _beat;
    private SolidColorBrush ManualBrush => _manual ??= Frozen(Color.FromRgb(0x6F, 0xB7, 0xC8));
    private SolidColorBrush WaveBrush => _wave ??= Frozen(Color.FromRgb(0x2B, 0x2B, 0x33));
    private SolidColorBrush BeatBrush => _beat ??= Frozen(Color.FromRgb(0x3A, 0x3A, 0x44));

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static readonly Dictionary<long, SolidColorBrush> _hueCache = new();

    /// <summary>hsla(h, s%, l%, a) 切分块配色（按 h/s/l/a 量化缓存，避免每帧新建刷子）。</summary>
    private static SolidColorBrush Hb(double h, double s, double l, double a)
    {
        var k = (long)Math.Round(((h % 360) + 360) % 360) * 1_000_000L
                + (long)s * 1000L + (long)l + (long)Math.Round(a * 100) * 100_000_000L;
        if (_hueCache.TryGetValue(k, out var b)) return b;
        b = Frozen(Hsla(h, s / 100.0, l / 100.0, a));
        _hueCache[k] = b;
        return b;
    }
}
