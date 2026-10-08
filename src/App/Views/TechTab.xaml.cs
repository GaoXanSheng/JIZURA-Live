using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using JizuraLive.App.Controls;
using JizuraLive.App.I18n;
using JizuraLive.Engine;

namespace JizuraLive.App.Views;

/// <summary>
/// 手法选项卡（对位 frontend/src/components/panels/TechTab.vue + jizura/preview.ts）：
/// 套装开关（.checkblock 的 Switch）+ 名称筛选 + 10 组手法段（开关墙 / 全开全关反转 / 组锁）+ 预览缩略图。
/// 视觉树按 Vue 类名逐点对位：组段 = details.border.border-line.rounded-md.bg-raised，
/// 卡片 = 自适应网格 repeat(auto-fill,minmax(112px,1fr)) gap 8 的 label（缩略图 + 名称行徽标）。
///
/// 缩略图渲染（对位 jizura/preview.ts）：全部交给 TechThumbPreview 共享服务——
/// 可见卡（展开组 + 在视口内）按 90ms 节拍循环出图，相位按组取循环窗口（enter/exit/trans/fx/其余），
/// 与切分替换面板共用一台专用 Renderer 与一份 plan 缓存。本类只负责
/// 「建卡 → 交可见集 → 卡面回贴」这三件事，预算与让路规矩见 TechThumbPreview。
/// </summary>
public partial class TechTab : UserControl
{
    // ---------------- 卡片网格几何（旧 div.grid gap-2 px-2.5 pb-2.5 [minmax(112px,1fr)]） ----------------

    private const double CardGap = 8;    // gap-2
    private const double CardMinW = 112; // repeat(auto-fill,minmax(112px,1fr))
    private const double CardPad = 10;   // 网格左右内边距 px-2.5

    // ---------------- 常量（对位 Vue GROUPS / SETS / TECH_LOCK_ON） ----------------

    private static readonly (string G, string LKey)[] Groups =
    {
        ("layout", "Tech.Group.Layout"), ("enter", "Tech.Group.Enter"), ("hold", "Tech.Group.Hold"), ("exit", "Tech.Group.Exit"), ("decor", "Tech.Group.Decor"),
        ("treat", "Tech.Group.Treat"), ("bg", "Tech.Group.Bg"), ("cam", "Tech.Group.Cam"), ("fx", "Tech.Group.Fx"), ("trans", "Tech.Group.Trans"),
    };

    private static string TechLockOn => L.T("Tech.LockOn");
    private static string TechLockOff => L.T("Tech.LockOff");

    private sealed class SetDef
    {
        public string Cls = "";
        public string LabelKey = "";
        public string SubKey = "";
        public Func<Project, bool> Get = static _ => false;
        public Action<Project, bool> Set = static (_, _) => { };
    }

    private static readonly SetDef[] Sets =
    {
        new()
        {
            Cls = "extra", LabelKey = "Tech.Set.Extra",
            SubKey = "Tech.Set.Extra.Sub",
            Get = static p => p.Extra, Set = static (p, v) => p.Extra = v,
        },
        new()
        {
            Cls = "wa", LabelKey = "Tech.Set.Wa",
            SubKey = "Tech.Set.Wa.Sub",
            Get = static p => p.Wa, Set = static (p, v) => p.Wa = v,
        },
        new()
        {
            Cls = "typo", LabelKey = "Tech.Set.Typo",
            SubKey = "Tech.Set.Typo.Sub",
            Get = static p => p.Typo, Set = static (p, v) => p.Typo = v,
        },
        new()
        {
            Cls = "kinetic", LabelKey = "Tech.Set.Kinetic",
            SubKey = "Tech.Set.Kinetic.Sub",
            Get = static p => p.Kinetic, Set = static (p, v) => p.Kinetic = v,
        },
        new()
        {
            Cls = "horror", LabelKey = "Tech.Set.Horror",
            SubKey = "Tech.Set.Horror.Sub",
            Get = static p => p.Horror, Set = static (p, v) => p.Horror = v,
        },
    };

    // ---------------- UI 结构引用 ----------------

    private sealed class GroupUi
    {
        public string G = "";
        public string LabelKey = "";      // 组名词库键（L.T 在构建/换语言时解析）
        public Border Box = null!;        // 整段外框（折叠头 + 内容）
        public Button Header = null!;
        public TextBlock Arrow = null!;
        public TextBlock Lab = null!;     // 组名文本（LanguageChanged 重刷）
        public TextBlock Count = null!;
        public Button LockBtn = null!;
        public StackPanel Body = null!;
        public StackPanel Tools = null!;  // 全开/全关/反转行（LanguageChanged 整行重建）
        public Grid Cards = null!;        // 自适应卡片网格（列数随可用宽度算，1fr 由星列拉伸）
        public bool Open;                 // 折叠状态存这里，筛选清空后还原（Vue openSet 对位）
    }

    private sealed class CardUi
    {
        public string G = "";
        public string K = "";
        public Image Face = null!;        // 缩略图面（Source = 服务写入的 WriteableBitmap）
        public ThumbCard? Thumb;          // 服务侧渲染卡（可见集里交给 TechThumbPreview）
        public CheckBox Chk = null!;
        public Border Box = null!;
    }

    private readonly List<GroupUi> _groupList = new();
    private readonly Dictionary<string, Dictionary<string, CardUi>> _cards = new();
    private readonly List<ToggleButton> _setChecks = new();
    private bool _syncing; // 程序回填控件时不触发反向 Mutate

    // 卡片态常用画刷（BuildGroups 时取一次，RefreshCard 逐卡刷态不再走资源查找）
    private Brush? _brushAmber;
    private Brush? _brushLine;
    private Brush? _brushInk;
    private Brush? _brushPanel;
    private Brush? _brushFg;
    private Brush? _brushMut;
    private Brush? _brushRaised;

    // ---------------- 缩略图（渲染交给 TechThumbPreview 共享服务） ----------------

    public TechTab()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    // ---------------- 生命周期 ----------------

    private bool _wired;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_wired)
        {
            _wired = true;
            var main = App.MainInstance!;
            main.State.StateChanged += () => Dispatcher.Invoke(OnStateChanged);
            CacheBrushes();
            BuildSets();
            BuildGroups();
            GroupsScroll.ScrollChanged += GroupsScroll_ScrollChanged;
            GroupsScroll.SizeChanged += GroupsScroll_SizeChanged;
            IsVisibleChanged += TechTab_IsVisibleChanged;
        }
        L.LanguageChanged += OnLanguageChanged;
        ApplyTexts();   // 开关态/计数/锁；语言可能在面板不在屏时切换过，一并重刷（内含 RefreshAll/ApplyFilter）
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        L.LanguageChanged -= OnLanguageChanged;
        TechThumbPreview.Shared.SetLive(this, Array.Empty<ThumbCard>()); // 离树后别再排帧
    }

    private void OnLanguageChanged() => Dispatcher.Invoke(ApplyTexts);

    /// <summary>
    /// 语言切换后重刷 code-behind 动态 UI：套装开关整块重建（Label/Sub/悬浮）、组名与工具行重刷，
    /// 再经 RefreshAll（锁钮悬浮/计数/卡片悬浮，词库键即时解析）与 ApplyFilter（展开组重建卡片 → 徽标字）兜底。
    /// 重建期间 _syncing 防回填触发 Mutate（同 StateChanged 回填样板）。
    /// </summary>
    private void ApplyTexts()
    {
        if (!_wired) return;
        _syncing = true;
        try
        {
            SetsHost.Children.Clear();
            _setChecks.Clear();
            BuildSets();
            foreach (var ui in _groupList)
            {
                ui.Lab.Text = L.T(ui.LabelKey);
                BuildTools(ui);
            }
        }
        finally
        {
            _syncing = false;
        }
        RefreshAll();   // 锁钮悬浮、计数、卡片态与悬浮
        ApplyFilter();  // 展开组重建卡片（徽标字随语言）
    }

    private void OnStateChanged()
    {
        RefreshAll();
        // 缩略图：SetLive 内部比对渲染签名（风格/画幅/seed/配色/字体），变了就清缓存重渲
        RefreshLive();
    }

    /// <summary>视觉树常用画刷取一次（套装开关/组段/卡片态都在刷这些键，别逐卡走资源查找）。</summary>
    private void CacheBrushes()
    {
        _brushFg = TryFindResource("Fg") as Brush;
        _brushMut = TryFindResource("Mut") as Brush;
        _brushAmber = TryFindResource("Amber") as Brush;
        _brushLine = TryFindResource("LineBrush") as Brush;
        _brushInk = TryFindResource("Ink") as Brush;
        _brushPanel = TryFindResource("Panel") as Brush;
        _brushRaised = TryFindResource("Raised") as Brush;
    }

    // ---------------- 顶部：套装开关（Vue SETS checkblocks） ----------------

    /// <summary>Vue checkblock：.switch（30×17 胶囊）+ label/small 两行文案（gap-2、gap-1、mb-3）。</summary>
    private void BuildSets()
    {
        var fg = _brushFg;
        var mut = _brushMut;
        var swStyle = (Style)FindResource("Switch");
        for (var i = 0; i < Sets.Length; i++)
        {
            var t = Sets[i];
            var sw = new ToggleButton
            {
                Style = swStyle,
                VerticalAlignment = VerticalAlignment.Top, // .checkblock items-start
            };
            sw.Checked += (_, _) => OnSetToggle(t, true);
            sw.Unchecked += (_, _) => OnSetToggle(t, false);
            _setChecks.Add(sw);

            var text = new StackPanel { Margin = new Thickness(8, 0, 0, 0) }; // gap-2
            text.Children.Add(new TextBlock
            {
                Text = L.T(t.LabelKey), FontSize = 12, LineHeight = 18, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            });
            text.Children.Add(new TextBlock
            {
                Text = L.T(t.SubKey), FontSize = 11, LineHeight = 16.5, Foreground = mut, TextWrapping = TextWrapping.Wrap,
            });

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                // flex-col gap-1：末行不留（容器自身的 mb-3 已在 XAML）
                Margin = i == Sets.Length - 1 ? new Thickness(0) : new Thickness(0, 0, 0, 4),
            };
            row.Children.Add(sw);
            row.Children.Add(text);
            ToolTipService.SetToolTip(row, L.T(t.SubKey));
            SetsHost.Children.Add(row);
        }
    }

    private void OnSetToggle(SetDef t, bool on)
    {
        if (_syncing) return;
        var p = App.MainInstance!.State.Project;
        if (t.Get(p) == on) return;
        // Vue onSet：remember/commit（观感历史）+ rebuild + replan
        App.MainInstance!.State.Mutate(pp => t.Set(pp, on),
            L.F(on ? "Tech.Mutate.SetOn" : "Tech.Mutate.SetOff", L.T(t.LabelKey)), replan: true, look: true);
    }

    // ---------------- 组段构建（Vue details/summary 对位） ----------------

    private void BuildGroups()
    {
        foreach (var (g, lkey) in Groups)
        {
            var ui = new GroupUi { G = g, LabelKey = lkey };

            // summary：before:'▸'(mut) + flex-1 组名(semibold) + mono 11px 计数 + 锁按钮
            ui.Arrow = new TextBlock
            {
                Text = "▸", Foreground = _brushMut, FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 14, 0), // before:mr-2 + gap-1.5
            };
            ui.Count = new TextBlock
            {
                Style = (Style)FindResource("MonoMut"), FontSize = 11,
                Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            };
            ui.LockBtn = new Button
            {
                Style = (Style)FindResource("BtnIcon"), Content = LockIcon(),
                VerticalAlignment = VerticalAlignment.Center,
            };
            ui.LockBtn.Click += (_, _) => OnLock(g);
            ToolTipService.SetToolTip(ui.LockBtn, TechLockOn);

            var hdrGrid = new Grid();
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdrGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hdrGrid.Children.Add(ui.Arrow);
            var lab = new TextBlock
            {
                Text = L.T(lkey), FontSize = 12, LineHeight = 18, FontWeight = FontWeights.SemiBold,
                Foreground = _brushFg, VerticalAlignment = VerticalAlignment.Center,
            };
            ui.Lab = lab;
            Grid.SetColumn(lab, 1);
            hdrGrid.Children.Add(lab);
            Grid.SetColumn(ui.Count, 2);
            hdrGrid.Children.Add(ui.Count);
            Grid.SetColumn(ui.LockBtn, 3);
            hdrGrid.Children.Add(ui.LockBtn);

            ui.Header = new Button { Style = (Style)FindResource("GroupHeaderBtn"), Content = hdrGrid };
            ui.Header.Click += (_, _) =>
            {
                ui.Open = !ui.Open;
                ApplyFilter();
            };

            // 段内工具行：全部开启 / 全部关闭 / 反转（Vue .flex.gap-1.5.px-2.5.pb-2）
            ui.Tools = new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 10, 8),
            };
            BuildTools(ui);

            // 卡片网格：列数/margin 由 BuildCards 按可用宽度摆（mb-2.5 的底间距给 2，行距走卡片自身）
            ui.Cards = new Grid { Margin = new Thickness(CardPad, 0, CardPad, 2) };

            ui.Body = new StackPanel { Visibility = Visibility.Collapsed };
            ui.Body.Children.Add(ui.Tools);
            ui.Body.Children.Add(ui.Cards);

            // 组段：border border-line rounded-md bg-raised mb-2
            ui.Box = new Border
            {
                Background = _brushRaised, BorderBrush = _brushLine, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 8),
            };
            var stack = new StackPanel();
            stack.Children.Add(ui.Header);
            stack.Children.Add(ui.Body);
            ui.Box.Child = stack;

            GroupsHost.Children.Add(ui.Box);
            _groupList.Add(ui);
        }
    }

    /// <summary>工具行内容：全部开启 / 全部关闭 / 反转（文案经 L.T，LanguageChanged 时整行重建）。</summary>
    private void BuildTools(GroupUi ui)
    {
        var g = ui.G;
        ui.Tools.Children.Clear();
        var toolDefs = new[] { ("Tech.Tool.AllOn", "on"), ("Tech.Tool.AllOff", "off"), ("Tech.Tool.Flip", "flip") };
        for (var i = 0; i < toolDefs.Length; i++)
        {
            var d = toolDefs[i];
            var b = ToolBtn(L.T(d.Item1), () => OnAll(g, d.Item2));
            if (i < toolDefs.Length - 1) b.Margin = new Thickness(0, 0, 6, 0); // gap-1.5
            ui.Tools.Children.Add(b);
        }
    }

    private Button ToolBtn(string text, Action click)
    {
        var b = new Button { Style = (Style)FindResource("BtnGhostSm"), Content = text }; // btn btn-ghost btn-sm
        b.Click += (_, _) => click();
        return b;
    }

    /// <summary>旧 svg（16 视图框：rect 锁体 + 锁梁 arc，stroke=currentColor，15px）→ Theme 几何 + Viewbox。
    /// 形式同 LineList/FxTab 的图标资源：套 15×15 Viewbox 的 16×16 Canvas，描边随按钮前景（锁定 = 琥珀）。</summary>
    private UIElement LockIcon()
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        var stroke = new Binding("Foreground")
        {
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(Button) },
        };
        foreach (var key in new[] { "IconLockBody", "IconLockPath" })
        {
            var p = new Path { StrokeThickness = 1.4 };
            if (TryFindResource(key) is Geometry geo) p.Data = geo;
            p.SetBinding(Shape.StrokeProperty, stroke);
            canvas.Children.Add(p);
        }
        return new Viewbox { Width = 15, Height = 15, Child = canvas };
    }

    // ---------------- 卡片 ----------------

    /// <summary>网格几何：列数按 auto-fill minmax(112px,1fr) 算，列宽 = 可用宽去掉列间距后平分（星列拉伸）。</summary>
    private (int Cols, double ItemW) CardMetrics()
    {
        var w = GroupsHost.ActualWidth;
        if (w <= 1) w = 346; // 未布局兜底（370 栏 − 滚动条 − 组框描边的实测可用值）
        var avail = Math.Max(140, w - 2 - CardPad * 2); // 组框 1px 描边 + 网格左右内边距
        var cols = Math.Max(1, (int)Math.Floor((avail + CardGap) / (CardMinW + CardGap)));
        return (cols, (avail - CardGap * (cols - 1)) / cols);
    }

    /// <summary>名称行徽标（旧 badgesOf）：新增 ex / 和 / 恐・文・动，各自带 title。</summary>
    private void AddBadges(StackPanel host, string g, string k)
    {
        void Add(string text, string title, string? styleKey)
        {
            var b = new JzBadge { Text = text, Margin = new Thickness(4, 0, 0, 0) }; // gap-1
            if (styleKey != null) b.Style = (Style)FindResource(styleKey);
            ToolTipService.SetToolTip(b, title);
            host.Children.Add(b);
        }

        if (Omakase.IsExtra(g, k)) Add(L.T("Tech.Badge.Extra"), L.T("Tech.Badge.Extra.Tip"), "BadgeEx");
        if (Omakase.IsWa(g, k)) Add(L.T("Tech.Badge.Wa"), L.T("Tech.Badge.Wa.Tip"), null);
        switch (Omakase.SetOf(g, k))
        {
            case "horror": Add(L.T("Tech.Badge.Horror"), L.T("Tech.Badge.Horror.Tip"), "BadgeHorror"); break;
            case "typo": Add(L.T("Tech.Badge.Typo"), L.T("Tech.Badge.Typo.Tip"), "BadgeTypo"); break;
            case "kinetic": Add(L.T("Tech.Badge.Kinetic"), L.T("Tech.Badge.Kinetic.Tip"), "BadgeKinetic"); break;
        }
    }

    /// <summary>展开内容重建（展开/筛选时调用）。折叠的组不建卡片：M5 后 800+ 卡会撑爆可视树，对位 Vue v-if 卸载。</summary>
    private int BuildCards(GroupUi ui, string q)
    {
        ui.Cards.Children.Clear();
        ui.Cards.ColumnDefinitions.Clear();
        ui.Cards.RowDefinitions.Clear();
        var map = new Dictionary<string, CardUi>();
        var p = App.MainInstance!.State.Project;
        var en = EnabledOf(p, ui.G);

        // 网格列：星列平分（1fr），列间塞定宽空列（gap-2）
        var (cols, itemW) = CardMetrics();
        for (var c = 0; c < cols; c++)
        {
            if (c > 0) ui.Cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CardGap) });
            ui.Cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        var thumbW = Math.Max(40, itemW - 2);         // 卡内宽 = 列宽 − 左右各 1px 描边
        var thumbH = Math.Round(thumbW * 9.0 / 16.0); // 旧 canvas 的 aspect-video 外框

        // 先定可见条目，再一次性备好行（行距由卡片自身的下边距承担）
        var keys = new List<string>();
        foreach (var k in Reg.OrderOf(ui.G))
        {
            var def = DefOf(ui.G, k);
            if (def == null) continue;
            if (q.Length > 0 && !(def.Name + " " + k).ToLowerInvariant().Contains(q)) continue;
            keys.Add(k);
        }
        var rows = (keys.Count + cols - 1) / cols;
        for (var r = 0; r < rows; r++)
            ui.Cards.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        for (var i = 0; i < keys.Count; i++)
        {
            var k = keys[i];
            var def = DefOf(ui.G, k)!;

            // 缩略图（旧 canvas w-full aspect-video bg-ink block）：Image 承载共享服务写入的 WriteableBitmap，
            // UniformToFill 铺满 + 顶角圆角（overflow-hidden 对位）。底 ink 兜住未出图期。
            var face = new Image
            {
                Stretch = Stretch.UniformToFill,
                SnapsToDevicePixels = true,
            };
            RenderOptions.SetBitmapScalingMode(face, BitmapScalingMode.LowQuality); // 90px 缩略图，快比细重要
            var thumb = new Border
            {
                Background = _brushInk, CornerRadius = new CornerRadius(5, 5, 0, 0), Height = thumbH,
                Child = face, SnapsToDevicePixels = true, ClipToBounds = true,
            };

            // 名称行（旧 flex items-center gap-1 text-[11px] px-1.5 pb-1.5）：勾选 + 裁切名 + 徽标
            // 勾选框模板自带 7px 内容边距，右留 -3 把 gap-1（4px）找回来
            var chk = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, -3, 0) };
            var name = new TextBlock
            {
                Text = def.Name, FontSize = 11, LineHeight = 16.5, Foreground = _brushFg,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            };
            var badges = new StackPanel
            {
                Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
            };
            AddBadges(badges, ui.G, k);

            var nameRow = new Grid { Margin = new Thickness(6, 4, 6, 6) };
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            nameRow.Children.Add(chk);
            Grid.SetColumn(name, 1);
            nameRow.Children.Add(name);
            Grid.SetColumn(badges, 2);
            nameRow.Children.Add(badges);

            // 卡（旧 label flex flex-col gap-1 p-0 border border-line rounded-md overflow-hidden bg-panel）
            var box = new Border
            {
                Background = _brushPanel, BorderBrush = _brushLine, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, CardGap),
                Cursor = Cursors.Hand,
                Child = new StackPanel { Children = { thumb, nameRow } },
            };

            var card = new CardUi { G = ui.G, K = k, Face = face, Chk = chk, Box = box };
            card.Thumb = new ThumbCard { G = ui.G, K = k, Host = face };
            map[k] = card;

            chk.Checked += (_, _) => OnCardCheck(ui.G, k, true);
            chk.Unchecked += (_, _) => OnCardCheck(ui.G, k, false);
            // Vue 的整卡 <label>：点卡任意处切换开关（CheckBox 自己吃掉点击，不会双触发）
            box.MouseLeftButtonUp += (_, _) => chk.IsChecked = chk.IsChecked != true;

            RefreshCard(card, p, en);

            Grid.SetColumn(box, i % cols * 2);
            Grid.SetRow(box, i / cols);
            ui.Cards.Children.Add(box);
        }

        _cards[ui.G] = map;
        return map.Count;
    }

    /// <summary>单卡开关（Vue onCheck）：写 Enabled + 清 Mood + replan（enabled 参与 plan 抽取）。</summary>
    private void OnCardCheck(string g, string k, bool on)
    {
        if (_syncing) return;
        App.MainInstance!.State.Mutate(p =>
        {
            if (!p.Enabled.TryGetValue(g, out var m)) p.Enabled[g] = m = new Dictionary<string, bool>();
            m[k] = on;
            p.Mood = null;
        }, L.F(on ? "Tech.Mutate.CardOn" : "Tech.Mutate.CardOff", g, k), replan: true);
    }

    /// <summary>组级全开/全关/反转（Vue onAll）：一次事务 + 保底键。</summary>
    private void OnAll(string g, string a)
    {
        var g0 = _groupList.FirstOrDefault(x => x.G == g);
        var label = g0 != null ? L.T(g0.LabelKey) : g;
        App.MainInstance!.State.Mutate(p =>
        {
            if (!p.Enabled.TryGetValue(g, out var m)) p.Enabled[g] = m = new Dictionary<string, bool>();
            foreach (var k in Reg.OrderOf(g))
            {
                var cur = !m.TryGetValue(k, out var v) || v;
                m[k] = a == "on" ? true : a == "off" ? false : !cur;
            }
            // 保底键（Vue onAll 同款）：全关后随机仍留恒可用条目
            if (g == "layout" && !Reg.OrderOf("layout").Any(k => !m.TryGetValue(k, out var v) || v)) m["center"] = true;
            if (g == "enter") m["cut"] = true;
            if (g == "exit") m["cut"] = true;
            if (g == "hold") m["still"] = true;
            if (g == "treat") m["none"] = true;
            if (g == "bg") m["none"] = true;
            if (g == "cam") m["push"] = true;
            p.Mood = null;
        }, L.F(a == "on" ? "Tech.Mutate.AllOn" : a == "off" ? "Tech.Mutate.AllOff" : "Tech.Mutate.Flip", label), replan: true);
    }

    /// <summary>组锁（Vue toggleTechLock）：写 Locks.Tech[g]，一键生成/随机不改变该组的开/关；无需 replan。</summary>
    private void OnLock(string g)
    {
        var g0 = _groupList.FirstOrDefault(x => x.G == g);
        var label = g0 != null ? L.T(g0.LabelKey) : g;
        App.MainInstance!.State.Mutate(p =>
        {
            if (p.Locks.Tech.ContainsKey(g)) p.Locks.Tech.Remove(g);
            else p.Locks.Tech[g] = true;
        }, L.F("Tech.Mutate.LockGroup", label), replan: false, look: true);
    }

    // ---------------- 状态回填（StateChanged / Loaded） ----------------

    private void RefreshAll()
    {
        var p = App.MainInstance!.State.Project;
        _syncing = true;
        try
        {
            for (var i = 0; i < Sets.Length && i < _setChecks.Count; i++)
                _setChecks[i].IsChecked = Sets[i].Get(p);

            int onAll = 0, total = 0;
            foreach (var ui in _groupList)
            {
                var en = EnabledOf(p, ui.G);
                var items = Reg.OrderOf(ui.G);
                var onN = items.Count(k => IsOn(en, k));
                total += items.Count;
                onAll += onN;
                ui.Count.Text = $"{onN}/{items.Count}";

                var locked = p.Locks.Tech.ContainsKey(ui.G);
                ui.LockBtn.Foreground = locked ? _brushAmber : _brushMut;     // 旧 :class="locked ? '!text-amber' : ''"
                ui.LockBtn.BorderBrush = locked ? _brushAmber : _brushLine;   // 同 FxTab 的锁按钮：锁定态琥珀描边
                ToolTipService.SetToolTip(ui.LockBtn, locked ? TechLockOff : TechLockOn);

                if (_cards.TryGetValue(ui.G, out var map))
                    foreach (var card in map.Values)
                        RefreshCard(card, p, en);
            }
            TblTotal.Text = $"{onAll}/{total}";
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>单卡开关态（含选中琥珀描边）+ 随机不可用（randomOk 关）半透明 + 悬停说明。调用方持有 _syncing 守卫。</summary>
    private void RefreshCard(CardUi card, Project p, Dictionary<string, bool>? en)
    {
        var on = IsOn(en, card.K);
        card.Chk.IsChecked = on;
        card.Box.BorderBrush = on ? _brushAmber : _brushLine; // 选中/开启态边框琥珀
        var off = !Reg.RandomAllows(p, card.G, card.K);
        card.Box.Opacity = off ? 0.5 : 1;

        // 悬停说明（Vue c.title）：键 +（氛围名・…）+ 随机不可用原因
        var def = DefOf(card.G, card.K);
        var tags = def is { Tags.Length: > 0 }
            ? "（" + string.Join("・", def.Tags.Select(t => Omakase.Moods.TryGetValue(t, out var m) ? m.Name : t)) + "）"
            : "";
        var set = def?.Set;
        var note = !off ? ""
            : Omakase.IsExtra(card.G, card.K) && !p.Extra ? L.T("Tech.OffNote.Extra")
                : set != null && !Omakase.SetOn(p, set) ? L.T("Tech.OffNote.Set")
                    : L.T("Tech.OffNote.Wa");
        ToolTipService.SetToolTip(card.Box, card.K + tags + note);
    }

    // ---------------- 筛选 / 折叠（Vue filter + details open） ----------------

    private void TbFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        FilterHint.Visibility = TbFilter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyFilter();
    }

    /// <summary>按筛选词重建各组可见卡片；筛选时自动展开全部组，清空后还原各自折叠态（Vue open: !!q || openSet）。</summary>
    private void ApplyFilter()
    {
        var q = TbFilter.Text.Trim().ToLowerInvariant();
        _syncing = true; // 重建时回填 CheckBox 不触发 Mutate
        try
        {
            foreach (var ui in _groupList)
            {
                var visOpen = ui.Open || q.Length > 0;
                var shown = 0;
                if (visOpen) shown = BuildCards(ui, q);
                else
                {
                    ui.Cards.Children.Clear();
                    _cards.Remove(ui.G); // 收起即卸载（Vue v-if 语义）；位图缓存保留，重开秒回
                }
                ui.Body.Visibility = visOpen ? Visibility.Visible : Visibility.Collapsed;
                ui.Arrow.Text = visOpen ? "▾" : "▸";
                ui.Box.Visibility = q.Length > 0 && shown == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
        }
        finally
        {
            _syncing = false;
        }
        RefreshLive();
    }

    // ---------------- 可见集（交给共享预览服务） ----------------

    /// <summary>
    /// 把「展开且落在视口内（±40px，对位 rootMargin）」的卡片交给 TechThumbPreview。
    /// 面板不可见（右栏停在别的页）或窗口失活时交空集——动画只画看得见的卡。
    /// 触发点：展开/筛选、滚动、尺寸变化、切页、状态变化（签名比对在服务内做）。
    /// </summary>
    private void RefreshLive()
    {
        if (!IsLoaded) return;
        var live = new List<ThumbCard>();
        if (IsVisible && Window.GetWindow(this)?.IsVisible == true)
        {
            const double margin = 40; // rootMargin: '40px 0px'
            var viewport = new Rect(0, 0, GroupsScroll.ViewportWidth, GroupsScroll.ViewportHeight);
            viewport.Inflate(0, margin);
            foreach (var ui in _groupList)
            {
                if (!ui.Open || ui.Body.Visibility != Visibility.Visible) continue;
                if (!_cards.TryGetValue(ui.G, out var map)) continue;
                foreach (var card in map.Values)
                {
                    var thumb = card.Thumb;
                    if (thumb == null || card.Box.Visibility != Visibility.Visible) continue;
                    if (card.Box.ActualHeight <= 0) continue;
                    var pos = card.Box.TransformToAncestor(GroupsScroll).Transform(new Point(0, 0));
                    if (!viewport.Contains(pos) && !viewport.Contains(new Point(pos.X, pos.Y + card.Box.ActualHeight)))
                        continue;
                    live.Add(thumb);
                }
            }
        }
        TechThumbPreview.Shared.SetLive(this, live);
    }

    private void GroupsScroll_ScrollChanged(object sender, ScrollChangedEventArgs e) => RefreshLive();

    private void GroupsScroll_SizeChanged(object sender, SizeChangedEventArgs e) => RefreshLive();

    private void TechTab_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => RefreshLive();

    // ---------------- 注册表/工程小工具 ----------------

    private static Dictionary<string, bool>? EnabledOf(Project p, string g) =>
        p.Enabled.TryGetValue(g, out var m) ? m : null;

    /// <summary>enabled 开关语义：没显式关 = 开（老工程里新手法包的条目自动可用）。</summary>
    private static bool IsOn(Dictionary<string, bool>? en, string k) => en == null || !en.TryGetValue(k, out var v) || v;

    /// <summary>组 → 条目 Def（Reg 未公开 per-group 查表，这里对位其私有 RegistryDef）。</summary>
    private static IRegEntry? DefOf(string g, string k) => g switch
    {
        "layout" => Reg.Layouts.GetValueOrDefault(k),
        "enter" => Reg.Enter.GetValueOrDefault(k),
        "hold" => Reg.Hold.GetValueOrDefault(k),
        "exit" => Reg.Exit.GetValueOrDefault(k),
        "decor" => Reg.Decor.GetValueOrDefault(k),
        "treat" => Reg.Treat.GetValueOrDefault(k),
        "bg" => Reg.Bg.GetValueOrDefault(k),
        "cam" => Reg.Camera.GetValueOrDefault(k),
        "fx" => Reg.Fxe.GetValueOrDefault(k),
        "trans" => Reg.Trans.GetValueOrDefault(k),
        _ => null,
    };
}
