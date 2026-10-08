using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using JizuraLive.App.Controls;
using JizuraLive.Engine;
using JizuraLive.Logging;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace JizuraLive.App.Views;

// 风格面板（对位 StyleTab.vue + LyricsPanel 的语言下拉）：
// 风格卡片墙（Reg.Styles，192×108 缩略图 canvas，选中 Amber 描边 + 1px 琥珀外环）、语言、
// 字体三角色（display/serif/body，选项 = JFonts.Table）、本机/文件字体、强调色与背景文字色。
// 样式类改动一律 State.Mutate(look: true)（走观感历史栈）。
public partial class StyleTab : UserControl
{
    private bool _built;
    private bool _syncing;

    private EditorState S => App.MainInstance!.State;

    // 色板两组（对位 BASE_KEYS / ACCENT_KEYS）
    private static readonly (string K, string Label)[] AccentKeys =
        { ("accent", "强调色"), ("ghostA", "错位色A"), ("ghostB", "错位色B") };

    private static readonly (string K, string Label)[] BaseKeys =
        { ("bg", "背景"), ("fg", "文字"), ("sub", "辅助") };

    // 套装徽章（对位 ui-helpers 的 SET_UI：徽标字 / 悬浮说明 / 描边与字色 = .badge-set.set-*）
    private static readonly Dictionary<string, (string Text, string Title, string Border, string Fg)> SetBadge = new()
    {
        ["horror"] = ("恐", "恐怖", "#C0544A", "#E0786E"),
        ["typo"] = ("文", "文字排版", "#9AA3AD", "#C5CCD3"),
        ["kinetic"] = ("动", "动态文字", "#7FBF6A", "#9FD98A"),
    };

    private readonly Dictionary<string, TextBox> _hexBoxes = new();
    private readonly Dictionary<string, string> _hexFlag = new();

    public StyleTab()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_built)
        {
            _built = true;
            BuildColorRow(AccentColorRows, AccentKeys, "accentOn");
            BuildColorRow(BaseColorRows, BaseKeys, "enabled");
            BuildCards();
        }
        S.StateChanged += OnState;
        Pull();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => S.StateChanged -= OnState;

    private void OnState() => Dispatcher.Invoke(Pull);

    // ---------------- 风格卡片 ----------------

    private void BuildCards()
    {
        var cards = new List<Button>();
        foreach (var key in JStyles.StyleOrder)
        {
            if (!Reg.Styles.TryGetValue(key, out var st)) continue;
            var sc0 = st.Schemes.FirstOrDefault();

            var card = new Button { Style = (Style)Resources["StyleCard"], Tag = key };

            // 缩略图：192×108 逻辑画布（对位 Vue canvas 的 width/height 与 paintGrid 的全部坐标），
            // Viewbox 按卡片宽度等比缩放 → 16:9（canvas w-full aspect-video bg-ink）
            var canvas = new Canvas { Width = 192, Height = 108, Background = Brush(sc0?.Str("bg"), "#0C0C0E") };
            // scheme 角标（schemes[1..3] 的 bg 色块 14×10，贴右上角）
            for (var i = 1; i < st.Schemes.Count && i <= 3; i++)
            {
                var chip = new Rectangle { Width = 14, Height = 10, Fill = Brush(st.Schemes[i].Str("bg"), "#222226") };
                Canvas.SetLeft(chip, 192 - 14 * i);
                canvas.Children.Add(chip);
            }
            // 「字面」三层错位（ghostB 偏 (-3,-1) / ghostA 偏 (+3,+2) / fg 居中）
            var glyphs = new Grid { Width = 192, Height = 108 };
            glyphs.Children.Add(Glyph(sc0?.Str("ghostB"), new Thickness(0, 0, 6, 2)));
            glyphs.Children.Add(Glyph(sc0?.Str("ghostA"), new Thickness(6, 4, 0, 0)));
            glyphs.Children.Add(Glyph(sc0?.Str("fg"), new Thickness(0)));
            canvas.Children.Add(glyphs);
            // accent 条（12,90 起 30×4）
            var bar = new Rectangle { Width = 30, Height = 4, Fill = Brush(sc0?.Str("accent"), "#F5A50C") };
            Canvas.SetLeft(bar, 12);
            Canvas.SetTop(bar, 90);
            canvas.Children.Add(bar);
            // 风格键（大写 mono 9px，左起 48，垂直中心 y=93）
            var keyTx = new TextBlock
            {
                Text = key.ToUpperInvariant(),
                FontSize = 9,
                FontFamily = (FontFamily)FindResource("MonoFont"),
                LineHeight = 12,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = Brush(sc0?.Str("sub"), "#888888"),
            };
            Canvas.SetLeft(keyTx, 48);
            Canvas.SetTop(keyTx, 87);
            canvas.Children.Add(keyTx);

            // 徽标浮层（absolute top-1 left-1：压在缩略图上，不随缩略图缩放）
            var badges = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(4, 4, 0, 0),
            };
            if (Flag(st.Extra, "extra")) badges.Children.Add(Badge("新增", "最初公开版之后追加", "#16F4D4", "#16F4D4"));
            if (Flag(st.Extra, "wa")) badges.Children.Add(Badge("和", "和风特效", "#F5A50C", "#F5A50C"));
            if (st.Extra != null && st.Extra.TryGetValue("set", out var setTok) && setTok.Type == JTokenType.String
                && SetBadge.TryGetValue((string)setTok, out var sb))
                badges.Children.Add(Badge(sb.Text, sb.Title, sb.Border, sb.Fg));

            var thumbBox = new Grid();
            var thumb = new Border
            {
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                ClipToBounds = true,
                Background = Brush(sc0?.Str("bg"), "#0C0C0E"),
                Child = new Viewbox { Stretch = Stretch.Uniform, Child = canvas },
            };
            // aspect-video：卡片宽度定高（宽 × 9/16），Viewbox 负责把 192×108 的设计画布装进去
            thumb.SizeChanged += (_, ev) =>
            {
                var h = ev.NewSize.Width * 9.0 / 16.0;
                if (double.IsNaN(thumb.Height) || Math.Abs(thumb.Height - h) > 0.5) thumb.Height = h;
            };
            thumbBox.Children.Add(thumb);
            if (badges.Children.Count > 0) thumbBox.Children.Add(badges);

            var stack = new StackPanel();
            stack.Children.Add(thumbBox);
            // 名称行（flex items-center gap-1 text-[11px] px-1.5 pb-1.5 truncate）
            stack.Children.Add(new TextBlock
            {
                Text = st.Name,
                FontSize = 11,
                LineHeight = 16.5,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = (Brush)FindResource("Fg"),
                Margin = new Thickness(6, 4, 6, 6),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            card.Content = stack;
            card.Click += StyleCard_Click;
            cards.Add(card);
            StyleGrid.Children.Add(card);
        }

        // 3 列排布 + gap-2 间隙（末列右侧、末行下侧不留）
        for (var i = 0; i < cards.Count; i++)
        {
            var row = i / 3;
            while (StyleGrid.RowDefinitions.Count <= row)
                StyleGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(cards[i], row);
            Grid.SetColumn(cards[i], i % 3);
            cards[i].Margin = new Thickness(0, 0, i % 3 == 2 ? 0 : 8, row == (cards.Count - 1) / 3 ? 0 : 8);
        }
    }

    /// <summary>缩略图里的「字面」（对位 canvas fillText 46px；错位偏移用 Margin 折算）。</summary>
    private static TextBlock Glyph(string? color, Thickness offset) => new()
    {
        Text = "字面",
        FontSize = 46,
        FontWeight = FontWeights.Bold,
        LineHeight = 60,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        Foreground = Brush(color, "#888888"),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = offset,
    };

    /// <summary>缩略图徽标（.badge-set：10px 描边小牌 + 半透明黑底）。</summary>
    private JzBadge Badge(string text, string title, string border, string fg) => new()
    {
        Text = text,
        Style = (Style)Resources["CardBadge"],
        BorderBrush = Brush(border, "#F5A50C"),
        Foreground = Brush(fg, "#F5A50C"),
        ToolTip = title,
    };

    /// <summary>随机能不能选中（off 注记，对位 rebuildStyles 的 offNote）。</summary>
    private static string OffNote(StyleDef st, Project p)
    {
        if (Reg.RandomAllows(p, "style", st.Key)) return "";
        if (Flag(st.Extra, "extra") && p.Extra != true)
            return "（新增特效未开启，一键随机不会选中）";
        if (st.Extra != null && st.Extra.TryGetValue("set", out var setTok) && setTok.Type == JTokenType.String
            && !SetOn(p, (string)setTok))
            return "（该套装未开启，一键随机不会选中）";
        return "（和风特效未开启，一键随机不会选中）";
    }

    /// <summary>J.setOn：套装开关（horror/typo/kinetic，typed 字段直读）。</summary>
    private static bool SetOn(Project p, string set) => set switch
    {
        "horror" => p.Horror,
        "typo" => p.Typo,
        "kinetic" => p.Kinetic,
        _ => true,
    };

    private static bool Flag(Dictionary<string, JToken>? extra, string k)
        => extra != null && extra.TryGetValue(k, out var v) && v.Type == JTokenType.Boolean && (bool)v;

    private void StyleCard_Click(object sender, RoutedEventArgs e)
    {
        var key = (string)((Button)sender).Tag!;
        if (S.Project.Style == key) return;
        S.Mutate(p =>
        {
            p.Style = key;
            p.Colors["enabled"] = false; // pickStyle：换风格把背景文字色覆盖关掉
        }, "风格：" + Reg.Styles[key].Name, look: true);
    }

    // ---------------- 语言 / 字体 ----------------

    private void SyncLang()
    {
        if (CbLang.Items.Count == 0)
            foreach (var kv in JLang.LangLabel)
                CbLang.Items.Add(new ComboBoxItem { Content = kv.Value, Tag = kv.Key });
        var lang = JLang.LangLabel.ContainsKey(S.Project.Lang) ? S.Project.Lang : "auto";
        SelectTag(CbLang, lang);
    }

    private void CbLang_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || CbLang.SelectedItem is not ComboBoxItem it) return;
        var v = (string)it.Tag!;
        if (v == S.Project.Lang) return;
        S.Mutate(p => p.Lang = v, "歌词语言", look: true);
    }

    private void SyncFontCombos()
    {
        SyncFontCombo(CbFontDisplay, "display");
        SyncFontCombo(CbFontSerif, "serif");
        SyncFontCombo(CbFontBody, "body");
    }

    private void SyncFontCombo(ComboBox cb, string role)
    {
        if (cb.Items.Count != JFonts.Table.Count + 1)
        {
            cb.Items.Clear();
            cb.Items.Add(new ComboBoxItem { Content = "风格默认", Tag = "" });
            foreach (var kv in JFonts.Table)
            {
                // 当前语言下的实际用脸（对位 faceOf 的 → 别名标注）
                var face = JLang.FaceOf(kv.Key);
                var alt = !string.IsNullOrEmpty(face.Label) && face.Label != kv.Value.Label ? " → " + face.Label : "";
                cb.Items.Add(new ComboBoxItem { Content = kv.Value.Label + alt, Tag = kv.Key });
            }
        }
        SelectTag(cb, S.Project.Fonts.GetValueOrDefault(role) ?? "");
    }

    private void FontRole_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || sender is not ComboBox { Name: var name } cb || cb.SelectedItem is not ComboBoxItem it) return;
        var role = name switch { "CbFontDisplay" => "display", "CbFontSerif" => "serif", _ => "body" };
        var v = (string)it.Tag!;
        if (v == (S.Project.Fonts.GetValueOrDefault(role) ?? "")) return;
        var roleLabel = role == "display" ? "标题" : role == "serif" ? "衬线体" : "正文";
        S.Mutate(p =>
        {
            if (v.Length == 0) p.Fonts.Remove(role);
            else p.Fonts[role] = v;
        }, "字体・" + roleLabel, look: true);
    }

    private void BtnAddLocalFont_Click(object sender, RoutedEventArgs e)
    {
        var name = TbLocalFont.Text.Trim();
        if (name.Length == 0) return;
        var key = "local_" + Regex.Replace(name, "\\s+", "_");
        var weight = Regex.IsMatch(name, "bold|太|black|heavy|w[6-9]|[6-9]00", RegexOptions.IgnoreCase) ? 700 : 400;
        JFonts.AddUserFont(key, name + "（本机）", name, weight);
        S.Mutate(p =>
        {
            p.UserFonts = p.UserFonts.Where(u => u.Key != key)
                .Concat(new[] { new UserFont { Key = key, Label = name + "（本机）", Family = name, Weight = weight } })
                .ToList();
            p.Fonts["display"] = key;
        }, "本机字体：" + name, look: true);
        TbLocalFont.Text = "";
    }

    private void BtnFontFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "字体文件|*.ttf;*.otf;*.woff;*.woff2", Title = "选择字体文件" };
        if (dlg.ShowDialog(Window.GetWindow(this)!) != true) return;
        try
        {
            var uf = JFonts.LoadFontFile(dlg.FileName);
            S.Mutate(p =>
            {
                p.UserFonts = p.UserFonts.Where(u => u.Key != uf.Key)
                    .Concat(new[] { new UserFont { Key = uf.Key, Label = uf.Label, Family = uf.Family, Weight = uf.Weight } })
                    .ToList();
                p.Fonts["display"] = uf.Key;
            }, "字体文件：" + uf.Label, look: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, "无法读取该字体文件", "字面 Live",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Log.Debug("字体文件加载失败：" + ex);
        }
    }

    // ---------------- 配色 ----------------

    /// <summary>三色块一行（对位 .grid.grid-cols-3.gap-2 + .field：11px mut 标签 + 色块）。</summary>
    private void BuildColorRow(Grid parent, (string K, string Label)[] keys, string flag)
    {
        for (var i = 0; i < keys.Length; i++)
            parent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        for (var i = 0; i < keys.Length; i++)
        {
            var (k, label) = keys[i];

            var cell = new StackPanel { Margin = new Thickness(0, 0, i < keys.Length - 1 ? 8 : 0, 0) };
            cell.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                LineHeight = 16.5,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Foreground = (Brush)FindResource("Mut"),
                Margin = new Thickness(0, 0, 0, 4),
            });

            var tb = new TextBox { Style = (Style)Resources["ColorBox"] };
            var k1 = k;
            tb.LostFocus += (_, _) => { if (!_syncing) CommitHex(k1, flag); };
            tb.KeyDown += (_, ev) =>
            {
                if (ev.Key == Key.Enter)
                {
                    if (!_syncing) CommitHex(k1, flag);
                    ev.Handled = true;
                }
            };
            cell.Children.Add(tb);

            Grid.SetColumn(cell, i);
            parent.Children.Add(cell);

            _hexBoxes[k] = tb;
            _hexFlag[k] = flag;
        }
    }

    private void CommitHex(string k, string flag)
    {
        if (!TryNormHex(_hexBoxes[k].Text, out var hex))
        {
            Pull(); // 非法输入回显旧值
            return;
        }
        var c = S.Project.Colors;
        var cur = c[k]?.Type == JTokenType.String ? (string)c[k] : null;
        if (string.Equals(cur, hex, StringComparison.OrdinalIgnoreCase) && Truthy(c[flag])) return;
        S.Mutate(p =>
        {
            p.Colors[k] = hex;
            if (!Truthy(p.Colors[flag])) p.Colors[flag] = true; // onColor：改色即开覆盖
        }, "配色：" + k, look: true);
    }

    private void AccentToggle_Changed(object sender, RoutedEventArgs e) => ColorToggle("accentOn", AccentKeys, "强调色覆盖");
    private void ColorToggle_Changed(object sender, RoutedEventArgs e) => ColorToggle("enabled", BaseKeys, "背景文字色覆盖");

    private void ColorToggle(string flag, (string K, string Label)[] keys, string label)
    {
        if (_syncing) return;
        var on = (flag == "accentOn" ? CkAccentOn : CkColorOn).IsChecked == true;
        S.Mutate(p =>
        {
            p.Colors[flag] = on;
            if (on)
            {
                var sc0 = (Reg.Styles.GetValueOrDefault(p.Style) ?? Reg.Styles["noir"]).Schemes.FirstOrDefault();
                if (sc0 != null)
                    foreach (var (k, _) in keys)
                        if (p.Colors[k] == null && sc0.Str(k) != null)
                            p.Colors[k] = sc0.Str(k);
            }
        }, label, look: true);
    }

    private void BtnRandomPalette_Click(object sender, RoutedEventArgs e)
    {
        var c = S.Project.Colors;
        var sc0 = (Reg.Styles.GetValueOrDefault(S.Project.Style) ?? Reg.Styles["noir"]).Schemes.FirstOrDefault();
        var bg = Truthy(c["enabled"]) && c["bg"]?.Type == JTokenType.String ? (string)c["bg"] : sc0?.Str("bg") ?? "#0a0a0c";
        string a = "", ga = "", gb = "";
        for (var guard = 0; guard < 6; guard++)
        {
            (a, ga, gb, _) = J.RandomPalette(bg);
            if (ga != (string?)c["ghostA"] || gb != (string?)c["ghostB"]) break;
        }
        S.Mutate(p =>
        {
            var pc = p.Colors;
            pc["accent"] = a;
            pc["ghostA"] = ga;
            pc["ghostB"] = gb;
            pc["accentOn"] = true;
        }, "随机配色", look: true);
    }

    // ---------------- 回刷 ----------------

    private void Pull()
    {
        if (App.MainInstance == null) return;
        _syncing = true;
        try
        {
            var p = S.Project;

            // 风格卡片选中态（Amber 描边 + 1px 琥珀外环）+ tooltip（desc + off 注记）
            foreach (var card in StyleGrid.Children.OfType<Button>())
            {
                var key = (string)card.Tag!;
                var st = Reg.Styles[key];
                var on = p.Style == key;
                card.BorderBrush = on ? (Brush)FindResource("Amber") : (Brush)FindResource("LineBrush");
                card.ApplyTemplate();
                if (card.Template?.FindName("ring", card) is Border ring)
                    ring.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                card.ToolTip = st.Extra != null && st.Extra.TryGetValue("desc", out var d)
                    ? (string)d + OffNote(st, p)
                    : OffNote(st, p);
            }

            SyncLang();
            SyncFontCombos();

            var st0 = Reg.Styles.GetValueOrDefault(p.Style) ?? Reg.Styles.GetValueOrDefault("noir");
            var sc0 = st0?.Schemes.FirstOrDefault();
            SetChecked(CkAccentOn, Truthy(p.Colors["accentOn"]));
            SetChecked(CkColorOn, Truthy(p.Colors["enabled"]));
            // 色块 = 彩色底 + 十六进制字（亮底换深字保证可读）
            foreach (var kv in _hexBoxes)
            {
                var hex = ColorOf(p.Colors, kv.Key, sc0);
                kv.Value.Text = hex;
                kv.Value.Background = Brush(hex, "#808080");
                kv.Value.Foreground = ReadableOn(hex);
                kv.Value.ToolTip = hex;
            }

            // 面板色板：当前 plan 的 scheme0（accent/ghostA/ghostB）
            var ps = S.Plan?.Style.Schemes.FirstOrDefault();
            SetSwatch(SwAccent, ps?.Str("accent"));
            SetSwatch(SwGhostA, ps?.Str("ghostA"));
            SetSwatch(SwGhostB, ps?.Str("ghostB"));
        }
        finally
        {
            _syncing = false;
        }
    }

    // ---------------- 小工具 ----------------

    /// <summary>色板小方块（w-5 h-5 rounded border-white/20，title = 色值）。</summary>
    private static void SetSwatch(Border sw, string? hex)
    {
        sw.Background = Brush(hex, "#808080");
        sw.ToolTip = hex ?? "";
    }

    /// <summary>toColorInput：取色输入值 = colors[k]（是颜色时），否则风格 scheme 缺省。</summary>
    private static string ColorOf(JObject c, string k, Scheme? sc0)
    {
        var v = c[k]?.Type == JTokenType.String ? (string)c[k]! : null;
        if (v != null && TryNormHex(v, out var hex)) return hex;
        var d = sc0?.Str(k);
        return d != null && TryNormHex(d, out var dh) ? dh : "#000000";
    }

    /// <summary>#abc / #aabbcc → #aabbcc（小写）；其余 false。</summary>
    private static bool TryNormHex(string? s, out string hex)
    {
        hex = "";
        var t = (s ?? "").Trim();
        if (!t.StartsWith('#')) return false;
        var body = t[1..];
        if (body.Length == 3 && body.All(Uri.IsHexDigit))
            body = string.Concat(body.Select(ch => new string(ch, 2)));
        if (body.Length != 6 || !body.All(Uri.IsHexDigit)) return false;
        hex = "#" + body.ToLowerInvariant();
        return true;
    }

    /// <summary>色块上的十六进制字：按亮度选前景（亮底深字 / 暗底亮字）。</summary>
    private static SolidColorBrush ReadableOn(string hex)
    {
        var c = Brush(hex, "#808080").Color;
        var lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
        var b = new SolidColorBrush(lum > 0.55 ? Color.FromRgb(0x12, 0x0C, 0x00) : Color.FromRgb(0xEC, 0xE7, 0xE1));
        b.Freeze();
        return b;
    }

    private static bool Truthy(JToken? t) => t?.Type == JTokenType.Boolean && (bool)t;

    private static void SetChecked(CheckBox cb, bool v)
    {
        if (cb.IsChecked != v) cb.IsChecked = v;
    }

    private static void SelectTag(ComboBox cb, string tag)
    {
        foreach (var i in cb.Items.OfType<ComboBoxItem>())
            if ((string)i.Tag! == tag)
            {
                if (!ReferenceEquals(cb.SelectedItem, i)) cb.SelectedItem = i;
                return;
            }
        if (cb.Items.Count > 0) cb.SelectedIndex = 0;
    }

    private static SolidColorBrush Brush(string? hex, string fallback)
    {
        try
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex ?? fallback));
            b.Freeze();
            return b;
        }
        catch
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
            b.Freeze();
            return b;
        }
    }
}
