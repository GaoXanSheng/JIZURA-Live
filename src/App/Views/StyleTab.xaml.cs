using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using JizuraLive.Engine;
using JizuraLive.Logging;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace JizuraLive.App.Views;

// 风格面板（对位 StyleTab.vue + LyricsPanel 的语言下拉）：
// 风格卡片墙（Reg.Styles，scheme 配色缩略图，选中 Amber 描边）、语言、字体三角色
// （display/serif/body，选项 = JFonts.Table）、本机/文件字体、强调色与背景文字色。
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

    // 套装徽章（对位 ui-helpers 的 SET_UI）
    private static readonly Dictionary<string, string> SetBadge = new() { ["horror"] = "恐", ["typo"] = "文", ["kinetic"] = "动" };

    private readonly Dictionary<string, TextBox> _hexBoxes = new();
    private readonly Dictionary<string, Border> _swatches = new();
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
        foreach (var key in JStyles.StyleOrder)
        {
            if (!Reg.Styles.TryGetValue(key, out var st)) continue;
            var sc0 = st.Schemes.FirstOrDefault();

            var card = new Button
            {
                Style = (Style)Resources["StyleCard"],
                Tag = key,
                Margin = new Thickness(0, 0, 4, 4),
            };
            var stack = new StackPanel();

            // 缩略图（Vue 的 canvas：bg 底 + 右上 scheme 角标 + 「字面」三层错位 + accent 条 + key）
            var thumb = new Border
            {
                Height = 52,
                CornerRadius = new CornerRadius(5, 5, 0, 0),
                ClipToBounds = true,
                Background = Brush(sc0?.Str("bg"), "#111114"),
            };
            var gl = new Grid();
            thumb.Child = gl;

            var chips = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
            };
            for (var i = 1; i < st.Schemes.Count && i <= 3; i++)
                chips.Children.Add(new Border { Width = 11, Height = 7, Background = Brush(st.Schemes[i].Str("bg"), "#222226") });
            gl.Children.Add(chips);

            var badges = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(3, 2, 0, 0),
            };
            if (Flag(st.Extra, "extra")) badges.Children.Add(Chip("新增"));
            if (Flag(st.Extra, "wa")) badges.Children.Add(Chip("和"));
            if (st.Extra != null && st.Extra.TryGetValue("set", out var setTok) && setTok.Type == JTokenType.String
                && SetBadge.TryGetValue((string)setTok, out var badge))
                badges.Children.Add(Chip(badge));
            if (badges.Children.Count > 0) gl.Children.Add(badges);

            AddGlyph(gl, sc0?.Str("ghostB"), new Thickness(0, 0, 5, 3));
            AddGlyph(gl, sc0?.Str("ghostA"), new Thickness(5, 3, 0, 0));
            AddGlyph(gl, sc0?.Str("fg"), new Thickness(0));

            gl.Children.Add(new Border
            {
                Width = 16, Height = 3,
                Background = Brush(sc0?.Str("accent"), "#F5A50C"),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(6, 0, 0, 8),
            });
            gl.Children.Add(new TextBlock
            {
                Text = key.ToUpperInvariant(),
                FontSize = 7,
                FontFamily = new FontFamily("Consolas"),
                Foreground = Brush(sc0?.Str("sub"), "#888888"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, 0, 5),
            });

            stack.Children.Add(thumb);
            stack.Children.Add(new TextBlock
            {
                Text = st.Name,
                FontSize = 11,
                Foreground = (Brush)FindResource("Fg"),
                Margin = new Thickness(4, 2, 4, 5),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });

            card.Content = stack;
            card.Click += StyleCard_Click;
            StyleGrid.Children.Add(card);
        }
    }

    private static void AddGlyph(Grid gl, string? color, Thickness offset)
    {
        gl.Children.Add(new TextBlock
        {
            Text = "字面",
            FontSize = 24,
            FontWeight = FontWeights.Bold,
            Foreground = Brush(color, "#888888"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = offset,
        });
    }

    private static Border Chip(string text) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)),
        CornerRadius = new CornerRadius(2),
        Margin = new Thickness(0, 0, 3, 0),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 8,
            Foreground = Brushes.White,
            Margin = new Thickness(3, 0, 3, 1),
        },
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

    private void BuildColorRow(Grid parent, (string K, string Label)[] keys, string flag)
    {
        for (var i = 0; i < keys.Length; i++)
        {
            var (k, label) = keys[i];
            parent.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var lab = new TextBlock { Text = label, Foreground = (Brush)FindResource("Mut"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(lab, i);
            parent.Children.Add(lab);

            var sw = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(3),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 3, 0, 3),
            };
            Grid.SetColumn(sw, 1);
            Grid.SetRow(sw, i);
            parent.Children.Add(sw);

            var tb = new TextBox { Width = 88, Margin = new Thickness(6, 3, 0, 3), HorizontalAlignment = HorizontalAlignment.Left };
            Grid.SetColumn(tb, 2);
            Grid.SetRow(tb, i);
            parent.Children.Add(tb);

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

            parent.ColumnDefinitions.Clear();
            parent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            parent.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            parent.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            _hexBoxes[k] = tb;
            _swatches[k] = sw;
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

            // 风格卡片选中态 + tooltip（desc + off 注记）
            foreach (var card in StyleGrid.Children.OfType<Button>())
            {
                var key = (string)card.Tag!;
                var st = Reg.Styles[key];
                var on = p.Style == key;
                card.BorderBrush = on ? (Brush)FindResource("Amber") : (Brush)FindResource("LineBrush");
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
            foreach (var kv in _hexBoxes)
            {
                var hex = ColorOf(p.Colors, kv.Key, sc0);
                kv.Value.Text = hex;
                _swatches[kv.Key].Background = Brush(hex, "#808080");
            }

            // 面板色板：当前 plan 的 scheme0（accent/ghostA/ghostB）
            var ps = S.Plan?.Style.Schemes.FirstOrDefault();
            SwAccent.Background = Brush(ps?.Str("accent"), "#808080");
            SwGhostA.Background = Brush(ps?.Str("ghostA"), "#808080");
            SwGhostB.Background = Brush(ps?.Str("ghostB"), "#808080");
        }
        finally
        {
            _syncing = false;
        }
    }

    // ---------------- 小工具 ----------------

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
