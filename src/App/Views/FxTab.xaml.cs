using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JizuraLive.App.I18n;
using JizuraLive.Engine;

namespace JizuraLive.App.Views;

// 特效面板（对位 FxTab.vue）：fx 参数滑块（双向绑定手写：拖动中只刷显示，DragCompleted / 键盘改动才 Mutate 防循环）+
// 闪光/抽帧/HUD/种子/隐藏装饰编号时刻开关。
// 音频效果（预设/效果链/变速/EQ）已搬进独立弹窗 AudioFxDialog——由播放条声音组的「音频效果」按钮打开，
// 本面板只留画面特效（对位 Vue：特效弹窗不落在特效页里）。
public partial class FxTab : UserControl
{
    private bool _built;
    private bool _syncing;

    private EditorState S => App.MainInstance!.State;

    /// <summary>fx 参数滑块表（对位 FX 常量：key → 词库键，L.T 在构建/换语言时解析）。</summary>
    private static readonly (string Key, string LKey)[] FxKeys =
    {
        ("motion", "Fx.Param.Motion"), ("glitch", "Fx.Param.Glitch"), ("chroma", "Fx.Param.Chroma"), ("decor", "Fx.Param.Decor"),
        ("density", "Fx.Param.Density"), ("texture", "Fx.Param.Texture"), ("bgSwitch", "Fx.Param.BgSwitch"),
    };

    /// <summary>抽帧档位（koma：0/8/12/24）。</summary>
    private static readonly (int V, string LKey)[] KomaOptions =
    {
        (0, "Fx.Koma.Full"), (8, "Fx.Koma.Threes"), (12, "Fx.Koma.Twos"), (24, "Fx.Koma.Ones"),
    };

    // 锁定按钮的悬浮说明（对位 Vue 的 LOCK_ON / LOCK_OFF）
    private static string LockOn => L.T("Fx.LockOn");
    private static string LockOff => L.T("Fx.LockOff");

    private sealed record FxRow(string Key, string Label, Slider Slider, TextBlock Value, Button Lock);

    private readonly List<FxRow> _rows = new();
    private readonly Dictionary<string, bool> _thumbDrag = new();
    private readonly DispatcherTimer _fxDebounce; // 拖动中的参数合并提交（对位 Vue 的 replanSoon(120)）
    private string _pendingFxKey = "";
    private double _pendingFxVal;

    public FxTab()
    {
        InitializeComponent();
        _fxDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _fxDebounce.Tick += (_, _) =>
        {
            _fxDebounce.Stop();
            FlushPendingFx();
        };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_built)
        {
            _built = true;
            BuildSliderRows();
            BuildComboItems();
        }
        S.StateChanged += OnState;
        L.LanguageChanged += OnLanguageChanged;
        Pull();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        S.StateChanged -= OnState;
        L.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged() => Dispatcher.Invoke(ApplyTexts);

    /// <summary>语言切换后重刷 code-behind 动态文案：抽帧/HUD 下拉项（清空后由 BuildComboItems 重建）。
    /// 选择复位全程 _syncing 防误触发。</summary>
    private void ApplyTexts()
    {
        if (App.MainInstance == null) return;
        _syncing = true;
        try
        {
            CbKoma.Items.Clear();
            CbHud.Items.Clear();
            BuildComboItems();
            // 滑块行文案在 code-behind 里落文本，换语言整段重建（值由 Pull 回填）
            SliderRows.Children.Clear();
            _rows.Clear();
            BuildSliderRows();
        }
        finally
        {
            _syncing = false;
        }
        Pull();
    }

    private void OnState() => Dispatcher.Invoke(Pull);

    // ---------------- 构建 ----------------

    /// <summary>
    /// fx 参数滑块行（7 行，对位 FxTab.vue 的 sliders）：标签 96 | 滑块 | 数值 34 | 锁定键，行距 8。
    /// 提交节奏：拖动中 120ms 防抖（预览边拖边变，对位 replanSoon(120)）、松手立即落定；
    /// 键盘/点轨道等非拖动改动立即落。锁定键写 Locks.Params（一键随机不动的项）。
    /// </summary>
    private void BuildSliderRows()
    {
        foreach (var (key, lkey) in FxKeys)
        {
            var label = L.T(lkey);
            var lab = new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

            var slider = new Slider
            {
                Style = (Style)FindResource("FxSlider"),
                Margin = new Thickness(8, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var value = new TextBlock
            {
                FontSize = 11,
                FontFamily = (FontFamily)FindResource("MonoFont"),
                Foreground = (Brush)FindResource("Mut"),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 34,
            };

            var lockBtn = new Button
            {
                Style = (Style)FindResource("LockBtn"),
                Content = FindResource("FxLock"),
                Margin = new Thickness(8, 0, 0, 0),
                ToolTip = LockOn,
            };

            var grid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(lab, 0);
            Grid.SetColumn(slider, 1);
            Grid.SetColumn(value, 2);
            Grid.SetColumn(lockBtn, 3);
            grid.Children.Add(lab);
            grid.Children.Add(slider);
            grid.Children.Add(value);
            grid.Children.Add(lockBtn);
            SliderRows.Children.Add(grid);

            _rows.Add(new FxRow(key, label, slider, value, lockBtn));

            var k = key;
            slider.ValueChanged += (_, ev) =>
            {
                value.Text = Round100(ev.NewValue);
                if (_syncing) return;
                _pendingFxKey = k;
                _pendingFxVal = ev.NewValue;
                if (_thumbDrag.TryGetValue(k, out var dragging) && dragging)
                {
                    _fxDebounce.Stop(); // 拖动中：合并成一次写（120ms），预览边拖边变
                    _fxDebounce.Start();
                    return;
                }
                FlushPendingFx(); // 键盘/滚轮/单击轨道：立即落
            };
            slider.Loaded += (_, _) =>
            {
                if (slider.Template?.FindName("thumb", slider) is not Thumb thumb) return;
                thumb.DragStarted += (_, _) => _thumbDrag[k] = true;
                thumb.DragCompleted += (_, _) =>
                {
                    _thumbDrag[k] = false;
                    _pendingFxKey = k;
                    _pendingFxVal = slider.Value;
                    FlushPendingFx(); // 松手立即落定（把拖动中的最后一次值写下去）
                };
            };

            lockBtn.Click += (_, _) => ToggleLock(k);
        }
    }

    /// <summary>把挂起的参数改动写进工程（ValueChanged 与 DragCompleted 共用）。</summary>
    private void FlushPendingFx()
    {
        _fxDebounce.Stop();
        if (_syncing || App.MainInstance == null) return;
        if (_pendingFxKey.Length == 0) return;
        var key = _pendingFxKey;
        _pendingFxKey = "";
        CommitFx(key, _pendingFxVal);
    }

    private void BuildComboItems()
    {
        foreach (var (v, lkey) in KomaOptions)
            CbKoma.Items.Add(new ComboBoxItem { Content = L.T(lkey), Tag = v });
        foreach (var (v, lkey) in new[] { ("auto", "Fx.Hud.Auto"), ("on", "Fx.Hud.On"), ("off", "Fx.Hud.Off") })
            CbHud.Items.Add(new ComboBoxItem { Content = L.T(lkey), Tag = v });
    }

    // ---------------- 提交（Mutate） ----------------

    private void CommitFx(string key, double v)
    {
        if (Math.Abs(GetFx(S.Project.Fx, key) - v) < 1e-9) return;
        S.Mutate(p =>
        {
            SetFx(p.Fx, key, v);
            p.Mood = null; // onSlider：动了参数就丢掉一键随机的主题
        }, L.F("Fx.Mutate.Param", L.T(FxKeys.First(f => f.Key == key).LKey)));
    }

    private void ToggleLock(string key)
    {
        var on = !S.Project.Locks.Params.TryGetValue(key, out var v) || !v;
        S.Mutate(p =>
        {
            if (on) p.Locks.Params[key] = true;
            else p.Locks.Params.Remove(key);
        }, L.F("Fx.Mutate.LockParam", key), replan: false);
    }

    private void BtnKomaLock_Click(object sender, RoutedEventArgs e) => ToggleLock("koma");

    private void Unify_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        S.Mutate(p => p.Unify = CkUnify.IsChecked == true, L.T("Fx.Unify"), look: true);
    }

    private void Typeset_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        S.Mutate(p => p.Typeset = CkTypeset.IsChecked == true, L.T("Fx.Typeset"), look: true);
    }

    private void Flash_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        S.Mutate(p => p.Fx.Flash = CkFlash.IsChecked == true, L.T("Fx.Flash"));
    }

    private void CbKoma_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || CbKoma.SelectedItem is not ComboBoxItem it) return;
        var k = (int)it.Tag!;
        S.Mutate(p =>
        {
            p.Fx.Koma = k;
            p.Fx.OnTwos = k > 0; // onKoma 语义：有抽帧即二拍作画
            p.Mood = null;
        }, L.F("Fx.Mutate.Koma", k));
    }

    private void CbHud_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || CbHud.SelectedItem is not ComboBoxItem it) return;
        var v = (string)it.Tag!;
        if (v == (S.Project.Fx.Hud ?? "auto")) return;
        S.Mutate(p => p.Fx.Hud = v, L.T("Fx.Hud"));
    }

    private void HideDeco_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        S.Mutate(p =>
        {
            p.Fx.HideNo = CkHideNo.IsChecked == true;
            p.Fx.HideTime = CkHideTime.IsChecked == true;
        }, L.T("Fx.Mutate.HideDeco"));
    }

    private void TbSeed_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitSeed();
            e.Handled = true;
        }
    }

    private void TbSeed_Commit(object sender, RoutedEventArgs e) => CommitSeed();

    private void CommitSeed()
    {
        if (_syncing) return;
        if (!uint.TryParse(TbSeed.Text.Trim(), out var seed))
        {
            TbSeed.Text = S.Project.Seed.ToString(); // 非法回显
            return;
        }
        if (seed == S.Project.Seed) return;
        S.Mutate(p => p.Seed = seed, L.T("Fx.Seed"));
    }

    private void BtnNewSeed_Click(object sender, RoutedEventArgs e)
        => S.Mutate(p => p.Seed = (uint)(Random.Shared.NextDouble() * 1e9), L.T("Fx.NewSeed")); // (Math.random()*1e9)|0

    // ---------------- 回刷 ----------------

    private void Pull()
    {
        if (App.MainInstance == null) return;
        _syncing = true;
        try
        {
            var p = S.Project;

            SetChecked(CkUnify, p.Unify);
            SetChecked(CkTypeset, p.Typeset);
            SetChecked(CkFlash, p.Fx.Flash);
            SetChecked(CkHideNo, p.Fx.HideNo);
            SetChecked(CkHideTime, p.Fx.HideTime);

            foreach (var row in _rows)
            {
                var v = GetFx(p.Fx, row.Key);
                if (Math.Abs(row.Slider.Value - v) > 1e-9) row.Slider.Value = v;
                row.Value.Text = Round100(v);
                var locked = p.Locks.Params.TryGetValue(row.Key, out var lv) && lv;
                row.Lock.Tag = locked ? "on" : "off";
                row.Lock.ToolTip = locked ? LockOff : LockOn;
            }

            // 显示值取「有效档位」= Planner.KomaOf（对位前端 rebuild 的 String(J.komaOf(fx))）：
            // Koma 为 null（工程里没写这个键）时按 OnTwos 落到 0/12，直接看 Koma 字段会对不上
            var effKoma = Planner.KomaOf(p.Fx);
            var koma = KomaOptions.Where(o => o.V == effKoma).Select(o => o.V).FirstOrDefault(0);
            SelectTag(CbKoma, koma.ToString());
            SelectTag(CbHud, string.IsNullOrEmpty(p.Fx.Hud) ? "auto" : p.Fx.Hud);
            if (TbSeed.Text != p.Seed.ToString()) TbSeed.Text = p.Seed.ToString();

            var komaLocked = p.Locks.Params.TryGetValue("koma", out var klv) && klv;
            BtnKomaLock.Tag = komaLocked ? "on" : "off";
            BtnKomaLock.ToolTip = komaLocked ? LockOff : LockOn;
        }
        finally
        {
            _syncing = false;
        }
    }

    // ---------------- 小工具 ----------------

    private static double GetFx(FxSettings f, string k) => k switch
    {
        "motion" => f.Motion,
        "glitch" => f.Glitch,
        "chroma" => f.Chroma,
        "decor" => f.Decor,
        "density" => f.Density,
        "texture" => f.Texture,
        "bgSwitch" => f.BgSwitch,
        _ => 0.5,
    };

    private static void SetFx(FxSettings f, string k, double v)
    {
        switch (k)
        {
            case "motion": f.Motion = v; break;
            case "glitch": f.Glitch = v; break;
            case "chroma": f.Chroma = v; break;
            case "decor": f.Decor = v; break;
            case "density": f.Density = v; break;
            case "texture": f.Texture = v; break;
            case "bgSwitch": f.BgSwitch = v; break;
        }
    }

    private static string Round100(double v) => Math.Round(v * 100).ToString();

    // 统一感/文字排列是 ToggleButton（.switch），其余开关仍是 CheckBox
    private static void SetChecked(ToggleButton tb, bool v)
    {
        if (tb.IsChecked != v) tb.IsChecked = v;
    }

    private static void SelectTag(ComboBox cb, string tag)
    {
        foreach (var i in cb.Items.OfType<ComboBoxItem>())
            if (string.Equals(i.Tag?.ToString(), tag, StringComparison.Ordinal))
            {
                if (!ReferenceEquals(cb.SelectedItem, i)) cb.SelectedItem = i;
                return;
            }
        if (cb.Items.Count > 0) cb.SelectedIndex = 0;
    }
}
