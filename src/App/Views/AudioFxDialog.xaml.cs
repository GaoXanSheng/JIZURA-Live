using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using JizuraLive.App.I18n;
using JizuraLive.Engine;
using Newtonsoft.Json.Linq;

namespace JizuraLive.App.Views;

/// <summary>
/// 音频效果弹窗（对位 Dialogs.vue 的「音频特效」弹窗，逐块对位）：
/// 预设 chips（一键写整条链）+ 变速滑块（改歌曲时长，歌词/节拍按原时刻跟随）+
/// 效果链 rack（开关 / 展开参数 / ↑↓ 调序 / ✕ 移除；单位与参数表来自引擎 AudioFx.Units）+
/// EQ 七段曲线画布（拖点 ±12dB）+ 添加效果（未入链的单元）+ 注记。
///
/// 全部改动经 EditorState.Mutate 写 project.audioFx（replan:false，不动画面编排）；
/// 预览链由引擎在 Tick/Play 时按 AudioFx 配置差异重建（PreviewEngine.RebuildFxIfConfigChanged），
/// 所以拖动参数/换预设立即听得到；导出侧同一条 AudioFx.Build（逐样本一致）。
/// 入口只有播放条声音组的「音频效果」按钮（不再落在右栏「特效」面板里）。
///
/// 提交节奏（对位 Vue 的 @input/@change 分工）：拖动滑块只刷显示，松手立即提交；
/// 点击/键盘等非拖动改动走 120ms 防抖；EQ 曲线拖动中 120ms 防抖、松手落定。
/// </summary>
public partial class AudioFxDialog : Window
{
    private const double EqH = 120;   // EQ 画布高（对位 canvas height=120）
    private const double EqW = 452;   // EQ 画布宽（对位 512，本弹窗内容宽 560−48 后取整）
    private const double EqL = 30;    // 曲线左侧留白（对位 eqX 的 30）

    private static readonly string[] EqLabels = { "60", "150", "400", "1k", "2.4k", "6k", "15k" };

    private EditorState S => App.MainInstance!.State;
    private PreviewEngine Preview => App.MainInstance!.Preview;

    private readonly DispatcherTimer _debounce; // 非拖动改动的提交防抖
    private bool _ready;                        // OnLoaded 之后才允许提交（构造期 XAML 赋值不提交）
    private bool _syncing;                      // 程序回填时不提交
    private bool _dragging;                     // 任一滑块拖动中（拖动只刷显示，松手才提交）
    private string _pendingLabel = "";
    private Func<JObject, JObject>? _pendingEdit;

    private string _shape = "";    // 链形状签名（id 序列）：变了才整段重建
    private int _openUnit = -1;    // 展开的单元下标（对位 openUnit）

    private sealed class UnitRow
    {
        public int Index;
        public string Id = "";
        public CheckBox On = null!;
        public Button Head = null!;
        public TextBlock Summary = null!;
        public StackPanel Detail = null!;
        public readonly List<Action> RefreshValues = new(); // 展开区回填（参数滑块 / EQ 曲线）
    }

    private readonly List<UnitRow> _rows = new();

    public AudioFxDialog()
    {
        InitializeComponent();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Flush();
        };
        BuildPresets();
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 遮罩铺满 owner（对位 fixed inset-0）
        if (Owner != null)
        {
            Left = Owner.Left;
            Top = Owner.Top;
            Width = Owner.ActualWidth;
            Height = Owner.ActualHeight;
            Card.MaxHeight = Math.Max(200, Owner.ActualHeight - 48);
        }
        SlSpeed.TickFrequency = 0.05;
        SlSpeed.IsSnapToTickEnabled = true; // 档位 0.05（对位 step=0.05）
        AttachDrag(SlSpeed, () => SubmitNow(SpeedLabel(SlSpeed.Value), SpeedEdit(SlSpeed.Value)));
        S.StateChanged += OnState;
        L.LanguageChanged += OnLang;
        _ready = true;
        Refresh();
        // 打开动效：遮罩淡入 + 卡片微缩放上浮（只做进入，关闭即时）
        JzAnim.PlayWindowIn(Overlay, Card);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Flush(); // 关窗把挂起的提交落掉，别丢最后一次拖动
        _ready = false;
        S.StateChanged -= OnState;
        L.LanguageChanged -= OnLang;
    }

    private void OnState() => Dispatcher.Invoke(Refresh);

    /// <summary>语言切换：预设 chips 与添加下拉重读文案（动态文案都在 code-behind），再整段回填。</summary>
    private void OnLang() => Dispatcher.Invoke(() =>
    {
        BuildPresets();
        CbAdd.Tag = null; // 强制重建添加下拉项（占位文案）
        Refresh();
    });

    // ---------------- 关闭 ----------------

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>点遮罩空白处关闭（点卡片内部由 Card_MouseDown 吃掉，不会冒到这里）。</summary>
    private void Overlay_MouseDown(object sender, MouseButtonEventArgs e) => Close();

    private void Card_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        Close();
        e.Handled = true;
    }

    // ---------------- 预设（对位 usePreset） ----------------

    private void BuildPresets()
    {
        PresetHost.Children.Clear();
        foreach (var preset in AudioFx.Presets)
        {
            var b = new Button
            {
                Style = (Style)FindResource("BtnGhostSm"),
                Content = preset.Label,
                Margin = new Thickness(0, 0, 4, 4),
            };
            var p = preset;
            b.Click += (_, _) =>
            {
                _openUnit = -1;
                var cfg = AudioFx.SanitizeAudioFx((JObject)p.Cfg.DeepClone());
                S.Mutate(pp => pp.AudioFx = cfg, L.F("AudioFx.Mutate.Preset", p.Label), replan: false);
            };
            ToolTipService.SetToolTip(b, L.F("AudioFx.Preset.TipFmt", p.Label));
            PresetHost.Children.Add(b);
        }
    }

    // ---------------- 回填 / 重建 ----------------

    private void Refresh()
    {
        if (App.MainInstance == null) return;
        _syncing = true;
        try
        {
            var cfg = AudioFx.SanitizeAudioFx(S.Project.AudioFx);
            var units = (JArray)cfg["units"]!;
            var shape = string.Join(",", units.OfType<JObject>().Select(u => u["id"]?.ToString() ?? ""));
            if (shape != _shape)
            {
                _shape = shape;
                BuildRack(units);
            }
            else
            {
                UpdateRackValues(units);
            }
            TblEmpty.Visibility = units.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RefreshAddCombo(units);
            RefreshSpeed(cfg);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RefreshSpeed(JObject cfg)
    {
        var audio = Preview.Audio;
        SpeedRow.Visibility = audio != null ? Visibility.Visible : Visibility.Collapsed;
        if (audio == null) return;
        var speed = AudioFx.FxSpeedOf(cfg);
        if (Math.Abs(SlSpeed.Value - speed) > 1e-6) SlSpeed.Value = speed;
        TblSpeedVal.Text = SpeedLabel(speed);
        TblSpeedDur.Text = DurationText(audio.Duration / Math.Max(0.01, speed));
    }

    /// <summary>变速后的歌曲时长文案（对位 fmtDur：≥60 秒写「x分y秒」）。</summary>
    private static string DurationText(double secs) => secs >= 60
        ? L.F("AudioFx.Dur.MinSecFmt", (int)(secs / 60), Math.Round(secs % 60))
        : L.F("AudioFx.Dur.SecFmt", secs.ToString("F1"));

    private void BuildRack(JArray units)
    {
        RackHost.Children.Clear();
        _rows.Clear();
        foreach (var u in units.OfType<JObject>()) RackHost.Children.Add(BuildRow(_rows.Count, u));
        if (_openUnit >= _rows.Count) _openUnit = -1;
        if (_openUnit >= 0) BuildDetail(_rows[_openUnit]);
    }

    /// <summary>单行（对位 Vue 的 rack 行）：开关 + 名称（▸/▾）+ 参数摘要 + ↑ ↓ ✕。</summary>
    private UIElement BuildRow(int index, JObject unit)
    {
        var id = unit["id"]?.ToString() ?? "";
        if (!AudioFx.Units.TryGetValue(id, out var def)) return new Border();
        var row = new UnitRow { Index = index, Id = id };

        var head = new Grid { Margin = new Thickness(8, 4, 8, 4) };
        for (var c = 0; c < 6; c++)
            head.ColumnDefinitions.Add(new ColumnDefinition
            {
                Width = c == 2 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto,
            });

        var on = new CheckBox
        {
            IsChecked = unit["on"]?.Type != JTokenType.Boolean || (bool)unit["on"]!,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = def.Note,
        };
        on.Checked += (_, _) => { if (!_syncing) ToggleUnit(row, true); };
        on.Unchecked += (_, _) => { if (!_syncing) ToggleUnit(row, false); };
        row.On = on;
        head.Children.Add(on);

        var headBtn = new Button
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = (Brush)FindResource("Fg"),
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(6, 0, 6, 0),
            Height = 20,
            Cursor = Cursors.Hand,
            Focusable = false,
            VerticalAlignment = VerticalAlignment.Center,
            Content = def.Label + " ▸",
        };
        headBtn.Click += (_, _) => ToggleOpen(row);
        row.Head = headBtn;
        Grid.SetColumn(headBtn, 1);
        head.Children.Add(headBtn);

        var summary = new TextBlock
        {
            FontSize = 11,
            Foreground = (Brush)FindResource("Mut"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
        };
        row.Summary = summary;
        Grid.SetColumn(summary, 2);
        head.Children.Add(summary);

        var up = MakeSmall("↑", "AudioFx.Rack.Up");
        up.IsEnabled = index > 0;
        up.Click += (_, _) => MoveUnit(row, -1);
        Grid.SetColumn(up, 3);
        head.Children.Add(up);

        var down = MakeSmall("↓", "AudioFx.Rack.Down");
        down.IsEnabled = index < _rows.Count; // 建最后一行时 _rows.Count == index，故末尾行为 false
        down.Click += (_, _) => MoveUnit(row, 1);
        Grid.SetColumn(down, 4);
        head.Children.Add(down);

        var del = MakeSmall("✕", "AudioFx.Rack.Remove");
        del.Click += (_, _) => RemoveUnit(row);
        Grid.SetColumn(del, 5);
        head.Children.Add(del);

        row.Detail = new StackPanel { Margin = new Thickness(12, 0, 10, 8) };
        row.Summary.Text = Summary(def, unit);
        _rows.Add(row);

        return new Border
        {
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 0, 0, 4),
            Child = new StackPanel { Children = { head, row.Detail } },
        };
    }

    private Button MakeSmall(string glyph, string tipKey)
    {
        var b = new Button
        {
            Style = (Style)FindResource("BtnGhostSm"),
            Content = glyph,
            Padding = new Thickness(6, 0, 6, 0),
            MinWidth = 26,
            Margin = new Thickness(2, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(b, L.T(tipKey));
        return b;
    }

    /// <summary>展开/收起（对位 openUnit：同一下标再点收起）。</summary>
    private void ToggleOpen(UnitRow row)
    {
        _openUnit = _openUnit == row.Index ? -1 : row.Index;
        foreach (var r in _rows)
            if (AudioFx.Units.TryGetValue(r.Id, out var d))
                r.Head.Content = d.Label + (r.Index == _openUnit ? " ▾" : " ▸");
        if (_openUnit >= 0) BuildDetail(_rows[_openUnit]);
    }

    /// <summary>展开区（对位 Vue 的 v-if="openUnit === i" 块）：note + 参数行 / EQ 曲线，每次展开重建。</summary>
    private void BuildDetail(UnitRow row)
    {
        row.Detail.Children.Clear();
        row.RefreshValues.Clear();
        if (!AudioFx.Units.TryGetValue(row.Id, out var def)) return;
        var units = (JArray)AudioFx.SanitizeAudioFx(S.Project.AudioFx)["units"]!;
        if (row.Index < 0 || row.Index >= units.Count) return;
        var pr = (JObject)(((JObject)units[row.Index]!)["params"] ?? new JObject())!;

        row.Detail.Children.Add(new TextBlock
        {
            Text = def.Note,
            FontSize = 10,
            Foreground = (Brush)FindResource("Mut"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 4),
        });
        foreach (var ps in def.Params)
        {
            if (ps.Type == "eq") row.Detail.Children.Add(BuildEq(row, pr));
            else row.Detail.Children.Add(BuildParamRow(row, ps, pr));
        }
    }

    /// <summary>数值参数行：标签 56 | 滑块 | 值 64（对位 `w-14` / `flex-1` / `w-16`）。</summary>
    private UIElement BuildParamRow(UnitRow row, FxParamDef ps, JObject pr)
    {
        var g = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

        g.Children.Add(new TextBlock
        {
            Text = ps.Label ?? ps.Key,
            FontSize = 11,
            Foreground = (Brush)FindResource("Mut"),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var valueText = new TextBlock
        {
            Style = (Style)FindResource("MonoMut"),
            FontSize = 11,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var sl = new Slider
        {
            Style = (Style)FindResource("FxSlider"),
            Minimum = ps.Min,
            Maximum = ps.Max,
            SmallChange = ps.Step,
            LargeChange = ps.Step * 10,
            Value = ParamValue(pr, ps),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (ps.Type == "int")
        {
            sl.TickFrequency = Math.Max(1, ps.Step);
            sl.IsSnapToTickEnabled = true;
        }
        void Show() => valueText.Text = Round2(sl.Value) + (ps.Unit ?? "");
        Show();
        string Label() => L.F("AudioFx.Mutate.Param", ps.Label ?? ps.Key);
        JToken Value() => ps.Type == "int" ? new JValue((int)Math.Round(sl.Value)) : new JValue(sl.Value);

        sl.ValueChanged += (_, _) =>
        {
            Show();
            if (_syncing || _dragging || !_ready) return; // 拖动中只刷显示（松手统一提交）
            SubmitDebounced(Label(), cur => SetParamOn(cur, row.Index, ps.Key, Value()));
        };
        AttachDrag(sl, () =>
        {
            if (_ready) SubmitNow(Label(), cur => SetParamOn(cur, row.Index, ps.Key, Value()));
        });

        Grid.SetColumn(sl, 1);
        g.Children.Add(sl);
        Grid.SetColumn(valueText, 2);
        g.Children.Add(valueText);

        row.RefreshValues.Add(() =>
        {
            var cfg = AudioFx.SanitizeAudioFx(S.Project.AudioFx);
            if (cfg["units"] is not JArray us || row.Index >= us.Count) return;
            var pr2 = ((JObject)us[row.Index]!)["params"] as JObject;
            if (pr2 == null) return;
            var v = ParamValueOrNull(pr2, ps);
            if (v != null && Math.Abs(sl.Value - v.Value) > 1e-9) sl.Value = v.Value;
            Show();
        });
        return g;
    }

    /// <summary>给滑块的 Thumb 挂拖动事件（WPF 的 Slider 不公开 IsDragging，只能听 Thumb 的路由事件）。</summary>
    private void AttachDrag(Slider sl, Action onRelease)
    {
        sl.Loaded += (_, _) =>
        {
            if (sl.Template.FindName("PART_Track", sl) is not Track track) return;
            track.Thumb.DragStarted += (_, _) => _dragging = true;
            track.Thumb.DragCompleted += (_, _) =>
            {
                _dragging = false;
                onRelease();
            };
        };
    }

    private static double EqX(int i) => EqL + i * (EqW - EqL - 12) / (AudioFx.EqBands.Length - 1);

    private static double EqY(double db) => EqH / 2 - (Math.Clamp(db, -12, 12) / 12) * (EqH / 2 - 8);

    private static int NearestBand(double x)
    {
        var best = 0;
        var bd = double.MaxValue;
        for (var i = 0; i < AudioFx.EqBands.Length; i++)
        {
            var d = Math.Abs(EqX(i) - x);
            if (d >= bd) continue;
            bd = d;
            best = i;
        }
        return best;
    }

    /// <summary>EQ 七段曲线（对位 eqCanvas）：折线 + 拉点，拖点改 ±12dB；拖动中 120ms 防抖、松手落定。</summary>
    private UIElement BuildEq(UnitRow row, JObject pr)
    {
        var gains = new double[AudioFx.EqBands.Length];
        var src = pr["gains"] as JArray;
        for (var i = 0; i < gains.Length; i++)
            if (src != null && i < src.Count) gains[i] = JNum(src[i]);

        var canvas = new Canvas
        {
            Width = EqW,
            Height = EqH,
            Background = (Brush)FindResource("Ink"),
            Cursor = Cursors.Cross,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            SnapsToDevicePixels = true,
        };
        canvas.Children.Add(new Line
        {
            X1 = 0,
            X2 = EqW,
            Y1 = EqY(0),
            Y2 = EqY(0),
            Stroke = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255)), // 对位 rgba(255,255,255,.18)
            StrokeThickness = 1,
        });
        var curve = new Polyline
        {
            Stroke = (Brush)FindResource("Amber"),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        };
        canvas.Children.Add(curve);
        var dots = new List<Ellipse>();
        for (var i = 0; i < gains.Length; i++)
        {
            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = (Brush)FindResource("Amber"),
                IsHitTestVisible = false,
            };
            dots.Add(dot);
            canvas.Children.Add(dot);
        }
        foreach (var (text, top) in new[] { ("+12", EqY(12) - 6), ("0", EqY(0) - 6), ("-12", EqY(-12) + 10) })
        {
            var cap = new TextBlock
            {
                Text = text,
                Style = (Style)FindResource("MonoMut"),
                FontSize = 9,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(cap, 2);
            Canvas.SetTop(cap, top);
            canvas.Children.Add(cap);
        }

        void Redraw()
        {
            curve.Points = new PointCollection(gains.Select((db, i) => new Point(EqX(i), EqY(db))));
            for (var i = 0; i < dots.Count; i++)
            {
                Canvas.SetLeft(dots[i], EqX(i) - 4);
                Canvas.SetTop(dots[i], EqY(gains[i]) - 4);
            }
        }
        Redraw();

        var drag = -1;
        void ApplyEq(double y)
        {
            if (drag < 0) return;
            var db = Math.Clamp((EqH / 2 - y) / (EqH / 2 - 8) * 12, -12, 12);        // 对位 eqApply 的映射
            gains[drag] = Math.Round(db * 10) / 10;                                  // 对位 +db.toFixed(1)
            Redraw();
            SubmitDebounced(L.T("AudioFx.Mutate.Eq"),
                cur => SetParamOn(cur, row.Index, "gains", new JArray(gains)));
        }
        canvas.MouseLeftButtonDown += (_, e) =>
        {
            canvas.CaptureMouse();
            drag = NearestBand(e.GetPosition(canvas).X);
            ApplyEq(e.GetPosition(canvas).Y);
            e.Handled = true;
        };
        canvas.MouseMove += (_, e) =>
        {
            if (drag >= 0) ApplyEq(e.GetPosition(canvas).Y);
        };
        canvas.MouseLeftButtonUp += (_, _) =>
        {
            if (drag < 0) return;
            drag = -1;
            canvas.ReleaseMouseCapture();
            SubmitNow(L.T("AudioFx.Mutate.Eq"), cur => SetParamOn(cur, row.Index, "gains", new JArray(gains)));
        };
        canvas.LostMouseCapture += (_, _) => drag = -1;

        var labels = new Grid { Width = EqW, Margin = new Thickness(0, -2, 0, 0) };
        for (var i = 0; i < EqLabels.Length; i++)
        {
            labels.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var t = new TextBlock
            {
                Text = EqLabels[i],
                Style = (Style)FindResource("MonoMut"),
                FontSize = 9,
                HorizontalAlignment = i == 0 ? HorizontalAlignment.Left
                    : i == EqLabels.Length - 1 ? HorizontalAlignment.Right
                    : HorizontalAlignment.Center,
            };
            Grid.SetColumn(t, i);
            labels.Children.Add(t);
        }

        var wrap = new StackPanel { Margin = new Thickness(0, 4, 0, 0) };
        wrap.Children.Add(canvas);
        wrap.Children.Add(labels);

        row.RefreshValues.Add(() =>
        {
            var cfg = AudioFx.SanitizeAudioFx(S.Project.AudioFx);
            if (cfg["units"] is not JArray us || row.Index >= us.Count) return;
            var g2 = ((JObject)us[row.Index]!)["params"]?["gains"] as JArray;
            if (g2 == null || g2.Count != gains.Length) return;
            for (var i = 0; i < gains.Length; i++) gains[i] = JNum(g2[i]);
            Redraw();
        });
        return wrap;
    }

    /// <summary>回填不回建：开关态、名称箭头、参数摘要、展开区控件。</summary>
    private void UpdateRackValues(JArray units)
    {
        for (var i = 0; i < _rows.Count && i < units.Count; i++)
        {
            var row = _rows[i];
            var u = (JObject)units[i]!;
            var on = u["on"]?.Type != JTokenType.Boolean || (bool)u["on"]!;
            if (row.On.IsChecked != on) row.On.IsChecked = on;
            if (AudioFx.Units.TryGetValue(row.Id, out var def))
            {
                row.Head.Content = def.Label + (i == _openUnit ? " ▾" : " ▸");
                row.Summary.Text = Summary(def, u);
            }
            foreach (var f in row.RefreshValues) f();
        }
    }

    /// <summary>参数摘要（对位 paramSummary）：`标签 值单位` 用「・」连；EQ 写 平直 / 自定义曲线。</summary>
    private static string Summary(FxUnitDef def, JObject unit)
    {
        var pr = (JObject)(unit["params"] ?? new JObject())!;
        var parts = new List<string>();
        foreach (var ps in def.Params)
        {
            if (ps.Type == "eq")
            {
                var any = (pr["gains"] as JArray)?.Any(v => Math.Abs(JNum(v)) > 0.05) == true;
                parts.Add(L.T(any ? "AudioFx.Eq.Custom" : "AudioFx.Eq.Flat"));
            }
            else
            {
                parts.Add($"{ps.Label} {Round2(ParamValue(pr, ps))}{ps.Unit}");
            }
        }
        return string.Join("・", parts);
    }

    private static double ParamValue(JObject pr, FxParamDef ps) => ParamValueOrNull(pr, ps) ?? ps.Def;

    private static double? ParamValueOrNull(JObject pr, FxParamDef ps)
        => pr[ps.Key] is JValue { Type: JTokenType.Float or JTokenType.Integer } v ? Convert.ToDouble(v.Value) : null;

    /// <summary>JSON 数值取 double（非数值回 0）——JToken.Value 是泛型方法，不能直接喂给 Convert。</summary>
    private static double JNum(JToken? tok)
        => tok is JValue { Type: JTokenType.Float or JTokenType.Integer } v ? Convert.ToDouble(v.Value) : 0;

    private static string Round2(double v)
        => Math.Abs(v) >= 100 ? Math.Round(v).ToString("0") : Math.Round(v, 2).ToString("0.##");

    // ---------------- 添加效果（对位 addable / addUnit） ----------------

    private void RefreshAddCombo(JArray units)
    {
        var inChain = units.OfType<JObject>().Select(u => u["id"]?.ToString() ?? "").ToHashSet();
        var addable = AudioFx.Units.Values.Where(d => !inChain.Contains(d.Id)).ToList();
        var sig = string.Join(",", addable.Select(d => d.Id));
        if (CbAdd.Tag as string != sig)
        {
            CbAdd.Tag = sig;
            CbAdd.Items.Clear();
            CbAdd.Items.Add(new ComboBoxItem { Content = L.T("AudioFx.AddPlaceholder"), Tag = "" });
            foreach (var d in addable) CbAdd.Items.Add(new ComboBoxItem { Content = d.Label, Tag = d.Id });
            CbAdd.SelectedIndex = 0;
        }
        BtnAdd.IsEnabled = SelectedAddId().Length > 0;
    }

    private string SelectedAddId() => (CbAdd.SelectedItem as ComboBoxItem)?.Tag as string ?? "";

    private void CbAdd_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        BtnAdd.IsEnabled = SelectedAddId().Length > 0;
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var id = SelectedAddId();
        if (id.Length == 0 || !AudioFx.Units.TryGetValue(id, out var def)) return;
        var label = def.Label;
        var defaults = def.Defaults.DeepClone();
        EditUnits(L.F("AudioFx.Mutate.Add", label), units =>
        {
            units.Add(new JObject { ["id"] = id, ["on"] = true, ["params"] = defaults });
            return units;
        });
        _openUnit = _rows.Count; // 新单元落在末尾并自动展开（对位 addUnit 的 openUnit = length-1）
    }

    // ---------------- 链操作（对位 addUnit/rmUnit/moveUnit/toggleUnit） ----------------

    private void ToggleUnit(UnitRow row, bool on)
    {
        var label = LabelOf(row);
        EditUnits(L.F(on ? "AudioFx.Mutate.UnitOn" : "AudioFx.Mutate.UnitOff", label), units =>
        {
            if (row.Index >= 0 && row.Index < units.Count && units[row.Index] is JObject u) u["on"] = on;
            return units;
        });
    }

    private void MoveUnit(UnitRow row, int delta)
    {
        var i = row.Index;
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _rows.Count) return;
        EditUnits(L.T("AudioFx.Mutate.Move"), units =>
        {
            if (i >= units.Count || j >= units.Count) return units;
            (units[i], units[j]) = (units[j], units[i]);
            return units;
        });
        _openUnit = -1;
    }

    private void RemoveUnit(UnitRow row)
    {
        var label = LabelOf(row);
        var i = row.Index;
        EditUnits(L.F("AudioFx.Mutate.Remove", label), units =>
        {
            if (i >= 0 && i < units.Count) units.RemoveAt(i);
            return units;
        });
        _openUnit = -1;
    }

    private string LabelOf(UnitRow row) => AudioFx.Units.TryGetValue(row.Id, out var d) ? d.Label : "";

    // ---------------- 提交（防抖 / 立即） ----------------

    private void EditUnits(string label, Func<JArray, JArray> edit)
        => SubmitNow(label, cur =>
        {
            cur["units"] = edit((JArray)cur["units"]!);
            return cur;
        });

    /// <summary>立即提交（结构操作与松手落定走这条）；挂起的防抖先作废，避免旧值后到。</summary>
    private void SubmitNow(string label, Func<JObject, JObject> edit)
    {
        _debounce.Stop();
        _pendingEdit = null;
        var next = AudioFx.SanitizeAudioFx(edit(AudioFx.SanitizeAudioFx(S.Project.AudioFx)));
        S.Mutate(p => p.AudioFx = next, label, replan: false);
    }

    /// <summary>防抖提交（点击/键盘/EQ 拖动中的连续改动合成一次写）。</summary>
    private void SubmitDebounced(string label, Func<JObject, JObject> edit)
    {
        _pendingLabel = label;
        _pendingEdit = edit;
        _debounce.Stop();
        _debounce.Start();
    }

    private void Flush()
    {
        var edit = _pendingEdit;
        _pendingEdit = null;
        if (edit == null) return;
        var next = AudioFx.SanitizeAudioFx(edit(AudioFx.SanitizeAudioFx(S.Project.AudioFx)));
        S.Mutate(p => p.AudioFx = next, _pendingLabel, replan: false);
    }

    private static JObject SetParamOn(JObject cfg, int index, string key, JToken value)
    {
        if (cfg["units"] is not JArray units || index < 0 || index >= units.Count) return cfg;
        if (units[index] is not JObject u) return cfg;
        var pr = (JObject)(u["params"] ?? new JObject())!;
        pr[key] = value;
        u["params"] = pr;
        return cfg;
    }

    // ---------------- 变速（对位 applyFx({speed})） ----------------

    private static string SpeedLabel(double speed) => L.F("AudioFx.Speed.ValueFmt", speed.ToString("F2"));

    private static Func<JObject, JObject> SpeedEdit(double speed) => cur =>
    {
        cur["speed"] = Math.Round(speed / 0.05) * 0.05;
        return cur;
    };

    private void SlSpeed_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || !_ready || App.MainInstance == null) return; // 构造期与回填早退
        var speed = Math.Round(e.NewValue / 0.05) * 0.05;            // 档位对齐 0.05
        TblSpeedVal.Text = SpeedLabel(speed);
        var audio = Preview.Audio;
        if (audio != null) TblSpeedDur.Text = DurationText(audio.Duration / Math.Max(0.01, speed));
        if (_dragging) return;                                       // 拖动中只刷显示（松手由 AttachDrag 提交）
        SubmitDebounced(SpeedLabel(speed), SpeedEdit(speed));
    }
}
