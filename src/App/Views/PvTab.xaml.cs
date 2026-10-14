using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using JizuraLive.App.I18n;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Spout;
using JizuraLive.Vts;
using AppSettings = JizuraLive.App.Settings;

namespace JizuraLive.App.Views;

// PV 面板（PV 模式的控制面）：多路外部 Spout 源（VTS/游戏/桌面捕获）合成到主通道与预览。
//
// 为什么卡片在代码里建：四路源同构（同一组控件 + 同一套事件），还要随「添加/删除源」重建，
// 写成四份 XAML 只是把同一棵树抄四遍。XAML 只放壳（PvTab.xaml）。
//
// 参数纪律与设置页一致——**即时生效**：写进 AppSettings.Pv 后 Spout 泵与预览泵每拍读一次
// （Revision++ 让暂停态的预览知道要重出一帧），所以拖动滑块画面立刻跟着变，不用点重启。
// 源名用可编辑下拉：下拉列出注册表里正在输出的发送端（SpoutNames.Enumerate），
// 名字不在列表里也能手输（枚举读不到时的保底路径）。
public partial class PvTab : UserControl
{
    private const string FallbackSource = "VTubeStudioSpout";
    private readonly List<Card> _cards = new();
    private bool _syncing;
    private DispatcherTimer? _timer;

    private static AppSettings? St => App.SettingsOf();

    public PvTab()
    {
        InitializeComponent();
        // 面板长在折叠的页签里：Loaded 的时序按可见性走不稳，统一以 IsVisibleChanged 为准——
        // 可见才建卡片/刷新状态，收起就停 1s 计时器（枚举注册表 + 状态回显别在后台空转）。
        Loaded += (_, _) => L.LanguageChanged += OnLanguageChanged;
        Unloaded += (_, _) => L.LanguageChanged -= OnLanguageChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                Pull();
                StartTimer();
            }
            else
            {
                _timer?.Stop();
            }
        };
    }

    private void StartTimer()
    {
        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    /// <summary>
    /// 页签被切到时的入口（RightPanel 调用）：建卡片 + 开状态计时器。
    /// 不依赖 Loaded/IsVisibleChanged 的时序——折叠页签里这两个事件的时机按容器实现而变，
    /// 由切页方直接驱动最稳。
    /// </summary>
    public void EnsureReady()
    {
        if (St == null) return;
        Pull();
        StartTimer();
    }

    /// <summary>语言切换：卡片文案是构建期用 L.T 落的，整组重建（含 VTS 卡片）。</summary>
    private void OnLanguageChanged() => Dispatcher.Invoke(() =>
    {
        if (_vtsCard != null)
        {
            if (Vts is { } v) v.Changed -= OnVtsChanged;
            VtsHost.Children.Clear();
            _vtsCard = null;
            _vtsRows.Clear();
            _driveRows.Clear();
            _driveHotkey.ItemsSource = null;
            _driveEvery.ItemsSource = null;
        }

        Pull();
    });

    private void OnTick(object? sender, EventArgs e)
    {
        if (IsVisible) TickState();
    }

    // ---------------- 拉/推 ----------------

    /// <summary>设置 → 控件（含按当前源重建成卡片）。</summary>
    private void Pull()
    {
        var st = St;
        if (st == null) return;
        _syncing = true;
        try
        {
            CkEnabled.IsChecked = st.Pv.Enabled;
            CkGlobalCam.IsChecked = st.Pv.GlobalCamera;
            CkFollow.IsChecked = st.Pv.FollowCamera;
            CkSubtitleTop.IsChecked = st.Pv.SubtitleOnTop;
            CkFollow.Visibility = st.Pv.GlobalCamera ? Visibility.Collapsed : Visibility.Visible;
            Rebuild();
            EnsureVtsCard();
            PullVts();
        }
        finally
        {
            _syncing = false;
        }

        TickState();
    }

    /// <summary>总开关类（启用/镜头/字幕）改动 → 立即生效。</summary>
    private void Flag_Changed(object sender, RoutedEventArgs e)
    {
        var st = St;
        if (st == null || _syncing) return;
        st.Pv.Enabled = CkEnabled.IsChecked == true;
        st.Pv.GlobalCamera = CkGlobalCam.IsChecked == true;
        st.Pv.FollowCamera = CkFollow.IsChecked == true;
        st.Pv.SubtitleOnTop = CkSubtitleTop.IsChecked == true;
        st.Pv.Revision++;
        st.SaveDebounced();
        CkFollow.Visibility = st.Pv.GlobalCamera ? Visibility.Collapsed : Visibility.Visible;

        var main = App.MainInstance;
        if (main != null && st.Pv.Enabled && !main.Spout.IsRunning)
        {
            main.StartSpout();
        }

        TickState();
    }

    private void BtnRefresh_Click(object sender, RoutedEventArgs e)
    {
        SpoutNames.Enumerate(refresh: true);
        RefreshNameItems(force: true);
    }

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        var st = St;
        if (st == null || st.Pv.Sources.Count >= PvOptions.MaxSources) return;
        st.Pv.Sources.Add(new LiveLayerOptions { Enabled = true, SenderName = NextFreeName(st) });
        if (!st.Pv.Enabled) st.Pv.Enabled = true; // 添加源多半就是想用：顺手把总开关带上
        st.Pv.Revision++;
        st.SaveDebounced();
        Log.Info($"[PV] 已添加源（共 {st.Pv.Sources.Count} 路）");
        Pull();
    }

    /// <summary>新源的默认名：优先取「已枚举到但还没被占用」的发送端，都没则给 VTS 默认名。</summary>
    private static string NextFreeName(AppSettings st)
    {
        var used = st.Pv.Sources.Select(s => s.SenderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var n in SpoutNames.Enumerate())
            if (!used.Contains(n)) return n;
        return FallbackSource;
    }

    // ---------------- 卡片构建 ----------------

    private void Rebuild()
    {
        var st = St;
        if (st == null) return;
        SourceList.Children.Clear();
        _cards.Clear();
        var names = SourceNameList(st);
        for (var i = 0; i < st.Pv.Sources.Count && i < PvOptions.MaxSources; i++)
        {
            var card = BuildCard(i, st.Pv.Sources[i], names);
            _cards.Add(card);
            SourceList.Children.Add(card.Root);
            PullCard(card);
        }

        BtnAdd.IsEnabled = st.Pv.Sources.Count < PvOptions.MaxSources;
    }

    /// <summary>下拉候选 = 注册表里活跃的发送端 ∪ 已配置的源名 ∪ 默认名（去重保序）。</summary>
    private static List<string> SourceNameList(AppSettings st)
    {
        var list = new List<string>();
        void Add(string? n)
        {
            if (!string.IsNullOrWhiteSpace(n) && !list.Contains(n)) list.Add(n);
        }

        foreach (var n in SpoutNames.Enumerate()) Add(n);
        foreach (var s in st.Pv.Sources) Add(s.SenderName);
        Add(FallbackSource);
        Add("VTubeStudioSpout");
        Add("VTubeStudio");
        return list;
    }

    private Card BuildCard(int index, LiveLayerOptions opt, List<string> names)
    {
        var card = new Card { Index = index, Opt = opt };
        var body = new StackPanel();

        // 头部：[启用] 源 N ……………… [✕]
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        card.CkEnable = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
        card.CkEnable.Checked += (_, _) => ApplyCard(card);
        card.CkEnable.Unchecked += (_, _) => ApplyCard(card);
        Grid.SetColumn(card.CkEnable, 0);
        head.Children.Add(card.CkEnable);
        var title = new TextBlock
        {
            Text = L.F("Pv.Source.Index", index + 1),
            Style = (Style)FindResource("RowLab"),
            Margin = new Thickness(6, 0, 0, 0),
        };
        Grid.SetColumn(title, 1);
        head.Children.Add(title);
        var del = new Button
        {
            Style = (Style)FindResource("DelBtn"),
            Content = "✕",
            ToolTip = L.T("Pv.Source.Remove"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        del.Click += (_, _) => RemoveSource(card);
        Grid.SetColumn(del, 2);
        head.Children.Add(del);
        body.Children.Add(head);

        // 源名（可编辑下拉）+ 接收状态
        var nameRow = new Grid { Margin = new Thickness(0, 6, 0, 2) };
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var nameLab = new TextBlock { Text = L.T("Pv.Source.Name"), Style = (Style)FindResource("RowLab") };
        Grid.SetColumn(nameLab, 0);
        nameRow.Children.Add(nameLab);
        card.CbName = new ComboBox
        {
            IsEditable = true,                 // 枚举里没有的源名可以手输（读不到注册表时的保底）
            IsTextSearchEnabled = false,       // 打字即输入，不要自动补全抢字
            ItemsSource = names,
            Margin = new Thickness(8, 0, 0, 0),
            FontSize = 12,
            Foreground = (Brush)FindResource("Fg"),
            Background = (Brush)FindResource("Raised"),
        };
        card.CbName.SelectionChanged += (_, e) =>
        {
            if (e.AddedItems.Count > 0 && e.AddedItems[0] is string s)
                card.Opt.SenderName = s.Trim();
            ApplyCard(card);
        };
        card.CbName.LostFocus += (_, _) => ApplyCard(card);              // 手输完（失焦）再生效：
        // 逐键生效会让接收端每敲一个字就重建一次（半截名字连不上，白折腾原生接收端）
        Grid.SetColumn(card.CbName, 1);
        nameRow.Children.Add(card.CbName);
        body.Children.Add(nameRow);

        card.TblState = new TextBlock
        {
            Style = (Style)FindResource("MonoMut"),
            FontSize = 11,
            Text = "—",
            Margin = new Thickness(0, 0, 0, 4),
        };
        body.Children.Add(card.TblState);

        // 参数行：标签 + 滑块 + 数值回显
        AddRow(body, card, "Pv.Alpha", 0, 1, 0.01, v => v.ToString("F2"), v => opt.Alpha = v);
        AddRow(body, card, "Pv.Scale", 0.1, 3, 0.01, v => v.ToString("F2"), v => opt.Scale = v);
        AddRow(body, card, "Pv.OffX", -0.5, 0.5, 0.005, v => v.ToString("+0.00;-0.00;0.00"), v => opt.OffsetX = v);
        AddRow(body, card, "Pv.OffY", -0.5, 0.5, 0.005, v => v.ToString("+0.00;-0.00;0.00"), v => opt.OffsetY = v);
        AddRow(body, card, "Pv.Rotation", -180, 180, 1, v => v.ToString("F0") + "°", v => opt.Rotation = v);
        AddRow(body, card, "Pv.Blur", 0, 40, 0.5, v => v.ToString("F0"), v => opt.Blur = v);
        AddRow(body, card, "Pv.Glow", 0, 1, 0.01, v => v.ToString("F2"), v => opt.Glow = v);
        AddRow(body, card, "Pv.Hue", -180, 180, 1, v => v.ToString("F0") + "°", v => opt.Hue = v);

        var checks = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        card.CkMirror = AddCheck(checks, "Pv.Mirror", null, opt.MirrorX);
        card.CkCover = AddCheck(checks, "Pv.Cover", "Pv.Cover.Tip", opt.Cover);
        card.CkPremul = AddCheck(checks, "Pv.Premul", "Pv.Premul.Tip", opt.Premultiplied);
        card.CkMirror.Checked += (_, _) => ApplyCard(card);
        card.CkMirror.Unchecked += (_, _) => ApplyCard(card);
        card.CkCover.Checked += (_, _) => ApplyCard(card);
        card.CkCover.Unchecked += (_, _) => ApplyCard(card);
        card.CkPremul.Checked += (_, _) => ApplyCard(card);
        card.CkPremul.Unchecked += (_, _) => ApplyCard(card);
        body.Children.Add(checks);

        card.Root = new Border { Style = (Style)FindResource("SrcCard"), Child = body };
        return card;
    }

    /// <summary>一行「标签 + 滑块 + 数值」；改动回调把值写进源参数（ApplyCard 兜生效与落盘）。</summary>
    private void AddRow(Panel host, Card card, string labelKey, double min, double max, double step,
        Func<double, string> fmt, Action<double> store)
    {
        var row = new Row { Fmt = fmt, Store = store };
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        var lab = new TextBlock { Text = L.T(labelKey), Style = (Style)FindResource("RowLab") };
        Grid.SetColumn(lab, 0);
        grid.Children.Add(lab);
        row.Sl = new Slider
        {
            Style = (Style)FindResource("FxSlider"),
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            LargeChange = step * 10,
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Sl.ValueChanged += (_, _) =>
        {
            if (card.Syncing) return;
            row.Store(row.Sl.Value);
            row.Val.Text = row.Fmt(row.Sl.Value);
            ApplyCard(card);
        };
        Grid.SetColumn(row.Sl, 1);
        grid.Children.Add(row.Sl);
        row.Val = new TextBlock
        {
            Style = (Style)FindResource("MonoMut"),
            FontSize = 11,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(row.Val, 2);
        grid.Children.Add(row.Val);
        host.Children.Add(grid);
        card.Rows.Add(row);
    }

    private CheckBox AddCheck(Panel host, string labelKey, string? tipKey, bool on)
    {
        var ck = new CheckBox
        {
            IsChecked = on,
            Margin = new Thickness(0, 0, 14, 4),
            Content = new TextBlock { Text = L.T(labelKey), FontSize = 12 },
        };
        if (tipKey != null) ck.ToolTip = L.T(tipKey);
        host.Children.Add(ck);
        return ck;
    }

    /// <summary>设置 → 卡片控件（_syncing/Syncing 防回声提交）。</summary>
    private void PullCard(Card card)
    {
        card.Syncing = true;
        try
        {
            var o = card.Opt;
            card.CkEnable.IsChecked = o.Enabled;
            if (card.CbName.ItemsSource is IEnumerable<string> items)
                card.CbName.SelectedItem = items.FirstOrDefault(n => string.Equals(n, o.SenderName, StringComparison.OrdinalIgnoreCase)) ?? o.SenderName;
            card.CbName.Text = o.SenderName;
            var v = new[] { o.Alpha, o.Scale, o.OffsetX, o.OffsetY, o.Rotation, o.Blur, o.Glow, o.Hue };
            for (var i = 0; i < card.Rows.Count && i < v.Length; i++)
            {
                var val = Math.Clamp(v[i], card.Rows[i].Sl.Minimum, card.Rows[i].Sl.Maximum);
                card.Rows[i].Sl.Value = val;
                card.Rows[i].Val.Text = card.Rows[i].Fmt(val);
            }

            card.CkMirror.IsChecked = o.MirrorX;
            card.CkCover.IsChecked = o.Cover;
            card.CkPremul.IsChecked = o.Premultiplied;
        }
        finally
        {
            card.Syncing = false;
        }
    }

    /// <summary>卡片控件 → 设置（由各控件的事件回调），立即生效 + 防抖落盘。</summary>
    private void ApplyCard(Card card)
    {
        var st = St;
        if (st == null || _syncing || card.Syncing) return;
        card.Syncing = true;
        try
        {
            var o = card.Opt;
            o.Enabled = card.CkEnable.IsChecked == true;
            var sel = card.CbName.SelectedItem as string;
            var txt = card.CbName.Text.Trim();
            var name = !string.IsNullOrWhiteSpace(sel) ? sel.Trim() : txt;
            o.SenderName = name.Length == 0 ? FallbackSource : name;
            o.MirrorX = card.CkMirror.IsChecked == true;
            o.Cover = card.CkCover.IsChecked == true;
            o.Premultiplied = card.CkPremul.IsChecked == true;

            // 联动：只要启用了任一路源，自动勾上 PV 合成总开关，绝不让用户在总开关未开时感到困惑
            if (o.Enabled && !st.Pv.Enabled)
            {
                st.Pv.Enabled = true;
                CkEnabled.IsChecked = true;
            }
        }
        finally
        {
            card.Syncing = false;
        }

        st.Pv.Revision++;
        st.SaveDebounced();

        // 确保 Spout 接收与发送管线随源启用而拉起
        var main = App.MainInstance;
        if (main != null && st.Pv.Enabled && !main.Spout.IsRunning)
        {
            main.StartSpout();
        }

        TickState();
    }

    private void RemoveSource(Card card)
    {
        var st = St;
        if (st == null || card.Index < 0 || card.Index >= st.Pv.Sources.Count) return;
        st.Pv.Sources.RemoveAt(card.Index);
        st.Pv.Revision++;
        st.SaveDebounced();
        Log.Info($"[PV] 已删除源（剩 {st.Pv.Sources.Count} 路）");
        Pull();
    }

    /// <summary>源列表下拉内容刷新（1s 随状态行一起刷；正在输入/展开时不动，免得抢用户的字）。</summary>
    private void RefreshNameItems(bool force)
    {
        var st = St;
        if (st == null) return;
        var names = SourceNameList(st);
        foreach (var card in _cards)
        {
            if (!force && (card.CbName.IsDropDownOpen || card.CbName.IsKeyboardFocusWithin)) continue;
            if (card.CbName.ItemsSource is List<string> cur && cur.SequenceEqual(names)) continue;
            card.Syncing = true;
            try
            {
                var targetName = !string.IsNullOrWhiteSpace(card.Opt.SenderName) ? card.Opt.SenderName : card.CbName.Text;
                card.CbName.ItemsSource = names;
                card.CbName.SelectedItem = names.FirstOrDefault(n => string.Equals(n, targetName, StringComparison.OrdinalIgnoreCase)) ?? targetName;
                card.CbName.Text = targetName;
            }
            finally
            {
                card.Syncing = false;
            }
        }
    }

    // ---------------- 状态回显 ----------------

    /// <summary>接收状态（1s 一次，只在面板可见时跑；纯读属性）：每路源的连接/尺寸/出错 + 输出通道。</summary>
    private void TickState()
    {
        var st = St;
        var main = App.MainInstance;
        var ins = main != null && main.Spout.IsRunning ? main.Spout.LiveIns : null;
        var mut = (Brush)FindResource("Mut");
        var amber = (Brush)FindResource("Amber");
        var cyan = (Brush)FindResource("Cyan");
        var danger = (Brush)FindResource("Danger");

        for (var i = 0; i < _cards.Count; i++)
        {
            var card = _cards[i];
            string txt;
            Brush brush;
            var tip = "";
            if (!card.Opt.Enabled)
            {
                txt = L.T("Pv.State.Disabled");
                brush = mut;
            }
            else if (ins == null)
            {
                txt = L.T("Pv.State.NoSpout");
                brush = amber;
            }
            else if (i >= ins.Count)
            {
                txt = L.T("Pv.State.Wait");
                brush = mut;
            }
            else
            {
                var inSink = ins[i];
                if (inSink.Connected)
                {
                    txt = L.F("Pv.State.On", inSink.SourceWidth, inSink.SourceHeight);
                    brush = cyan;
                }
                else
                {
                    tip = inSink.LastError;
                    txt = inSink.LastError.Length > 0 ? L.T("Pv.State.Error") : L.T("Pv.State.Wait");
                    brush = inSink.LastError.Length > 0 ? danger : mut;
                }
            }

            card.TblState.Text = txt;
            card.TblState.Foreground = brush;
            card.TblState.ToolTip = tip.Length > 0 ? tip : null;
        }

        TblOut.Text = st == null
            ? "—"
            : L.F("Pv.Output", st.SpoutName) + (st.SpoutEnabled ? "" : L.T("Pv.Output.Off"));
        RefreshNameItems(force: false);
    }

    // ---------------- VTube Studio 联动（插件 API：模型取景 + 热键） ----------------

    private Border? _vtsCard;
    private TextBlock _vtsState = null!;
    private Button _vtsAuth = null!;
    private TextBlock _vtsModel = null!;
    private ComboBox _vtsHotkeys = null!;
    private Button _vtsFire = null!;
    private CheckBox _vtsAuto = null!;
    private readonly List<Row> _vtsRows = new();
    private readonly List<Row> _driveRows = new();   // 直接驱动（模型跟随节拍/能量/镜头）
    private CheckBox _driveEnable = null!;
    private ComboBox _driveHotkey = null!;
    private ComboBox _driveEvery = null!;
    private bool _vtsSyncing;

    private static VtsApi? Vts => App.MainInstance?.Vts;

    /// <summary>VTS 卡片只建一次（语言切换时整张重建）。</summary>
    private void EnsureVtsCard()
    {
        var vts = Vts;
        if (vts == null || _vtsCard != null) return;
        var body = new StackPanel();

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(new TextBlock
        {
            Text = L.T("Pv.Vts.Title"),
            Style = (Style)FindResource("RowLab"),
            FontWeight = FontWeights.SemiBold,
        });
        _vtsState = new TextBlock
        {
            Style = (Style)FindResource("MonoMut"),
            FontSize = 11,
            Text = "—",
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(_vtsState, 1);
        head.Children.Add(_vtsState);
        body.Children.Add(head);

        _vtsModel = new TextBlock
        {
            Style = (Style)FindResource("MonoMut"),
            FontSize = 11,
            Margin = new Thickness(0, 2, 0, 6),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        body.Children.Add(_vtsModel);

        var buttons = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        _vtsAuth = new Button
        {
            Style = (Style)FindResource("BtnGhostSm"),
            Content = L.T("Pv.Vts.Auth"),
            Margin = new Thickness(0, 0, 6, 4),
            ToolTip = L.T("Pv.Vts.Auth.Tip"),
        };
        _vtsAuth.Click += (_, _) =>
        {
            Vts?.RequestAuth();
            _vtsState.Text = L.T("Pv.Vts.Approving");
        };
        buttons.Children.Add(_vtsAuth);
        var reconnect = new Button
        {
            Style = (Style)FindResource("BtnGhostSm"),
            Content = L.T("Pv.Vts.Reconnect"),
            Margin = new Thickness(0, 0, 6, 4),
        };
        reconnect.Click += (_, _) => Vts?.Reconnect();
        buttons.Children.Add(reconnect);
        var refresh = new Button
        {
            Style = (Style)FindResource("BtnGhostSm"),
            Content = L.T("Pv.Vts.RefreshHotkeys"),
            Margin = new Thickness(0, 0, 6, 4),
        };
        refresh.Click += (_, _) => Vts?.RefreshHotkeys();
        buttons.Children.Add(refresh);
        body.Children.Add(buttons);

        body.Children.Add(new TextBlock
        {
            Text = L.T("Pv.Vts.Frame"),
            Style = (Style)FindResource("RowLab"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        AddVtsRow(body, "Pv.Vts.Size", -100, 100, 1, v => (v >= 0 ? "+" : "") + v.ToString("F0"), 0);
        AddVtsRow(body, "Pv.Vts.X", -2.0, 2.0, 0.01, v => v.ToString("+0.00;-0.00;0.00"), 1);
        AddVtsRow(body, "Pv.Vts.Y", -2.0, 2.0, 0.01, v => v.ToString("+0.00;-0.00;0.00"), 2);
        AddVtsRow(body, "Pv.Vts.Rotation", -180, 180, 1, v => v.ToString("F0") + "°", 3);
        var full = new Button
        {
            Style = (Style)FindResource("BtnGhostSm"),
            Content = L.T("Pv.Vts.FullFrame"),
            Margin = new Thickness(0, 4, 0, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            ToolTip = L.T("Pv.Vts.FullFrame.Tip"),
        };
        full.Click += (_, _) =>
        {
            // 全身取景：VTS 中缩小至 -45，垂直微下移 -0.15，确保模型从头至脚完整置于画面内
            Vts?.MoveModel(-45, 0, -0.15, 0, 0.35);
            SetVtsRows(-45, 0, -0.15, 0);
        };
        body.Children.Add(full);

        // ---- 直接驱动：把节拍/能量/镜头映射到模型本体（不是只在我们这边叠特效） ----
        body.Children.Add(new TextBlock
        {
            Text = L.T("Pv.Vts.Drive.Title"),
            Style = (Style)FindResource("RowLab"),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 2, 0, 2),
        });
        _driveEnable = new CheckBox
        {
            Content = L.T("Pv.Vts.Drive.Enable"),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 4),
            ToolTip = L.T("Pv.Vts.Drive.Enable.Tip"),
        };
        _driveEnable.Checked += (_, _) => ApplyDrive();
        _driveEnable.Unchecked += (_, _) => ApplyDrive();
        body.Children.Add(_driveEnable);
        var driveBox = new StackPanel { Margin = new Thickness(18, 0, 0, 6) };
        AddDriveRow(driveBox, "Pv.Vts.Drive.Bounce", 0, 0.2, 0.005,
            v => v.ToString("F3"), o => o.Bounce, (o, v) => o.Bounce = v);
        AddDriveRow(driveBox, "Pv.Vts.Drive.Sway", 0, 0.2, 0.005,
            v => v.ToString("F3"), o => o.Sway, (o, v) => o.Sway = v);
        AddDriveRow(driveBox, "Pv.Vts.Drive.SwayRot", 0, 20, 0.5,
            v => v.ToString("F1") + "°", o => o.SwayRotation, (o, v) => o.SwayRotation = v);
        AddDriveRow(driveBox, "Pv.Vts.Drive.Mouth", 0, 1, 0.01,
            v => v.ToString("F2"), o => o.MouthWeight, (o, v) => o.MouthWeight = v);
        AddDriveRow(driveBox, "Pv.Vts.Drive.Head", 0, 1, 0.01,
            v => v.ToString("F2"), o => o.HeadWeight, (o, v) => o.HeadWeight = v);
        AddDriveRow(driveBox, "Pv.Vts.Drive.Camera", 0, 1.5, 0.01,
            v => v.ToString("F2"), o => o.CameraFollow, (o, v) => o.CameraFollow = v);
        body.Children.Add(driveBox);

        // 按拍触发热键（VTS 侧的动作/表情跟着节拍走）
        var bhRow = new Grid { Margin = new Thickness(18, 0, 0, 4) };
        bhRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bhRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bhRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var bhLab = new TextBlock { Text = L.T("Pv.Vts.Drive.BeatHotkey"), Style = (Style)FindResource("RowLab") };
        Grid.SetColumn(bhLab, 0);
        bhRow.Children.Add(bhLab);
        _driveHotkey = new ComboBox { FontSize = 12, Margin = new Thickness(8, 0, 6, 0) };
        _driveHotkey.SelectionChanged += (_, _) => ApplyBeatHotkey();
        Grid.SetColumn(_driveHotkey, 1);
        bhRow.Children.Add(_driveHotkey);
        _driveEvery = new ComboBox { FontSize = 12, Width = 84 };
        _driveEvery.SelectionChanged += (_, _) => ApplyBeatHotkey();
        Grid.SetColumn(_driveEvery, 2);
        bhRow.Children.Add(_driveEvery);
        body.Children.Add(bhRow);

        body.Children.Add(new TextBlock
        {
            Text = L.T("Pv.Vts.Drive.Hint"),
            Style = (Style)FindResource("HintXs"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(18, 0, 0, 8),
        });

        body.Children.Add(new TextBlock
        {
            Text = L.T("Pv.Vts.Hotkey"),
            Style = (Style)FindResource("RowLab"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        var hkRow = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        hkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hkRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _vtsHotkeys = new ComboBox
        {
            FontSize = 12,
            Margin = new Thickness(0, 0, 6, 0),
        };
        _vtsHotkeys.SelectionChanged += (_, _) =>
        {
            var st = St;
            if (st == null || _vtsSyncing) return;
            st.Pv.AutoHotkeyId = (_vtsHotkeys.SelectedItem as VtsHotkey)?.HotkeyId ?? "";
            st.SaveDebounced();
        };
        hkRow.Children.Add(_vtsHotkeys);
        _vtsFire = new Button
        {
            Style = (Style)FindResource("BtnGhostSm"),
            Content = L.T("Pv.Vts.Fire"),
        };
        _vtsFire.Click += (_, _) =>
        {
            if (_vtsHotkeys.SelectedItem is VtsHotkey h) Vts?.TriggerHotkey(h.HotkeyId);
        };
        Grid.SetColumn(_vtsFire, 1);
        hkRow.Children.Add(_vtsFire);
        body.Children.Add(hkRow);

        _vtsAuto = new CheckBox
        {
            Content = new TextBlock { Text = L.T("Pv.Vts.AutoOnSong"), FontSize = 12 },
            Margin = new Thickness(0, 0, 0, 2),
        };
        _vtsAuto.Checked += (_, _) => SaveVtsFlags();
        _vtsAuto.Unchecked += (_, _) => SaveVtsFlags();
        body.Children.Add(_vtsAuto);

        body.Children.Add(new TextBlock
        {
            Text = L.T("Pv.Vts.Hint"),
            Style = (Style)FindResource("HintXs"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 276,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 6, 0, 0),
        });

        _vtsCard = new Border { Style = (Style)FindResource("SrcCard"), Child = body };
        VtsHost.Children.Add(_vtsCard);
        vts.Changed += OnVtsChanged; // 工作线程 → Dispatcher 回 UI
    }

    private void AddVtsRow(Panel host, string labelKey, double min, double max, double step,
        Func<double, string> fmt, int index)
    {
        var row = new Row { Fmt = fmt, Store = _ => { } };
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        var lab = new TextBlock { Text = L.T(labelKey), Style = (Style)FindResource("RowLab") };
        Grid.SetColumn(lab, 0);
        grid.Children.Add(lab);
        row.Sl = new Slider
        {
            Style = (Style)FindResource("FxSlider"),
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            LargeChange = step * 10,
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Sl.ValueChanged += (_, _) =>
        {
            if (_vtsSyncing) return;
            row.Val.Text = row.Fmt(row.Sl.Value);
            SendVtsFrame();
        };
        Grid.SetColumn(row.Sl, 1);
        grid.Children.Add(row.Sl);
        row.Val = new TextBlock
        {
            Style = (Style)FindResource("MonoMut"),
            FontSize = 11,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(row.Val, 2);
        grid.Children.Add(row.Val);
        host.Children.Add(grid);
        _vtsRows.Add(row);
    }

    /// <summary>直接驱动的一行滑块：写进 Pv.VtsDrive（驱动线程每拍读，改即生效）。</summary>
    private void AddDriveRow(Panel host, string labelKey, double min, double max, double step,
        Func<double, string> fmt, Func<VtsDriveOptions, double> get, Action<VtsDriveOptions, double> set)
    {
        var row = new Row
        {
            Fmt = fmt,
            Store = v =>
            {
                var st = St;
                if (st == null) return;
                set(st.Pv.VtsDrive, v);
                st.Pv.VtsDrive.Revision++;
                st.SaveDebounced();
            },
        };
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        var lab = new TextBlock { Text = L.T(labelKey), Style = (Style)FindResource("RowLab") };
        Grid.SetColumn(lab, 0);
        grid.Children.Add(lab);
        row.Sl = new Slider
        {
            Style = (Style)FindResource("FxSlider"),
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            LargeChange = step * 10,
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        row.Sl.ValueChanged += (_, _) =>
        {
            if (_vtsSyncing) return;
            row.Val.Text = row.Fmt(row.Sl.Value);
            row.Store(row.Sl.Value);
        };
        Grid.SetColumn(row.Sl, 1);
        grid.Children.Add(row.Sl);
        row.Val = new TextBlock
        {
            Style = (Style)FindResource("MonoMut"),
            FontSize = 11,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(row.Val, 2);
        grid.Children.Add(row.Val);
        host.Children.Add(grid);
        _driveRows.Add(row);
    }

    /// <summary>按拍热键改动 → 落盘（下拉第一项是「不触发」）。</summary>
    private void ApplyBeatHotkey()
    {
        var st = St;
        if (st == null || _vtsSyncing) return;
        var d = st.Pv.VtsDrive;
        d.BeatHotkeyId = (_driveHotkey.SelectedItem as VtsHotkey)?.HotkeyId ?? "";
        d.BeatHotkeyEvery = EveryValues[Math.Max(0, _driveEvery.SelectedIndex)];
        d.Revision++;
        st.SaveDebounced();
    }

    private static readonly int[] EveryValues = [1, 2, 4, 8, 16];

    /// <summary>总开关改动 → 落盘（驱动线程下一拍就按新状态跑）。</summary>
    private void ApplyDrive()
    {
        ApplyVtsDriveFlag();
        PullDriveRows();
    }

    private void ApplyVtsDriveFlag()
    {
        var st = St;
        if (st == null || _vtsSyncing) return;
        st.Pv.VtsDrive.Enabled = _driveEnable.IsChecked == true;
        st.Pv.VtsDrive.Revision++;
        st.SaveDebounced();
    }

    /// <summary>设置 → 直接驱动控件（滑块正被拖的那一行不动）。</summary>
    private void PullDriveRows()
    {
        var st = St;
        if (st == null || _driveEnable == null) return;
        var d = st.Pv.VtsDrive;
        _driveEnable.IsChecked = d.Enabled;
        var vals = new[] { d.Bounce, d.Sway, d.SwayRotation, d.MouthWeight, d.HeadWeight, d.CameraFollow };
        for (var i = 0; i < _driveRows.Count && i < vals.Length; i++)
        {
            var row = _driveRows[i];
            if (row.Sl.IsMouseCaptureWithin) continue;
            var v = Math.Clamp(vals[i], row.Sl.Minimum, row.Sl.Maximum);
            row.Sl.Value = v;
            row.Val.Text = row.Fmt(v);
        }

        // 按拍热键：候选 = 当前模型热键（第一项「不触发」），拍数档 1/2/4/8/16
        var keys = Vts?.Hotkeys ?? [];
        if (_driveHotkey.ItemsSource is not IReadOnlyList<VtsHotkey> cur || !cur.SequenceEqual(keys))
        {
            var items = new List<VtsHotkey> { new("", L.T("Pv.Vts.Drive.BeatHotkeyNone"), "", "") };
            items.AddRange(keys);
            _driveHotkey.ItemsSource = items;
        }

        if (_driveEvery.Items.Count == 0)
            _driveEvery.ItemsSource = EveryValues.Select(n => L.F("Pv.Vts.Drive.EveryFmt", n)).ToList();
        var everyIdx = Math.Max(0, Array.IndexOf(EveryValues, d.BeatHotkeyEvery));
        if (_driveEvery.SelectedIndex != everyIdx) _driveEvery.SelectedIndex = everyIdx;

        var wantKey = (_driveHotkey.ItemsSource as IReadOnlyList<VtsHotkey>)?
            .FirstOrDefault(k => k.HotkeyId == d.BeatHotkeyId);
        if (!ReferenceEquals(_driveHotkey.SelectedItem, wantKey)) _driveHotkey.SelectedItem = wantKey;
        _driveHotkey.IsEnabled = keys.Count > 0;
    }

    /// <summary>四个滑块 → 一次 MoveModel（客户端自己合并高频拖动，这里只管拼当前值）。</summary>
    private void SendVtsFrame()
    {
        if (_vtsRows.Count < 4) return;
        Vts?.MoveModel(
            _vtsRows[0].Sl.Value,
            _vtsRows[1].Sl.Value,
            _vtsRows[2].Sl.Value,
            _vtsRows[3].Sl.Value,
            seconds: 0.12);
    }

    private void SetVtsRows(double size, double x, double y, double rot)
    {
        if (_vtsRows.Count < 4) return;
        var vals = new[] { size, x, y, rot };
        for (var i = 0; i < 4; i++)
        {
            _vtsRows[i].Sl.Value = Math.Clamp(vals[i], _vtsRows[i].Sl.Minimum, _vtsRows[i].Sl.Maximum);
            _vtsRows[i].Val.Text = _vtsRows[i].Fmt(_vtsRows[i].Sl.Value);
        }
    }

    private void SaveVtsFlags()
    {
        var st = St;
        if (st == null || _vtsSyncing) return;
        st.Pv.AutoHotkeyOnSong = _vtsAuto.IsChecked == true;
        st.SaveDebounced();
    }

    private void OnVtsChanged() => Dispatcher.BeginInvoke(PullVts);

    /// <summary>VTS 状态/取景/热键 → 卡片（工作线程触发，已回 UI 线程）。</summary>
    private void PullVts()
    {
        var vts = Vts;
        if (_vtsCard == null || vts == null) return;
        var st = St;
        if (st == null) return;

        _vtsSyncing = true;
        try
        {
            (string Text, string BrushKey) state = vts.State switch
            {
                VtsState.Ready => (L.F("Pv.Vts.StateOn", vts.Version), "Cyan"),
                VtsState.AwaitApproval => (L.T("Pv.Vts.StateApproving"), "Amber"),
                VtsState.NeedAuth => (L.T("Pv.Vts.StateNeedAuth"), "Amber"),
                _ => (vts.LastError.Length > 0 ? L.T("Pv.Vts.StateError") : L.T("Pv.Vts.StateOff"), "Mut"),
            };
            _vtsState.Text = state.Text;
            _vtsState.Foreground = (Brush)FindResource(state.BrushKey);
            _vtsState.ToolTip = vts.LastError.Length > 0 ? vts.LastError : null;
            _vtsModel.Text = vts.ModelName.Length > 0
                ? L.F("Pv.Vts.Model", vts.ModelName)
                : "";
            _vtsAuth.IsEnabled = vts.State is VtsState.NeedAuth or VtsState.Disconnected;

            // 取景回填：用户正拖着的滑块不动（否则松手会被 VTS 回读顶回去一格）
            if (vts.Framing is { } f)
            {
                var vals = new[] { f.Size, f.X, f.Y, f.Rotation };
                for (var i = 0; i < _vtsRows.Count && i < vals.Length; i++)
                {
                    var row = _vtsRows[i];
                    if (row.Sl.IsMouseCaptureWithin) continue;
                    var v = Math.Clamp(vals[i], row.Sl.Minimum, row.Sl.Maximum);
                    row.Sl.Value = v;
                    row.Val.Text = row.Fmt(v);
                }
            }

            var keys = vts.Hotkeys;
            if (_vtsHotkeys.ItemsSource is not IReadOnlyList<VtsHotkey> cur || !cur.SequenceEqual(keys))
            {
                _vtsHotkeys.ItemsSource = keys;
                _vtsHotkeys.SelectedItem = keys.FirstOrDefault(k => k.HotkeyId == st.Pv.AutoHotkeyId);
            }

            _vtsHotkeys.IsEnabled = keys.Count > 0;
            _vtsFire.IsEnabled = _vtsHotkeys.SelectedItem is VtsHotkey;
            _vtsAuto.IsChecked = st.Pv.AutoHotkeyOnSong;
            PullDriveRows();
        }
        finally
        {
            _vtsSyncing = false;
        }
    }

    // ---------------- 内部类型 ----------------

    /// <summary>一路源的卡片（控件 + 它绑定的参数对象）。</summary>
    private sealed class Card
    {
        public int Index;
        public LiveLayerOptions Opt = null!;
        public Border Root = null!;
        public CheckBox CkEnable = null!;
        public ComboBox CbName = null!;
        public TextBlock TblState = null!;
        public CheckBox CkMirror = null!;
        public CheckBox CkCover = null!;
        public CheckBox CkPremul = null!;
        public readonly List<Row> Rows = new();
        public bool Syncing;
    }

    /// <summary>一行参数（滑块 + 回显 + 写入目标）。</summary>
    private sealed class Row
    {
        public Slider Sl = null!;
        public TextBlock Val = null!;
        public Func<double, string> Fmt = null!;
        public Action<double> Store = null!;
    }
}
