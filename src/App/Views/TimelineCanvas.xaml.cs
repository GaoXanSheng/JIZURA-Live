using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JizuraLive.App.I18n;
using JizuraLive.Engine;

namespace JizuraLive.App.Views;

// 时间轴（对位 TimelineCanvas.vue）：上条视图滑动 / 中条播放头 / 下条时间轴本体。
//
// 实现选型：WPF DrawingContext + FormattedText 自绘（不用 SKElement）——
// 这条时间轴只有几百个矩形 + 十几段 10px 小字，矢量直绘在任何 DPI 下都锐利，
// 无需 Skia 光栅面与像素拷贝；SKElement 的价值在逐像素特效（舞台预览那种），这里用不上。
//
// 视觉对齐（照抄 Vue 三块 canvas 的绘制参数）：
//   上条 16px：白 4.5% 底 + 视窗块（缩放时琥珀 45% 块，内缩 1.5px；未缩放时白 9% 通条）
//   中条 20px：白 2% 底 + 白 7% 中线（1px）+ 播放头竖线 2px 与 10×11 三角
//   本体 60px：1px line 边框（CSS border border-line）+ 内容盒 58px；行界刻度带 16px
//              （刻度 1/2px、10×7 三角、10px mono 行号基线 14）、波形 #2b2b33、
//              节拍 #3a3a44 底缘 6px、切分块 hsla(hue,70%,58%,.28)+左缘 2px hsla(80%,62%,.95)+
//              10px mono 名称（基线 top+13，裁到块内）、缩放范围条 2px（白 18% 槽 + 琥珀块）
//   色相：hue = (LAYOUT_ORDER 序号 * 37 + 30) % 360（未知 layout 落 353）
//
// 交互清单（逐项对齐 Vue 版）：
//   上条：点按=视窗块中心瞬移；拖动=块跟手 1:1 平移；Shift/横向滚轮=平移，滚轮=缩放
//   中条：点按/拖动=自由 seek（BeginScrub/EndScrub，音频静默跟随）
//   下条：点按/拖动=seek；行界刻度悬停出 ew-resize，拖动改行时刻（吸附最近节拍 0.12s，
//         Shift 自由；钳在相邻行 ±0.2s；一次手势一条历史）；滚轮缩放（Ctrl 加速），Shift 平移
//   播放头跟随：放大且播放中，播放头越过视窗 92% 即自动平移跟走
//
// 与 Vue 的差异：上条的 grab/grabbing 光标 WPF 无对应内置光标，沿用 Hand。
// 导出范围底色与循环区间 overlay 均已完整接入。
public partial class TimelineCanvas : UserControl
{
    private readonly DispatcherTimer _clock;

    public TimelineCanvas()
    {
        InitializeComponent();
        // 空闲 10Hz / 播放中 ~30fps：播放头是自绘的，只有重绘才会动 —— 10Hz 看着就是一格一格跳。
        // 静态层的文字有缓存（_cutLabels/_lineLabels），播放中的重绘只重记几百个矩形 + 播放头；
        // 暂停时 OnClock 靠「时刻没变就跳过」自动回到零重绘。
        _clock = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _clock.Tick += (_, _) =>
        {
            Surface.OnClock();
            var want = App.MainInstance?.Preview.IsPlaying == true ? 33 : 100;
            if (Math.Abs(_clock.Interval.TotalMilliseconds - want) > 1)
                _clock.Interval = TimeSpan.FromMilliseconds(want);
        };
        Loaded += (_, _) =>
        {
            var st = App.MainInstance?.State;
            if (st != null)
            {
                st.StateChanged += Surface.NotifyPlanChanged; // plan 更新重算布局
                st.AudioChanged += Surface.NotifyPlanChanged; // 波形投入/卸载
            }
            L.LanguageChanged -= OnLanguageChanged;
            L.LanguageChanged += OnLanguageChanged;
            Surface.NotifyPlanChanged();
            _clock.Start();
        };
        Unloaded += (_, _) => L.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>换语言：丢弃绘制的「间奏」标注缓存并整幅重绘（L.LanguageChanged 在 UI 线程广播）。</summary>
    private void OnLanguageChanged() => Surface.InvalidateTexts();

    private void OnUndo(object sender, RoutedEventArgs e) => App.MainInstance?.State.Undo();
    private void OnZoomIn(object sender, RoutedEventArgs e) => Surface.ZoomBy(1.6);
    private void OnZoomOut(object sender, RoutedEventArgs e) => Surface.ZoomBy(1 / 1.6);
    private void OnZoomAll(object sender, RoutedEventArgs e) => Surface.ZoomAll();
}

/// <summary>三带合一的时间轴表面：绘制、命中与拖拽都在这一个元素里，状态不用跨元素同步。</summary>
public sealed class TimelineSurface : FrameworkElement
{
    // ---------------- 三带几何（对位 Vue 三块 canvas 的高度） ----------------

    private const double PanH = 16;              // 上条：视图滑动（canvas h-[16px]）
    private const double PhH = 20;               // 中条：播放头（canvas h-[20px]）
    internal const double BodyH = 60;            // 下条：时间轴本体（canvas h-[60px]，含 1px 边框）
    private const double BodyInset = 1;          // 本体边框宽：Vue 用 CSS border border-line，
                                                 // box-sizing: border-box → 内容盒 58px，绘制全在内容盒里
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

    /// <summary>循环档位与范围不再本地存：直接读引擎（Preview.Loop / ActiveLoopRange），
    /// 换档/改选段时进静态层缓存键，下一次重绘自动跟上（对位 Vue 的 tlCacheKey 含 S.loop）。</summary>

    private int _planVersion;                    // plan/音频变化时 +1，参与静态层缓存键
    private string? _cacheKey;                   // 静态层缓存键（对位 tlCacheKey）
    private double _lastT = double.NaN;          // 上次时钟读数：没动且静态层没变就跳过重绘
    private double _ppd = 1;                     // PixelsPerDip
    private string? _interludeTag;               // 行界标注「间奏」（词库缓存：绘制路径逐帧取词太贵，换语言时置空）
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

    /// <summary>换语言：「间奏」标注重新取词 + 整幅重建（L.LanguageChanged 调，UI 线程）。</summary>
    public void InvalidateTexts()
    {
        _interludeTag = null;
        NotifyPlanChanged();
    }

    /// <summary>时钟（空闲 10Hz / 播放中 ~30fps，见 ctor）：放大且播放中，播放头出可视区就跟着平移；
    /// 其余情况只重绘播放头，时刻没变则整帧跳过。</summary>
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
            // 悬停光标（对位 Vue 三块 canvas 的 CSS cursor）：上条 grab / 中条 ew-resize /
            // 本体 pointer（行界上 ew-resize）。WPF 无 grab 开手光标，用 Hand 顶。
            Cursor = hv >= 0 || (p.Y >= PanH && p.Y < BodyTop) ? Cursors.SizeWE : Cursors.Hand;
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
        }, L.T("Timeline.Mutate.LineDrag"));
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

    // ---------------- 绘制 ----------------

    private static SolidColorBrush _bPanel = null!, _bAmber = null!, _bMut = null!, _bFaint = null!, _bFg = null!, _bLine = null!;
    private static Pen _penEdge = null!;

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

    /// <summary>「间奏」标注（词库取词，字段缓存；InvalidateTexts 换语言时置空）。</summary>
    private string InterludeTag => _interludeTag ??= L.T("Timeline.Interlude");

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
            _bLine = BrushOf(this, "LineBrush", Color.FromRgb(0x2A, 0x2A, 0x31)); // 本体边框（CSS border-line）
            _penEdge = new Pen(_bLine, 1);
            _penEdge.Freeze();
        }
        var white = Color.FromRgb(255, 255, 255);
        var amber = _bAmber.Color;
        _ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // 循环档位 + 生效范围、导出范围（选段）都进静态层缓存键：
        // 换档 / 改选段 / 播放头换行导致范围变化时，下一次重绘自动重建静态层
        // （对位 Vue tlCacheKey 含 S.loop + activeLoopRange + exportRange）
        var loopMode = preview.Loop;
        var (loopS, loopE) = preview.ActiveLoopRange();
        var exportSpan = Ranges.ExportSpanSeconds(plan, state.Project.ExportRange);

        // 静态层缓存键（对位 tlCacheKey）：视图/悬停/plan/音频变了才重建文字缓存，播放头走零分配路径
        var key = $"{w:F0}|{h:F0}|{_off:F3}|{vd:F3}|{_zoom:F4}|{_dragLine}|{_hoverLine}|{loopMode}|{loopS:F3}|{loopE:F3}"
                  + $"|{exportSpan?.T0:F3}|{exportSpan?.T1:F3}|{_planVersion}|{(state.Audio?.Duration ?? 0):F2}|{plan?.Cuts.Count ?? 0}|{plan?.Lines.Count ?? 0}";
        if (key != _cacheKey)
        {
            _cacheKey = key;
            _cutLabels.Clear();
            _lineLabels.Clear();
        }

        // ---- 几何：三带（上条 0..PanH / 中条 PanH..BodyTop / 本体 BodyTop..+BodyH）----
        // 本体内容盒缩进 1px：对位 Vue 给 body canvas 的 CSS `border border-line`
        // （box-sizing: border-box → clientWidth/Height 各少 2px，canvas 绘制全在内容盒里）。
        var bodyTop = BodyTop;
        var bx = BodyInset;                        // 内容盒左缘
        var by = bodyTop + BodyInset;              // 内容盒上缘
        var bw = Math.Max(1, w - 2 * BodyInset);   // 内容宽（对位 clientWidth）
        var bh = BodyH - 2 * BodyInset;            // 内容高 58（对位 clientHeight）
        var top = by + 16;                         // 行界带占本体内容盒顶部 16px（drawStatic 的 top）
        var bot = by + bh - 8;                     // 波形/切分块下缘（drawStatic 的 bot）
        // 本体局部映射（对位 drawStatic 的 X / T；命中与 seek 仍走全宽 XOf，差 1px 内）
        double BX(double tt) => bx + (tt - _off) / vd * bw;
        double BTimeOf(double x) => _off + (x - bx) / bw * vd;

        // 底（三带共底的 #131316）
        dc.DrawRectangle(_bPanel, null, new Rect(0, 0, w, h));

        // ---- 上条：视图滑动（对位 drawPanBar：白 4.5% 底 + 视窗块）----
        dc.DrawRectangle(Alpha(white, 11), null, new Rect(0, 0, w, PanH)); // white 4.5%
        if (_zoom > 1)
            dc.DrawRectangle(Alpha(amber, 115), null,                                          // rgba(245,165,12,.45)
                new Rect(_off / d * w, 1.5, Math.Max(6, vd / d * w), PanH - 3));
        else
            dc.DrawRectangle(Alpha(white, 23), null, new Rect(0, 1.5, w, PanH - 3)); // white 9%

        // ---- 中条：播放头底 + 中线（对位 drawPhBar 的白 2% 底与白 7% 中线；三角/竖线在最后叠）----
        dc.DrawRectangle(Alpha(white, 5), null, new Rect(0, PanH, w, PhH)); // white 2%
        dc.DrawRectangle(Alpha(white, 18), null, new Rect(0, PanH + PhH / 2 - 0.5, w, 1)); // white 7%

        // ---- 本体：内容裁进内容盒（对位 canvas 自己裁内容，勾线/块不压边框）----
        dc.PushClip(new RectangleGeometry(new Rect(bx, by, bw, bh)));
        if (plan != null)
        {
            var lineTimes = state.Project.Timing.LineTimes;

            // ---- 行界：刻度线 + 三角手柄 + 行号（基线落在内容盒顶 14px，对位 fillText(..., 14*dpr)）----
            foreach (var ln in plan.Lines)
            {
                var lx = BX(ln.Start);
                if (lx < bx - 20 || lx > bx + bw + 2) continue;
                var on = ln.Index == _dragLine || ln.Index == _hoverLine;
                var man = lineTimes.ContainsKey(ln.Index.ToString());
                var brush = on ? _bAmber : man ? ManualBrush : _bFaint;
                dc.DrawRectangle(brush, null, new Rect(Math.Round(lx) - (on ? 1 : 0), by, on ? 2 : 1, 16));
                var tri = new StreamGeometry();
                using (var g = tri.Open())
                {
                    g.BeginFigure(new Point(lx - 5, by), true, true);
                    g.LineTo(new Point(lx + 5, by), true, false);
                    g.LineTo(new Point(lx, by + 7), true, false);
                }
                tri.Freeze();
                dc.DrawGeometry(brush, null, tri);
                var lkey = (long)ln.Index * 2 + (on ? 1 : 0);
                if (!_lineLabels.TryGetValue(lkey, out var lft))
                {
                    lft = Ft((ln.Index + 1).ToString("00") + (ln.Interlude ? " " + InterludeTag : ""), 10, on ? _bAmber : _bMut, _ppd);
                    _lineLabels[lkey] = lft;
                }
                dc.DrawText(lft, new Point(lx + 4, by + 14 - lft.Baseline));
            }

            // ---- 波形（垫底）----
            if (state.Audio?.Peaks is { Length: > 0 } pk)
            {
                var n = pk.Length;
                var sd = Math.Max(0.001, state.Audio.Duration);
                var wf = WaveBrush;
                var mid = top + (bot - top) / 2;
                for (var i = 0.0; i < bw; i += 2)
                {
                    var st = BTimeOf(i);
                    if (st > sd) break;
                    if (st < 0) continue;
                    var v = pk[Math.Min(n - 1, (int)(st / sd * n))];
                    var hh = v * (bot - top) * 0.85;
                    if (hh < 0.5) continue;
                    dc.DrawRectangle(wf, null, new Rect(bx + i, mid - hh / 2, 1.5, hh));
                }
            }

            // ---- 节拍刻度（本体内容盒底缘 6px）----
            var bt2 = BeatBrush;
            foreach (var b in plan.Beats)
            {
                if (b < _off) continue;
                if (b > _off + vd) break;
                dc.DrawRectangle(bt2, null, new Rect(Math.Round(BX(b)), by + bh - 6, 1, 6));
            }

            // ---- 切分块（layout 决定色相；interlude/title 不在 LAYOUT_ORDER，同落 353 区分色）----
            foreach (var cut in plan.Cuts)
            {
                var x0 = BX(cut.Start);
                var x1 = BX(cut.End);
                if (x1 < bx || x0 > bx + bw) continue;
                var hue = LayoutHue(cut.Layout);
                var cw = Math.Max(1, x1 - x0 - 1);
                dc.DrawRectangle(Hb(hue, 70, 58, 0.28), null, new Rect(x0, top, cw, bot - top));
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
                    // 基线落在块顶 13px（对位 fillText(..., top + 13*dpr)），左内边距 5px
                    dc.PushClip(new RectangleGeometry(new Rect(x0, top, Math.Max(1, x1 - x0 - 3), bot - top)));
                    dc.DrawText(cft, new Point(x0 + 5, top + 13 - cft.Baseline));
                    dc.Pop();
                }
            }

            // ---- 导出范围（选段）底色：琥珀 10%（对位 TimelineCanvas.vue 的 rgba(245,165,12,.10)）----
            if (exportSpan is { } span)
            {
                var ex0 = BX(span.T0);
                dc.DrawRectangle(Alpha(amber, 26), null,
                    new Rect(ex0, by, Math.Max(2, BX(span.T1) - ex0), bh));
            }

            // ---- 循环区间（分段/选段档画琥珀罩 16%；整首与不循环由本身语义表达，不画）----
            if (loopMode is LoopMode.Line or LoopMode.Cut or LoopMode.Range)
            {
                var rx0 = BX(loopS);
                dc.DrawRectangle(Alpha(amber, 41), null,
                    new Rect(rx0, by, Math.Max(2, BX(loopE) - rx0), bh));
            }
        }

        // ---- 缩放范围条（本体内容盒底缘 2px，对位 Vue 的底部黄条；在白 18% 槽上叠琥珀块）----
        if (_zoom > 1)
        {
            dc.DrawRectangle(Alpha(white, 46), null, new Rect(bx, by + bh - 2, bw, 2)); // white 18%
            dc.DrawRectangle(_bAmber, null, new Rect(bx + _off / d * bw, by + bh - 2, Math.Max(4, vd / d * bw), 2));
        }
        dc.Pop();

        // ---- 本体 1px 边框（对位 body canvas 的 border border-line；半像素对齐求清晰）----
        dc.DrawRectangle(null, _penEdge, new Rect(0.5, bodyTop + 0.5, Math.Max(0, w - 1), BodyH - 1));

        // ---- 播放头（最后叠：中条竖线 + 三角 + 本体竖线）----
        var px = Math.Round(XOf(t));   // 中条按 ph canvas 全宽算（对位 drawPhBar 的 w）
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
        var pxb = Math.Round(BX(t));   // 本体按 body canvas 内容宽算（对位 drawPlayheadLine；与中条最多差 2px，Vue 同）
        dc.PushClip(new RectangleGeometry(new Rect(bx, by, bw, bh)));
        dc.DrawRectangle(_bAmber, null, new Rect(pxb - 1, by, 2, bh));
        dc.Pop();
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
