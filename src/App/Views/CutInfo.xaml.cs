using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JizuraLive.App.Controls;
using JizuraLive.App.I18n;
using JizuraLive.Engine;
using Newtonsoft.Json.Linq;

namespace JizuraLive.App.Views;

// 当前切分信息行（对位 CutInfo.vue 的 chips 行）：10Hz 或切分变化时重读
// Planner.CutAt(plan, 引擎时钟)，展示 #序号 + 九个手法组 chip（组名 + 手法名）。
// is-forced（用户在替换面板钉过该组）→ 琥珀描边，语义对位 cutTechSlot/cutQuietSlot。
// 末尾「重抽 / 一键随机」= McpTools.RerollCurrentCut 的同款落点（Reg/Omakase/Mutate 直调，
// 不经过 MCP 协议层——工具实例可能没起，状态与引擎调用与 MCP 路径一致）。
// 点 chip 弹「切分手法替换面板」（对位 CutInfo.vue 的 .cut-pick）：候选卡带走动画预览
// （与手法栏共用 TechThumbPreview 服务）、搜索筛选、自动（恢复该组自动）、点卡即套用到该切分。
public partial class CutInfo : UserControl
{
    /// <summary>手法组表（对位 look.ts CHIP_GROUPS：组 → 组名的词库键（chip 前缀）→ 描边色键）。</summary>
    private static readonly (string G, string Key, string? Chip)[] Groups =
    {
        ("layout", "CutInfo.Group.Layout", "ChipL"), ("enter", "CutInfo.Group.Enter", "ChipE"),
        ("hold", "CutInfo.Group.Hold", "ChipH"), ("exit", "CutInfo.Group.Exit", "ChipX"),
        ("decor", "CutInfo.Group.Decor", null), ("treat", "CutInfo.Group.Treat", "ChipT"),
        ("bg", "CutInfo.Group.Bg", "ChipB"), ("cam", "CutInfo.Group.Cam", "ChipC"),
        ("trans", "CutInfo.Group.Trans", "ChipC"),
    };

    /// <summary>重抽（shuffle）动的组（对位 look.ts rerollCurrentCut）。</summary>
    private static readonly string[] ShuffleGroups = { "layout", "enter", "hold", "exit", "cam", "trans" };

    private int _lastIdx = -2; // -2 = 未初始化，首帧必刷
    private Brush _amber = null!;
    private Brush _panel = null!, _ink = null!, _line = null!;

    // 替换面板状态（对位 Vue 的 pick / pickCards / pickSearch）
    private string _pickG = "";
    private int _pickLine = -1, _pickK = -1;
    private string _pickSearch = "";
    private readonly List<ThumbCard> _pickCards = new();

    public CutInfo()
    {
        InitializeComponent();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => Sync();
        Loaded += (_, _) =>
        {
            _amber = (Brush)FindResource("Amber");
            _panel = (Brush)FindResource("Panel");
            _ink = (Brush)FindResource("Ink");
            _line = (Brush)FindResource("LineBrush");
            PickScroll.ScrollChanged += (_, _) => RefreshPickLive();
            IsVisibleChanged += (_, _) => RefreshPickLive();
            var st = App.MainInstance?.State;
            if (st != null) st.StateChanged += () => _lastIdx = -2; // plan 更新后强制重算
            L.LanguageChanged -= ApplyTexts;
            L.LanguageChanged += ApplyTexts;
            Sync();
            timer.Start();
        };
        Unloaded += (_, _) =>
        {
            L.LanguageChanged -= ApplyTexts;
            TechThumbPreview.Shared.SetLive(this, Array.Empty<ThumbCard>());
        };
    }

    /// <summary>换语言：chips 整行重建（-2 强制 Sync 重算）。</summary>
    private void ApplyTexts()
    {
        _lastIdx = -2;
        Sync();
    }

    private void Sync()
    {
        var state = App.MainInstance?.State;
        var preview = App.MainInstance?.Preview;
        if (state?.Plan == null || preview == null) return;
        var cut = Planner.CutAt(state.Plan, preview.CurrentTime);
        var idx = cut?.Index ?? -1;
        if (idx == _lastIdx) return;
        _lastIdx = idx;
        Rebuild(state, cut);
    }

    private void Rebuild(EditorState state, Cut? cut)
    {
        Row.Children.Clear();
        if (cut == null)
        {
            Row.Children.Add(new TextBlock
            {
                Text = L.T("CutInfo.NoCut"),
                Style = (Style)FindResource("Hint"),
                VerticalAlignment = VerticalAlignment.Center,
            });
            RefillPick(state, null, -1);
            return;
        }

        var plan = state.Plan!;
        var k = LyricCutK(plan, cut);

        // 第一个 chip：mono 的两位数序号（对位 Vue 的 label='#' 被 v-if 抑制，只留值）
        Row.Children.Add(MakeChip("", (cut.Index + 1).ToString("00"), null, forced: false, enabled: true, mono: true));
        foreach (var (g, key, chip) in Groups)
        {
            var v = GroupVal(cut, g);
            var forced = k >= 0 && IsForced(state.Project, cut.Line, k, g);
            Row.Children.Add(MakeChip(L.T(key), GroupName(g, v), chip, forced, enabled: k >= 0, mono: false,
                onClick: k >= 0 ? () => TogglePick(g) : null));
        }

        var on = k >= 0; // 非歌词切分（片头/间奏）不可重抽（对位 !items.some(it => it.enabled)）
        Row.Children.Add(MakeBtn(L.T("CutInfo.Reroll"), L.T("CutInfo.Reroll.Tip"), "BtnGhostSm", on, () => RerollCut("shuffle")));
        Row.Children.Add(MakeBtn(L.T("CutInfo.Omakase"), L.T("CutInfo.Omakase.Tip"), "BtnAccentSm", on, () => RerollCut("omakase")));

        RefillPick(state, cut, k); // 面板跟着当前切分（关着则不动）
    }

    /// <summary>一颗 chip（ctl:JzChip：mut 色组名前缀 + 手法值；is-forced 琥珀描边）。
    /// 带 onClick 的组 chip 可点开替换面板（对位 chip-clickable）。</summary>
    private JzChip MakeChip(string lead, string value, string? chip, bool forced, bool enabled, bool mono,
        Action? onClick = null)
    {
        var el = new JzChip
        {
            Lead = lead,
            Value = value,
            IsEnabled = enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (chip != null) el.Style = (Style)FindResource(chip);
        if (mono) el.FontFamily = (FontFamily)FindResource("MonoFont");
        if (forced) el.BorderBrush = _amber;
        if (onClick != null)
        {
            el.Cursor = Cursors.Hand;
            el.Click += (_, _) => onClick();
            ToolTipService.SetToolTip(el, L.T("CutInfo.Pick.Tip"));
        }
        return el;
    }

    private Button MakeBtn(string text, string tip, string styleKey, bool enabled, Action act)
    {
        var b = new Button
        {
            Content = text,
            ToolTip = tip,
            Style = (Style)FindResource(styleKey),
            IsEnabled = enabled,
            VerticalAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => act();
        return b;
    }

    // ---------------- 替换面板（对位 onChip / fillPick / onPickCard / onPickAuto） ----------------

    /// <summary>点组 chip：开/关替换面板（同组再点收起）。</summary>
    private void TogglePick(string g)
    {
        if (_pickG == g)
        {
            ClosePick();
            return;
        }
        _pickG = g;
        _pickSearch = "";
        TbPickSearch.Text = "";
        var state = App.MainInstance?.State;
        var plan = state?.Plan;
        if (state != null && plan != null)
        {
            var cut = Planner.CutAt(plan, App.MainInstance!.Preview.CurrentTime);
            var k = cut != null ? LyricCutK(plan, cut) : -1;
            if (cut != null && k >= 0)
            {
                _pickLine = cut.Line;
                _pickK = k;
                FillPick(state, cut);
                RefreshPickLive();
                return;
            }
        }
        ClosePick(); // 非歌词切分（片头/间奏）没有候选
    }

    private void ClosePick()
    {
        _pickG = "";
        _pickSearch = "";
        _pickCards.Clear();
        PickGrid.Children.Clear();
        Pick.Visibility = Visibility.Collapsed;
        RefreshPickLive();
    }

    /// <summary>面板跟随当前切分（对位 Vue refresh 里的 fillPick；切分没了就收起）。</summary>
    private void RefillPick(EditorState state, Cut? cut, int k)
    {
        if (_pickG.Length == 0) return;
        if (cut == null || k < 0)
        {
            ClosePick();
            return;
        }
        _pickLine = cut.Line;
        _pickK = k;
        FillPick(state, cut);
        RefreshPickLive();
    }

    /// <summary>标题（组名 · #序号）+ 最大高度（对位 max-h-[min(42vh,420px)]）+ 候选卡重建。</summary>
    private void FillPick(EditorState state, Cut cut)
    {
        var meta = Groups.FirstOrDefault(x => x.G == _pickG);
        TblPickTitle.Text = (meta.Key is { Length: > 0 } ? L.T(meta.Key) : _pickG) + " · #" + (cut.Index + 1).ToString("00");
        // 面板进入：只在从隐藏转显示时淡入（fillPick 会跟着当前切分被反复调用，不能每次都重播）
        if (Pick.Visibility != Visibility.Visible) JzAnim.FadeSlideIn(Pick, 4);
        Pick.Visibility = Visibility.Visible;
        var win = Window.GetWindow(this);
        if (win != null) PickScroll.MaxHeight = Math.Min(420, Math.Max(120, win.ActualHeight * 0.42));
        BuildPickCards(state.Plan!, state.Project, cut);
    }

    /// <summary>
    /// 候选卡（对位 fillPick）：当前值琥珀描边；decor/trans 前置「无」卡；搜索按手法名过滤
    /// （过滤时「无」卡不参与，同 Vue）。卡面动画走 TechThumbPreview（与手法栏同一台渲染器）。
    /// </summary>
    private void BuildPickCards(Plan plan, Project p, Cut cut)
    {
        _pickCards.Clear();
        PickGrid.Children.Clear();
        var forced = TechSlot(p, _pickLine, _pickK)[_pickG]?.ToString();
        var onKey = !string.IsNullOrEmpty(forced) ? forced : GroupVal(cut, _pickG);
        var filtering = _pickSearch.Length > 0;
        if (!filtering && _pickG is "decor" or "trans")
            PickGrid.Children.Add(MakePickCard(_pickG, "none", L.T("CutInfo.None"), null, onKey == "none", isNone: true));
        foreach (var key in Reg.OrderOf(_pickG))
        {
            var def = RegDef(_pickG, key);
            if (def == null || def.Special) continue; // special（片头/间奏类）不出候选（对位 !special）
            if (filtering && !(def.Name ?? key).ToLowerInvariant().Contains(_pickSearch)) continue;
            PickGrid.Children.Add(MakePickCard(_pickG, key, def.Name ?? key, def, onKey == key, isNone: false));
        }
        if (PickGrid.Children.Count == 0)
            PickGrid.Children.Add(new TextBlock
            {
                Text = L.T("CutInfo.Pick.Empty"),
                Style = (Style)FindResource("Hint"),
                Margin = new Thickness(2, 4, 0, 4),
            });
        _ = plan;
    }

    /// <summary>一张候选卡：缩略图（动画）+ 名称 + 徽标；琥珀描边 = 当前值。</summary>
    private FrameworkElement MakePickCard(string g, string key, string name, IRegEntry? def, bool on, bool isNone)
    {
        var stack = new StackPanel();
        stack.Children.Add(new Border
        {
            Height = 63, // 卡宽 112 的 aspect-video
            Background = _ink,
            ClipToBounds = true,
            Child = isNone
                ? new TextBlock
                {
                    Text = L.T("CutInfo.None"),
                    Foreground = _line,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }
                : BuildPickThumb(g, key),
        });

        var nameRow = new Grid { Margin = new Thickness(6, 4, 6, 6) };
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        nameRow.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 11,
            Foreground = (Brush)FindResource("Fg"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        if (def != null && Omakase.IsExtra(g, key))
        {
            var badge = new JzBadge { Text = L.T("Tech.Badge.Extra"), Margin = new Thickness(4, 0, 0, 0) };
            if (TryFindResource("BadgeEx") is Style st) badge.Style = st;
            Grid.SetColumn(badge, 1);
            nameRow.Children.Add(badge);
        }
        stack.Children.Add(nameRow);

        // 徽标只出「新增」一枚（子集/和风/排版的彩色徽标样式在手法栏局部，这里用悬浮说明补足）
        var tags = def is { Tags.Length: > 0 }
            ? "（" + string.Join("・", def.Tags.Select(t => Omakase.Moods.TryGetValue(t, out var m) ? m.Name : t)) + "）"
            : "";
        if (def?.Set is { Length: > 0 } set)
            tags += "［" + L.T(set == "horror" ? "Tech.Badge.Horror" : set == "typo" ? "Tech.Badge.Typo" : "Tech.Badge.Kinetic") + "］";

        var box = new Border
        {
            Width = 112,
            Margin = new Thickness(0, 0, 8, 8),
            Background = _panel,
            BorderBrush = on ? _amber : _line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Cursor = Cursors.Hand,
            Child = stack,
        };
        ToolTipService.SetToolTip(box, name + tags);
        box.MouseLeftButtonUp += (_, _) => ApplyPick(g, key, name);
        return box;
    }

    private UIElement BuildPickThumb(string g, string key)
    {
        var img = new Image { Stretch = Stretch.UniformToFill, SnapsToDevicePixels = true };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.LowQuality);
        _pickCards.Add(new ThumbCard { G = g, K = key, Host = img, W = 80, H = 80 });
        return img;
    }

    /// <summary>点卡：只改这个切分的这一组（key 空 = 恢复自动），随后重规划（对位 onPickCard/onPickAuto）。</summary>
    private void ApplyPick(string g, string key, string name)
    {
        var state = App.MainInstance?.State;
        if (state?.Plan == null || g.Length == 0) return;
        var line = _pickLine;
        var k = _pickK;
        state.Mutate(p => CutTechEdit.SetCutTech(p, line, k, g, key),
            key.Length == 0 ? L.F("CutInfo.Mutate.Auto", name) : L.F("CutInfo.Mutate.Pick", name),
            replan: true, look: true);
        // 面板保持开着；StateChanged → _lastIdx = -2 → Sync → RefillPick 回填（新 plan 到位后再回填一次）
    }

    private void PickSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        TbPickHint.Visibility = TbPickSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _pickSearch = TbPickSearch.Text.Trim().ToLowerInvariant();
        var state = App.MainInstance?.State;
        var plan = state?.Plan;
        if (state == null || plan == null || _pickG.Length == 0) return;
        var cut = Planner.CutAt(plan, App.MainInstance!.Preview.CurrentTime);
        if (cut == null) return;
        BuildPickCards(plan, state.Project, cut);
        RefreshPickLive();
    }

    private void PickAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_pickG.Length == 0) return;
        var name = TechName(_pickG, GroupVal(CutAtNow(), _pickG)) ?? _pickG;
        ApplyPick(_pickG, "", name);
    }

    private void PickClose_Click(object sender, RoutedEventArgs e) => ClosePick();

    /// <summary>当前播放头下的切分（面板按钮用）。</summary>
    private static Cut? CutAtNow()
    {
        var plan = App.MainInstance?.State.Plan;
        return plan == null ? null : Planner.CutAt(plan, App.MainInstance!.Preview.CurrentTime);
    }

    /// <summary>面板候选卡的可见集交给共享预览服务（面板收起/离树 → 交空集）。</summary>
    private void RefreshPickLive()
    {
        if (!IsLoaded)
        {
            TechThumbPreview.Shared.SetLive(this, Array.Empty<ThumbCard>());
            return;
        }
        TechThumbPreview.Shared.SetLive(this,
            Pick.Visibility == Visibility.Visible && IsVisible ? _pickCards : Array.Empty<ThumbCard>());
    }

    // ---------------- 重抽 / 一键随机（对位 look.ts rerollCurrentCut） ----------------

    /// <summary>重抽当前切分构成：shuffle = 六个构成组换一版；omakase = 全部组随机（避开同款）。</summary>
    private void RerollCut(string kind)
    {
        var state = App.MainInstance?.State;
        var preview = App.MainInstance?.Preview;
        if (state?.Plan == null || preview == null) return;
        var plan = state.Plan;
        var t0 = preview.CurrentTime;
        var cut = Planner.CutAt(plan, t0);
        if (cut == null) return;
        var k = LyricCutK(plan, cut);
        if (k < 0) return;
        var n = CodePointCount(Regex.Replace(cut.Text ?? "", @"\s+", ""));
        var groups = kind == "omakase" ? Groups.Select(x => x.G).ToArray() : ShuffleGroups;
        state.Mutate(p =>
        {
            foreach (var g in groups)
            {
                var allowNone = g is "decor" or "trans";
                var avoid = kind == "omakase" ? GroupVal(cut, g) : null;
                var key = PickEnabledTech(p, g, n, allowNone, avoid);
                if (key.Length > 0) CutTechEdit.SetCutTech(p, cut.Line, k, g, key);
            }
            CutTechEdit.MarkCutQuiet(p, cut.Line, k, groups, true);
        }, kind == "omakase" ? L.T("CutInfo.Mutate.Omakase") : L.T("CutInfo.Mutate.Reroll"), replan: true, look: true);
        preview.Seek(t0); // 画面留在原处（对位 seek(t0)）
        // 面板刷新交给 Sync 的 100ms 定时器：StateChanged 已把 _lastIdx 置 -2，新 plan 落地即重算。
        // （旧实现这里跟一个同步 ReplanNow，把整个重规划压在 UI 线程上——重抽按钮卡顿的元凶）
        _lastIdx = -2;
        Sync();
    }

    /// <summary>pickEnabledTech：启用中、随机允许的手法里抽一枚（对位 look.ts/McpTools）。</summary>
    private static string PickEnabledTech(Project p, string g, int n, bool allowNone, string? avoid)
    {
        p.Enabled.TryGetValue(g, out var en);
        var order = Reg.OrderOf(g);
        bool On(string key) => en == null || !en.TryGetValue(key, out var v) || v;
        var keys = order.Where(key => RegDef(g, key) is { Special: false } && On(key) && Omakase.RandomOk(p, g, key)).ToList();
        if (keys.Count == 0) keys = order.Where(key => RegDef(g, key) is { Special: false } && On(key)).ToList();
        if (g == "layout")
        {
            var fit = keys.Where(key => Reg.Layouts.TryGetValue(key, out var ld) && (ld.Fits == null || ld.Fits(n))).ToList();
            if (fit.Count > 0) keys = fit;
        }
        if (allowNone && !keys.Contains("none")) keys.Insert(0, "none");
        if (!string.IsNullOrEmpty(avoid) && keys.Count > 1) keys = keys.Where(key => key != avoid).ToList();
        if (keys.Count == 0) return "";
        return keys[Random.Shared.Next(keys.Count)];
    }

    private static int CodePointCount(string s)
    {
        var n = 0;
        for (var i = 0; i < s.Length; i++, n++)
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
        return n;
    }

    private static IRegEntry? RegDef(string g, string k) => g switch
    {
        "layout" => Reg.Layouts.GetValueOrDefault(k),
        "enter" => Reg.Enter.GetValueOrDefault(k),
        "hold" => Reg.Hold.GetValueOrDefault(k),
        "exit" => Reg.Exit.GetValueOrDefault(k),
        "decor" => Reg.Decor.GetValueOrDefault(k),
        "treat" => Reg.Treat.GetValueOrDefault(k),
        "bg" => Reg.Bg.GetValueOrDefault(k),
        "cam" => Reg.Camera.GetValueOrDefault(k),
        "trans" => Reg.Trans.GetValueOrDefault(k),
        _ => null,
    };

    // ---------------- look.ts 对位（只读部分） ----------------

    /// <summary>cutGroupVal：切分在各手法组上的当前值（缺省值对齐 JS）。</summary>
    private static string GroupVal(Cut? c, string g) => c == null ? "" : g switch
    {
        "layout" => c.Layout ?? "",
        "enter" => c.Enter ?? "",
        "hold" => c.Hold ?? "",
        "exit" => c.Exit ?? "",
        "treat" => string.IsNullOrEmpty(c.Treat) ? "none" : c.Treat,
        "bg" => string.IsNullOrEmpty(c.Bg) ? "none" : c.Bg,
        "cam" => string.IsNullOrEmpty(c.Cam) ? "push" : c.Cam,
        "trans" => c.Trans ?? "",
        "decor" => c.Decor is { Count: > 0 } ? c.Decor[0].Id ?? "" : "",
        _ => "",
    };

    /// <summary>groupName：registry 里的手法显示名，查不到原样回 key，空值 = 无。</summary>
    private static string GroupName(string g, string k)
    {
        if (string.IsNullOrEmpty(k)) return L.T("CutInfo.None");
        return TechName(g, k) ?? k;
    }

    private static string? TechName(string g, string k) => g switch
    {
        "layout" => Reg.Layouts.GetValueOrDefault(k)?.Name,
        "enter" => Reg.Enter.GetValueOrDefault(k)?.Name,
        "hold" => Reg.Hold.GetValueOrDefault(k)?.Name,
        "exit" => Reg.Exit.GetValueOrDefault(k)?.Name,
        "decor" => Reg.Decor.GetValueOrDefault(k)?.Name,
        "treat" => Reg.Treat.GetValueOrDefault(k)?.Name,
        "bg" => Reg.Bg.GetValueOrDefault(k)?.Name,
        "cam" => Reg.Camera.GetValueOrDefault(k)?.Name,
        "trans" => Reg.Trans.GetValueOrDefault(k)?.Name,
        _ => null,
    };

    /// <summary>lyricCutK：本行第几份歌词切分（special 的 title/interlude 不计）。</summary>
    private static int LyricCutK(Plan plan, Cut cut)
    {
        if (cut.Line < 0) return -1;
        var k = 0;
        foreach (var c in plan.Cuts)
        {
            if (c.Line != cut.Line) continue;
            if (Reg.Layouts.GetValueOrDefault(c.Layout)?.Special == true) continue;
            if (c.Index == cut.Index) return k;
            k++;
        }
        return -1;
    }

    /// <summary>cutTechSlot：行覆盖里钉到这份切分的组值（cutLayouts 并进 layout 槽，对齐 JS 合并顺序）。</summary>
    private static JObject TechSlot(Project p, int line, int k)
    {
        var ks = k.ToString();
        if (!p.Overrides.TryGetValue(line.ToString(), out var o)) return new JObject();
        var t = (o["cutTech"] as JObject)?[ks] as JObject ?? new JObject();
        var lay = (o["cutLayouts"] as JObject)?[ks]?.ToString();
        if (lay != null && t["layout"] == null)
        {
            var m = new JObject { ["layout"] = lay };
            foreach (var kv in t) m[kv.Key] = kv.Value;
            t = m;
        }
        return t;
    }

    /// <summary>cutQuietSlot：被静默（随机不许再动）的组。</summary>
    private static JObject QuietSlot(Project p, int line, int k)
    {
        if (!p.Overrides.TryGetValue(line.ToString(), out var o)) return new JObject();
        return (o["cutQuiet"] as JObject)?[k.ToString()] as JObject ?? new JObject();
    }

    /// <summary>is-forced：该组被用户钉过且没被静默（对位 Vue 的 forced 判定）。</summary>
    private static bool IsForced(Project p, int line, int k, string g)
    {
        var s = TechSlot(p, line, k)[g];
        if (s == null || s.Type == JTokenType.Null) return false;
        if (s.Type == JTokenType.String && string.IsNullOrEmpty((string?)s)) return false;
        return QuietSlot(p, line, k)[g] == null;
    }
}
