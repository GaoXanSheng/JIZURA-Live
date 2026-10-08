using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JizuraLive.Engine;
using Newtonsoft.Json.Linq;

namespace JizuraLive.App.Views;

// 播放条（对位 Transport.vue）：播放/暂停、时间码、自绘进度条 scrub、循环、声音组、历史、
// 重抽构成 / 主题 / 一键随机；时间跟随引擎。一整行 flex-wrap（见 FlexWrapRow），无外框卡片。
//
// M3 适配保留：进度条拖动走引擎 scrub（拖动中画面跟手、音频静默，松手恢复，对位 barDown/barUp）；
// 并负责把 State 的音频投进引擎（State.AudioChanged → Preview.SetAudioSource）——
// 本控件常驻中栏，是引擎音频源最稳的接线点；时长经引擎重规划自然变为音频时长。
//
// 声音组（对位 volDown/volMove/volUp/volKey/toggleMute）：音量条自绘拖动 → 引擎预览增益，
// 松手写回 Settings（旧版 localStorage 的 jizura.previewVolume 记忆）；消音只影响预览出声。
//
// 未接（WPF 侧没有对应状态，仅保留视觉）：
//   · 循环线性/切分两态——引擎只有 Loop 布尔（McpTools 的 preview 工具同样只认 all/off）。
public partial class Transport : UserControl
{
    private const double ThumbSize = 12; // 拖柄直径（对位 w-3）

    private bool _scrubbing;
    private bool _volDragging;
    private bool _wired;
    private bool _syncTheme;
    private double _frac;
    private Brush _fg = null!, _cyan = null!;
    private readonly DispatcherTimer _timer;

    public Transport()
    {
        InitializeComponent();
        _fg = (Brush)FindResource("Fg");
        _cyan = (Brush)FindResource("Cyan");
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Sync();
        _timer.Start();
        Loaded += (_, _) =>
        {
            var main = App.MainInstance;
            if (main != null && !_wired)
            {
                _wired = true;
                // EditorState.SetAudio 里的那次 Replan 发生在 AudioChanged 之前（拿不到音频），
                // 这里接通后引擎会再补一次带音频的重规划（SetAudioSource 内部处理）
                main.State.AudioChanged += WireAudioSource;
                main.State.StateChanged += SyncTheme; // 打开/初始化/一键随机后主题下拉跟上
                WireAudioSource();
                BuildThemeItems();
            }
            SetVolBar();
            SyncTheme();
            Sync();
            App.MainInstance!.Preview.Play();
        };
    }

    private PreviewEngine Preview => App.MainInstance!.Preview;

    /// <summary>音频投入/卸载（含 ResetAll 清空）：交给引擎换链，失败自动降级墙钟。</summary>
    private void WireAudioSource()
    {
        var st = App.MainInstance!.State;
        Preview.SetAudioSource(st.Audio, st.AudioPath);
    }

    // ---------------- 播放 / 循环 ----------------

    private void BtnPlay_Click(object sender, RoutedEventArgs e) => Preview.TogglePlay();

    private void BtnLoop_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        Preview.Loop = !Preview.Loop;
        Sync();
    }

    // ---------------- 进度条（自绘，对位 barDown/barMove/barUp/barSeek） ----------------

    private void ProgBar_Down(object sender, MouseButtonEventArgs e)
    {
        if (!IsLoaded) return;
        _scrubbing = true;
        Preview.BeginScrub(); // 拖动中音频静默跟随，松手恢复（对位 beginScrub）
        ProgBar.CaptureMouse();
        BarSeekTo(e.GetPosition(ProgBar).X);
        e.Handled = true;
    }

    private void ProgBar_Move(object sender, MouseEventArgs e)
    {
        if (_scrubbing) BarSeekTo(e.GetPosition(ProgBar).X);
    }

    private void ProgBar_Up(object sender, MouseButtonEventArgs e) => EndScrub();

    /// <summary>捕获丢失（窗口失活一类）也要收尾，否则 scrub 态永久钉住播放推进。</summary>
    private void ProgBar_LostCapture(object sender, MouseEventArgs e) => EndScrub();

    private void EndScrub()
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        Preview.EndScrub(); // 松手从当前位置恢复出声（对位 endScrub）
        ProgBar.ReleaseMouseCapture();
    }

    /// <summary>按下/拖动：按命中区比例 seek（对位 barSeek）。拖动期间 frame 刷新被挡住，必须这里立即跟手。</summary>
    private void BarSeekTo(double x)
    {
        var dur = Preview.Duration;
        var w = ProgBar.ActualWidth;
        if (w <= 0 || dur <= 0) return;
        var f = Math.Clamp(x / w, 0, 1);
        Preview.Seek(f * dur);
        SetBar(f);
        TblNow.Text = J.FmtTime(Preview.CurrentTime);
    }

    private void ProgBar_SizeChanged(object sender, SizeChangedEventArgs e) => SetBar(_frac);

    /// <summary>自绘进度条：填充宽按比例 + 拖柄平移（对位 setBar 的 scaleX / translateX）。</summary>
    private void SetBar(double f)
    {
        _frac = Math.Clamp(f, 0, 1);
        var w = ProgBar.ActualWidth;
        ProgFill.Width = Math.Max(0, w * _frac);
        ProgThumbShift.X = Math.Max(0, (w - ThumbSize) * _frac);
    }

    // ---------------- 声音组（音量 / 消音；对位 volDown/volMove/volUp/volKey/toggleMute） ----------------

    /// <summary>消音开关（对位 toggleMute）：只动引擎预览增益，设置落盘记忆。</summary>
    private void BtnMute_Click(object sender, RoutedEventArgs e)
    {
        var on = !Preview.Muted;
        Preview.SetVolume(null, on);
        SaveVolume();
        Sync();
    }

    private void VolBar_Down(object sender, MouseButtonEventArgs e)
    {
        if (!IsLoaded) return;
        _volDragging = true;
        if (Preview.Muted) Preview.SetVolume(null, false); // 拖动即解除消音（对位 volDown）
        VolBar.CaptureMouse();
        VolBar.Focus();
        VolApply(e.GetPosition(VolBar).X);
        e.Handled = true;
    }

    private void VolBar_Move(object sender, MouseEventArgs e)
    {
        if (_volDragging) VolApply(e.GetPosition(VolBar).X);
    }

    private void VolBar_Up(object sender, MouseButtonEventArgs e) => EndVolDrag();

    /// <summary>捕获丢失（窗口失活一类）也要收尾，否则拖动态钉住音量条不跟引擎。</summary>
    private void VolBar_LostCapture(object sender, MouseEventArgs e) => EndVolDrag();

    private void EndVolDrag()
    {
        if (!_volDragging) return;
        _volDragging = false;
        VolBar.ReleaseMouseCapture();
        SaveVolume(); // 旧版：拖动中以指针频率只改引擎音量，松手才落盘
    }

    /// <summary>键盘微调（对位 volKey）：←/↓ 减、→/↑ 加（5% 档），Home/End 两端；同时解除消音。</summary>
    private void VolBar_KeyDown(object sender, KeyEventArgs e)
    {
        var cur = Math.Round(Preview.Volume * 20) / 20;
        double? v = e.Key switch
        {
            Key.Right or Key.Up => Math.Min(1, cur + 0.05),
            Key.Left or Key.Down => Math.Max(0, cur - 0.05),
            Key.Home => 0,
            Key.End => 1,
            _ => null,
        };
        if (v == null) return;
        Preview.SetVolume(v, false);
        SaveVolume();
        SetVolBar();
        e.Handled = true;
    }

    private void VolApply(double x)
    {
        var w = VolBar.ActualWidth;
        if (w <= 0) return;
        Preview.SetVolume(Math.Clamp(x / w, 0, 1));
        SetVolBar();
    }

    /// <summary>预览音量/消音写回设置（对位 saveVolume：localStorage → Settings）。</summary>
    private void SaveVolume()
    {
        var s = App.SettingsOf();
        if (s == null) return;
        s.PreviewVolume = Preview.Volume;
        s.PreviewMuted = Preview.Muted;
        s.Save();
    }

    /// <summary>音量条同款自绘：填充宽按比例 + 拖柄平移（消音时条显示 0，免得按钮变青而条说谎）。</summary>
    private void SetVolBar()
    {
        var w = VolBar.Width;
        var frac = Math.Clamp(Preview.Muted ? 0 : Preview.Volume, 0, 1);
        VolFill.Width = Math.Max(0, w * frac);
        VolThumbShift.X = Math.Max(0, (w - ThumbSize) * frac);
    }

    // ---------------- 历史（外观栈，对位 histGo/histInfo） ----------------

    private void BtnHistBack_Click(object sender, RoutedEventArgs e)
    {
        var st = App.MainInstance?.State;
        if (st != null && st.UndoLook()) st.ReplanNow();
    }

    private void BtnHistFwd_Click(object sender, RoutedEventArgs e)
    {
        var st = App.MainInstance?.State;
        if (st != null && st.RedoLook()) st.ReplanNow();
    }

    // ---------------- 重抽构成 / 主题 / 一键随机 ----------------

    /// <summary>doShuffle：设置不动，只换 seed 后重规划（锁定的行由 planner 保留）。</summary>
    private void BtnShuffle_Click(object sender, RoutedEventArgs e)
    {
        var st = App.MainInstance?.State;
        if (st == null) return;
        st.Mutate(p => p.Seed = (uint)Random.Shared.Next(1_000_000_000), "重抽构成", replan: true, look: true);
    }

    private void BuildThemeItems()
    {
        _syncTheme = true;
        SelTheme.Items.Clear();
        SelTheme.Items.Add(new ComboBoxItem { Content = "无主题", Tag = "" });
        foreach (var k in Omakase.ThemeOrder)
            if (Omakase.Themes.TryGetValue(k, out var t))
                SelTheme.Items.Add(new ComboBoxItem { Content = t.Name, Tag = k });
        _syncTheme = false;
    }

    /// <summary>下拉选值跟工程走（打开/初始化/一键随机后同步），对位 sync-ui 里的 theme 回填。</summary>
    private void SyncTheme()
    {
        if (SelTheme.Items.Count == 0) return;
        var key = App.MainInstance?.State.Project.ThemeId;
        if (key != null && !Omakase.Themes.ContainsKey(key)) key = null;
        var want = SelTheme.Items.OfType<ComboBoxItem>().FirstOrDefault(it => (string?)it.Tag == (key ?? ""));
        if (want == null || ReferenceEquals(want, SelTheme.SelectedItem)) return;
        _syncTheme = true;
        SelTheme.SelectedItem = want;
        _syncTheme = false;
    }

    private void SelTheme_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncTheme || !IsLoaded) return;
        var st = App.MainInstance?.State;
        if (st == null) return;
        var key = (SelTheme.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
        if (key == (st.Project.ThemeId ?? "")) return;
        // setTheme：只写 themeId + 存盘（只有一键随机读它，不动画面）
        st.Mutate(p => p.ThemeId = key.Length == 0 ? null : key, "一键随机主题", replan: false, look: true);
    }

    /// <summary>omakase：风格/氛围/演出/配色整体随机（锁定项由 OmakaseApply 还原），回到片头。</summary>
    private void BtnOmakase_Click(object sender, RoutedEventArgs e)
    {
        var main = App.MainInstance;
        if (main == null) return;
        var st = main.State;
        var themeId = st.Project.ThemeId;
        if (themeId != null && !Omakase.Themes.ContainsKey(themeId)) themeId = null;
        st.Mutate(p => Omakase.OmakaseApply(p, null, themeId), "一键随机", replan: false, look: true);
        st.ReplanNow();
        main.Preview.Seek(0); // 对位 restartPreview
    }

    // ---------------- 低频状态同步（10Hz 定时器） ----------------

    private void Sync()
    {
        var main = App.MainInstance;
        if (main == null) return;
        var preview = main.Preview;
        var dur = preview.Duration;
        if (!_scrubbing) // 拖动期间不跟引擎（对位 scrubbing 挡 frame 刷新）
        {
            SetBar(dur > 0 ? Math.Clamp(preview.CurrentTime / dur, 0, 1) : 0);
            TblNow.Text = J.FmtTime(preview.CurrentTime);
        }
        TblDur.Text = J.FmtTime(dur);
        BtnPlay.Content = preview.IsPlaying ? "❚❚" : "▶";
        // 循环：WPF 只有 全曲/关 两态（对位 loopMode 的 all/off）；关循环整体透明度 0.55
        var loop = preview.Loop;
        BtnLoop.Opacity = loop ? 1 : 0.55;
        BtnLoop.ToolTip = loop ? "循环整首" : "不循环";
        var st = main.State;
        BtnHistBack.IsEnabled = st.CanUndoLook;
        BtnHistFwd.IsEnabled = st.CanRedoLook;
        BtnFx.Foreground = FxActive(st) ? _cyan : _fg; // 音效链有开启单元 → 青字（对位 fxOn）
        // 声音组：消音态按钮点亮（对位 muted → 青字），音量条跟引擎档位（拖动中不抢指针）
        BtnMute.Foreground = preview.Muted ? _cyan : _fg;
        if (!_volDragging) SetVolBar();
    }

    /// <summary>打开右栏「特效」页的音效区（对位旧版 Transport 的音效按钮 → 音频特效弹窗）。</summary>
    private void BtnFx_Click(object sender, RoutedEventArgs e) =>
        (Window.GetWindow(this) as MainWindow)?.OpenAudioFx();

    /// <summary>fxActive：音频特效链上有开启的单元（speed 变速不算特效）。</summary>
    private static bool FxActive(EditorState st) =>
        st.Project.AudioFx?["units"] is JArray arr
        && arr.OfType<JObject>().Any(u => u.Value<bool?>("on") == true);
}

/// <summary>
/// flex-wrap 行（对位旧 CSS 的 `flex items-center gap-N flex-wrap`）：
/// 子元素按自然宽从左到右排，放不下折到下一行（行间距同 gap）；标了 Grow&gt;0 的元素在自己那一行里
/// 分掉剩余宽度（对位 `flex-[1_1_160px]`：flex-basis 取元素的 MinWidth）；行内元素垂直居中。
/// </summary>
public sealed class FlexWrapRow : Panel
{
    public static readonly DependencyProperty GrowProperty = DependencyProperty.RegisterAttached(
        "Grow", typeof(double), typeof(FlexWrapRow),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsParentMeasure));

    public static void SetGrow(DependencyObject d, double value) => d.SetValue(GrowProperty, value);
    public static double GetGrow(DependencyObject d) => (double)d.GetValue(GrowProperty);

    /// <summary>元素间距（对位旧 CSS 的 gap：Transport = gap-2.5 = 10，CutInfo = gap-1.5 = 6）。</summary>
    public double Gap { get; set; } = 10;

    private double[] _basis = Array.Empty<double>();  // 自然宽（grow 元素取 basis = MinWidth）
    private double[] _width = Array.Empty<double>();  // 最终宽（含行内剩余分配）
    private double[] _height = Array.Empty<double>();
    private double[] _x = Array.Empty<double>();
    private double[] _y = Array.Empty<double>();
    private readonly List<int> _order = new();               // 参与排布的可见子元素
    private readonly List<(int Start, int End)> _lines = new(); // _order 上的行区间

    protected override Size MeasureOverride(Size availableSize)
    {
        var n = Children.Count;
        Ensure(n);
        for (var i = 0; i < n; i++)
        {
            Children[i].Measure(new Size(double.PositiveInfinity, double.PositiveInfinity)); // 自然尺寸
            var fe = Children[i] as FrameworkElement;
            // flex-basis：grow 元素按 MinWidth 折行（对位 flex-[1_1_160px] 的 160px；不能取自然宽——
            // 进度条的填充是动态宽的，会把 basis 顶大导致折行抖动），其余按自然宽
            _basis[i] = fe != null && GetGrow(fe) > 0
                ? (fe.MinWidth > 0 ? fe.MinWidth : Children[i].DesiredSize.Width)
                : Children[i].DesiredSize.Width;
            _height[i] = Children[i].DesiredSize.Height;
        }
        var avail = availableSize.Width;
        if (double.IsInfinity(avail)) avail = NaturalWidth();
        Wrap(avail);
        // 分了剩余宽的 grow 元素按最终宽重测：内部布局得按最终宽算（高度若有变，跟随更新）
        foreach (var i in _order)
            if (Children[i] is FrameworkElement fe2 && GetGrow(fe2) > 0)
            {
                Children[i].Measure(new Size(_width[i], double.PositiveInfinity));
                _height[i] = Children[i].DesiredSize.Height;
            }
        return new Size(avail, Stack());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0) return finalSize;
        Wrap(finalSize.Width);
        var h = Stack();
        var dy = Math.Max(0, (finalSize.Height - h) / 2); // MinHeight 撑高时整块垂直居中
        foreach (var i in _order)
            Children[i].Arrange(new Rect(_x[i], _y[i] + dy, _width[i], _height[i]));
        return finalSize;
    }

    private void Ensure(int n)
    {
        if (_basis.Length != n)
        {
            _basis = new double[n];
            _width = new double[n];
            _height = new double[n];
            _x = new double[n];
            _y = new double[n];
        }
        _order.Clear();
        for (var i = 0; i < n; i++)
            if (Children[i].Visibility == Visibility.Visible) _order.Add(i);
    }

    private double NaturalWidth()
    {
        var w = 0.0;
        for (var j = 0; j < _order.Count; j++) w += (j > 0 ? Gap : 0) + _basis[_order[j]];
        return w;
    }

    /// <summary>折行（按 basis，同 flex-wrap）→ 每行剩余宽分给 grow 元素 → 写入最终宽与 x。</summary>
    private void Wrap(double avail)
    {
        for (var i = 0; i < _width.Length; i++) { _width[i] = 0; _x[i] = 0; _y[i] = 0; }
        _lines.Clear();
        var used = 0.0;
        var start = 0;
        for (var j = 0; j < _order.Count; j++)
        {
            var gap = j > start ? Gap : 0;
            var w = _basis[_order[j]];
            if (j > start && used + gap + w > avail + 0.01) // 放不下且不是行首 → 折行
            {
                _lines.Add((start, j));
                start = j;
                used = 0;
                gap = 0;
            }
            used += gap + w;
        }
        if (_order.Count > 0) _lines.Add((start, _order.Count));

        foreach (var (s, e) in _lines)
        {
            var fixedW = (e - s - 1) * Gap;
            var growSum = 0.0;
            for (var j = s; j < e; j++)
            {
                fixedW += _basis[_order[j]];
                growSum += GrowOf(_order[j]);
            }
            var free = double.IsInfinity(avail) ? 0 : avail - fixedW;
            var x = 0.0;
            for (var j = s; j < e; j++)
            {
                var i = _order[j];
                var g = GrowOf(i);
                var w = _basis[i] + (g > 0 && growSum > 0 && free > 0 ? free * g / growSum : 0);
                _width[i] = w;
                _x[i] = x;
                x += w + Gap;
            }
        }
    }

    /// <summary>行高与行内垂直居中；返回总高（行间距 = Gap）。</summary>
    private double Stack()
    {
        var y = 0.0;
        for (var li = 0; li < _lines.Count; li++)
        {
            var (s, e) = _lines[li];
            var lineH = 0.0;
            for (var j = s; j < e; j++) lineH = Math.Max(lineH, _height[_order[j]]);
            for (var j = s; j < e; j++)
            {
                var i = _order[j];
                _y[i] = y + Math.Max(0, (lineH - _height[i]) / 2);
            }
            y += lineH + (li < _lines.Count - 1 ? Gap : 0);
        }
        return y;
    }

    private double GrowOf(int i) => Children[i] is FrameworkElement fe ? GetGrow(fe) : 0;
}
