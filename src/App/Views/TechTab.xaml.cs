using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using JizuraLive.App.Controls;
using JizuraLive.Engine;
using JizuraLive.Logging;
using Newtonsoft.Json;
using SkiaSharp;

namespace JizuraLive.App.Views;

/// <summary>
/// 手法选项卡（对位 frontend/src/components/panels/TechTab.vue + jizura/preview.ts）：
/// 套装开关（.checkblock 的 Switch）+ 名称筛选 + 10 组手法段（开关墙 / 全开全关反转 / 组锁）+ 预览缩略图。
/// 视觉树按 Vue 类名逐点对位：组段 = details.border.border-line.rounded-md.bg-raised，
/// 卡片 = 自适应网格 repeat(auto-fill,minmax(112px,1fr)) gap 8 的 label（缩略图 + 名称行徽标）。
///
/// 渲染策略（M3）：静态单帧（每组取代表性相位；Vue 版是预算制 rAF 动态循环，见「取舍」），
/// 后台单线程队列逐张出图：Task.Run 循环 + EngineRenderGate 串行（JText/JLang 静态缓存
/// 非线程安全，同 MainWindow.Stage_PaintSurface 的规矩），帧间 Thread.Sleep(6) 让路主预览。
/// 离屏 SKSurface 按尺寸保留复用、绝不 Dispose——Renderer 整帧缓存持有面的快照（宿主须知）。
/// plan 与成品位图按「渲染签名」（风格/画幅/seed/配色/字体）缓存；签名变化走 800ms 防抖全量重渲。
/// </summary>
public partial class TechTab : UserControl
{
    // ---------------- 卡片网格几何（旧 div.grid gap-2 px-2.5 pb-2.5 [minmax(112px,1fr)]） ----------------

    private const double CardGap = 8;    // gap-2
    private const double CardMinW = 112; // repeat(auto-fill,minmax(112px,1fr))
    private const double CardPad = 10;   // 网格左右内边距 px-2.5

    // ---------------- 常量（对位 Vue GROUPS / SETS / TECH_LOCK_ON） ----------------

    private static readonly (string G, string Label)[] Groups =
    {
        ("layout", "布局"), ("enter", "入场"), ("hold", "保持"), ("exit", "退场"), ("decor", "装饰"),
        ("treat", "文字处理"), ("bg", "背景"), ("cam", "运镜"), ("fx", "画面效果"), ("trans", "镜头转场"),
    };

    private const string TechLockOn = "锁定：一键生成不改变这一组的开/关";
    private const string TechLockOff = "锁定中，点击解除";

    private sealed class SetDef
    {
        public string Cls = "";
        public string Label = "";
        public string Sub = "";
        public Func<Project, bool> Get = static _ => false;
        public Action<Project, bool> Set = static (_, _) => { };
    }

    private static readonly SetDef[] Sets =
    {
        new()
        {
            Cls = "extra", Label = "使用新增特效",
            Sub = "带“新增”标记的手法、风格和字体。关闭时，一键生成和随机编排只使用首个公开版的特效",
            Get = static p => p.Extra, Set = static (p, v) => p.Extra = v,
        },
        new()
        {
            Cls = "wa", Label = "使用和风特效",
            Sub = "带“和”标记的手法和风格。关闭后，一键生成和随机编排不会选用（在新增内容的判断之后应用；逐行手动指定时仍可使用）",
            Get = static p => p.Wa, Set = static (p, v) => p.Wa = v,
        },
        new()
        {
            Cls = "typo", Label = "使用文字排版部件",
            Sub = "标有“文”的手法。关闭后，一键生成与随机重排不会选用",
            Get = static p => p.Typo, Set = static (p, v) => p.Typo = v,
        },
        new()
        {
            Cls = "kinetic", Label = "使用动态文字部件",
            Sub = "标有“动”的手法。关闭后，一键生成与随机重排不会选用",
            Get = static p => p.Kinetic, Set = static (p, v) => p.Kinetic = v,
        },
        new()
        {
            Cls = "horror", Label = "也使用恐怖特效",
            Sub = "标有“恐”的手法与风格。一键生成只在“恐怖”氛围时使用",
            Get = static p => p.Horror, Set = static (p, v) => p.Horror = v,
        },
    };

    // ---------------- UI 结构引用 ----------------

    private sealed class GroupUi
    {
        public string G = "";
        public string Label = "";
        public Border Box = null!;        // 整段外框（折叠头 + 内容）
        public Button Header = null!;
        public TextBlock Arrow = null!;
        public TextBlock Count = null!;
        public Button LockBtn = null!;
        public StackPanel Body = null!;
        public Grid Cards = null!;        // 自适应卡片网格（列数随可用宽度算，1fr 由星列拉伸）
        public bool Open;                 // 折叠状态存这里，筛选清空后还原（Vue openSet 对位）
    }

    private sealed class CardUi
    {
        public string G = "";
        public string K = "";
        public Border Face = null!;       // 缩略图面（bg = ImageBrush，圆角承担旧 overflow-hidden）
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

    // ---------------- 缩略图渲染管线 ----------------

    private sealed class Job
    {
        public string Sig = "";      // 入队时的渲染签名（变了就丢）
        public string G = "";
        public string K = "";
        public Border Img = null!;   // 回贴目标（卡片可能已被折叠卸载，回贴成孤儿无害）
        public Project Snap = null!; // UI 线程工程的只读快照（入队时成对取走，无跨线程竞态）
    }

    // 专用渲染器：与主预览（PreviewEngine.Renderer）的整帧缓存互相隔离（Vue previewR 对位）
    private readonly Renderer _thumbRenderer = new();

    // 离屏面按尺寸保留复用。宿主须知：交给 Frame 的面绝不 Dispose——
    // 整帧缓存里的 Snapshot 会跨帧引用它，提前释放下次缓存命中即 AV（同 MainWindow 墓园写法）。
    private readonly Dictionary<(int W, int H), SKSurface> _surfaces = new();

    // plan / 成品位图缓存，键 = "sig|g|k"（Vue previewPlans / canvas 内容对位），并发双端访问
    private readonly ConcurrentDictionary<string, Plan> _plans = new();
    private readonly ConcurrentDictionary<string, BitmapSource> _bmp = new();

    private readonly ConcurrentQueue<Job> _queue = new();
    private readonly AutoResetEvent _kick = new(false);
    private CancellationTokenSource? _workerCts;

    private volatile string _sig = "";   // 渲染签名：风格|画幅|seed|抠像|语言|整列|统一感|配色|字体
    private Project _snap = new();       // 与 _sig 成对发布的只读快照（仅 UI 线程写，worker 经 Job 读引用）
    private readonly DispatcherTimer _redebounce; // seed/风格等签名变化 → 800ms 防抖全量重渲

    public TechTab()
    {
        InitializeComponent();
        _redebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _redebounce.Tick += (_, _) =>
        {
            _redebounce.Stop();
            ReRenderAll();
        };
        Loaded += OnLoaded;
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
            StartWorker();
        }
        RefreshAll();   // 开关态/计数/锁（缩略图不重渲）
        ApplyFilter();  // 折叠态还原 + 已展开组补排缺图
    }

    private void OnStateChanged()
    {
        RefreshAll();
        // seed/风格/画幅/配色/字体变了 → 防抖后全量重渲（开关变化不动签名，不触发）
        if (CurrentSig() != _sig)
        {
            _redebounce.Stop();
            _redebounce.Start();
        }
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
                Text = t.Label, FontSize = 12, LineHeight = 18, Foreground = fg, TextWrapping = TextWrapping.Wrap,
            });
            text.Children.Add(new TextBlock
            {
                Text = t.Sub, FontSize = 11, LineHeight = 16.5, Foreground = mut, TextWrapping = TextWrapping.Wrap,
            });

            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                // flex-col gap-1：末行不留（容器自身的 mb-3 已在 XAML）
                Margin = i == Sets.Length - 1 ? new Thickness(0) : new Thickness(0, 0, 0, 4),
            };
            row.Children.Add(sw);
            row.Children.Add(text);
            ToolTipService.SetToolTip(row, t.Sub);
            SetsHost.Children.Add(row);
        }
    }

    private void OnSetToggle(SetDef t, bool on)
    {
        if (_syncing) return;
        var p = App.MainInstance!.State.Project;
        if (t.Get(p) == on) return;
        // Vue onSet：remember/commit（观感历史）+ rebuild + replan
        App.MainInstance!.State.Mutate(pp => t.Set(pp, on), $"套装{(on ? "开" : "关")}：{t.Label}", replan: true, look: true);
    }

    // ---------------- 组段构建（Vue details/summary 对位） ----------------

    private void BuildGroups()
    {
        foreach (var (g, label) in Groups)
        {
            var ui = new GroupUi { G = g, Label = label };

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
                Text = label, FontSize = 12, LineHeight = 18, FontWeight = FontWeights.SemiBold,
                Foreground = _brushFg, VerticalAlignment = VerticalAlignment.Center,
            };
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
            var tools = new StackPanel
            {
                Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 10, 8),
            };
            var toolDefs = new[] { ("全部开启", "on"), ("全部关闭", "off"), ("反转", "flip") };
            for (var i = 0; i < toolDefs.Length; i++)
            {
                var d = toolDefs[i];
                var b = ToolBtn(d.Item1, () => OnAll(g, d.Item2));
                if (i < toolDefs.Length - 1) b.Margin = new Thickness(0, 0, 6, 0); // gap-1.5
                tools.Children.Add(b);
            }

            // 卡片网格：列数/margin 由 BuildCards 按可用宽度摆（mb-2.5 的底间距给 2，行距走卡片自身）
            ui.Cards = new Grid { Margin = new Thickness(CardPad, 0, CardPad, 2) };

            ui.Body = new StackPanel { Visibility = Visibility.Collapsed };
            ui.Body.Children.Add(tools);
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

        if (Omakase.IsExtra(g, k)) Add("新增", "最初公开版之后追加", "BadgeEx");
        if (Omakase.IsWa(g, k)) Add("和", "和风特效", null);
        switch (Omakase.SetOf(g, k))
        {
            case "horror": Add("恐", "恐怖", "BadgeHorror"); break;
            case "typo": Add("文", "文字排版", "BadgeTypo"); break;
            case "kinetic": Add("动", "动态文字", "BadgeKinetic"); break;
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

            // 缩略图（旧 canvas w-full aspect-video bg-ink block）：位图经 ImageBrush 铺满 + 顶角圆角（overflow-hidden 对位）。
            // 底 ink 兜住未出图期，面（Face）在后台队列回贴 ImageBrush。
            var face = new Border { CornerRadius = new CornerRadius(5, 5, 0, 0) };
            var thumb = new Border
            {
                Background = _brushInk, CornerRadius = new CornerRadius(5, 5, 0, 0), Height = thumbH,
                Child = face, SnapsToDevicePixels = true,
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

            // 缓存命中直接回贴（折叠重开/筛选重建不重渲）；未命中的进后台队列
            if (_bmp.TryGetValue($"{_sig}|{ui.G}|{k}", out var cached)) face.Background = new ImageBrush(cached);

            var card = new CardUi { G = ui.G, K = k, Face = face, Chk = chk, Box = box };
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
        }, $"手法{(on ? "开" : "关")}：{g}.{k}", replan: true);
    }

    /// <summary>组级全开/全关/反转（Vue onAll）：一次事务 + 保底键。</summary>
    private void OnAll(string g, string a)
    {
        var label = _groupList.FirstOrDefault(x => x.G == g)?.Label ?? g;
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
        }, $"手法{(a == "on" ? "全开" : a == "off" ? "全关" : "反转")}：{label}", replan: true);
    }

    /// <summary>组锁（Vue toggleTechLock）：写 Locks.Tech[g]，一键生成/随机不改变该组的开/关；无需 replan。</summary>
    private void OnLock(string g)
    {
        var label = _groupList.FirstOrDefault(x => x.G == g)?.Label ?? g;
        App.MainInstance!.State.Mutate(p =>
        {
            if (p.Locks.Tech.ContainsKey(g)) p.Locks.Tech.Remove(g);
            else p.Locks.Tech[g] = true;
        }, $"锁定手法组：{label}", replan: false, look: true);
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
            : Omakase.IsExtra(card.G, card.K) && !p.Extra ? "（新增特效未开启，自动不会选中）"
                : set != null && !Omakase.SetOn(p, set) ? "（该套装未开启，自动不会选中）"
                    : "（和风特效未开启，自动不会选中）";
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
        EnqueueMissing();
    }

    // ---------------- 缩略图渲染管线 ----------------

    /// <summary>渲染签名：随风格/画幅/seed/抠像/语言/整列/统一感/配色/字体变（Vue cacheKey 超集 + seed）。</summary>
    private static string CurrentSig()
    {
        var p = App.MainInstance!.State.Project;
        return string.Join("|",
            p.Style, p.Aspect, p.Seed, p.KeyBg, p.Lang, p.Typeset, p.Unify,
            p.Colors.ToString(Formatting.None),
            JsonConvert.SerializeObject(p.Fonts),
            JsonConvert.SerializeObject(p.UserFonts));
    }

    /// <summary>
    /// 签名变化则换只读快照并清 plan/位图缓存。快照在 UI 线程 JSON 克隆（Snapshot 同款手段），
    /// worker 只经 Job 拿引用，不与 UI 线程的 Mutate 撞共享工程。
    /// </summary>
    private void EnsureSnapshot()
    {
        var sig = CurrentSig();
        if (sig == _sig) return;
        _sig = sig;
        _snap = JsonConvert.DeserializeObject<Project>(
                    JsonConvert.SerializeObject(App.MainInstance!.State.Project)) ?? new Project();
        _plans.Clear();
        _bmp.Clear();
    }

    /// <summary>把已展开组里缺位图的卡排进后台队列（去重靠 _bmp 命中检查；重复入队只是幂等重渲）。</summary>
    private void EnqueueMissing()
    {
        if (_workerCts == null) return;
        EnsureSnapshot();
        var sig = _sig;
        var snap = _snap;
        var queued = 0;
        foreach (var ui in _groupList)
        {
            if (!ui.Open || ui.Box.Visibility != Visibility.Visible) continue;
            if (!_cards.TryGetValue(ui.G, out var map)) continue;
            foreach (var (k, card) in map)
            {
                if (_bmp.ContainsKey($"{sig}|{ui.G}|{k}")) continue;
                _queue.Enqueue(new Job { Sig = sig, G = ui.G, K = k, Img = card.Face, Snap = snap });
                queued++;
            }
        }
        if (queued > 0) _kick.Set();
    }

    /// <summary>签名变化后的全量重渲（防抖 800ms 到点）；旧队列任务按签名自动作废。</summary>
    private void ReRenderAll()
    {
        EnsureSnapshot();
        EnqueueMissing();
    }

    private void StartWorker()
    {
        _workerCts = new CancellationTokenSource();
        var ct = _workerCts.Token;
        _ = Task.Run(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_queue.TryDequeue(out var job))
                {
                    try { _kick.WaitOne(500); }
                    catch (ObjectDisposedException) { return; }
                    continue;
                }
                try
                {
                    if (job.Sig != _sig) continue; // 旧签名的队列任务作废
                    var bmp = RenderOne(job);
                    if (bmp == null) continue;
                    _bmp[$"{job.Sig}|{job.G}|{job.K}"] = bmp;
                    var img = job.Img;
                    Dispatcher.BeginInvoke(() => img.Background = new ImageBrush(bmp));
                }
                catch (Exception ex)
                {
                    Log.Error($"[TechTab] 缩略图渲染失败：{job.G}.{job.K}", ex);
                }
                Thread.Sleep(6); // 预算制让路：单帧 10~40ms + 间歇 6ms，主预览（同一把 EngineRenderGate）不被饿死
            }
        }, ct);
    }

    /// <summary>
    /// 单张出图：PreviewPlan（缓存）→ 共享离屏面 Frame（静态代表帧）→ PeekPixels 拷成冻结位图。
    /// 全程持 EngineRenderGate（引擎静态缓存规矩，同 MainWindow）。失败出暗底占位（Vue 的 catch 底色）。
    /// </summary>
    private BitmapSource? RenderOne(Job job)
    {
        lock (EngineRenderGate.Lock)
        {
            var key = $"{job.Sig}|{job.G}|{job.K}";
            if (!_plans.TryGetValue(key, out var plan))
            {
                try
                {
                    plan = Planner.PreviewPlan(job.Snap, job.G, job.K);
                }
                catch (Exception ex)
                {
                    Log.Error($"[TechTab] PreviewPlan 失败：{job.G}.{job.K}", ex);
                    return DarkFrame(160, 90);
                }
                _plans[key] = plan;
                if (_plans.Count > 900) _plans.Clear(); // M5 八百卡保险丝：容量兜底清空，重建只是时间
            }

            // 画面尺寸跟 plan 走（Vue scale = cv.width / plan.W；竖帧同款裁切观感）
            const int th = 90;
            var tw = Math.Max(80, (int)Math.Round(th * (double)plan.W / plan.H));
            var surf = GetSurface(tw, th);
            try
            {
                _thumbRenderer.Frame(surf.Canvas, plan, PreviewTime(job.G, plan), new RenderOpts
                {
                    Scale = (double)tw / plan.W,
                    Fast = true,
                    NoHud = true,
                    NoGhost = job.G != "fx", // Vue preview 的 opts 原样
                });
            }
            catch (Exception ex)
            {
                Log.Warn($"[TechTab] Frame 失败（暗底占位）：{job.G}.{job.K} · {ex.Message}");
                surf.Canvas.Clear(new SKColor(19, 19, 22)); // #131316
            }

            var px = surf.PeekPixels();
            if (px == null) return null;
            var bmp = BitmapSource.Create(px.Width, px.Height, 96, 96, PixelFormats.Bgra32, null,
                px.GetPixels(), px.Height * px.RowBytes, px.RowBytes);
            bmp.Freeze(); // 后台线程产物 → 冻结后跨线程贴 UI
            if (_bmp.Count > 700) _bmp.Clear(); // M5 保险丝：70 卡 ≈ 4MB，40MB 封顶后重来
            return bmp;
        }
    }

    private BitmapSource DarkFrame(int w, int h)
    {
        var surf = GetSurface(w, h);
        surf.Canvas.Clear(new SKColor(19, 19, 22));
        var px = surf.PeekPixels()!;
        var bmp = BitmapSource.Create(px.Width, px.Height, 96, 96, PixelFormats.Bgra32, null,
            px.GetPixels(), px.Height * px.RowBytes, px.RowBytes);
        bmp.Freeze();
        return bmp;
    }

    private SKSurface GetSurface(int w, int h)
    {
        if (_surfaces.TryGetValue((w, h), out var s)) return s;
        // 离屏面按尺寸建、保留复用；进过 Frame 的面不逐张 Dispose（宿主须知，同 MainWindow 墓园）
        s = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul))
            ?? throw new OutOfMemoryException("SKSurface.Create");
        _surfaces[(w, h)] = s;
        return s;
    }

    /// <summary>静态代表帧（Vue previewTime 的单相取法）：enter 取进场中段、exit 取出场中段、
    /// trans 取转场混合中点、fx 取事件峰值后一刻，其余取切分中段（hold 相位）。</summary>
    private static double PreviewTime(string g, Plan plan)
    {
        if (plan.Cuts.Count == 0) return 0;
        var c = plan.Cuts[^1];
        switch (g)
        {
            case "enter": return c.Start + Math.Max(0.3, c.InDur) * 0.5;
            case "exit":
            {
                var od = Math.Max(0.3, c.OutDur > 0 ? c.OutDur : 0.5);
                return c.End - od * 0.5;
            }
            case "trans": return c.Start + Math.Max(0.05, c.TransDur > 0 ? c.TransDur : 0.35) * 0.5;
            case "fx": return 0.1; // PreviewPlan 把事件打在 t=0.04（衰减窗口内）
            default: return c.Start + Math.Max(1.6, c.Dur * 0.96) * 0.5;
        }
    }

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
