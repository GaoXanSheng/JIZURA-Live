using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JizuraLive.LegacyEngine;
using JizuraLive.Logging;
using JizuraLive.Pv;
using JizuraLive.App.I18n;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private bool _stylePanelBuilt;
    private bool _techPanelBuilt;
    private bool _presetSync;
    private DispatcherTimer? _presetDebounce;

    private readonly List<(Slider Slider, Func<double> Get)> _presetSliders = new();
    private readonly List<(Wpf.Ui.Controls.ToggleSwitch Toggle, Func<bool> Get)> _presetToggles = new();
    private readonly List<(ComboBox Combo, Func<string> Get)> _presetCombos = new();
    private readonly List<(TextBox Box, Func<string> Get)> _presetColorBoxes = new();
    private readonly Dictionary<string, Border> _themeSwatches = new(StringComparer.Ordinal);
    private readonly List<TechCategory> _techCategories = new();

    private sealed class TechCategory
    {
        public string Group = "";
        public Expander Expander = null!;
        public TextBlock Header = null!;
        public StackPanel List = null!;
        public WrapPanel Cards = null!;
        public List<(string Key, string Label)> Items = new();
        public List<(string Key, string Label, Grid Row, CheckBox Box)> Rows = new();
        public bool Built;
        public string BuiltFilter = "";
        public int BuiltPreviewVersion = -1;
    }

    private LegacyPresetSettings Preset => _project.Preset;

    private void EnsureStylePanel()
    {
        if (_stylePanelBuilt) return;
        EngineBoot.InstallPacks();
        _stylePanelBuilt = true;
        BuildThemeGrid();
        BuildPresetRows();
        SyncPresetControls();
    }

    private void EnsureTechPanel()
    {
        if (_techPanelBuilt) return;
        EngineBoot.InstallPacks();
        Preset.EnsureEnabled();
        _techPanelBuilt = true;
        BuildTechPackToggles();
        BuildTechLists();
        SyncPresetControls();
        ApplyTechFilter();
    }

    private void BuildThemeGrid()
    {
        ThemeGrid.Children.Clear();
        _themeSwatches.Clear();

        foreach (var style in Reg.Styles.Values)
        {
            var scheme = style.Schemes.FirstOrDefault();
            var bg = scheme?.Str("bg") ?? "#222222";
            var fg = scheme?.Str("fg") ?? "#ffffff";
            var accent = scheme?.Str("accent") ?? "#ffcc00";

            var tile = new StackPanel
            {
                Width = 82,
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand,
                ToolTip = ThemeTooltip(style),
            };

            var swatch = new Border
            {
                Height = 54,
                CornerRadius = new CornerRadius(6),
                Background = BrushFromHex(bg),
                BorderThickness = new Thickness(2),
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, CardStrokeKey);

            var sample = new TextBlock
            {
                Text = L.T("字面"),
                FontSize = 19,
                FontWeight = FontWeights.Bold,
                Foreground = BrushFromHex(fg),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var bar = new Border
            {
                Height = 3,
                Margin = new Thickness(8, 0, 8, 6),
                CornerRadius = new CornerRadius(1.5),
                Background = BrushFromHex(accent),
                VerticalAlignment = VerticalAlignment.Bottom,
            };
            var layer = new Grid();
            layer.Children.Add(sample);
            layer.Children.Add(bar);
            swatch.Child = layer;

            var name = new TextBlock
            {
                Text = style.Name,
                FontSize = 11,
                Margin = new Thickness(0, 3, 0, 0),
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            name.SetResourceReference(TextBlock.ForegroundProperty, TextSecondaryKey);

            tile.Children.Add(swatch);
            tile.Children.Add(name);
            tile.MouseLeftButtonDown += (_, _) => ApplyTheme(style.Key);
            ThemeGrid.Children.Add(tile);
            _themeSwatches[style.Key] = swatch;
        }

        SyncThemeCards();
    }

    private void ApplyTheme(string key)
    {
        _project.LegacyStyle = key;
        _scene.LyricStyleName = LegacyStyleToLyric(key);
        SyncLyricStylePicker();
        SyncThemeCards();
        PresetChanged();
        RefreshTechPreviews();
        TblStatus.Text = L.F("主题风格：{0}（正在重建…）", ThemeLabel(key));
    }

    private void RefreshTechPreviews()
    {
        foreach (var category in _techCategories)
            if (category.Built && category.Expander.IsExpanded)
                EnsureCategoryBuilt(category);
    }

    private void SyncThemeCards()
    {
        var current = _project.LegacyStyle ?? "";
        foreach (var (key, swatch) in _themeSwatches)
        {
            if (string.Equals(key, current, StringComparison.OrdinalIgnoreCase))
                swatch.SetResourceReference(Border.BorderBrushProperty, TextAccentKey);
            else
                swatch.SetResourceReference(Border.BorderBrushProperty, CardStrokeKey);
        }

        if (TblThemeName is not null) TblThemeName.Text = L.T("当前主题：") + ThemeLabel(current);
    }

    private static string ThemeLabel(string? key)
    {
        if (!string.IsNullOrWhiteSpace(key) && Reg.Styles.TryGetValue(key, out var style)
            && !string.IsNullOrWhiteSpace(style.Name))
            return style.Name;
        return key ?? "";
    }

    private static string ThemeTooltip(StyleDef style)
    {
        if (ZhLabels.Descriptions.TryGetValue("style." + style.Key, out var desc) && !string.IsNullOrWhiteSpace(desc))
            return style.Name + "\n" + desc;
        return style.Name;
    }

    private void BuildPresetRows()
    {
        _presetSliders.Clear();
        FxSliderHost.Children.Clear();

        AddSlider(L.T("动态强度"), () => Preset.Motion, v => Preset.Motion = v);
        AddSlider(L.T("故障效果"), () => Preset.Glitch, v => Preset.Glitch = v);
        AddSlider(L.T("色彩错位"), () => Preset.Chroma, v => Preset.Chroma = v);
        AddSlider(L.T("装饰量"), () => Preset.Decor, v => Preset.Decor = v);
        AddSlider(L.T("镜头密度"), () => Preset.Density, v => Preset.Density = v);
        AddSlider(L.T("质感"), () => Preset.Texture, v => Preset.Texture = v);
        AddSlider(L.T("背景切换"), () => Preset.BgSwitch, v => Preset.BgSwitch = v);

        _presetToggles.Clear();
        FxToggleHost.Children.Clear();

        AddToggle(L.T("统一感"), () => Preset.Unify, v => Preset.Unify = v);
        AddToggle(L.T("文字整列"), () => Preset.Typeset, v => Preset.Typeset = v);
        AddToggle(L.T("闪光"), () => Preset.Flash, v => Preset.Flash = v);
        AddToggle(L.T("隐藏装饰编号"), () => Preset.HideNo, v => Preset.HideNo = v);
        AddToggle(L.T("隐藏装饰时间"), () => Preset.HideTime, v => Preset.HideTime = v);

        TxtPresetSeed.LostFocus -= SeedBox_LostFocus;
        TxtPresetSeed.LostFocus += SeedBox_LostFocus;
        TxtPresetSeed.KeyDown -= SeedBox_KeyDown;
        TxtPresetSeed.KeyDown += SeedBox_KeyDown;

        CmbKoma.SelectionChanged -= CmbKoma_SelectionChanged;
        CmbKoma.SelectionChanged += CmbKoma_SelectionChanged;
        CmbHud.SelectionChanged -= CmbHud_SelectionChanged;
        CmbHud.SelectionChanged += CmbHud_SelectionChanged;
        CmbKeyBg.SelectionChanged -= CmbKeyBg_SelectionChanged;
        CmbKeyBg.SelectionChanged += CmbKeyBg_SelectionChanged;

        BuildStyleExtras();
    }

    private void BuildStyleExtras()
    {
        StyleExtraHost.Children.Clear();
        _presetCombos.Clear();
        _presetColorBoxes.Clear();

        StyleExtraHost.Children.Add(ComboRow(L.T("歌词语言"), new[]
        {
            (L.T("自动识别"), "auto"), (L.T("简体中文"), "zh-Hans"), (L.T("繁體中文"), "zh-Hant"),
            (L.T("日本語"), "ja"), (L.T("한국어"), "ko"), ("English", "en"),
        }, () => Preset.Lang, v =>
        {
            Preset.Lang = v;
            PresetChanged();
        }));

        StyleExtraHost.Children.Add(ComboRow(L.T("标题字体"), FontOptions(), () => Preset.FontDisplay, v =>
        {
            Preset.FontDisplay = v;
            PresetChanged();
        }));
        StyleExtraHost.Children.Add(ComboRow(L.T("衬线字体"), FontOptions(), () => Preset.FontSerif, v =>
        {
            Preset.FontSerif = v;
            PresetChanged();
        }));
        StyleExtraHost.Children.Add(ComboRow(L.T("正文字体"), FontOptions(), () => Preset.FontBody, v =>
        {
            Preset.FontBody = v;
            PresetChanged();
        }));

        AddToggleTo(StyleExtraHost, L.T("启用自定义颜色"), () => Preset.ColorsEnabled, v =>
        {
            Preset.ColorsEnabled = v;
            PresetChanged();
        });
        StyleExtraHost.Children.Add(ColorRow(L.T("背景色"), () => Preset.ColorBg,
            "#101014", v =>
            {
                Preset.ColorBg = v;
                PresetChanged();
            }));
        StyleExtraHost.Children.Add(ColorRow(L.T("文字色"), () => Preset.ColorFg,
            "#f5eeea", v =>
            {
                Preset.ColorFg = v;
                PresetChanged();
            }));
        StyleExtraHost.Children.Add(ColorRow(L.T("次级文字"), () => Preset.ColorSub,
            "#bdb6b2", v =>
            {
                Preset.ColorSub = v;
                PresetChanged();
            }));

        AddToggleTo(StyleExtraHost, L.T("自定义强调色"), () => Preset.AccentOn, v =>
        {
            Preset.AccentOn = v;
            PresetChanged();
        });
        StyleExtraHost.Children.Add(ColorRow(L.T("强调色"), () => Preset.ColorAccent,
            "#f5a50c", v =>
            {
                Preset.ColorAccent = v;
                PresetChanged();
            }));
        StyleExtraHost.Children.Add(ColorRow(L.T("错位色 A"), () => Preset.ColorGhostA,
            "#f5a50c", v =>
            {
                Preset.ColorGhostA = v;
                PresetChanged();
            }));
        StyleExtraHost.Children.Add(ColorRow(L.T("错位色 B"), () => Preset.ColorGhostB,
            "#16f4d4", v =>
            {
                Preset.ColorGhostB = v;
                PresetChanged();
            }));
    }

    private static IEnumerable<(string Label, string Key)> FontOptions()
    {
        yield return (L.T("风格默认"), "");
        foreach (var (key, face) in JFonts.Table.OrderBy(p => p.Value.Label, StringComparer.CurrentCulture))
            yield return (string.IsNullOrWhiteSpace(face.Label) ? key : face.Label, key);
    }

    private FrameworkElement ComboRow(string label, IEnumerable<(string Label, string Key)> options,
        Func<string> get, Action<string> set)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var text = ThemeText(label, TextSecondaryKey, 12);
        Grid.SetColumn(text, 0);
        row.Children.Add(text);

        var combo = new ComboBox { FontSize = 12, Margin = new Thickness(0, 1, 0, 1) };
        foreach (var (itemLabel, key) in options)
            combo.Items.Add(new ComboBoxItem { Content = itemLabel, Tag = key });

        var current = get() ?? "";
        var selected = combo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, current, StringComparison.Ordinal));
        combo.SelectedItem = selected ?? combo.Items[0];
        combo.SelectionChanged += (_, _) =>
        {
            if (_presetSync || combo.SelectedItem is not ComboBoxItem { Tag: string key }) return;
            if (string.Equals(key, get(), StringComparison.Ordinal)) return;
            set(key);
        };
        Grid.SetColumn(combo, 1);
        row.Children.Add(combo);
        _presetCombos.Add((combo, get));
        return row;
    }

    private FrameworkElement ColorRow(string label, Func<string> get, string fallback, Action<string> set)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });

        var text = ThemeText(label, TextSecondaryKey, 12);
        Grid.SetColumn(text, 0);
        row.Children.Add(text);

        var value = string.IsNullOrWhiteSpace(get()) ? fallback : get();
        var box = new Wpf.Ui.Controls.TextBox
        {
            Text = value,
            FontSize = 12,
            FontFamily = MonoFontFamily,
            Height = 30,
            Padding = new Thickness(6, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var swatch = new Border
        {
            Width = 20,
            Height = 18,
            CornerRadius = new CornerRadius(3),
            Background = BrushFromHex(value),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = Cursors.Hand,
            ToolTip = L.T("打开拾色器"),
        };
        swatch.SetResourceReference(Border.BorderBrushProperty, CardStrokeKey);
        swatch.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var current = box.Text.Trim();
            if (!IsHexColor(current)) current = fallback;
            Dispatcher.BeginInvoke(new Action(() =>
                ColorPickerPopup.Show(swatch, current,
                    hex => box.Text = hex,
                    hex => swatch.Background = BrushFromHex(hex))));
        };

        box.TextChanged += (_, _) =>
        {
            if (_presetSync) return;
            var hex = box.Text.Trim();
            if (!IsHexColor(hex)) return;
            swatch.Background = BrushFromHex(hex);
            if (!string.Equals(hex, get(), StringComparison.OrdinalIgnoreCase)) set(hex);
        };
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        Grid.SetColumn(swatch, 2);
        row.Children.Add(swatch);
        _presetColorBoxes.Add((box, get));
        return row;
    }

    private void AddSlider(string label, Func<double> get, Action<double> set)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });

        var text = ThemeText(label, TextSecondaryKey, 12);
        Grid.SetColumn(text, 0);
        row.Children.Add(text);

        var valueText = ThemeValue(((int)Math.Round(get() * 100)).ToString(CultureInfo.InvariantCulture));
        Grid.SetColumn(valueText, 2);
        row.Children.Add(valueText);

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            SmallChange = 1,
            IsMoveToPointEnabled = true,
            Value = Math.Clamp(get() * 100, 0, 100),
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, e) =>
        {
            if (_presetSync) return;
            set(e.NewValue / 100.0);
            valueText.Text = ((int)Math.Round(e.NewValue)).ToString(CultureInfo.InvariantCulture);
            if (!slider.IsMouseCaptureWithin) PresetChanged();
        };
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);

        _presetSliders.Add((slider, get));
        FxSliderHost.Children.Add(row);
    }

    private void AddToggle(string label, Func<bool> get, Action<bool> set) =>
        AddToggleTo(FxToggleHost, label, get, set);

    private void AddToggleTo(Panel host, string label, Func<bool> get, Action<bool> set)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = ThemeText(label, TextSecondaryKey, 12);
        Grid.SetColumn(text, 0);
        row.Children.Add(text);

        var toggle = new Wpf.Ui.Controls.ToggleSwitch
        {
            IsChecked = get(),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        toggle.Checked += (_, _) =>
        {
            if (_presetSync) return;
            set(true);
            PresetChanged();
        };
        toggle.Unchecked += (_, _) =>
        {
            if (_presetSync) return;
            set(false);
            PresetChanged();
        };
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);

        _presetToggles.Add((toggle, get));
        host.Children.Add(row);
    }

    private void SyncPresetControls()
    {
        if (!_stylePanelBuilt && !_techPanelBuilt) return;
        _presetSync = true;
        try
        {
            foreach (var (slider, get) in _presetSliders)
                slider.Value = Math.Clamp(get() * 100, 0, 100);
            foreach (var (toggle, get) in _presetToggles)
                toggle.IsChecked = get();

            if (TxtPresetSeed is not null)
                TxtPresetSeed.Text = _project.LegacySeed.ToString(CultureInfo.InvariantCulture);

            SelectComboTag(CmbKoma, Preset.Koma > 0 ? Preset.Koma.ToString(CultureInfo.InvariantCulture) : "0");
            SelectComboTag(CmbHud, string.IsNullOrWhiteSpace(Preset.Hud) ? "auto" : Preset.Hud);
            SelectComboTag(CmbKeyBg, string.IsNullOrWhiteSpace(Preset.KeyBg) ? "off" : Preset.KeyBg);

            foreach (var (combo, get) in _presetCombos) SelectComboTag(combo, get() ?? "");
            foreach (var (box, get) in _presetColorBoxes) box.Text = get() ?? "";

            SyncThemeCards();
        }
        finally
        {
            _presetSync = false;
        }
    }

    private static void SelectComboTag(ComboBox combo, string tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }

        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void BuildTechPackToggles()
    {
        TechPackHost.Children.Clear();
        AddPackToggle(L.T("新增手法"), () => Preset.Extra, v => Preset.Extra = v);
        AddPackToggle(L.T("和风手法"), () => Preset.Wa, v => Preset.Wa = v);
        AddPackToggle(L.T("文字排版手法"), () => Preset.Typo, v => Preset.Typo = v);
        AddPackToggle(L.T("动态文字手法"), () => Preset.Kinetic, v => Preset.Kinetic = v);
        AddPackToggle(L.T("恐怖手法"), () => Preset.Horror, v => Preset.Horror = v);
    }

    private void AddPackToggle(string label, Func<bool> get, Action<bool> set)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = ThemeText(label, TextSecondaryKey, 12);
        Grid.SetColumn(text, 0);
        row.Children.Add(text);

        var toggle = new Wpf.Ui.Controls.ToggleSwitch { IsChecked = get() };
        toggle.Checked += (_, _) =>
        {
            if (_presetSync) return;
            set(true);
            PresetChanged();
        };
        toggle.Unchecked += (_, _) =>
        {
            if (_presetSync) return;
            set(false);
            PresetChanged();
        };
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);

        _presetToggles.Add((toggle, get));
        TechPackHost.Children.Add(row);
    }

    private static readonly string[] TechGroups =
        ["layout", "enter", "hold", "exit", "decor", "treat", "bg", "cam", "trans", "fx"];

    private void BuildTechLists()
    {
        TechHost.Children.Clear();
        _techCategories.Clear();

        foreach (var group in TechGroups)
        {
            var keys = Reg.OrderOf(group);
            if (keys.Count == 0) continue;

            var header = ThemeText("", TextSecondaryKey, 12);
            header.FontWeight = FontWeights.SemiBold;
            var expander = new Expander
            {
                Header = header,
                IsExpanded = false,
                Margin = new Thickness(0, 2, 0, 2),
            };

            var list = new StackPanel { Margin = new Thickness(8, 4, 0, 4) };
            var cards = new WrapPanel();
            var category = new TechCategory
            {
                Group = group,
                Expander = expander,
                Header = header,
                List = list,
                Cards = cards,
            };

            var tools = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
            tools.Children.Add(TechBulkButton(L.T("全部开启"), category, 1));
            tools.Children.Add(TechBulkButton(L.T("全部关闭"), category, 0));
            tools.Children.Add(TechBulkButton(L.T("反转"), category, 2));
            list.Children.Add(tools);
            list.Children.Add(cards);

            foreach (var key in keys)
                category.Items.Add((key, TechLabel(group, key)));

            expander.Content = list;
            expander.Expanded += (_, _) => EnsureCategoryBuilt(category);
            TechHost.Children.Add(expander);
            _techCategories.Add(category);
        }

        RefreshTechHeaders();
    }

    private void EnsureCategoryBuilt(TechCategory category)
    {
        if (category.Built
            && string.Equals(category.BuiltFilter, _techFilter, StringComparison.Ordinal)
            && category.BuiltPreviewVersion == TechniquePreview.Version) return;

        if (category.Built)
        {
            category.Cards.Children.Clear();
            category.Rows.Clear();
        }

        category.Built = true;
        category.BuiltFilter = _techFilter;
        category.BuiltPreviewVersion = TechniquePreview.Version;

        foreach (var (key, label) in category.Items)
        {
            if (!MatchesFilter(label)) continue;

            var check = new CheckBox
            {
                IsChecked = IsTechEnabled(category.Group, key),
                Visibility = Visibility.Collapsed,
            };
            var desc = TechDescription(category.Group, key);
            check.Checked += (_, _) => SetTechEnabled(category.Group, key, true);
            check.Unchecked += (_, _) => SetTechEnabled(category.Group, key, false);

            var placeholder = ThemeText(L.T("预览"), TextTertiaryKey, 9);
            placeholder.HorizontalAlignment = HorizontalAlignment.Center;
            placeholder.VerticalAlignment = VerticalAlignment.Center;

            var thumb = new Border
            {
                Width = 74,
                Height = 42,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1a)),
                ClipToBounds = true,
                Child = placeholder,
            };
            thumb.SetResourceReference(Border.BorderBrushProperty, CardStrokeKey);

            var mark = new TextBlock
            {
                Text = "✓",
                FontSize = 12,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 1, 4, 0),
                IsHitTestVisible = false,
            };
            mark.SetResourceReference(TextBlock.ForegroundProperty, TextAccentKey);

            var thumbLayer = new Grid { Width = 74, Height = 42 };
            thumbLayer.Children.Add(thumb);
            thumbLayer.Children.Add(mark);

            var text = ThemeText(label, TextSecondaryKey, 10, wrap: true);
            text.TextAlignment = TextAlignment.Center;
            text.MaxWidth = 74;
            text.Margin = new Thickness(0, 3, 0, 0);

            var stack = new StackPanel { Width = 74 };
            stack.Children.Add(thumbLayer);
            stack.Children.Add(text);

            var card = new Grid
            {
                Width = 82,
                Margin = new Thickness(0, 0, 4, 6),
                Cursor = Cursors.Hand,
                ToolTip = string.IsNullOrWhiteSpace(desc) ? label : desc,
            };
            card.Children.Add(stack);

            void Sync()
            {
                var on = check.IsChecked == true;
                thumb.Opacity = on ? 1.0 : 0.4;
                thumb.SetResourceReference(Border.BorderBrushProperty, on ? SelectStrokeKey : CardStrokeKey);
                mark.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            }

            check.Checked += (_, _) => Sync();
            check.Unchecked += (_, _) => Sync();
            Sync();

            card.MouseEnter += (_, _) => EnsureTechniquePreview(thumb, category.Group, key, 74);
            card.MouseLeftButtonDown += (_, _) => check.IsChecked = check.IsChecked != true;
            category.Cards.Children.Add(card);
            category.Rows.Add((key, label, card, check));
        }
    }

    private bool IsTechEnabled(string group, string key) =>
        Preset.Enabled.TryGetValue(group, out var map) && map.TryGetValue(key, out var on) ? on : true;

    private FrameworkElement TechBulkButton(string text, TechCategory category, int mode)
    {
        var button = new Wpf.Ui.Controls.Button
        {
            Content = text,
            FontSize = 11,
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 4, 0),
        };
        button.Click += (_, _) => SetCategoryEnabled(category, mode);
        return button;
    }

    private void SetCategoryEnabled(TechCategory category, int mode)
    {
        if (!Preset.Enabled.TryGetValue(category.Group, out var map))
            Preset.Enabled[category.Group] = map = new Dictionary<string, bool>(StringComparer.Ordinal);

        foreach (var (key, _) in category.Items)
        {
            map[key] = mode switch
            {
                1 => true,
                2 => !IsTechEnabled(category.Group, key),
                _ => false,
            };
        }

        if (category.Built)
        {
            _presetSync = true;
            try
            {
                foreach (var row in category.Rows)
                    if (map.TryGetValue(row.Key, out var on)) row.Box.IsChecked = on;
            }
            finally
            {
                _presetSync = false;
            }
        }

        RefreshTechHeaders();
        PresetChanged();
    }

    private static string TechDescription(string group, string key) =>
        ZhLabels.Descriptions.TryGetValue(group + "." + key, out var desc) ? desc : "";

    private void SetTechEnabled(string group, string key, bool on)
    {
        if (_presetSync) return;
        if (!Preset.Enabled.TryGetValue(group, out var map))
            Preset.Enabled[group] = map = new Dictionary<string, bool>(StringComparer.Ordinal);
        map[key] = on;
        RefreshTechHeaders();
        PresetChanged();
    }

    private void RefreshTechHeaders()
    {
        var enabled = 0;
        var total = 0;
        foreach (var category in _techCategories)
        {
            var on = category.Items.Count(item => IsTechEnabled(category.Group, item.Key));
            category.Header.Text = _techFilter.Length == 0
                ? L.F("{0}   {1}/{2}", TechGroupLabel(category.Group), on, category.Items.Count)
                : L.F("{0}   {1} 项匹配", TechGroupLabel(category.Group), MatchCount(category));
            enabled += on;
            total += category.Items.Count;
        }

        if (TblTechCount is not null) TblTechCount.Text = $"{enabled}/{total}";
    }

    private string _techFilter = "";

    internal void TxtTechFilter_TextChanged(object sender, TextChangedEventArgs e)
    {
        _techFilter = TxtTechFilter.Text.Trim();
        ApplyTechFilter();
    }

    private bool MatchesFilter(string label) =>
        _techFilter.Length == 0 || label.Contains(_techFilter, StringComparison.OrdinalIgnoreCase);

    private int MatchCount(TechCategory category) =>
        category.Items.Count(item => MatchesFilter(item.Label));

    private void ApplyTechFilter()
    {
        var totalMatches = 0;
        foreach (var category in _techCategories)
        {
            var matches = MatchCount(category);
            totalMatches += matches;
            category.Expander.Visibility = matches > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (category.Built && category.Expander.IsExpanded) EnsureCategoryBuilt(category);
        }

        RefreshTechHeaders();

        if (_techFilter.Length == 0 || totalMatches is <= 0 or > 40) return;
        foreach (var category in _techCategories)
        {
            if (MatchCount(category) > 0) category.Expander.IsExpanded = true;
        }
    }

    private static string TechGroupLabel(string group) => group switch
    {
        "layout" => L.T("布局"),
        "enter" => L.T("入场"),
        "hold" => L.T("保持"),
        "exit" => L.T("退场"),
        "decor" => L.T("装饰"),
        "treat" => L.T("文字处理"),
        "bg" => L.T("背景"),
        "cam" => L.T("运镜"),
        "trans" => L.T("转场"),
        "fx" => L.T("特效"),
        _ => group,
    };

    private static string TechLabel(string group, string key)
    {
        IRegEntry? entry = group switch
        {
            "layout" => Reg.Layouts.GetValueOrDefault(key),
            "enter" => Reg.Enter.GetValueOrDefault(key),
            "hold" => Reg.Hold.GetValueOrDefault(key),
            "exit" => Reg.Exit.GetValueOrDefault(key),
            "decor" => Reg.Decor.GetValueOrDefault(key),
            "treat" => Reg.Treat.GetValueOrDefault(key),
            "bg" => Reg.Bg.GetValueOrDefault(key),
            "cam" => Reg.Camera.GetValueOrDefault(key),
            "trans" => Reg.Trans.GetValueOrDefault(key),
            "fx" => Reg.Fxe.GetValueOrDefault(key),
            _ => null,
        };
        if (!string.IsNullOrWhiteSpace(entry?.Name)) return entry.Name!;
        return ZhLabel(group, key);
    }

    internal void CmbKoma_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_presetSync || CmbKoma.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        Preset.Koma = int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        PresetChanged();
    }

    internal void CmbHud_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_presetSync || CmbHud.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        Preset.Hud = tag;
        PresetChanged();
    }

    internal void CmbKeyBg_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_presetSync || CmbKeyBg.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        Preset.KeyBg = tag;
        _saveError = SaveProjectNow();
        TblStatus.Text = tag switch
        {
            "black" => L.T("背景：黑幕"),
            "green" => L.T("背景：绿幕"),
            _ => L.T("背景：常规"),
        };
        Log.Info($"[风格] 背景切换：{tag}");
        PresetChanged();
    }

    private void SeedBox_LostFocus(object sender, RoutedEventArgs e) => ApplySeedText();

    private void SeedBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        ApplySeedText();
        e.Handled = true;
    }

    private void ApplySeedText()
    {
        if (!int.TryParse(TxtPresetSeed.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seed)) return;
        if (seed == _project.LegacySeed) return;
        _project.LegacySeed = seed;
        PresetChanged();
    }

    internal void BtnReseedPreset_Click(object sender, RoutedEventArgs e)
    {
        _project.LegacySeed = Random.Shared.Next(1, int.MaxValue);
        TxtPresetSeed.Text = _project.LegacySeed.ToString(CultureInfo.InvariantCulture);
        PresetChanged();
    }

    private void PresetChanged()
    {
        if (_presetSync) return;
        TechniquePreview.Invalidate();
        _saveError = SaveProjectNow();
        TblStatus.Text = L.T("预设已修改，正在重新生成…");

        _presetDebounce ??= CreatePresetDebounce();
        _presetDebounce.Stop();
        _presetDebounce.Start();
    }

    private DispatcherTimer CreatePresetDebounce()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_pipe is not null)
            {
                _rebuildPipeKeepTransport = true;
                _rebuildPipe = true;
            }
        };
        return timer;
    }
}
