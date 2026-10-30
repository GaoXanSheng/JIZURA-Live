using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using JizuraLive.LegacyEngine;
using JizuraLive.Pv;
using JizuraLive.Render.Effects;
using JizuraLive.App.I18n;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private sealed class AnimRow
    {
        public ParamAddress Address;
        public string Label = "";
        public double Min, Max;
        public double SpecMin, SpecMax;
        public double? SliderWindow;
        public Func<double> ReadBase = null!;
        public Action<double> WriteBase = null!;
        public Slider Slider = null!;
        public NumericValue Value = null!;
        public Wpf.Ui.Controls.Button KeyButton = null!;
        public Wpf.Ui.Controls.Badge Badge = null!;
        public TextBlock ClampMark = null!;
        public double EditTime;
        public bool Dirty;
    }

    private sealed class NumericValue
    {
        public Grid Root { get; }
        public TextBlock Display { get; }
        public TextBox Editor { get; }
        public Action<double>? Commit { get; set; }
        public double Min { get; set; } = double.NegativeInfinity;
        public double Max { get; set; } = double.PositiveInfinity;

        private double _value;
        private bool _editing;

        public NumericValue()
        {
            Display = new TextBlock
            {
                FontFamily = MonoFontFamily,
                FontSize = 12,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = AppCursors.Hand,
                ToolTip = L.T("StageUi.Inspector.ClickToEnterExactValue"),
            };
            Display.SetResourceReference(TextBlock.ForegroundProperty, TextPrimaryKey);
            Display.MouseLeftButtonDown += (_, e) =>
            {
                e.Handled = true;
                BeginEdit();
            };

            Editor = new TextBox
            {
                FontFamily = MonoFontFamily,
                FontSize = 12,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalContentAlignment = VerticalAlignment.Center,
                Height = 24,
                Width = 64,
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(2, 0, 2, 0),
                Visibility = Visibility.Collapsed,
            };
            if (Application.Current.TryFindResource("InlineValueBox") is Style inlineStyle)
                Editor.Style = inlineStyle;
            Editor.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    EndEdit(true);
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    EndEdit(false);
                    e.Handled = true;
                }
            };
            Editor.LostFocus += (_, _) => EndEdit(true);

            Root = new Grid();
            Root.Children.Add(Display);
            Root.Children.Add(Editor);
        }

        public void SetValue(double value)
        {
            _value = value;
            if (!_editing) Display.Text = value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private void BeginEdit()
        {
            if (_editing) return;
            _editing = true;
            Editor.Text = _value.ToString("0.###", CultureInfo.InvariantCulture);
            Display.Visibility = Visibility.Collapsed;
            Editor.Visibility = Visibility.Visible;
            Editor.Focus();
            Editor.SelectAll();
        }

        private void EndEdit(bool commit)
        {
            if (!_editing) return;
            _editing = false;
            Editor.Visibility = Visibility.Collapsed;
            Display.Visibility = Visibility.Visible;

            if (commit
                && double.TryParse(Editor.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                && double.IsFinite(value))
            {
                if (double.IsFinite(Min)) value = Math.Max(Min, value);
                if (double.IsFinite(Max)) value = Math.Min(Max, value);
                _value = value;
                Display.Text = value.ToString("0.###", CultureInfo.InvariantCulture);
                Commit?.Invoke(value);
                return;
            }

            Display.Text = _value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    private readonly List<AnimRow> _inspectorRows = new();
    private readonly List<(EffectNode Node, EffectParam Param, Wpf.Ui.Controls.ToggleSwitch Toggle)> _effectBoolRows = new();
    private readonly List<(EffectNode Node, EffectParam Param, Wpf.Ui.Controls.TextBox Box, Border Swatch)> _effectColorRows = new();
    private bool _syncingRows;
    private Panel _inspectorTarget = null!;

    internal void ClearLayerSelection() => SelectLayer(null);

    private void SelectLayer(PvLayerConfig? layer)
    {
        if (ReferenceEquals(_selectedLayer, layer) && _segmentTarget is null) return;

        _segmentTarget = null;
        _selectedLayer = layer;
        SyncStageSelection();
        HighlightLayerCards();
        TheTimeline.ClearDesignSelection();
        TheTimeline.SetSelectedOwner(layer is null ? "cam" : "layer:" + layer.Id);
        BuildInspector();
    }

    private void HighlightLayerCards()
    {
        foreach (var card in LayersHost.Children.OfType<Border>())
        {
            var active = ReferenceEquals(card.Tag, _selectedLayer);
            if (active) card.SetResourceReference(Border.BackgroundProperty, SelectFillKey);
            else card.Background = Brushes.Transparent;
        }

        TblInspectorTitle.Text = _selectedLayer is null ? L.T("StageUi.Inspector.Overall") : LayerTitle(_selectedLayer);
    }

    private static string LayerTitle(PvLayerConfig layer) =>
        string.IsNullOrWhiteSpace(layer.SpoutSenderName) ? layer.Name : layer.SpoutSenderName!;

    private void BuildInspector()
    {
        _inspectorRows.Clear();
        _effectBoolRows.Clear();
        _effectColorRows.Clear();
        InspectorStack.Children.Clear();
        _inspectorTarget = InspectorStack;
        TblInspectorTitle.Text = InspectorTargetTitle();

        if (_segmentTarget is { } target && SegmentTargetAlive(target))
        {
            BuildSegmentSection(target);
        }
        else
        {
            _segmentTarget = null;
            foreach (var section in InspectorLayout.Sections(_selectedLayer is not null))
            {
                switch (section)
                {
                    case InspectorSectionKind.Camera:
                        BuildCameraSection();
                        break;
                    case InspectorSectionKind.Layer:
                        BuildLayerSection(_selectedLayer!);
                        break;
                }
            }
        }

        RefreshParamRows();
        RefreshFxStack();
    }

    private string InspectorTargetTitle()
    {
        if (_segmentTarget is { } target && SegmentTargetAlive(target))
        {
            if (target.Effect is { } node) return EffectName(node);
            if (target.Shot is { } shot) return TechniqueLabel(target, shot.Preset);
            if (target.Design is { } design) return TechniqueLabel(target, design.Technique);
        }

        return _selectedLayer is { } layer ? LayerTitle(layer) : L.T("StageUi.Inspector.Overall");
    }

    private void BuildCameraSection()
    {
        AddSectionTitle(L.T("StageUi.Inspector.OverallCamera"));
        AddInspector(CameraRow("offsetX", L.T("StageUi.Inspector.OffsetX"), 1));
        AddInspector(CameraRow("offsetY", L.T("StageUi.Inspector.OffsetY"), 1));
        AddInspector(CameraRow("scale", L.T("StageUi.Inspector.Scale"), 0.01));
        AddInspector(CameraRow("rotation", L.T("StageUi.Inspector.Rotation"), 1));
        AddInspector(CameraRow("blur", L.T("StageUi.Inspector.Defocus"), 0.5));
        AddInspector(CameraRow("brighten", L.T("StageUi.Inspector.Brighten"), 0.01));
        AddSectionTitle(L.T("StageUi.Inspector.OverallCameraMove"));
        AddInspector(CameraAddRow());
        AddSectionTitle(L.T("StageUi.Inspector.OverallTransition"));
        AddInspector(TransitionAddRow());
    }

    private FrameworkElement CameraAddRow()
    {
        var panel = new StackPanel();
        var combo = new ComboBox { FontSize = 12 };
        foreach (var preset in CameraDirection.Presets)
            combo.Items.Add(new ComboBoxItem { Content = ZhLabel("cam", preset.Key), Tag = preset.Key });
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        panel.Children.Add(combo);

        var add = new Wpf.Ui.Controls.Button
        {
            Content = L.T("StageUi.Inspector.AddOverallCameraMoveAtPlayhead"),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 0),
        };
        add.Click += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string preset })
                AddGlobalCameraShot(preset);
        };
        panel.Children.Add(add);
        return panel;
    }

    private void AddGlobalCameraShot(string preset)
    {
        var track = _project.CameraTrack ??= NewCameraTrack();
        track.Width = _scene.Width;
        track.Height = _scene.Height;
        track.Fps = _scene.Fps;
        if (track.BeatLen <= 0.05)
            track.BeatLen = _beatMap is { Bpm: > 0 } bm2 ? 60.0 / bm2.Bpm : 0.5;

        double start = Math.Max(0, _audio.Time);
        double end = Math.Min(ProjectDuration(), start + Math.Max(2.0, track.BeatLen * 8));
        if (end <= start + 0.2) end = start + 2.0;

        var shot = NewCameraShot(track, preset, start, end);
        track.Shots.Add(shot);
        track.Shots.Sort((a, b) => a.Start.CompareTo(b.Start));

        _evaluator = DirectorEvaluator.FromSaves(_project.Curves, _project.CameraTrack, _project.LayerCameraTracks);
        _segmentTarget = SegmentTarget.Of(shot);
        PublishDirector();
        RefreshTimeline();
        BuildInspector();
        PushUndo();
        _saveError = SaveProjectNow();
        TblStatus.Text = L.F("StageUi.Inspector.AddedOverallCameraMove", ZhLabel("cam", preset));
    }

    private CameraTrack NewCameraTrack() => new()
    {
        Width = _scene.Width,
        Height = _scene.Height,
        Fps = _scene.Fps,
        BeatLen = _beatMap is { Bpm: > 0 } bm ? 60.0 / bm.Bpm : 0.5,
        Beats = _beatMap is { } map ? new List<double>(map.Beats) : new List<double>(),
    };

    private CameraShot NewCameraShot(CameraTrack track, string preset, double start, double end)
    {
        var rng = new CameraRng(unchecked((uint)(_project.LegacySeed * 7919 + track.Shots.Count * 104729 + 17)));
        var pars = CameraDirection.PlanParams(preset, rng);
        pars["shotScale"] = 1.0;
        pars["shotX"] = 0;
        pars["shotY"] = 0;
        pars["shotRot"] = 0;
        pars["gain"] = 1.0;

        return new CameraShot
        {
            Preset = preset,
            Start = start,
            End = end,
            Seed = unchecked((int)(rng.Next() * int.MaxValue)),
            Params = pars,
            Source = "user",
        };
    }

    private FrameworkElement CameraRow(string param, string label, double step)
    {
        var addr = new ParamAddress(ParamAddressKind.Camera, "", param);
        if (!ParamRegistry.TryResolve(addr, _effects, _scene, out var spec, out _)) return new Grid();

        Func<double> read = param switch
        {
            "offsetX" => () => _scene.Camera?.X ?? 0,
            "offsetY" => () => _scene.Camera?.Y ?? 0,
            "scale" => () => _scene.Camera?.Scale ?? 1,
            "rotation" => () => _scene.Camera?.Rotation ?? 0,
            _ => () => spec.Default,
        };
        Action<double> write = param switch
        {
            "offsetX" => v => Camera().X = v,
            "offsetY" => v => Camera().Y = v,
            "scale" => v => Camera().Scale = v,
            "rotation" => v => Camera().Rotation = v,
            _ => v => WriteCurveKeyAtPlayhead(addr, v),
        };
        return AnimParamRow(_inspectorRows, addr, label, spec.Min, spec.Max, step, read, write);
    }

    private PvCameraConfig Camera() => _scene.Camera ??= new PvCameraConfig();

    private void BuildLayerSection(PvLayerConfig layer)
    {
        AddSectionTitle(L.T("StageUi.Inspector.Layer"));
        AddInspector(ThemeText(
            L.T("StageUi.Inspector.LayerTransformHint"),
            TextTertiaryKey, 11, wrap: true));
        AddInspector(LayerRow(layer, "offsetX", L.T("StageUi.Inspector.OffsetX"), 0.01));
        AddInspector(LayerRow(layer, "offsetY", L.T("StageUi.Inspector.OffsetY"), 0.01));
        AddInspector(LayerRow(layer, "scale", L.T("StageUi.Inspector.Scale"), 0.01));
        AddInspector(LayerRow(layer, "opacity", L.T("StageUi.Inspector.Opacity"), 0.02));
        AddInspector(LayerRow(layer, "rotation", L.T("StageUi.Inspector.Rotation"), 1));
        AddInspector(ConstRow(L.T("StageUi.Inspector.Depth"), 0, 3, 0.01,
            () => layer.Parallax.Depth, v => layer.Parallax.Depth = v));
        if (_scene.IsCharacterLayer(layer))
            AddInspector(IndependentCameraRow(layer));
        else
            AddInspector(ConstRow(L.T("StageUi.Inspector.CameraFollow"), 0, 1, 0.01,
                () => layer.Parallax.Follow ?? layer.Parallax.Depth,
                v => layer.Parallax.Follow = v));
        AddInspector(ConstRow(L.T("StageUi.Inspector.AmbientLight"), 0, 1, 0.02,
            () => layer.VisualBlend.AmbientTintIntensity, v => layer.VisualBlend.AmbientTintIntensity = v));
        AddInspector(ConstRow(L.T("StageUi.Inspector.RimLight"), 0, 1, 0.02,
            () => layer.VisualBlend.RimGlow, v => layer.VisualBlend.RimGlow = v));
        AddInspector(ConstRow(L.T("StageUi.Inspector.Shadow"), 0, 1, 0.02,
            () => layer.VisualBlend.DropShadow, v => layer.VisualBlend.DropShadow = v));

        if (!string.IsNullOrWhiteSpace(layer.SpoutSenderName))
        {
            var isCharacter = layer.VtsCharacter
                              ?? string.Equals(layer.Id, PvScene.CharacterLayerId, StringComparison.Ordinal);
            AddInspector(BoolRow(L.T("StageUi.Inspector.VtsCharacterSource"), isCharacter,
                v => SetVtsCharacterSource(layer, v)));
            AddInspector(BoolRow(L.T("StageUi.Inspector.PremultipliedAlpha"), layer.Premultiplied, v =>
            {
                layer.Premultiplied = v;
                RebuildStage();
            }));
        }

        if (_scene.IsCharacterLayer(layer))
        {
            if (UsesIndependentCameraTrack(layer) && !_scene.CameraAffectsCharacter)
                BuildCharacterCameraTrackSection(layer);
            else if (_scene.CameraAffectsCharacter)
                AddInspector(ThemeText(L.T("StageUi.Inspector.CharacterCameraGlobalForced"),
                    TextTertiaryKey, 11, wrap: true));

            if (VtsEnabled)
            {
                BuildCharacterMoveSection();
                BuildCharacterExpressionSection();
                BuildCharacterTintSection();
            }
        }
    }

    private FrameworkElement IndependentCameraRow(PvLayerConfig layer)
    {
        var panel = new StackPanel();
        panel.Children.Add(BoolRow(L.T("StageUi.Inspector.IndependentCameraTrack"),
            UsesIndependentCameraTrack(layer), v =>
            {
                layer.Parallax.Follow = v ? 0 : 1;
                PublishDirector();
                BuildInspector();
                PushUndo();
                _saveError = SaveProjectNow();
            }));

        var hint = ThemeText(L.T("StageUi.Inspector.IndependentCameraHint"),
            TextTertiaryKey, 11, wrap: true);
        hint.Margin = new Thickness(0, 4, 0, 0);
        panel.Children.Add(hint);
        return panel;
    }

    private bool UsesIndependentCameraTrack(PvLayerConfig layer) =>
        _scene.IsCharacterLayer(layer)
        && (layer.Parallax.Follow ?? layer.Parallax.Depth) < 0.999;

    private void SetVtsCharacterSource(PvLayerConfig layer, bool on)
    {
        foreach (var l in _scene.Layers)
        {
            if (ReferenceEquals(l, layer)) l.VtsCharacter = on;
            else if (on) l.VtsCharacter = false;
        }

        RebuildStage();
        RefreshTimeline();
        BuildInspector();
        TblStatus.Text = on ? L.F("StageUi.Inspector.VtsCharacterSourceSet", LayerTitle(layer)) : L.T("StageUi.Inspector.VtsCharacterSourceCleared");
    }

    private string LayerTrackId(PvLayerConfig layer) =>
        _scene.IsCharacterLayer(layer) ? PvScene.CharacterLayerId : layer.Id;

    private void BuildCharacterCameraTrackSection(PvLayerConfig layer)
    {
        AddSectionTitle(L.T("StageUi.Inspector.CharacterCameraMove"));
        AddInspector(ThemeText(
            L.T("StageUi.Inspector.CharacterCameraFollowHint"),
            TextSecondaryKey, 11, wrap: true));

        var combo = new ComboBox { FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var preset in CameraDirection.Presets)
            combo.Items.Add(new ComboBoxItem { Content = ZhLabel("cam", preset.Key), Tag = preset.Key });
        combo.SelectedIndex = 0;
        AddInspector(combo);

        var add = new Wpf.Ui.Controls.Button
        {
            Content = L.T("StageUi.Inspector.AddCharacterCameraMove"),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 0),
        };
        add.Click += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string key })
                AddLayerCameraShot(layer, key);
        };
        AddInspector(add);

        var copy = new Wpf.Ui.Controls.Button
        {
            Content = L.T("StageUi.Inspector.CopyGlobalCameraMove"),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 0),
        };
        copy.Click += (_, _) => CopyGlobalCameraToLayer(layer);
        AddInspector(copy);

        if (_project.LayerCameraTracks.TryGetValue(LayerTrackId(layer), out var track) && track.Shots.Count > 0)
            AddInspector(ThemeText(
                L.F("StageUi.Inspector.ExistingShotsHint", track.Shots.Count),
                TextSecondaryKey, 11, wrap: true));
    }

    private void CopyGlobalCameraToLayer(PvLayerConfig layer)
    {
        var source = _project.CameraTrack;
        if (source is null || source.Shots.Count == 0)
        {
            TblStatus.Text = L.T("StageUi.Inspector.GlobalCameraTrackEmpty");
            return;
        }

        var copy = source.Clone();
        copy.UserEdited = true;
        foreach (var shot in copy.Shots) shot.Source = "user";
        _project.LayerCameraTracks[LayerTrackId(layer)] = copy;
        layer.Parallax.Follow = 0;
        _evaluator = DirectorEvaluator.FromSaves(_project.Curves, _project.CameraTrack, _project.LayerCameraTracks);
        PublishDirector();
        RefreshTimeline();
        BuildInspector();
        _saveError = SaveProjectNow();
        TblStatus.Text = L.F("StageUi.Inspector.GlobalCameraCopiedToCharacterTrack", copy.Shots.Count);
    }

    private void AddLayerCameraShot(PvLayerConfig layer, string preset)
    {
        if (!_project.LayerCameraTracks.TryGetValue(LayerTrackId(layer), out var track))
        {
            track = NewCameraTrack();
            _project.LayerCameraTracks[LayerTrackId(layer)] = track;
        }

        double start = Math.Max(0, _audio.Time);
        double end = Math.Min(TimelineSpan(), start + Math.Max(2.0, track.BeatLen * 8));
        if (end <= start + 0.2) end = start + 2.0;

        track.Shots.Add(NewCameraShot(track, preset, start, end));
        track.Shots.Sort((a, b) => a.Start.CompareTo(b.Start));
        track.UserEdited = true;

        layer.Parallax.Follow = 0;
        _evaluator = DirectorEvaluator.FromSaves(_project.Curves, _project.CameraTrack, _project.LayerCameraTracks);
        PublishDirector();
        RefreshTimeline();
        BuildInspector();
        _saveError = SaveProjectNow();
        TblStatus.Text = L.F("StageUi.Inspector.AddedCharacterCameraMove", ZhLabel("cam", preset));
    }

    private FrameworkElement BoolRow(string label, bool value, Action<bool> write,
        Action<Wpf.Ui.Controls.ToggleSwitch>? capture = null)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lab = ThemeText(label, TextSecondaryKey, 12);
        Grid.SetColumn(lab, 0);
        row.Children.Add(lab);

        var toggle = new Wpf.Ui.Controls.ToggleSwitch
        {
            IsChecked = value,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        toggle.Checked += (_, _) => write(true);
        toggle.Unchecked += (_, _) => write(false);
        capture?.Invoke(toggle);
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);
        return row;
    }

    private void RebuildStage()
    {
        if (_pipe is not null)
        {
            RequestPipelineRebuild();
        }

        _saveError = SaveProjectNow();
    }

    private static bool IsHexColor(string text)
    {
        if (text.Length is not (4 or 7) || text[0] != '#') return false;
        foreach (var c in text.AsSpan(1))
            if (!Uri.IsHexDigit(c)) return false;
        return true;
    }

    private static System.Windows.Media.Brush BrushFromHex(string hex)
    {
        try
        {
            return new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
        }
        catch
        {
            return System.Windows.Media.Brushes.White;
        }
    }

    private FrameworkElement ConstRow(string label, double min, double max, double step,
        Func<double> read, Action<double> write, Action? onCommit = null)
    {
        var row = new Grid { Margin = new Thickness(0, 4, 0, 2) };
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lab = ThemeText(label, TextSecondaryKey, 12);
        lab.TextTrimming = TextTrimming.CharacterEllipsis;
        lab.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(lab, 0);
        head.Children.Add(lab);

        var value = new NumericValue { Min = min, Max = max };
        value.SetValue(read());
        Grid.SetColumn(value.Root, 1);
        head.Children.Add(value.Root);

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            Value = Math.Clamp(read(), min, max),
            IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        bool syncing = false;
        bool dirty = false;
        void Commit()
        {
            if (!dirty) return;
            dirty = false;
            onCommit?.Invoke();
            PushUndo();
            _saveError = SaveProjectNow();
        }
        value.Commit = v =>
        {
            write(v);
            syncing = true;
            try { slider.Value = Math.Clamp(v, min, max); }
            finally { syncing = false; }
            dirty = true;
            Commit();
        };
        slider.ValueChanged += (_, e) =>
        {
            if (syncing) return;
            write(e.NewValue);
            value.SetValue(e.NewValue);
            dirty = true;
            if (!slider.IsMouseCaptureWithin) Commit();
        };
        slider.LostMouseCapture += (_, _) => Commit();
        Grid.SetRow(slider, 1);
        row.Children.Add(slider);
        row.Children.Add(head);

        return row;
    }

    private FrameworkElement LayerRow(PvLayerConfig layer, string param, string label, double step)
    {
        var addr = new ParamAddress(ParamAddressKind.Layer, layer.Id, param);
        if (!ParamRegistry.TryResolve(addr, _effects, _scene, out var spec, out _)) return new Grid();

        Func<double> read = param switch
        {
            "offsetX" => () => layer.OffsetX,
            "offsetY" => () => layer.OffsetY,
            "scale" => () => layer.Scale,
            "opacity" => () => layer.Opacity,
            _ => () => layer.Rotation,
        };
        Action<double> write = param switch
        {
            "offsetX" => v => layer.OffsetX = v,
            "offsetY" => v => layer.OffsetY = v,
            "scale" => v => layer.Scale = v,
            "opacity" => v => layer.Opacity = v,
            _ => v => layer.Rotation = v,
        };
        return AnimParamRow(_inspectorRows, addr, label, spec.Min, spec.Max, step, read, write);
    }

    private FrameworkElement AnimParamRow(List<AnimRow> owner, ParamAddress address, string label,
        double min, double max, double step, Func<double> readBase, Action<double> writeBase,
        double? sliderWindow = null)
    {
        double current = _evaluator.TryEvaluate(address, _audio.Time, out var evaluated)
            ? evaluated
            : readBase();
        double lo = min;
        double hi = max;

        var state = new AnimRow
        {
            Address = address,
            Label = label,
            Min = lo,
            Max = hi,
            SpecMin = min,
            SpecMax = max,
            SliderWindow = sliderWindow,
            ReadBase = readBase,
            WriteBase = writeBase,
            EditTime = _audio.Time,
        };
        owner.Add(state);

        var row = new Grid { Margin = new Thickness(0, 4, 0, 2) };
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lab = ThemeText(label, TextSecondaryKey, 12);
        lab.TextTrimming = TextTrimming.CharacterEllipsis;
        lab.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(lab, 1);
        head.Children.Add(lab);

        double halfWindow = sliderWindow is { } window ? window / 2 : 0;
        double sliderMin = sliderWindow is null ? lo : current - halfWindow;
        double sliderMax = sliderWindow is null ? hi : current + halfWindow;

        var slider = new Slider
        {
            Minimum = sliderMin,
            Maximum = sliderMax,
            SmallChange = step,
            Value = Math.Clamp(current, sliderMin, sliderMax),
            IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(22, 3, 0, 0),
        };
        slider.ValueChanged += (_, e) => OnRowValueChanged(state, e.NewValue);
        slider.PreviewMouseLeftButtonDown += (_, _) => state.EditTime = _audio.Time;
        slider.LostMouseCapture += (_, _) =>
        {
            if (state.Dirty)
            {
                state.Dirty = false;
                PushUndo();
                _saveError = SaveProjectNow();
            }

            if (sliderWindow is not null) RefreshParamRows();
        };
        state.Slider = slider;
        Grid.SetRow(slider, 1);
        row.Children.Add(slider);

        var value = new NumericValue
        {
            Min = min,
            Max = max,
            Commit = v =>
            {
                state.EditTime = _audio.Time;
                OnRowValueChanged(state, v);
            },
        };
        state.Value = value;
        value.Display.FontSize = 11.5;
        value.Editor.FontSize = 11.5;

        var clamp = Themed(new TextBlock
        {
            Text = "↦",
            FontSize = 12,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0),
            Visibility = Visibility.Collapsed,
        }, TextBlock.ForegroundProperty, CautionKey);
        state.ClampMark = clamp;

        var valueCell = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        valueCell.Children.Add(clamp);
        valueCell.Children.Add(value.Root);
        Grid.SetColumn(valueCell, 2);
        head.Children.Add(valueCell);

        var key = new Wpf.Ui.Controls.Button
        {
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
            Content = "◆",
            FontSize = 10,
            Width = 18,
            Height = 18,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 0, 4, 0),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            ToolTip = L.T("StageUi.Inspector.KeyAtPlayheadReplaceHint"),
        };
        key.Click += (_, _) => KeyRowAtPlayhead(state);
        if (!ParamRegistry.TryResolve(address, _effects, _scene, out _, out _))
            key.Visibility = Visibility.Collapsed;
        state.KeyButton = key;
        Grid.SetColumn(key, 0);
        head.Children.Add(key);

        var badge = new Wpf.Ui.Controls.Badge
        {
            Cursor = AppCursors.Hand,
            FontSize = 10.5,
            Padding = new Thickness(4, 0, 4, 0),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
        };
        badge.MouseLeftButtonDown += (_, _) => JumpToRowTrack(state);
        state.Badge = badge;
        Grid.SetColumn(badge, 3);
        head.Children.Add(badge);

        row.Children.Add(head);
        return row;
    }

    private void MarkEffectUser(in ParamAddress addr)
    {
        if (addr.Kind != ParamAddressKind.Effect) return;
        var target = addr.Target;
        var node = _effects.Nodes.FirstOrDefault(n =>
            n is not null && string.Equals(n.InstanceId, target, StringComparison.Ordinal));
        if (node is not null) MarkFxUser(node);
    }

    private void KeyRowAtPlayhead(AnimRow row)
    {
        var t = _audio.Time;
        var v = _evaluator.TryEvaluate(row.Address, t, out var eval) ? eval : row.ReadBase();
        MarkEffectUser(row.Address);

        bool created = !_evaluator.Curves.ContainsKey(row.Address);
        var curve = EnsureCurve(row.Address, row.ReadBase(), t);
        var mode = ModeOfKeyAt(curve, t) ?? DefaultCurveMode();
        curve.Remove(t);
        curve.TryAdd(t, v, mode);

        PublishDirector();
        RefreshParamRows();
        RefreshTimeline();
        PushUndo();
        _saveError = SaveProjectNow();
        TblStatus.Text = created && t > 1e-6
            ? L.F("StageUi.Inspector.KeyframeAddedWithHoldKey", t, row.Label, v)
            : L.F("StageUi.Inspector.KeyframeAdded", t, row.Label, v);
    }

    private FloatCurve EnsureCurve(in ParamAddress addr, double rest, double time)
    {
        if (_evaluator.Curves.TryGetValue(addr, out var curve)) return curve;

        curve = new FloatCurve();
        if (time > 1e-6) curve.TryAdd(0, rest, DefaultCurveMode());
        _evaluator.SetCurve(addr, curve);
        return curve;
    }

    private void WriteCurveKeyAtPlayhead(in ParamAddress addr, double value)
    {
        var t = _audio.Time;
        var curve = EnsureCurve(addr, value, t);

        var mode = ModeOfKeyAt(curve, t) ?? DefaultCurveMode();
        curve.Remove(t);
        curve.TryAdd(t, value, mode);
        PublishDirector();
    }

    private static CurveInterp? ModeOfKeyAt(FloatCurve curve, double time)
    {
        foreach (var k in curve.Keys)
            if (Math.Abs(k.Time - time) < 1e-6) return k.Mode;
        return null;
    }

    private void OnRowValueChanged(AnimRow row, double value)
    {
        if (_syncingRows) return;
        MarkEffectUser(row.Address);

        if (row.Slider.IsMouseCaptureWithin
            && row.Address.Kind == ParamAddressKind.Vts
            && row.Address.Param is "x" or "y")
            value = SnapVtsMove(value);

        var t = _audio.Time;
        if (_evaluator.Curves.TryGetValue(row.Address, out var curve))
        {
            if (!row.Slider.IsMouseCaptureWithin) row.EditTime = t;
            var at = row.EditTime;
            var mode = ModeOfKeyAt(curve, at) ?? DefaultCurveMode();
            curve.Remove(at);
            curve.TryAdd(at, value, mode);
            PublishDirector();
        }
        else
        {
            row.WriteBase(value);
            row.EditTime = t;
        }

        row.Dirty = true;
        RefreshTimeline();
        RefreshParamRows();

        if (!row.Slider.IsMouseCaptureWithin)
        {
            row.Dirty = false;
            PushUndo();
            _saveError = SaveProjectNow();
        }
    }

    private void JumpToRowTrack(AnimRow row)
    {
        if (!_evaluator.Curves.TryGetValue(row.Address, out var curve) || curve.Count == 0) return;

        var t = _audio.Time;
        var best = curve.Keys[0].Time;
        foreach (var k in curve.Keys)
            if (Math.Abs(k.Time - t) < Math.Abs(best - t)) best = k.Time;
        TheTimeline.SelectKey(row.Address, best);
    }

    private void RefreshParamRows()
    {
        var t = _audio.Time;
        foreach (var row in _inspectorRows) RefreshRow(row, t);

        foreach (var (node, param, toggle) in _effectBoolRows)
        {
            var addr = new ParamAddress(ParamAddressKind.Effect, node.InstanceId, param.Key);
            var value = _evaluator.TryEvaluate(addr, t, out var evaluated) ? evaluated : node.Value(param);
            var on = value >= 0.5;
            if (toggle.IsChecked == on) continue;

            _syncingRows = true;
            try { toggle.IsChecked = on; }
            finally { _syncingRows = false; }
        }

        foreach (var (node, param, box, swatch) in _effectColorRows)
        {
            if (box.IsKeyboardFocusWithin) continue;
            var hex = ResolveEffectColor(node, param, t);
            if (!string.Equals(box.Text, hex, StringComparison.OrdinalIgnoreCase)) box.Text = hex;
            swatch.Background = BrushFromHex(hex);
        }
    }

    private void RefreshRow(AnimRow row, double time)
    {
        var hasCurve = _evaluator.Curves.TryGetValue(row.Address, out var curve) && curve.Count > 0;
        var value = hasCurve ? curve!.Evaluate(time) : row.ReadBase();
        var clamped = value < row.SpecMin || value > row.SpecMax;

        var source = ValueSourceBadge.Label(hasCurve);
        row.Badge.Content = source;
        row.Badge.SetResourceReference(Control.ForegroundProperty,
            hasCurve ? TextAccentKey : TextTertiaryKey);
        row.Badge.SetResourceReference(Control.BorderBrushProperty,
            hasCurve ? TextAccentKey : TextTertiaryKey);
        row.Badge.ToolTip = hasCurve ? L.T("StageUi.Inspector.DrivenByCurveHint") : L.T("StageUi.Inspector.DocumentConstant");

        var range = $"[{row.SpecMin.ToString("0.###", CultureInfo.InvariantCulture)}, {row.SpecMax.ToString("0.###", CultureInfo.InvariantCulture)}]";
        row.ClampMark.Visibility = clamped ? Visibility.Visible : Visibility.Collapsed;
        row.ClampMark.ToolTip = clamped
            ? L.F("StageUi.Inspector.ValueOutOfRangeClamped",
                value.ToString("0.###", CultureInfo.InvariantCulture), range)
            : null;

        bool keyHere = hasCurve && ModeOfKeyAt(curve!, time) is not null;
        row.KeyButton.SetResourceReference(Control.ForegroundProperty,
            hasCurve ? TextAccentKey : TextTertiaryKey);
        row.KeyButton.SetResourceReference(Control.BorderBrushProperty,
            hasCurve ? TextAccentKey : TextTertiaryKey);
        row.KeyButton.ToolTip = keyHere
            ? L.T("StageUi.Inspector.KeyframeExistsOverwriteHint")
            : L.T("StageUi.Inspector.KeyAtPlayheadAutoHoldHint");

        row.Value.SetValue(value);

        if (row.Slider.IsMouseCaptureWithin) return;

        double shown = Math.Clamp(value, row.Min, row.Max);
        _syncingRows = true;
        try
        {
            if (row.SliderWindow is { } window)
            {
                double half = window / 2;
                row.Slider.Minimum = value - half;
                row.Slider.Maximum = value + half;
                shown = value;
            }

            if (Math.Abs(row.Slider.Value - shown) > 1e-4) row.Slider.Value = shown;
        }
        finally
        {
            _syncingRows = false;
        }
    }

    private bool SegmentTargetAlive(SegmentTarget target)
    {
        if (target.Effect is { } node) return _effects.Nodes.Contains(node);
        if (target.Shot is { } shot)
            return target.LayerShot
                ? _project.LayerCameraTracks.TryGetValue(PvScene.CharacterLayerId, out var layerTrack)
                  && layerTrack.Shots.Contains(shot)
                : _project.CameraTrack?.Shots.Contains(shot) == true;
        if (target.Design is { } design) return _project.DesignSegments.Contains(design);
        if (target.LegacyCutIndex >= 0)
            return _legacyPlanInfo is { } plan && target.LegacyCutIndex < plan.Cuts.Count;
        return false;
    }

    private void BuildSegmentSection(SegmentTarget target)
    {
        string technique;
        string source;
        bool locked;
        string title;
        double[] times;
        bool showFades;

        switch (target)
        {
            case { Effect: { } node, WindowIndex: >= 0 } windowTarget:
                var win = windowTarget.WindowIndex < node.Windows.Count && node.Windows[windowTarget.WindowIndex].IsValid
                    ? node.Windows[windowTarget.WindowIndex]
                    : default;
                technique = node.DefId;
                times = [win.IsValid ? win.Start : 0, win.IsValid ? win.End : 0, win.IsValid ? win.FadeIn : 0, win.IsValid ? win.FadeOut : 0];
                source = node.Source;
                locked = node.Locked;
                title = EffectName(node) + L.F("StageUi.Inspector.WindowSuffix", windowTarget.WindowIndex + 1);
                showFades = true;
                break;
            case { Effect: { } node }:
                var first = node.Windows.Count > 0 && node.Windows[0].IsValid ? node.Windows[0] : default;
                technique = node.DefId;
                times = first.IsValid
                    ? [first.Start, first.End, first.FadeIn, first.FadeOut]
                    : [0, Math.Max(0, _audio.Duration), 0, 0];
                source = node.Source;
                locked = node.Locked;
                title = EffectName(node);
                showFades = first.IsValid;
                break;
            case { Shot: { } shot }:
                technique = shot.Preset;
                times = [shot.Start, shot.End, 0, 0];
                source = shot.Source;
                locked = shot.Locked;
                title = L.F("StageUi.Inspector.CameraMoveFormat", TechniqueLabel(target, shot.Preset));
                showFades = false;
                break;
            case { Design: { } design }:
                technique = design.Technique;
                times = [design.Start, design.End, design.FadeIn, design.FadeOut];
                source = design.Source;
                locked = design.Locked;
                title = DesignTrackNames.Label(design.Track) + " · " + TechniqueLabel(target, technique);
                showFades = design.Track == DesignTrackKind.Fx;
                break;
            case { LegacyCutIndex: >= 0 } legacyTarget:
                var cut = _legacyPlanInfo!.Cuts[legacyTarget.LegacyCutIndex];
                technique = legacyTarget.LegacyKind switch
                {
                    DesignTrackKind.Layout => cut.Layout,
                    DesignTrackKind.Enter => cut.Enter,
                    DesignTrackKind.Exit => cut.Exit,
                    DesignTrackKind.Treat => cut.Treat,
                    _ => "",
                };
                times = [cut.Start, cut.End, 0, 0];
                source = string.IsNullOrWhiteSpace(cut.Source) ? "auto" : cut.Source;
                locked = false;
                title = DesignTrackNames.Label(legacyTarget.LegacyKind) + " · " + TechniqueLabel(target, technique);
                showFades = false;
                break;
            default:
                return;
        }

        AddSectionTitle(title);
        AddInspector(TechniqueRow(target, technique));
        AddInspector(InfoRow(L.T("StageUi.Inspector.Source"), string.Equals(source, "user", StringComparison.OrdinalIgnoreCase) ? "user" : "auto"));

        bool editable = target.LegacyCutIndex < 0;
        if (editable)
        {
            AddInspector(SegmentTimeRow(L.T("StageUi.Inspector.Start"), () => times[0], v => times[0] = v));
            AddInspector(SegmentTimeRow(L.T("StageUi.Inspector.End"), () => times[1], v => times[1] = v));
            if (showFades)
            {
                AddInspector(SegmentTimeRow(L.T("StageUi.Inspector.FadeIn"), () => times[2], v => times[2] = v));
                AddInspector(SegmentTimeRow(L.T("StageUi.Inspector.FadeOut"), () => times[3], v => times[3] = v));
            }

            AddInspector(SegmentApplyRow(times, showFades));
            var hint = ThemeText(L.T("StageUi.Inspector.SegmentTimeHint"), TextTertiaryKey, 11);
            hint.Margin = new Thickness(0, 0, 0, 2);
            hint.TextWrapping = TextWrapping.Wrap;
            AddInspector(hint);
        }
        else
        {
            AddInspector(InfoRow(L.T("StageUi.Inspector.Start"), Fmt(times[0])));
            AddInspector(InfoRow(L.T("StageUi.Inspector.End"), Fmt(times[1])));
        }

        BuildSegmentParams(target);

        if (editable)
        {
            var lockRow = new Grid { Margin = new Thickness(0, 8, 0, 2) };
            lockRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            lockRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lockLab = ThemeText(L.T("StageUi.Inspector.LockPreventAutoCleanup"), TextSecondaryKey, 12);
            Grid.SetColumn(lockLab, 0);
            lockRow.Children.Add(lockLab);
            var lockToggle = new Wpf.Ui.Controls.ToggleSwitch
            {
                IsChecked = locked,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(12, 0, 0, 0),
            };
            lockToggle.Checked += (_, _) => WriteSegmentLocked(true);
            lockToggle.Unchecked += (_, _) => WriteSegmentLocked(false);
            Grid.SetColumn(lockToggle, 1);
            lockRow.Children.Add(lockToggle);
            AddInspector(lockRow);
        }
    }

    private sealed record TechniqueOption(string Key, string Label);

    private static string ZhLabel(string group, string key) =>
        ZhLabels.Tables.TryGetValue(group, out var table)
        && table.TryGetValue(key, out var label)
        && !string.IsNullOrWhiteSpace(label.Name)
            ? label.Name
            : key;

    private static string TechniqueLabel(SegmentTarget target, string key) => target switch
    {
        { Shot: not null } => ZhLabel("cam", key),
        { Effect: not null } => EffectCatalog.Get(key) is { } def ? EffectName(def) : key,
        { Design: { Track: DesignTrackKind.Trans } } => ZhLabel("trans", key),
        { LegacyCutIndex: >= 0 } legacy => LegacyTechniqueLabel(legacy.LegacyKind, key),
        _ => key,
    };

    private static string LegacyTechniqueLabel(DesignTrackKind kind, string key)
    {
        if (string.IsNullOrWhiteSpace(key) || string.Equals(key, "none", StringComparison.Ordinal)) return L.T("StageUi.Inspector.Off");
        IRegEntry? entry = kind switch
        {
            DesignTrackKind.Layout => Reg.Layouts.GetValueOrDefault(key),
            DesignTrackKind.Enter => Reg.Enter.GetValueOrDefault(key),
            DesignTrackKind.Exit => Reg.Exit.GetValueOrDefault(key),
            DesignTrackKind.Treat => Reg.Treat.GetValueOrDefault(key),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(entry?.Name) ? key : entry.Name!;
    }

    private static IEnumerable<TechniqueOption>? TechniqueOptions(SegmentTarget target) => target switch
    {
        { Shot: not null } => CameraDirection.Presets
            .Select(p => new TechniqueOption(p.Key, ZhLabel("cam", p.Key))),
        { Effect: not null } => EffectCatalog.All
            .Select(d => new TechniqueOption(d.Id, EffectName(d))),
        { Design: { Track: DesignTrackKind.Trans } } => TransitionCatalog.Ids
            .Select(id => new TechniqueOption(id, ZhLabel("trans", id)))
            .OrderBy(o => o.Label, StringComparer.CurrentCulture),
        { LegacyCutIndex: >= 0 } => LegacyOptions(target.LegacyKind),
        _ => null,
    };

    private static string TimelineTechniqueLabel(DesignTrackKind kind, string key) => kind switch
    {
        DesignTrackKind.Cam or DesignTrackKind.LayerCam => ZhLabel("cam", key),
        DesignTrackKind.Trans => ZhLabel("trans", key),
        DesignTrackKind.Layout or DesignTrackKind.Enter or DesignTrackKind.Exit or DesignTrackKind.Treat =>
            LegacyTechniqueLabel(kind, key),
        DesignTrackKind.Fx => EffectCatalog.Get(key) is { } def ? EffectName(def) : key,
        _ => key,
    };

    private static IEnumerable<TechniqueOption> LegacyOptions(DesignTrackKind kind)
    {
        if (kind == DesignTrackKind.Treat)
            return new[] { new TechniqueOption("none", L.T("StageUi.Inspector.Off")) }
                .Concat(Reg.Treat
                    .Where(p => !string.Equals(p.Key, "none", StringComparison.Ordinal))
                    .Select(p => new TechniqueOption(p.Key, p.Value.Name)))
                .OrderBy(o => o.Label, StringComparer.CurrentCulture);

        Dictionary<string, IRegEntry>? entries = kind switch
        {
            DesignTrackKind.Layout => Reg.Layouts.ToDictionary(p => p.Key, p => (IRegEntry)p.Value),
            DesignTrackKind.Enter => Reg.Enter.ToDictionary(p => p.Key, p => (IRegEntry)p.Value),
            DesignTrackKind.Exit => Reg.Exit.ToDictionary(p => p.Key, p => (IRegEntry)p.Value),
            _ => null,
        };

        return entries is null
            ? Array.Empty<TechniqueOption>()
            : entries.Values
                .Select(e => new TechniqueOption(e.Key, string.IsNullOrWhiteSpace(e.Name) ? e.Key : e.Name))
                .OrderBy(o => o.Label, StringComparer.CurrentCulture);
    }

    private FrameworkElement TechniqueRow(SegmentTarget target, string current)
    {
        var options = TechniqueOptions(target)?.ToList();
        if (options is null) return InfoRow(L.T("StageUi.Inspector.Technique"), TechniqueLabel(target, current));

        var selected = options.FirstOrDefault(o => string.Equals(o.Key, current, StringComparison.Ordinal));
        if (selected is null)
        {
            selected = new TechniqueOption(current, TechniqueLabel(target, current));
            options.Insert(0, selected);
        }

        var group = PreviewGroup(target);
        var preview = new Image { Width = 88, Height = 50, Stretch = Stretch.Uniform };
        var thumb = new Border
        {
            Width = 88,
            Height = 50,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            ClipToBounds = true,
            Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1a)),
            Child = preview,
        };
        thumb.SetResourceReference(Border.BorderBrushProperty, CardStrokeKey);
        if (group is null)
        {
            var placeholder = ThemeText("FX", TextTertiaryKey, 11);
            placeholder.HorizontalAlignment = HorizontalAlignment.Center;
            placeholder.VerticalAlignment = VerticalAlignment.Center;
            thumb.Child = placeholder;
        }
        else
        {
            LoadTechniquePreview(preview, group, selected.Key, 128);
        }

        var caption = new StackPanel
        {
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        caption.Children.Add(ThemeText(L.T("StageUi.Inspector.Technique"), TextSecondaryKey, 11));
        caption.Children.Add(ThemeText(selected.Label, TextPrimaryKey, 12));

        var content = new Grid();
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(thumb, 0);
        Grid.SetColumn(caption, 1);
        content.Children.Add(thumb);
        content.Children.Add(caption);

        var chevron = new Wpf.Ui.Controls.SymbolIcon
        {
            Symbol = Wpf.Ui.Controls.SymbolRegular.ChevronDown24,
            FontSize = 12,
            Margin = new Thickness(8, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        chevron.SetResourceReference(Control.ForegroundProperty, TextSecondaryKey);
        Grid.SetColumn(chevron, 2);
        content.Children.Add(chevron);

        var button = new Wpf.Ui.Controls.Button
        {
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(6),
            Margin = new Thickness(0, 4, 0, 2),
        };
        button.Click += (_, _) => ShowTechniquePicker(button, target, options, current, group);
        return button;
    }

    private static string? PreviewGroup(SegmentTarget target) => target switch
    {
        { Shot: not null } => "cam",
        { Design: { Track: DesignTrackKind.Trans } } => "trans",
        { LegacyCutIndex: >= 0 } legacy => PreviewGroup(legacy.LegacyKind),
        _ => null,
    };

    private void ShowTechniquePicker(FrameworkElement target, SegmentTarget segment,
        List<TechniqueOption> options, string current, string? group)
    {
        Popup? popup = null;
        var wrap = new WrapPanel { Width = 292 };

        foreach (var option in options)
        {
            var thumb = new Border
            {
                Width = 128,
                Height = 72,
                CornerRadius = new CornerRadius(4),
                BorderThickness = new Thickness(1),
                ClipToBounds = true,
                Background = new SolidColorBrush(Color.FromRgb(0x16, 0x16, 0x1a)),
            };
            thumb.SetResourceReference(Border.BorderBrushProperty, CardStrokeKey);

            var placeholder = ThemeText(group is null ? "FX" : L.T("StageUi.Inspector.Preview"), TextTertiaryKey, group is null ? 12 : 10);
            placeholder.HorizontalAlignment = HorizontalAlignment.Center;
            placeholder.VerticalAlignment = VerticalAlignment.Center;
            thumb.Child = placeholder;

            var selected = string.Equals(option.Key, current, StringComparison.Ordinal);
            if (selected) EnsureTechniquePreview(thumb, group, option.Key, 160);

            var label = ThemeText(option.Label, TextPrimaryKey, 11, wrap: true);
            label.TextAlignment = TextAlignment.Center;
            label.Margin = new Thickness(2, 4, 2, 2);
            label.MaxWidth = 128;

            var stack = new StackPanel();
            stack.Children.Add(thumb);
            stack.Children.Add(label);

            var card = new Border
            {
                Width = 140,
                Margin = new Thickness(2),
                Padding = new Thickness(4),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),
                Cursor = AppCursors.Hand,
                Child = stack,
                ToolTip = option.Label,
            };
            card.SetResourceReference(Border.BorderBrushProperty, selected ? TextAccentKey : CardStrokeKey);
            if (selected) card.SetResourceReference(Border.BackgroundProperty, SelectFillKey);

            var key = option.Key;
            card.MouseEnter += (_, _) =>
            {
                _techPreviewHost = thumb;
                EnsureTechniquePreview(thumb, group, option.Key, 160);
            };
            card.MouseLeave += (_, _) =>
            {
                if (ReferenceEquals(_techPreviewHost, thumb)) _techPreviewHost = null;
                StopTechniquePreview(thumb.Child as Image);
            };
            card.MouseLeftButtonDown += (_, _) =>
            {
                if (popup is not null) popup.IsOpen = false;
                if (string.Equals(key, current, StringComparison.Ordinal)) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    ApplyTechnique(segment, key);
                    BuildInspector();
                }));
            };

            wrap.Children.Add(card);
        }

        var scroll = new ScrollViewer
        {
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = wrap,
        };

        var root = new Border
        {
            Width = 304,
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6),
            Child = scroll,
        };
        root.Background = TryFindResource("ApplicationBackgroundBrush") as Brush
                          ?? new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x24));
        root.SetResourceReference(Border.BorderBrushProperty, CardStrokeKey);

        popup = new Popup
        {
            PlacementTarget = target,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 4,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = root,
        };
        popup.Closed += (_, _) => StopTechniquePreview();
        popup.IsOpen = true;
    }

    private void ApplyTechnique(SegmentTarget target, string value)
    {
        switch (target)
        {
            case { Shot: { } shot }:
                shot.Preset = value;
                shot.Source = "user";
                if (target.LayerShot)
                {
                    var rng = new CameraRng(unchecked((uint)(shot.Seed == 0 ? _project.LegacySeed : shot.Seed)));
                    var pars = CameraDirection.PlanParams(value, rng);
                    pars["shotScale"] = 1.0;
                    pars["shotX"] = 0;
                    pars["shotY"] = 0;
                    pars["shotRot"] = 0;
                    pars["gain"] = 1.0;
                    shot.Params = pars;
                }
                break;
            case { Effect: { } node }:
                ReplaceEffectNode(node, value);
                break;
            case { Design: { } design }:
                design.Technique = value;
                design.Source = "user";
                break;
            case { LegacyCutIndex: >= 0 } legacy:
                var cut = _legacyPlanInfo!.Cuts[legacy.LegacyCutIndex];
                switch (legacy.LegacyKind)
                {
                    case DesignTrackKind.Layout:
                        if (!string.Equals(cut.Layout, value, StringComparison.Ordinal))
                        {
                            cut.LayoutParams.Clear();
                            cut.LayoutTexts.Clear();
                        }
                        cut.Layout = value;
                        break;
                    case DesignTrackKind.Enter:
                        cut.Enter = value;
                        break;
                    case DesignTrackKind.Exit:
                        cut.Exit = value;
                        break;
                    case DesignTrackKind.Treat:
                        if (!string.Equals(cut.Treat, value, StringComparison.Ordinal))
                        {
                            cut.TreatParams.Clear();
                            cut.TreatTexts.Clear();
                        }
                        cut.Treat = value;
                        break;
                }

                cut.Source = "user";
                RecordCutOverride(cut);
                _pipe?.ApplyLegacyCutOverrides(_legacyPlanInfo);
                Dispatcher.BeginInvoke(new Action(BuildInspector));
                break;
        }

        AfterSegmentEdit();
    }

    private void ReplaceEffectNode(EffectNode oldNode, string defId)
    {
        int index = _effects.Nodes.IndexOf(oldNode);
        if (index < 0) return;

        _evaluator.RemoveCurvesForTarget(oldNode.InstanceId);
        var node = new EffectNode
        {
            DefId = defId,
            InstanceId = oldNode.InstanceId,
            Target = oldNode.Target,
            Source = "user",
            Locked = oldNode.Locked,
            Enabled = oldNode.Enabled,
        };

        foreach (var window in oldNode.Windows) node.Windows.Add(window);
        _effects.Nodes[index] = node;
        _segmentTarget = SegmentTarget.Of(node);
        EffectsChanged();
    }

    private FrameworkElement SegmentTimeRow(string label, Func<double> read, Action<double> capture)
    {
        var box = new TextBox
        {
            Text = read().ToString("0.###", CultureInfo.InvariantCulture),
            FontSize = 11,
        };
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lab = ThemeText(label, TextSecondaryKey, 12);
        Grid.SetColumn(lab, 0);
        row.Children.Add(lab);
        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        box.TextChanged += (_, _) =>
        {
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                capture(v);
        };
        box.GotFocus += (_, _) => box.SelectAll();
        box.LostFocus += (_, _) =>
        {
            if (double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                && Math.Abs(v - read()) > 1e-9)
                capture(v);
        };
        return row;
    }

    private FrameworkElement SegmentApplyRow(double[] times, bool showFades)
    {
        var apply = new Wpf.Ui.Controls.Button
        {
            Content = L.T("StageUi.Inspector.ApplyTime"),
            Margin = new Thickness(0, 6, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        apply.Click += (_, _) =>
        {
            WriteSegmentTime(times[0], times[1], showFades ? times[2] : 0, showFades ? times[3] : 0);
            BuildInspector();
        };
        return apply;
    }

    private void BuildSegmentParams(SegmentTarget target)
    {
        switch (target)
        {
            case { Effect: { } node, WindowIndex: >= 0 } windowTarget:
                AddEffectParams(node, windowTarget.WindowIndex);
                break;
            case { Effect: { } node }:
                AddEffectParams(node, node.Windows.Count > 0 ? 0 : -1);
                break;
            case { Shot: { } shot }:
                AddShotParams(shot, target.LayerShot);
                break;
            case { Design: { } design }:
                AddDesignParams(design);
                break;
            case { LegacyCutIndex: >= 0 } legacy:
                if (_legacyPlanInfo is { } plan && legacy.LegacyCutIndex < plan.Cuts.Count)
                    AddLegacyCutParams(plan, plan.Cuts[legacy.LegacyCutIndex]);
                break;
        }
    }

    private static readonly HashSet<string> LegacyBoolParams = new(StringComparer.Ordinal)
    {
        "mirror", "vertical", "vert", "center", "centered", "bold", "italic",
        "outline", "shadow", "underline", "strike", "reverse", "rev", "flip",
    };

    private static readonly HashSet<string> LegacyIntParams = new(StringComparer.Ordinal)
    {
        "n", "count", "cols", "columns", "rows", "type", "mode", "index",
        "to", "dir", "side", "style",
    };

    private sealed record LegacyRange(double Min, double Max, double Step, bool IsInteger, bool IsBool);

    private void AddLegacyCutParams(LegacyPvPlanInfo plan, LegacyPvCutInfo cut)
    {
        var groups = new (
            string Label,
            Dictionary<string, double> Numbers,
            Dictionary<string, string> Texts,
            Func<LegacyPvCutInfo, Dictionary<string, double>> SelectNumbers,
            Func<LegacyPvCutInfo, Dictionary<string, string>> SelectTexts)[]
        {
            (L.T("StageUi.Inspector.LayoutParams"), cut.LayoutParams, cut.LayoutTexts, c => c.LayoutParams, c => c.LayoutTexts),
            (L.T("StageUi.Inspector.TextTreatmentParams"), cut.TreatParams, cut.TreatTexts, c => c.TreatParams, c => c.TreatTexts),
            (L.T("StageUi.Inspector.BackgroundParams"), cut.BgParams, cut.BgTexts, c => c.BgParams, c => c.BgTexts),
            (L.T("StageUi.Inspector.CameraMoveParams"), cut.CamParams, cut.CamTexts, c => c.CamParams, c => c.CamTexts),
        };

        if (!groups.Any(g => g.Numbers.Count > 0 || g.Texts.Count > 0)) return;

        AddSectionTitle(L.T("StageUi.Inspector.Params"));
        foreach (var (label, numbers, texts, selectNumbers, selectTexts) in groups)
        {
            if (numbers.Count == 0 && texts.Count == 0) continue;
            var caption = ThemeText(label, TextSecondaryKey, 11);
            caption.Margin = new Thickness(0, 6, 0, 2);
            AddInspector(caption);

            foreach (var key in numbers.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var fallback = numbers[key];
                var range = LegacyRangeFor(plan, selectNumbers, key, fallback);
                if (range.IsBool)
                {
                    AddInspector(BoolRow(ParamDisplayName(key), fallback >= 0.5, v =>
                    {
                        numbers[key] = v ? 1 : 0;
                        cut.Source = "user";
                        RecordCutOverride(cut);
                        _pipe?.ApplyLegacyCutOverrides(plan);
                        RefreshTimeline();
                        PushUndo();
                        _saveError = SaveProjectNow();
                    }));
                    continue;
                }

                AddInspector(ConstRow(ParamDisplayName(key), range.Min, range.Max, range.Step,
                    () => numbers.TryGetValue(key, out var value) ? value : fallback,
                    v =>
                    {
                        numbers[key] = range.IsBool ? (v >= 0.5 ? 1 : 0)
                            : range.IsInteger ? Math.Round(v)
                            : v;
                        cut.Source = "user";
                        RecordCutOverride(cut);
                    },
                    () =>
                    {
                        _pipe?.ApplyLegacyCutOverrides(plan);
                        RefreshTimeline();
                    }));
            }

            foreach (var key in texts.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                var fallback = texts[key];
                var suggestions = plan.Cuts
                    .SelectMany(c => selectTexts(c).TryGetValue(key, out var value)
                        ? new[] { value }
                        : Array.Empty<string>())
                    .Where(v => !string.IsNullOrWhiteSpace(v))
                    .Distinct(StringComparer.Ordinal)
                    .OrderBy(v => v, StringComparer.Ordinal)
                    .ToList();

                AddInspector(TextParamRow(ParamDisplayName(key),
                    () => texts.TryGetValue(key, out var value) ? value : fallback,
                    v =>
                    {
                        texts[key] = v;
                        cut.Source = "user";
                        RecordCutOverride(cut);
                    },
                    () =>
                    {
                        _pipe?.ApplyLegacyCutOverrides(plan);
                        RefreshTimeline();
                    },
                    suggestions));
            }
        }
    }

    private static LegacyRange LegacyRangeFor(LegacyPvPlanInfo plan,
        Func<LegacyPvCutInfo, Dictionary<string, double>> select, string key, double value)
    {
        var samples = new List<double>();
        foreach (var cut in plan.Cuts)
        {
            if (cut is null) continue;
            var values = select(cut);
            if (values.TryGetValue(key, out var sample) && double.IsFinite(sample)) samples.Add(sample);
        }

        if (samples.Count == 0) samples.Add(value);

        if (LegacyBoolParams.Contains(key) && samples.All(v => v is 0 or 1))
            return new LegacyRange(0, 1, 1, false, true);

        if (key == "seed") return new LegacyRange(0, 1_000_000_000, 1, true, false);

        var isInteger = LegacyIntParams.Contains(key)
                        || samples.All(v => Math.Abs(v - Math.Round(v)) < 1e-9 && Math.Abs(v) < 1e9);
        if (LegacyParamSpec(key, value) is { } spec)
            return new LegacyRange(spec.Min, spec.Max, spec.Step, isInteger, false);

        var lo = samples.Min();
        var hi = samples.Max();
        var span = Math.Max(hi - lo, Math.Max(Math.Abs(hi) * 0.5, isInteger ? 2 : 0.02));
        if (isInteger)
        {
            var imin = Math.Floor(lo - span * 0.25);
            var imax = Math.Ceiling(hi + span * 0.25);
            if (imax - imin < 2)
            {
                imin -= 1;
                imax += 1;
            }

            return new LegacyRange(imin, imax, 1, true, false);
        }

        var fmin = lo - span * 0.25;
        var fmax = hi + span * 0.25;
        if (!double.IsFinite(fmin) || !double.IsFinite(fmax) || fmax - fmin < 1e-6)
        {
            fmin = value - 1;
            fmax = value + 1;
        }

        return new LegacyRange(fmin, fmax, Math.Max(0.0001, (fmax - fmin) / 200), false, false);
    }

    private FrameworkElement TextParamRow(string label, Func<string> read, Action<string> write,
        Action? onCommit = null, IReadOnlyList<string>? suggestions = null)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lab = ThemeText(label, TextSecondaryKey, 12);
        lab.TextTrimming = TextTrimming.CharacterEllipsis;
        lab.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(lab);

        var box = new ComboBox
        {
            IsEditable = true,
            IsTextSearchEnabled = false,
            Text = read() ?? "",
            FontSize = 11,
            Height = 24,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        if (suggestions is not null)
            foreach (var suggestion in suggestions) box.Items.Add(suggestion);
        Grid.SetColumn(box, 1);
        row.Children.Add(box);

        void Commit()
        {
            var value = box.Text.Trim();
            if (string.Equals(value, read(), StringComparison.Ordinal)) return;
            write(value);
            onCommit?.Invoke();
            PushUndo();
            _saveError = SaveProjectNow();
        }

        box.SelectionChanged += (_, _) => Commit();
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Commit();
                Keyboard.ClearFocus();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                box.Text = read() ?? "";
                Keyboard.ClearFocus();
                e.Handled = true;
            }
        };

        return row;
    }

    private static double CleanLegacyValue(string key, double value)
    {
        if (LegacyBoolParams.Contains(key)) return value >= 0.5 ? 1 : 0;
        if (LegacyIntParams.Contains(key)) return Math.Round(value);
        return value;
    }

    private static (double Min, double Max, double Step)? LegacyParamSpec(string key, double value)
    {
        return key switch
        {
            "rot" or "tilt" => (-180, 180, 1),
            "scale" or "size" or "z" => (0.1, 4, 0.01),
            "tracking" or "leading" or "gap" => (-2, 2, 0.01),
            "a" or "amp" => Math.Abs(value) > 10 ? (-360, 360, 1) : (-10, 10, 0.01),
            _ => null,
        };
    }

    private void AddEffectParams(EffectNode node, int windowIndex)
    {
        var def = EffectCatalog.Get(node.DefId);
        if (def is null || def.Params.Length == 0) return;

        AddSectionTitle(L.T("StageUi.Inspector.Params"));
        AddInspector(EffectStageRow(node));
        AddInspector(EffectBlendRow(node));
        AddInspector(ConstRow(L.T("StageUi.Inspector.OpacityAmount"), 0, 1, 0.01,
            () => node.Opacity,
            v =>
            {
                node.Opacity = (float)Math.Clamp(v, 0, 1);
                MarkFxUser(node);
            },
            () =>
            {
                EffectsChanged();
                RefreshTimeline();
            }));
        if (windowIndex >= 0 && windowIndex < node.Windows.Count && node.Windows[windowIndex].IsValid)
            AddInspector(EffectWindowGainRow(node, windowIndex));

        foreach (var p in def.Params)
        {
            switch (p.Kind)
            {
                case EffectParamKind.Number:
                    AddInspector(EffectNumberRow(node, p));
                    break;
                case EffectParamKind.Bool:
                    AddInspector(EffectBoolRow(node, p));
                    break;
                case EffectParamKind.Color:
                    AddInspector(EffectColorRow(node, p));
                    break;
            }
        }
    }

    private FrameworkElement EffectStageRow(EffectNode node)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lab = ThemeText(L.T("StageUi.Inspector.EffectStage"), TextSecondaryKey, 12);
        Grid.SetColumn(lab, 0);
        row.Children.Add(lab);

        var combo = new ComboBox { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.Items.Add(new ComboBoxItem { Content = L.T("StageUi.Inspector.GlobalPost"), Tag = EffectStage.GlobalPost });
        combo.Items.Add(new ComboBoxItem { Content = L.T("StageUi.Inspector.AfterCompositeBeforeTransition"), Tag = EffectStage.Composite });
        combo.SelectedIndex = node.Stage == EffectStage.Composite ? 1 : 0;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not ComboBoxItem { Tag: EffectStage stage }) return;
            if (node.Stage == stage) return;
            node.Stage = stage;
            if (stage != EffectStage.GlobalPost) node.Target = "";
            MarkFxUser(node);
            EffectsChanged();
            RefreshTimeline();
            PushUndo();
            _saveError = SaveProjectNow();
        };
        Grid.SetColumn(combo, 1);
        row.Children.Add(combo);
        return row;
    }

    private FrameworkElement EffectBlendRow(EffectNode node)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lab = ThemeText(L.T("StageUi.Inspector.BlendMode"), TextSecondaryKey, 12);
        Grid.SetColumn(lab, 0);
        row.Children.Add(lab);

        var combo = new ComboBox { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var blend in Enum.GetValues<EffectBlend>())
            combo.Items.Add(new ComboBoxItem { Content = BlendLabel(blend), Tag = blend });
        combo.SelectedIndex = (int)node.Blend;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not ComboBoxItem { Tag: EffectBlend blend }) return;
            if (node.Blend == blend) return;
            node.Blend = blend;
            MarkFxUser(node);
            EffectsChanged();
            RefreshTimeline();
            PushUndo();
            _saveError = SaveProjectNow();
        };
        Grid.SetColumn(combo, 1);
        row.Children.Add(combo);
        return row;
    }

    private static string BlendLabel(EffectBlend blend) => blend switch
    {
        EffectBlend.Normal => L.T("StageUi.Inspector.BlendNormal"),
        EffectBlend.Add => L.T("StageUi.Inspector.BlendAdd"),
        EffectBlend.Screen => L.T("StageUi.Inspector.BlendScreen"),
        EffectBlend.Multiply => L.T("StageUi.Inspector.BlendMultiply"),
        EffectBlend.Overlay => L.T("StageUi.Inspector.BlendOverlay"),
        _ => blend.ToString(),
    };

    private FrameworkElement EffectWindowGainRow(EffectNode node, int windowIndex)
    {
        return ConstRow(L.T("StageUi.Inspector.WindowGain"), 0, 2.5, 0.05,
            () => windowIndex < node.Windows.Count ? node.Windows[windowIndex].Gain : 1,
            v =>
            {
                if (windowIndex >= node.Windows.Count) return;
                var window = node.Windows[windowIndex];
                node.Windows[windowIndex] = window with { Gain = v };
                MarkFxUser(node);
            },
            () => EffectsChanged());
    }

    private FrameworkElement EffectNumberRow(EffectNode node, EffectParam p)
    {
        var addr = new ParamAddress(ParamAddressKind.Effect, node.InstanceId, p.Key);
        return AnimParamRow(_inspectorRows, addr, ParamLabel(p), p.Min, p.Max, Math.Max(0.0001, p.Step),
            () => node.Value(p),
            v =>
            {
                node.Set(p, (float)v);
                MarkFxUser(node);
                EffectsChanged();
            });
    }

    private FrameworkElement EffectBoolRow(EffectNode node, EffectParam p)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lab = ThemeText(ParamLabel(p), TextSecondaryKey, 12);
        lab.TextTrimming = TextTrimming.CharacterEllipsis;
        row.Children.Add(lab);

        var toggle = new Wpf.Ui.Controls.ToggleSwitch
        {
            IsChecked = node.Value(p) > 0.5f,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Grid.SetColumn(toggle, 1);
        row.Children.Add(toggle);

        var key = new Wpf.Ui.Controls.Button
        {
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
            Content = "◆K",
            FontSize = 11,
            Padding = new Thickness(2, 1, 2, 1),
            ToolTip = L.T("StageUi.Inspector.KeyBoolParamHint"),
        };
        key.Click += (_, _) => KeyEffectBoolAtPlayhead(node, p, toggle.IsChecked == true);
        Grid.SetColumn(key, 2);
        row.Children.Add(key);

        void Write(bool value)
        {
            if (_syncingRows) return;

            var addr = new ParamAddress(ParamAddressKind.Effect, node.InstanceId, p.Key);
            var t = _audio.Time;
            if (_evaluator.Curves.TryGetValue(addr, out var curve) && curve.Count > 0)
            {
                var mode = ModeOfKeyAt(curve, t) ?? CurveInterp.Step;
                curve.Remove(t);
                curve.TryAdd(t, value ? 1 : 0, mode);
                PublishDirector();
            }
            else
            {
                node.Set(p, value ? 1f : 0f);
            }

            MarkFxUser(node);
            EffectsChanged();
            RefreshTimeline();
            PushUndo();
            _saveError = SaveProjectNow();
        }

        toggle.Checked += (_, _) => Write(true);
        toggle.Unchecked += (_, _) => Write(false);
        _effectBoolRows.Add((node, p, toggle));
        return row;
    }

    private void KeyEffectBoolAtPlayhead(EffectNode node, EffectParam p, bool value)
    {
        var addr = new ParamAddress(ParamAddressKind.Effect, node.InstanceId, p.Key);
        var t = _audio.Time;
        var curve = EnsureCurve(addr, node.Value(p), t);
        curve.Remove(t);
        curve.TryAdd(t, value ? 1 : 0, CurveInterp.Step);
        MarkFxUser(node);
        PublishDirector();
        RefreshParamRows();
        RefreshTimeline();
        PushUndo();
        _saveError = SaveProjectNow();
    }

    private FrameworkElement EffectColorRow(EffectNode node, EffectParam p)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lab = ThemeText(ParamLabel(p), TextSecondaryKey, 12);
        lab.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(lab, 0);
        row.Children.Add(lab);

        var initial = ResolveEffectColor(node, p, _audio.Time);
        var box = new Wpf.Ui.Controls.TextBox
        {
            Text = initial,
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
            Background = BrushFromHex(initial),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Cursor = AppCursors.Hand,
            ToolTip = L.T("StageUi.Inspector.OpenColorPicker"),
        };
        swatch.SetResourceReference(Border.BorderBrushProperty, CardStrokeKey);

        var key = new Wpf.Ui.Controls.Button
        {
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
            Content = "◆K",
            FontSize = 11,
            Padding = new Thickness(2, 1, 2, 1),
            ToolTip = L.T("StageUi.Inspector.KeyColorParamHint"),
        };
        key.Click += (_, _) => KeyEffectColorAtPlayhead(node, p);
        Grid.SetColumn(key, 3);
        row.Children.Add(key);

        void Commit()
        {
            var hex = box.Text.Trim();
            if (!IsHexColor(hex))
            {
                box.Text = ResolveEffectColor(node, p, _audio.Time);
                return;
            }

            swatch.Background = BrushFromHex(hex);
            if (string.Equals(hex, ResolveEffectColor(node, p, _audio.Time), StringComparison.OrdinalIgnoreCase)) return;
            WriteEffectColor(node, p, hex, _audio.Time);
            MarkFxUser(node);
            EffectsChanged();
            RefreshTimeline();
            PushUndo();
            _saveError = SaveProjectNow();
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Commit();
            e.Handled = true;
        };
        swatch.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var current = IsHexColor(box.Text.Trim())
                ? box.Text.Trim()
                : ResolveEffectColor(node, p, _audio.Time);
            Dispatcher.BeginInvoke(new Action(() => ColorPickerPopup.Show(swatch, current, hex =>
            {
                box.Text = hex;
                swatch.Background = BrushFromHex(hex);
                Commit();
            }, hex => swatch.Background = BrushFromHex(hex))));
        };

        Grid.SetColumn(box, 1);
        row.Children.Add(box);
        Grid.SetColumn(swatch, 2);
        row.Children.Add(swatch);
        _effectColorRows.Add((node, p, box, swatch));
        return row;
    }

    private static ParamAddress ColorChannel(EffectNode node, EffectParam p, char channel) =>
        new(ParamAddressKind.Effect, node.InstanceId, $"{p.Key}.{channel}");

    private bool HasEffectColorCurves(EffectNode node, EffectParam p)
    {
        foreach (var channel in "rgb")
        {
            var addr = ColorChannel(node, p, channel);
            if (_evaluator.Curves.TryGetValue(addr, out var curve) && curve.Count > 0) return true;
        }

        return false;
    }

    private string ResolveEffectColor(EffectNode node, EffectParam p, double time)
    {
        var (r, g, b) = EffectNode.ParseHex(node.ColorValue(p));
        foreach (var channel in "rgb")
        {
            var addr = ColorChannel(node, p, channel);
            if (!_evaluator.Curves.TryGetValue(addr, out var curve) || curve.Count == 0) continue;
            var value = (float)Math.Clamp(curve.Evaluate(time), 0, 1);
            if (channel == 'r') r = value;
            else if (channel == 'g') g = value;
            else b = value;
        }

        return HexFromRgb(r, g, b);
    }

    private void WriteEffectColor(EffectNode node, EffectParam p, string hex, double time)
    {
        if (!HasEffectColorCurves(node, p))
        {
            node.SetColor(p, hex);
            return;
        }

        var (baseR, baseG, baseB) = EffectNode.ParseHex(node.ColorValue(p));
        var (r, g, b) = EffectNode.ParseHex(hex);
        WriteColorChannel(ColorChannel(node, p, 'r'), r, baseR, time);
        WriteColorChannel(ColorChannel(node, p, 'g'), g, baseG, time);
        WriteColorChannel(ColorChannel(node, p, 'b'), b, baseB, time);
        PublishDirector();
    }

    private void WriteColorChannel(ParamAddress addr, double value, double rest, double time)
    {
        var curve = EnsureCurve(addr, rest, time);
        var mode = ModeOfKeyAt(curve, time) ?? CurveInterp.Linear;
        curve.Remove(time);
        curve.TryAdd(time, value, mode);
    }

    private void KeyEffectColorAtPlayhead(EffectNode node, EffectParam p)
    {
        var time = _audio.Time;
        var (baseR, baseG, baseB) = EffectNode.ParseHex(node.ColorValue(p));
        var (r, g, b) = EffectNode.ParseHex(ResolveEffectColor(node, p, time));
        WriteColorChannel(ColorChannel(node, p, 'r'), r, baseR, time);
        WriteColorChannel(ColorChannel(node, p, 'g'), g, baseG, time);
        WriteColorChannel(ColorChannel(node, p, 'b'), b, baseB, time);
        MarkFxUser(node);
        PublishDirector();
        RefreshParamRows();
        RefreshTimeline();
        PushUndo();
        _saveError = SaveProjectNow();
    }

    private static string HexFromRgb(double r, double g, double b) =>
        $"#{ToByte(r):X2}{ToByte(g):X2}{ToByte(b):X2}";

    private static int ToByte(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);

    private void AddShotParams(CameraShot shot, bool layerShot)
    {
        var keys = new List<string> { "shotScale", "shotX", "shotY", "shotRot", "gain" };
        foreach (var key in shot.Params.Keys)
            if (!keys.Contains(key, StringComparer.Ordinal)) keys.Add(key);

        AddSectionTitle(L.T("StageUi.Inspector.Params"));
        foreach (var key in keys)
        {
            var (min, max, step) = CameraParamSpec(key, ReadShotParam(shot, key));
            AddInspector(ConstRow(ParamDisplayName(key), min, max, step,
                () => ReadShotParam(shot, key),
                v =>
                {
                    shot.Params[key] = CleanCameraValue(key, v);
                    shot.Source = "user";
                    if (layerShot
                        && _project.LayerCameraTracks.TryGetValue(PvScene.CharacterLayerId, out var track))
                        track.UserEdited = true;
                },
                () =>
                {
                    PublishDirector();
                    RefreshTimeline();
                }));
        }
    }

    private static double ReadShotParam(CameraShot shot, string key) =>
        shot.Params.TryGetValue(key, out var value) && double.IsFinite(value)
            ? value
            : key switch
            {
                "shotScale" or "gain" => 1,
                _ => 0,
            };

    private static double CleanCameraValue(string key, double value) => key switch
    {
        "dir" or "side" or "n" or "s0" => Math.Round(value),
        "vert" or "rev" => value >= 0.5 ? 1 : 0,
        _ => value,
    };

    private static (double Min, double Max, double Step) CameraParamSpec(string key, double value)
    {
        return key switch
        {
            "shotScale" => (0.5, 2.0, 0.01),
            "shotX" or "shotY" => (-0.25, 0.25, 0.005),
            "shotRot" => (-15, 15, 0.5),
            "gain" => (0, 2, 0.01),
            "dir" => (-2, 3, 1),
            "side" => (-1, 1, 1),
            "vert" or "rev" => (0, 1, 1),
            "a" or "amp" => Math.Abs(value) > 10 ? (-360, 360, 1) : (-10, 10, 0.01),
            "per" => (0.1, 10, 0.05),
            "d" => (0.05, 3, 0.01),
            "b" => (0, 40, 0.5),
            "at" => (0, 1, 0.01),
            "f" => (0.1, 30, 0.1),
            "jerk" => (0, 2, 0.05),
            "z" => (0.5, 3, 0.01),
            "r" => (-180, 180, 1),
            "s0" => (0, 999, 1),
            "dx" or "dy" => (-1, 1, 0.01),
            "a0" => (-6.28, 6.28, 0.01),
            "n" => (2, 12, 1),
            "amount" => (0, 2, 0.01),
            _ => FallbackRange(value),
        };
    }

    private void AddDesignParams(DesignSegment design)
    {
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        if (design.Track == DesignTrackKind.Trans && TransitionCatalog.Get(design.Technique) is not null)
        {
            var seed = design.Seed != 0 ? design.Seed : PvStageRenderer.SeedFor(design.Start);
            foreach (var (key, value) in TransitionRunner.Defaults(design.Technique, seed))
                values[key] = value;
        }

        foreach (var (key, value) in design.Params)
            values[key] = value;

        if (values.Count == 0) return;

        AddSectionTitle(L.T("StageUi.Inspector.Params"));
        foreach (var key in values.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var fallback = values[key];
            var (min, max, step) = TransitionParamSpec(design.Technique, key, fallback);
            AddInspector(ConstRow(ParamDisplayName(key), min, max, step,
                () => design.Params.TryGetValue(key, out var value) ? value : fallback,
                v =>
                {
                    design.Params[key] = CleanTransitionValue(key, v);
                    design.Source = "user";
                },
                () =>
                {
                    PublishDirector();
                    RefreshTimeline();
                }));
        }
    }

    private static double CleanTransitionValue(string key, double value) => key switch
    {
        "dir" or "n" or "rows" or "cols" or "c" or "seed" => Math.Round(value),
        "vert" or "rev" or "rtl" or "up" or "roll" or "half" or "tate" or "useAccent" =>
            value >= 0.5 ? 1 : 0,
        _ => value,
    };

    private static (double Min, double Max, double Step) TransitionParamSpec(string technique, string key, double value)
    {
        if (technique == "diagonalWipe" && key == "k") return (-1, 1, 0.01);
        if (technique == "pixelate" && key == "k") return (2, 32, 1);
        if (technique == "zoomThrough" && key == "z") return (1, 3, 0.01);

        return key switch
        {
            "dir" => (-2, 3, 1),
            "n" => (2, 20, 1),
            "rows" or "cols" => (2, 12, 1),
            "vert" or "rev" or "rtl" or "up" or "roll" or "half" or "tate" or "useAccent" => (0, 1, 1),
            "x" or "y" => (-0.5, 1.5, 0.01),
            "z" => (1, 3, 0.01),
            "rot" => (-360, 360, 1),
            "a0" => (-180, 180, 1),
            "c" => (0, 3, 1),
            "o" => (-0.2, 0.2, 0.005),
            "r" => (0, 1, 0.01),
            "seed" => (0, 1_000_000_000, 1),
            _ => FallbackRange(value),
        };
    }

    private static (double Min, double Max, double Step) FallbackRange(double value)
    {
        var span = Math.Max(1, Math.Abs(value) * 4);
        return (-span, span, Math.Max(0.0001, span / 100));
    }

    private FrameworkElement TransitionAddRow()
    {
        var panel = new StackPanel();
        var combo = new ComboBox { FontSize = 12 };
        foreach (var id in TransitionCatalog.Ids.OrderBy(id => ZhLabel("trans", id), StringComparer.CurrentCulture))
            combo.Items.Add(new ComboBoxItem { Content = ZhLabel("trans", id), Tag = id });
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        panel.Children.Add(combo);

        var add = new Wpf.Ui.Controls.Button
        {
            Content = L.T("StageUi.Inspector.AddTransitionAtPlayhead"),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 0),
        };
        add.Click += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem { Tag: string id })
                AddGlobalTransition(id);
        };
        panel.Children.Add(add);
        return panel;
    }

    private void AddGlobalTransition(string technique)
    {
        double start = Math.Max(0, _audio.Time);
        double end = start + 0.35;
        var duration = ProjectDuration();
        if (duration > start + 0.1) end = Math.Min(end, duration);
        if (end <= start + 0.05) end = start + 0.35;

        var segment = new DesignSegment
        {
            Track = DesignTrackKind.Trans,
            Start = start,
            End = end,
            Technique = technique,
            Source = "user",
            RefId = "user_trans_" + Guid.NewGuid().ToString("N")[..8],
        };
        _project.DesignSegments.Add(segment);
        _project.DesignSegments.Sort((a, b) => a.Start.CompareTo(b.Start));

        _segmentTarget = SegmentTarget.Of(segment);
        PublishDirector();
        RefreshTimeline();
        BuildInspector();
        PushUndo();
        _saveError = SaveProjectNow();
        TblStatus.Text = L.F("StageUi.Inspector.AddedTransition", ZhLabel("trans", technique));
    }

    private void AddSectionTitle(string text)
    {
        var content = new StackPanel();
        var body = new StackPanel();
        body.Children.Add(new TextBlock
        {
            Text = text,
            Style = TryFindResource("InspectorSectionTitle") as Style,
        });
        body.Children.Add(content);

        InspectorStack.Children.Add(new Border
        {
            Child = body,
            Style = TryFindResource("InspectorCard") as Style,
        });
        _inspectorTarget = content;
    }

    private void AddInspector(UIElement element)
    {
        if (element is FrameworkElement { Margin: { } margin } fe && margin == default)
            fe.Margin = new Thickness(0, 0, 0, 6);
        _inspectorTarget.Children.Add(element);
    }

    private FrameworkElement InfoRow(string label, string value, string? tip = null)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lab = ThemeText(label, TextSecondaryKey, 12);
        Grid.SetColumn(lab, 0);
        row.Children.Add(lab);

        var val = ThemeText(value, TextPrimaryKey, 12);
        val.TextTrimming = TextTrimming.CharacterEllipsis;
        val.ToolTip = tip;
        Grid.SetColumn(val, 1);
        row.Children.Add(val);
        return row;
    }

    private static string ParamDisplayName(string param) => param switch
    {
        "offsetX" => L.T("StageUi.Inspector.OffsetX"),
        "offsetY" => L.T("StageUi.Inspector.OffsetY"),
        "scale" => L.T("StageUi.Inspector.Scale"),
        "rotation" => L.T("StageUi.Inspector.Rotation"),
        "opacity" => L.T("StageUi.Inspector.Opacity"),
        "gain" => L.T("StageUi.Inspector.Strength"),
        "a" => L.T("StageUi.Inspector.Amplitude"),
        "dir" => L.T("StageUi.Inspector.Direction"),
        "sp" => L.T("StageUi.Inspector.Speed"),
        "per" => L.T("StageUi.Inspector.Period"),
        "d" => L.T("StageUi.Inspector.Duration"),
        "b" => L.T("StageUi.Inspector.Blur"),
        "at" => L.T("StageUi.Inspector.TriggerPoint"),
        "f" => L.T("StageUi.Inspector.Frequency"),
        "jerk" => L.T("StageUi.Inspector.Jitter"),
        "z" => L.T("StageUi.Inspector.Scale"),
        "r" => L.T("StageUi.Inspector.Rotation"),
        "s0" => L.T("StageUi.Inspector.Seed"),
        "vert" => L.T("StageUi.Inspector.Vertical"),
        "dx" => L.T("StageUi.Inspector.AmplitudeX"),
        "dy" => L.T("StageUi.Inspector.AmplitudeY"),
        "a0" => L.T("StageUi.Inspector.Phase"),
        "side" => L.T("StageUi.Inspector.Direction"),
        "amp" => L.T("StageUi.Inspector.Amplitude"),
        "amount" => L.T("StageUi.Inspector.Strength"),
        "shotScale" => L.T("StageUi.Inspector.ShotScale"),
        "shotX" => L.T("StageUi.Inspector.CompositionX"),
        "shotY" => L.T("StageUi.Inspector.CompositionY"),
        "shotRot" => L.T("StageUi.Inspector.Tilt"),
        "n" => L.T("StageUi.Inspector.Count"),
        "rows" => L.T("StageUi.Inspector.Rows"),
        "cols" => L.T("StageUi.Inspector.Columns"),
        "rev" => L.T("StageUi.Inspector.Reverse"),
        "rtl" => L.T("StageUi.Inspector.RightToLeft"),
        "up" => L.T("StageUi.Inspector.Upward"),
        "roll" => L.T("StageUi.Inspector.Roll"),
        "half" => L.T("StageUi.Inspector.HalfScreen"),
        "tate" => L.T("StageUi.Inspector.VerticalText"),
        "useAccent" => L.T("StageUi.Inspector.UseAccentColor"),
        "c" => L.T("StageUi.Inspector.Corner"),
        "o" => L.T("StageUi.Inspector.Offset"),
        "k" => L.T("StageUi.Inspector.Strength"),
        "seed" => L.T("StageUi.Inspector.Seed"),
        _ => param,
    };

    private string TrackLabel(in ParamAddress addr)
    {
        var name = ParamDisplayName(addr.Param);
        switch (addr.Kind)
        {
            case ParamAddressKind.Camera:
                return L.F("StageUi.Inspector.CameraLabelFormat", name);
            case ParamAddressKind.Vts:
                return L.F("StageUi.Inspector.CharacterMoveFormat", addr.Param switch
                {
                    "x" => L.T("StageUi.Inspector.OffsetX"),
                    "y" => L.T("StageUi.Inspector.OffsetY"),
                    "size" => L.T("StageUi.Inspector.ScaleZ"),
                    "rotation" => L.T("StageUi.Inspector.Rotation"),
                    _ => addr.Param,
                });
            case ParamAddressKind.Layer:
                foreach (var layer in _scene.Layers)
                    if (string.Equals(layer.Id, addr.Target, StringComparison.Ordinal))
                    {
                        var prefix = _scene.IsCharacterLayer(layer)
                            ? L.T("StageUi.Inspector.Character")
                            : LayerTitle(layer);
                        return prefix + " · " + name;
                    }
                return addr.Target + " · " + name;
            default:
                foreach (var node in _effects.Nodes)
                    if (string.Equals(node.InstanceId, addr.Target, StringComparison.Ordinal))
                        return EffectName(node) + " · " + EffectParamLabel(node, addr.Param);
                return addr.Target + " · " + name;
        }
    }

    private static string EffectParamLabel(EffectNode node, string key) =>
        EffectCatalog.Get(node.DefId)?.Params.FirstOrDefault(p => p.Key == key) is { } p ? ParamLabel(p) : key;
}

public enum InspectorSectionKind
{
    Camera,
    Layer,
}

public static class ValueSourceBadge
{
    public static string Curve => L.T("StageUi.Inspector.Curve");
    public static string Constant => L.T("StageUi.Inspector.Constant");

    public static string Label(bool hasCurve) => hasCurve ? Curve : Constant;
}

public static class InspectorLayout
{
    public static IReadOnlyList<InspectorSectionKind> Sections(bool hasLayerSelection) =>
        hasLayerSelection
            ? [InspectorSectionKind.Layer]
            : [InspectorSectionKind.Camera];
}
