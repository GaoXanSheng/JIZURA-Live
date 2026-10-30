using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using JizuraLive.LegacyEngine;
using JizuraLive.Logging;
using JizuraLive.Pv;
using JizuraLive.Render.Effects;
using JizuraLive.App.I18n;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private bool _stylePanelBuilt;
    private bool _techPanelBuilt;
    private bool _techPreviewHooked;
    private bool _fxStackBuilt;
    private bool _presetSync;
    private DispatcherTimer? _presetDebounce;

    private readonly List<(Slider Slider, Func<double> Get)> _presetSliders = new();
    private readonly List<(Action<bool> SetChecked, Func<bool> Get)> _presetToggles = new();
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
        public List<(string Key, string Label, Grid Row, CheckBox Box, Border Thumb)> Rows = new();
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
        BuildFxStack();
        BuildTechPackToggles();
        BuildTechLists();
        if (!_techPreviewHooked)
        {
            _techPreviewHooked = true;
            PanelTech.ScrollChanged += (_, _) => RefreshVisibleTechPreviews();
            PanelTech.SizeChanged += (_, _) => RefreshVisibleTechPreviews();
            PanelTech.IsVisibleChanged += (_, _) => RefreshVisibleTechPreviews();
        }
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
                Cursor = AppCursors.Hand,
                ToolTip = ThemeTooltip(style),
            };

            var swatch = new Border
            {
                Height = 48,
                CornerRadius = new CornerRadius(0),
                Background = BrushFromHex(bg),
                BorderThickness = new Thickness(1),
            };
            swatch.SetResourceReference(Border.BorderBrushProperty, CardStrokeKey);

            var sample = new TextBlock
            {
                Text = "Aa",
                FontSize = 18,
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
        TblStatus.Text = L.F("StageUi.Presets.ThemeStyleRebuilding", ThemeLabel(key));
    }

    private void RefreshTechPreviews()
    {
        foreach (var category in _techCategories)
            if (category.Built && category.Expander.IsExpanded)
                EnsureCategoryBuilt(category);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RefreshVisibleTechPreviews));
    }

    private void RefreshVisibleTechPreviews()
    {
        if (PanelTech is null) return;
        var panelVisible = PanelTech.IsVisible;
        foreach (var category in _techCategories)
        {
            if (!category.Built) continue;
            foreach (var row in category.Rows)
            {
                if (panelVisible && category.Expander.IsExpanded && IsInTechViewport(row.Thumb))
                    EnsureTechniquePreview(row.Thumb, category.Group, row.Key, 74, autoPlay: true);
                else
                    UnloadTechniquePreview(row.Thumb);
            }
        }
    }

    private bool IsInTechViewport(FrameworkElement element)
    {
        if (!element.IsVisible || element.ActualHeight <= 0) return false;
        try
        {
            var top = element.TranslatePoint(new Point(0, 0), PanelTech).Y;
            return top + element.ActualHeight > 0 && top < PanelTech.ActualHeight;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void ResetTechCategory(TechCategory category)
    {
        foreach (var row in category.Rows) UnloadTechniquePreview(row.Thumb);
        category.Cards.Children.Clear();
        category.Rows.Clear();
        category.Built = false;
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

        if (TblThemeName is not null) TblThemeName.Text = L.T("StageUi.Presets.CurrentTheme") + ThemeLabel(current);
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

        AddSlider(L.T("StageUi.Presets.MotionStrength"), () => Preset.Motion, v => Preset.Motion = v);
        AddSlider(L.T("StageUi.Presets.Glitch"), () => Preset.Glitch, v => Preset.Glitch = v);
        AddSlider(L.T("StageUi.Presets.Chroma"), () => Preset.Chroma, v => Preset.Chroma = v);
        AddSlider(L.T("StageUi.Presets.DecorAmount"), () => Preset.Decor, v => Preset.Decor = v);
        AddSlider(L.T("StageUi.Presets.CameraDensity"), () => Preset.Density, v => Preset.Density = v);
        AddSlider(L.T("StageUi.Presets.Texture"), () => Preset.Texture, v => Preset.Texture = v);
        AddSlider(L.T("StageUi.Presets.BackgroundSwitch"), () => Preset.BgSwitch, v => Preset.BgSwitch = v);

        _presetToggles.Clear();
        FxToggleHost.Children.Clear();

        AddToggle(L.T("StageUi.Presets.Unify"), () => Preset.Unify, v => Preset.Unify = v);
        AddToggle(L.T("StageUi.Presets.Typeset"), () => Preset.Typeset, v => Preset.Typeset = v);
        AddToggle(L.T("StageUi.Presets.Flash"), () => Preset.Flash, v => Preset.Flash = v);
        AddToggle(L.T("StageUi.Presets.HideDecorNumber"), () => Preset.HideNo, v => Preset.HideNo = v);
        AddToggle(L.T("StageUi.Presets.HideDecorTime"), () => Preset.HideTime, v => Preset.HideTime = v);

        TxtPresetSeed.LostFocus -= SeedBox_LostFocus;
        TxtPresetSeed.LostFocus += SeedBox_LostFocus;
        TxtPresetSeed.KeyDown -= SeedBox_KeyDown;
        TxtPresetSeed.KeyDown += SeedBox_KeyDown;

        CmbHud.SelectionChanged -= CmbHud_SelectionChanged;
        CmbHud.SelectionChanged += CmbHud_SelectionChanged;
        CmbKeyBg.SelectionChanged -= CmbKeyBg_SelectionChanged;
        CmbKeyBg.SelectionChanged += CmbKeyBg_SelectionChanged;

        BuildStyleExtras();
    }

    private void BuildFxStack()
    {
        if (!_fxStackBuilt)
        {
            CmbFxAdd.Items.Clear();
            foreach (var def in EffectCatalog.All)
                CmbFxAdd.Items.Add(new ComboBoxItem { Content = EffectName(def), Tag = def.Id });
            if (CmbFxAdd.Items.Count > 0) CmbFxAdd.SelectedIndex = 0;
            _fxStackBuilt = true;
        }

        RefreshFxStack();
    }

    internal void BtnFxAdd_Click(object sender, RoutedEventArgs e)
    {
        if (CmbFxAdd.SelectedItem is not ComboBoxItem { Tag: string defId })
        {
            TblStatus.Text = L.T("StageUi.Presets.SelectEffectFirst");
            return;
        }

        AddFxNode(defId);
    }

    private void AddFxNode(string defId)
    {
        var node = new EffectNode
        {
            DefId = defId,
            InstanceId = EffectGraph.NewInstanceId(defId),
            Source = "user",
        };

        _effects.Nodes.Add(node);
        _segmentTarget = SegmentTarget.Of(node);
        EffectsChanged();
        BuildInspector();
        RefreshTimeline();
        PushUndo();
        _saveError = SaveProjectNow();
        if (node.Windows.Any(w => w.IsValid))
            PreviewEffect(node);
        else
            TblStatus.Text = L.F("StageUi.Presets.PostEffectAdded", EffectName(node));
    }

    private void PreviewEffect(EffectNode node)
    {
        if (node.Windows.Count == 0)
        {
            if (!_audio.IsPlaying && _audio.Duration > 0)
            {
                _audio.Play();
                UpdatePlayIcon();
            }
            TblStatus.Text = L.F("StageUi.Presets.ContinuousEffectApplied", EffectName(node));
            return;
        }

        double start = 0;
        foreach (var window in node.Windows)
            if (window.IsValid)
            {
                start = Math.Max(0, window.Start);
                break;
            }

        if (_audio.Duration <= 0)
        {
            TblStatus.Text = L.F("StageUi.Presets.PostEffectAdded", EffectName(node));
            return;
        }

        StopShuttle();
        _audio.Seek(start);
        if (!_audio.IsPlaying) _audio.Play();
        UpdatePlayIcon();
        ShowStatus(L.F("StageUi.Presets.PreviewEffect", EffectName(node)), 3);
    }

    private void RefreshFxStack()
    {
        if (!_fxStackBuilt || FxStackHost is null) return;

        FxStackHost.Children.Clear();
        if (_effects.Nodes.Count == 0)
        {
            FxStackHost.Children.Add(ThemeText(L.T("StageUi.Presets.NoPostEffects"), TextTertiaryKey, 11, wrap: true));
            return;
        }

        for (var i = 0; i < _effects.Nodes.Count; i++)
        {
            var node = _effects.Nodes[i];
            if (node is not null) FxStackHost.Children.Add(FxStackRow(node, i));
        }
    }

    private FrameworkElement FxStackRow(EffectNode node, int index)
    {
        var body = new StackPanel();
        var row = new Grid { Margin = new Thickness(0) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var toggle = new Wpf.Ui.Controls.ToggleSwitch
        {
            IsChecked = node.Enabled,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 4, 0),
        };
        toggle.Checked += (_, _) => SetFxEnabled(node, true);
        toggle.Unchecked += (_, _) => SetFxEnabled(node, false);
        Grid.SetColumn(toggle, 0);
        row.Children.Add(toggle);

        var name = ThemeText(EffectName(node), TextPrimaryKey, 12);
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        name.Cursor = AppCursors.Hand;
        name.ToolTip = L.T("StageUi.Presets.ClickToEditParamsOnRight");
        name.MouseLeftButtonUp += (_, _) =>
        {
            _segmentTarget = node.Windows.Count > 0
                ? SegmentTarget.OfEffectWindow(node, 0)
                : SegmentTarget.Of(node);
            BuildInspector();
        };
        Grid.SetColumn(name, 1);
        row.Children.Add(name);

        var preview = SmallFxButton("▶");
        preview.ToolTip = L.T("StageUi.Presets.SeekToEffectWindowPreview");
        preview.Click += (_, _) => PreviewEffect(node);
        Grid.SetColumn(preview, 2);
        row.Children.Add(preview);

        var up = SmallFxButton("↑");
        up.IsEnabled = index > 0;
        up.Click += (_, _) => MoveFxNode(node, -1);
        Grid.SetColumn(up, 3);
        row.Children.Add(up);

        var down = SmallFxButton("↓");
        down.IsEnabled = index < _effects.Nodes.Count - 1;
        down.Click += (_, _) => MoveFxNode(node, 1);
        Grid.SetColumn(down, 4);
        row.Children.Add(down);

        var del = SmallFxButton("✕");
        del.Click += (_, _) => RemoveFxNode(node);
        Grid.SetColumn(del, 5);
        row.Children.Add(del);

        body.Children.Add(row);

        var targetRow = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        targetRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var targetLabel = ThemeText(L.T("StageUi.Presets.Target"), TextTertiaryKey, 11);
        Grid.SetColumn(targetLabel, 0);
        targetRow.Children.Add(targetLabel);

        bool composite = _pipe?.LegacyComposite == true;
        var target = new ComboBox { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Stretch };
        target.Items.Add(new ComboBoxItem { Content = L.T("StageUi.Presets.Global"), Tag = "" });
        foreach (var layer in _scene.Layers)
            if (layer is not null && !(composite && IsLegacyFront(layer.Id)))
                target.Items.Add(new ComboBoxItem { Content = LayerTitle(layer), Tag = layer.Id });

        ComboBoxItem? selected = null;
        foreach (var item in target.Items.OfType<ComboBoxItem>())
            if (string.Equals(item.Tag as string, node.Target, StringComparison.Ordinal))
            {
                selected = item;
                break;
            }

        if (selected is null)
        {
            selected = new ComboBoxItem { Content = node.Target, Tag = node.Target };
            target.Items.Add(selected);
        }

        target.SelectedItem = selected;
        target.SelectionChanged += (_, _) =>
        {
            if (target.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
            if (string.Equals(tag, node.Target, StringComparison.Ordinal)) return;
            node.Target = tag;
            MarkFxUser(node);
            CommitFxChange(false);
        };
        Grid.SetColumn(target, 1);
        targetRow.Children.Add(target);
        body.Children.Add(targetRow);

        var border = new Border
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(8, 6, 8, 6),
            Margin = new Thickness(0, 0, 0, 2),
            Tag = "fx",
            Child = body,
        };
        border.MouseEnter += (_, _) =>
            border.SetResourceReference(Border.BackgroundProperty, SelectFillKey);
        border.MouseLeave += (_, _) => border.Background = Brushes.Transparent;
        return border;
    }

    private static Wpf.Ui.Controls.Button SmallFxButton(string text) => new()
    {
        Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
        Content = text,
        FontSize = 11,
        Padding = new Thickness(4, 1, 4, 1),
        MinWidth = 0,
        MinHeight = 0,
        Margin = new Thickness(2, 0, 0, 0),
    };

    private static void MarkFxUser(EffectNode node)
    {
        if (!IsUserOwned(node.Source, node.Locked)) node.Source = "user";
    }

    private void SetFxEnabled(EffectNode node, bool enabled)
    {
        if (node.Enabled == enabled) return;
        node.Enabled = enabled;
        MarkFxUser(node);
        CommitFxChange(false);
    }

    private void MoveFxNode(EffectNode node, int delta)
    {
        var index = _effects.Nodes.IndexOf(node);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _effects.Nodes.Count) return;
        (_effects.Nodes[index], _effects.Nodes[target]) = (_effects.Nodes[target], _effects.Nodes[index]);
        MarkFxUser(node);
        CommitFxChange(true);
    }

    private void RemoveFxNode(EffectNode node)
    {
        if (!_effects.Nodes.Remove(node)) return;
        if (node.InstanceId.StartsWith(AutoDirector.AutoPrefix, StringComparison.Ordinal))
            AddAutoTombstone("fx:" + node.InstanceId);
        _evaluator.RemoveCurvesForTarget(node.InstanceId);
        if (ReferenceEquals(_segmentTarget?.Effect, node))
            _segmentTarget = null;

        CommitFxChange(true);
        BuildInspector();
        TblStatus.Text = L.F("StageUi.Presets.PostEffectRemoved", EffectName(node));
    }

    private void CommitFxChange(bool refreshStack)
    {
        EffectsChanged();
        if (refreshStack) RefreshFxStack();
        RefreshTimeline();
        PushUndo();
        _saveError = SaveProjectNow();
    }

    private void BuildStyleExtras()
    {
        StyleExtraHost.Children.Clear();
        _presetCombos.Clear();
        _presetColorBoxes.Clear();

        StyleExtraHost.Children.Add(ComboRow(L.T("StageUi.Presets.LyricLanguage"), new[]
        {
            (L.T("StageUi.Presets.AutoDetect"), "auto"), (L.T("StageUi.Presets.ChineseSimplified"), "zh-Hans"), (L.T("StageUi.Presets.ChineseTraditional"), "zh-Hant"),
            (L.T("StageUi.Presets.Japanese"), "ja"), (L.T("StageUi.Presets.Korean"), "ko"), ("English", "en"),
        }, () => Preset.Lang, v =>
        {
            Preset.Lang = v;
            PresetChanged();
        }));

        StyleExtraHost.Children.Add(ComboRow(L.T("StageUi.Presets.TitleFont"), FontOptions(), () => Preset.FontDisplay, v =>
        {
            Preset.FontDisplay = v;
            PresetChanged();
        }));
        StyleExtraHost.Children.Add(ComboRow(L.T("StageUi.Presets.SerifFont"), FontOptions(), () => Preset.FontSerif, v =>
        {
            Preset.FontSerif = v;
            PresetChanged();
        }));
        StyleExtraHost.Children.Add(ComboRow(L.T("StageUi.Presets.BodyFont"), FontOptions(), () => Preset.FontBody, v =>
        {
            Preset.FontBody = v;
            PresetChanged();
        }));

        AddToggleTo(StyleExtraHost, L.T("StageUi.Presets.EnableCustomColors"), () => Preset.ColorsEnabled, v =>
        {
            Preset.ColorsEnabled = v;
            PresetChanged();
        });
        StyleExtraHost.Children.Add(ColorRow(L.T("StageUi.Presets.BackgroundColor"), () => Preset.ColorBg,
            "#101014", v =>
            {
                Preset.ColorBg = v;
                PresetChanged();
            }));
        StyleExtraHost.Children.Add(ColorRow(L.T("StageUi.Presets.TextColor"), () => Preset.ColorFg,
            "#f5eeea", v =>
            {
                Preset.ColorFg = v;
                PresetChanged();
            }));
        StyleExtraHost.Children.Add(ColorRow(L.T("StageUi.Presets.SecondaryText"), () => Preset.ColorSub,
            "#bdb6b2", v =>
            {
                Preset.ColorSub = v;
                PresetChanged();
            }));

        AddToggleTo(StyleExtraHost, L.T("StageUi.Presets.CustomAccentColor"), () => Preset.AccentOn, v =>
        {
            Preset.AccentOn = v;
            PresetChanged();
        });
        StyleExtraHost.Children.Add(ColorRow(L.T("StageUi.Presets.AccentColor"), () => Preset.ColorAccent,
            "#f5a50c", v =>
            {
                Preset.ColorAccent = v;
                PresetChanged();
            }));
        StyleExtraHost.Children.Add(ColorRow(L.T("StageUi.Presets.ChromaColorA"), () => Preset.ColorGhostA,
            "#f5a50c", v =>
            {
                Preset.ColorGhostA = v;
                PresetChanged();
            }));
        StyleExtraHost.Children.Add(ColorRow(L.T("StageUi.Presets.ChromaColorB"), () => Preset.ColorGhostB,
            "#16f4d4", v =>
            {
                Preset.ColorGhostB = v;
                PresetChanged();
            }));
    }

    private static IEnumerable<(string Label, string Key)> FontOptions()
    {
        yield return (L.T("StageUi.Presets.StyleDefault"), "");
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
            Cursor = AppCursors.Hand,
            ToolTip = L.T("StageUi.Presets.OpenColorPicker"),
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
        bool dirty = false;
        void Commit()
        {
            if (!dirty) return;
            dirty = false;
            PresetChanged();
        }
        slider.ValueChanged += (_, e) =>
        {
            if (_presetSync) return;
            set(e.NewValue / 100.0);
            valueText.Text = ((int)Math.Round(e.NewValue)).ToString(CultureInfo.InvariantCulture);
            dirty = true;
            if (!slider.IsMouseCaptureWithin) Commit();
        };
        slider.LostMouseCapture += (_, _) => Commit();
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

        _presetToggles.Add((value => toggle.IsChecked = value, get));
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
            foreach (var (setChecked, get) in _presetToggles)
                setChecked(get());

            if (TxtPresetSeed is not null)
                TxtPresetSeed.Text = _project.LegacySeed.ToString(CultureInfo.InvariantCulture);

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
        AddPackToggle(L.T("StageUi.Presets.PackExtra"), () => Preset.Extra, v => Preset.Extra = v);
        AddPackToggle(L.T("StageUi.Presets.PackJapanese"), () => Preset.Wa, v => Preset.Wa = v);
        AddPackToggle(L.T("StageUi.Presets.PackTypography"), () => Preset.Typo, v => Preset.Typo = v);
        AddPackToggle(L.T("StageUi.Presets.PackKinetic"), () => Preset.Kinetic, v => Preset.Kinetic = v);
        AddPackToggle(L.T("StageUi.Presets.PackHorror"), () => Preset.Horror, v => Preset.Horror = v);
    }

    private void AddPackToggle(string label, Func<bool> get, Action<bool> set)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = ThemeText(label, TextSecondaryKey, 12);
        Grid.SetColumn(text, 0);
        row.Children.Add(text);

        var toggle = new CheckBox
        {
            IsChecked = get(),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0),
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

        _presetToggles.Add((value => toggle.IsChecked = value, get));
        TechPackHost.Children.Add(row);
    }

    private static readonly string[] TechGroups =
        ["layout", "enter", "hold", "exit", "decor", "treat", "bg", "trans", "fx"];

    private void BuildTechLists()
    {
        StopTechniquePreview();
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
            tools.Children.Add(TechBulkButton(L.T("StageUi.Presets.EnableAll"), category, 1));
            tools.Children.Add(TechBulkButton(L.T("StageUi.Presets.DisableAll"), category, 0));
            tools.Children.Add(TechBulkButton(L.T("StageUi.Presets.Invert"), category, 2));
            list.Children.Add(tools);
            list.Children.Add(cards);

            foreach (var key in keys)
                category.Items.Add((key, TechLabel(group, key)));

            expander.Content = list;
            expander.Expanded += (_, _) =>
            {
                EnsureCategoryBuilt(category);
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RefreshVisibleTechPreviews));
            };
            expander.Collapsed += (_, _) => ResetTechCategory(category);
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

        if (category.Built) ResetTechCategory(category);

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

            var placeholder = ThemeText(L.T("StageUi.Presets.Preview"), TextTertiaryKey, 9);
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
                Tag = placeholder,
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
                Cursor = AppCursors.Hand,
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

            card.MouseLeftButtonDown += (_, _) => check.IsChecked = check.IsChecked != true;
            category.Cards.Children.Add(card);
            category.Rows.Add((key, label, card, check, thumb));
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
                ? L.F("StageUi.Presets.TechGroupCount", TechGroupLabel(category.Group), on, category.Items.Count)
                : L.F("StageUi.Presets.TechGroupMatchCount", TechGroupLabel(category.Group), MatchCount(category));
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
        "layout" => L.T("StageUi.Presets.GroupLayout"),
        "enter" => L.T("StageUi.Presets.GroupEnter"),
        "hold" => L.T("StageUi.Presets.GroupHold"),
        "exit" => L.T("StageUi.Presets.GroupExit"),
        "decor" => L.T("StageUi.Presets.GroupDecor"),
        "treat" => L.T("StageUi.Presets.GroupTextTreatment"),
        "bg" => L.T("StageUi.Presets.GroupBackground"),
        "cam" => L.T("StageUi.Presets.GroupCamera"),
        "trans" => L.T("StageUi.Presets.GroupTransition"),
        "fx" => L.T("StageUi.Presets.GroupFx"),
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
            "black" => L.T("StageUi.Presets.KeyBackgroundBlack"),
            "green" => L.T("StageUi.Presets.KeyBackgroundGreen"),
            _ => L.T("StageUi.Presets.KeyBackgroundNormal"),
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
        TblStatus.Text = L.T("StageUi.Presets.PresetChangedRegenerating");

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
            RequestPipelineRebuild();
        };
        return timer;
    }
}
