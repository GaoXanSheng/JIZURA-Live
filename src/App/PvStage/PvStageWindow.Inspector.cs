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
                Cursor = Cursors.Hand,
                ToolTip = L.T("点击输入精确数值"),
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
    private bool _syncingRows;
    private Panel _inspectorTarget = null!;
    private TabControl? _inspectorTabs;
    private string? _inspectorTab;
    private bool _buildingInspector;

    private void SelectLayer(PvLayerConfig? layer)
    {
        _selectedLayer = layer;
        SyncStageSelection();
        HighlightLayerCards();
        TheTimeline.ClearDesignSelection();
        BuildInspector();
    }

    private void HighlightLayerCards()
    {
        foreach (var card in LayersHost.Children.OfType<Wpf.Ui.Controls.Card>())
        {
            var active = ReferenceEquals(card.Tag, _selectedLayer);
            card.SetResourceReference(Control.BackgroundProperty, active ? SelectFillKey : CardFillKey);
            card.SetResourceReference(Control.BorderBrushProperty, active ? SelectStrokeKey : CardStrokeKey);
        }

        TblInspectorTitle.Text = _selectedLayer is null ? L.T("工程") : LayerTitle(_selectedLayer);
    }

    private static string LayerTitle(PvLayerConfig layer) =>
        string.IsNullOrWhiteSpace(layer.SpoutSenderName) ? layer.Name : layer.SpoutSenderName!;

    private void BuildInspector()
    {
        _inspectorRows.Clear();
        InspectorHost.Children.Clear();
        _inspectorTabs = null;
        _inspectorTarget = InspectorHost;
        _buildingInspector = true;

        try
        {
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
                        case InspectorSectionKind.Project:
                            BuildProjectSection();
                            break;
                        case InspectorSectionKind.Layer:
                            BuildLayerSection(_selectedLayer!);
                            break;
                    }
                }
            }
        }
        finally
        {
            _buildingInspector = false;
        }

        if (_inspectorTabs is not null && _inspectorTab is { } keep)
        {
            foreach (TabItem item in _inspectorTabs.Items)
                if (string.Equals(item.Header as string, keep, StringComparison.Ordinal))
                {
                    item.IsSelected = true;
                    break;
                }
        }

        if (_inspectorTabs?.SelectedItem is TabItem { Header: string selected })
            _inspectorTab = selected;

        RefreshParamRows();
    }

    private void BuildCameraSection()
    {
        AddSectionTitle(L.T("整体相机"));
        AddInspector(CameraRow("offsetX", L.T("位移X"), 1));
        AddInspector(CameraRow("offsetY", L.T("位移Y"), 1));
        AddInspector(CameraRow("scale", L.T("缩放"), 0.01));
        AddInspector(CameraRow("rotation", L.T("旋转"), 1));
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
        AddSectionTitle(L.T("图层"));
        AddInspector(ThemeText(
            L.T("选中图层后，画面里会出现方框；拖框内移动，拖四角缩放，拖上方圆点旋转，Shift 锁定方向；已有对应关键帧时在播放头自动打帧。VTS 图层可改为直接控制 VTS 坐标。"),
            TextTertiaryKey, 11, wrap: true));
        AddInspector(LayerRow(layer, "offsetX", L.T("位移X"), 0.01));
        AddInspector(LayerRow(layer, "offsetY", L.T("位移Y"), 0.01));
        AddInspector(LayerRow(layer, "scale", L.T("缩放"), 0.01));
        AddInspector(LayerRow(layer, "opacity", L.T("不透明"), 0.02));
        AddInspector(LayerRow(layer, "rotation", L.T("旋转"), 1));
        AddInspector(ConstRow(L.T("深度"), 0, 3, 0.01,
            () => layer.Parallax.Depth, v => layer.Parallax.Depth = v));
        AddInspector(ConstRow(L.T("跟随运镜"), 0, 1, 0.01,
            () => layer.Parallax.Follow ?? layer.Parallax.Depth,
            v => layer.Parallax.Follow = v));
        AddInspector(ConstRow(L.T("环境光"), 0, 1, 0.02,
            () => layer.VisualBlend.AmbientTintIntensity, v => layer.VisualBlend.AmbientTintIntensity = v));
        AddInspector(ConstRow(L.T("边缘光"), 0, 1, 0.02,
            () => layer.VisualBlend.RimGlow, v => layer.VisualBlend.RimGlow = v));
        AddInspector(ConstRow(L.T("阴影"), 0, 1, 0.02,
            () => layer.VisualBlend.DropShadow, v => layer.VisualBlend.DropShadow = v));

        if (!string.IsNullOrWhiteSpace(layer.SpoutSenderName))
        {
            var isCharacter = layer.VtsCharacter
                              ?? string.Equals(layer.Id, PvScene.CharacterLayerId, StringComparison.Ordinal);
            AddInspector(BoolRow(L.T("VTS 角色来源"), isCharacter,
                v => SetVtsCharacterSource(layer, v)));
            AddInspector(BoolRow(L.T("预乘 Alpha"), layer.Premultiplied, v =>
            {
                layer.Premultiplied = v;
                RebuildStage();
            }));
        }

        if (_scene.IsCharacterLayer(layer))
            BuildLayerCameraSection(layer);

    }

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
        TblStatus.Text = on ? L.F("已设为 VTS 角色来源：{0}", LayerTitle(layer)) : L.T("已取消 VTS 角色来源");
    }

    private string LayerTrackId(PvLayerConfig layer) =>
        _scene.IsCharacterLayer(layer) ? PvScene.CharacterLayerId : layer.Id;

    private void BuildLayerCameraSection(PvLayerConfig layer)
    {
        AddSectionTitle(L.T("角色运镜"));
        AddInspector(ThemeText(
            L.T("角色默认跟随自己的运镜轨；跟随运镜 0 = 完全用角色运镜轨，1 = 完全跟全局运镜。"),
            TextSecondaryKey, 11, wrap: true));

        var combo = new ComboBox { FontSize = 12, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var preset in CameraDirection.Presets)
            combo.Items.Add(new ComboBoxItem { Content = ZhLabel("cam", preset.Key), Tag = preset.Key });
        combo.SelectedIndex = 0;
        AddInspector(combo);

        var add = new Wpf.Ui.Controls.Button
        {
            Content = L.T("添加角色运镜"),
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
            Content = L.T("拷贝全局运镜"),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(0, 6, 0, 0),
        };
        copy.Click += (_, _) => CopyGlobalCameraToLayer(layer);
        AddInspector(copy);

        if (_project.LayerCameraTracks.TryGetValue(LayerTrackId(layer), out var track) && track.Shots.Count > 0)
            AddInspector(ThemeText(
                L.F("已有 {0} 段，可在时间线“角色运镜”轨选中编辑。", track.Shots.Count),
                TextSecondaryKey, 11, wrap: true));

        BuildCharacterMoveSection();
        BuildCharacterExpressionSection();
        BuildCharacterTintSection();
    }

    private void CopyGlobalCameraToLayer(PvLayerConfig layer)
    {
        var source = _project.CameraTrack;
        if (source is null || source.Shots.Count == 0)
        {
            TblStatus.Text = L.T("全局运镜轨为空，先一键生成或手动添加运镜");
            return;
        }

        var copy = source.Clone();
        copy.UserEdited = false;
        _project.LayerCameraTracks[LayerTrackId(layer)] = copy;
        layer.Parallax.Follow = 0;
        _evaluator = DirectorEvaluator.FromSaves(_project.Curves, _project.CameraTrack, _project.LayerCameraTracks);
        PublishDirector();
        RefreshTimeline();
        BuildInspector();
        _saveError = SaveProjectNow();
        TblStatus.Text = L.F("已拷贝 {0} 段全局运镜到角色运镜轨", copy.Shots.Count);
    }

    private void AddLayerCameraShot(PvLayerConfig layer, string preset)
    {
        if (!_project.LayerCameraTracks.TryGetValue(LayerTrackId(layer), out var track))
        {
            track = new CameraTrack
            {
                Width = _scene.Width,
                Height = _scene.Height,
                BeatLen = _beatMap is { Bpm: > 0 } bm ? 60.0 / bm.Bpm : 0.5,
                Beats = _beatMap is { } map ? new List<double>(map.Beats) : new List<double>(),
            };
            _project.LayerCameraTracks[LayerTrackId(layer)] = track;
        }

        double start = Math.Max(0, _audio.Time);
        double end = Math.Min(TimelineSpan(), start + Math.Max(2.0, track.BeatLen * 8));
        if (end <= start + 0.2) end = start + 2.0;

        var rng = new CameraRng(unchecked((uint)(_project.LegacySeed * 7919 + track.Shots.Count * 104729 + 17)));
        var pars = CameraDirection.PlanParams(preset, rng);
        pars["shotScale"] = 1.0;
        pars["shotX"] = 0;
        pars["shotY"] = 0;
        pars["shotRot"] = 0;
        pars["gain"] = 1.0;

        track.Shots.Add(new CameraShot
        {
            Preset = preset,
            Start = start,
            End = end,
            Seed = unchecked((int)(rng.Next() * int.MaxValue)),
            Params = pars,
            Source = "user",
        });
        track.Shots.Sort((a, b) => a.Start.CompareTo(b.Start));
        track.UserEdited = true;

        layer.Parallax.Follow = 0;
        _evaluator = DirectorEvaluator.FromSaves(_project.Curves, _project.CameraTrack, _project.LayerCameraTracks);
        PublishDirector();
        RefreshTimeline();
        BuildInspector();
        _saveError = SaveProjectNow();
        TblStatus.Text = L.F("已添加角色运镜：{0}", ZhLabel("cam", preset));
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
            _rebuildPipeKeepTransport = true;
            _rebuildPipe = true;
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
        Func<double> read, Action<double> write)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });

        var lab = ThemeText(label, TextSecondaryKey, 12);
        lab.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(lab, 0);
        row.Children.Add(lab);

        var value = new NumericValue { Min = min, Max = max };
        value.SetValue(read());
        Grid.SetColumn(value.Root, 2);
        row.Children.Add(value.Root);

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            Value = Math.Clamp(read(), min, max),
            IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        bool syncing = false;
        value.Commit = v =>
        {
            write(v);
            syncing = true;
            try { slider.Value = Math.Clamp(v, min, max); }
            finally { syncing = false; }
            PushUndo();
            _saveError = SaveProjectNow();
        };
        slider.ValueChanged += (_, e) =>
        {
            if (syncing) return;
            write(e.NewValue);
            value.SetValue(e.NewValue);
            if (slider.IsMouseCaptureWithin) return;
            PushUndo();
            _saveError = SaveProjectNow();
        };
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);

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
        double span = Math.Max(max - min, 0);
        double margin = span > 1e-9 ? span * 0.5 : Math.Max(Math.Abs(current) * 0.5, 0.5);
        double lo = double.IsFinite(current) ? Math.Min(min, current - margin) : min;
        double hi = double.IsFinite(current) ? Math.Max(max, current + margin) : max;

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

        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lab = ThemeText(label, TextSecondaryKey, 12);
        lab.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(lab, 0);
        row.Children.Add(lab);

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
        };
        slider.ValueChanged += (_, e) => OnRowValueChanged(state, e.NewValue);
        slider.PreviewMouseLeftButtonDown += (_, _) => state.EditTime = _audio.Time;
        if (sliderWindow is not null)
            slider.LostMouseCapture += (_, _) => RefreshParamRows();
        state.Slider = slider;
        Grid.SetColumn(slider, 1);
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
        Grid.SetColumn(value.Root, 2);
        row.Children.Add(value.Root);

        var clamp = Themed(new TextBlock
        {
            Text = "↦",
            FontSize = 12,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        }, TextBlock.ForegroundProperty, CautionKey);
        state.ClampMark = clamp;
        Grid.SetColumn(clamp, 3);
        row.Children.Add(clamp);

        var key = new Wpf.Ui.Controls.Button
        {
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
            Content = "◆K",
            FontSize = 11,
            Padding = new Thickness(2, 1, 2, 1),
            ToolTip = L.T("在当前时刻打一个关键帧（该时刻已有则替换）"),
        };
        key.Click += (_, _) => KeyRowAtPlayhead(state);
        if (!ParamRegistry.TryResolve(address, _effects, _scene, out _, out _))
            key.Visibility = Visibility.Collapsed;
        state.KeyButton = key;
        Grid.SetColumn(key, 4);
        row.Children.Add(key);

        var badge = new Wpf.Ui.Controls.Badge
        {
            Cursor = Cursors.Hand,
            FontSize = 11,
            Padding = new Thickness(4, 0, 4, 0),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
        };
        badge.MouseLeftButtonDown += (_, _) => JumpToRowTrack(state);
        state.Badge = badge;
        Grid.SetColumn(badge, 5);
        row.Children.Add(badge);

        return row;
    }

    private void KeyRowAtPlayhead(AnimRow row)
    {
        var t = _audio.Time;
        var v = _evaluator.TryEvaluate(row.Address, t, out var eval) ? eval : row.ReadBase();

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
            ? L.F("已打关键帧 t={0:F2}s · {1} {2:F2}（并在 0s 补了保持键）", t, row.Label, v)
            : L.F("已打关键帧 t={0:F2}s · {1} {2:F2}", t, row.Label, v);
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

        RefreshTimeline();
        RefreshParamRows();

        if (!row.Slider.IsMouseCaptureWithin)
        {
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
        if (_inspectorRows.Count == 0) return;

        var t = _audio.Time;
        foreach (var row in _inspectorRows) RefreshRow(row, t);
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
        row.Badge.ToolTip = hasCurve ? L.T("由曲线驱动 base(t)（点徽标跳到时间轴该轨）") : L.T("文档里的常量");

        var range = $"[{row.SpecMin.ToString("0.###", CultureInfo.InvariantCulture)}, {row.SpecMax.ToString("0.###", CultureInfo.InvariantCulture)}]";
        row.ClampMark.Visibility = clamped ? Visibility.Visible : Visibility.Collapsed;
        row.ClampMark.ToolTip = clamped
            ? L.F("原值 {0} 越界，写入目标时按 {1} 限幅",
                value.ToString("0.###", CultureInfo.InvariantCulture), range)
            : null;

        bool keyHere = hasCurve && ModeOfKeyAt(curve!, time) is not null;
        row.KeyButton.SetResourceReference(Control.ForegroundProperty,
            keyHere ? TextAccentKey : TextTertiaryKey);
        row.KeyButton.ToolTip = keyHere
            ? L.T("当前时刻已有关键帧（点击用当前值覆盖）")
            : L.T("在当前时刻打一个关键帧（首次打帧会自动补 0 时刻保持键）");

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
        Dictionary<string, double> segParams;

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
                segParams = new Dictionary<string, double>(node.Values.ToDictionary(p => p.Key, p => (double)p.Value), StringComparer.Ordinal);
                title = EffectName(node) + L.F(" · 窗口 {0}", windowTarget.WindowIndex + 1);
                showFades = true;
                break;
            case { Effect: { } node }:
                technique = node.DefId;
                times = [0, Math.Max(0, _audio.Duration), 0, 0];
                source = node.Source;
                locked = node.Locked;
                segParams = new Dictionary<string, double>(node.Values.ToDictionary(p => p.Key, p => (double)p.Value), StringComparer.Ordinal);
                title = EffectName(node);
                showFades = false;
                break;
            case { Shot: { } shot }:
                technique = shot.Preset;
                times = [shot.Start, shot.End, 0, 0];
                source = shot.Source;
                locked = shot.Locked;
                segParams = new Dictionary<string, double>(shot.Params ?? new Dictionary<string, double>(), StringComparer.Ordinal);
                title = L.F("运镜 · {0}", TechniqueLabel(target, shot.Preset));
                showFades = false;
                break;
            case { Design: { } design }:
                technique = design.Technique;
                times = [design.Start, design.End, design.FadeIn, design.FadeOut];
                source = design.Source;
                locked = design.Locked;
                segParams = new Dictionary<string, double>(design.Params, StringComparer.Ordinal);
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
                source = "auto";
                locked = false;
                segParams = new Dictionary<string, double>(StringComparer.Ordinal);
                title = DesignTrackNames.Label(legacyTarget.LegacyKind) + " · " + TechniqueLabel(target, technique);
                showFades = false;
                break;
            default:
                return;
        }

        AddSectionTitle(title);
        AddInspector(TechniqueRow(target, technique));
        AddInspector(InfoRow(L.T("来源"), string.Equals(source, "user", StringComparison.OrdinalIgnoreCase) ? "user" : "auto"));

        bool editable = target.LegacyCutIndex < 0;
        if (editable)
        {
            AddInspector(SegmentTimeRow(L.T("开始"), () => times[0], v => times[0] = v));
            AddInspector(SegmentTimeRow(L.T("结束"), () => times[1], v => times[1] = v));
            if (showFades)
            {
                AddInspector(SegmentTimeRow(L.T("淡入"), () => times[2], v => times[2] = v));
                AddInspector(SegmentTimeRow(L.T("淡出"), () => times[3], v => times[3] = v));
            }

            AddInspector(SegmentApplyRow(times, showFades));
            var hint = ThemeText(L.T("时间单位为秒（0 = 音频开头），改完点「应用时间」生效"), TextTertiaryKey, 11);
            hint.Margin = new Thickness(0, 0, 0, 2);
            hint.TextWrapping = TextWrapping.Wrap;
            AddInspector(hint);
        }
        else
        {
            AddInspector(InfoRow(L.T("开始"), Fmt(times[0])));
            AddInspector(InfoRow(L.T("结束"), Fmt(times[1])));
        }

        if (segParams.Count > 0)
        {
            AddSectionTitle(L.T("参数"));
            foreach (var (key, value) in segParams.OrderBy(p => p.Key, StringComparer.Ordinal))
                AddInspector(SegmentParamRow(key, value));
        }

        if (editable)
        {
            var lockRow = new Grid { Margin = new Thickness(0, 8, 0, 2) };
            lockRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            lockRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var lockLab = ThemeText(L.T("锁定（防自动清理）"), TextSecondaryKey, 12);
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
        if (string.IsNullOrWhiteSpace(key) || string.Equals(key, "none", StringComparison.Ordinal)) return L.T("关闭");
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
            return new[] { new TechniqueOption("none", L.T("关闭")) }
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
        if (options is null) return InfoRow(L.T("手法"), TechniqueLabel(target, current));

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
        caption.Children.Add(ThemeText(L.T("手法"), TextSecondaryKey, 11));
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

            var placeholder = ThemeText(group is null ? "FX" : L.T("预览"), TextTertiaryKey, group is null ? 12 : 10);
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
                Cursor = Cursors.Hand,
                Child = stack,
                ToolTip = option.Label,
            };
            card.SetResourceReference(Border.BorderBrushProperty, selected ? TextAccentKey : CardStrokeKey);
            if (selected) card.SetResourceReference(Border.BackgroundProperty, SelectFillKey);

            var key = option.Key;
            card.MouseEnter += (_, _) => EnsureTechniquePreview(thumb, group, option.Key, 160);
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
                        cut.Layout = value;
                        break;
                    case DesignTrackKind.Enter:
                        cut.Enter = value;
                        break;
                    case DesignTrackKind.Exit:
                        cut.Exit = value;
                        break;
                    case DesignTrackKind.Treat:
                        cut.Treat = value;
                        break;
                }

                cut.Source = "user";
                _pipe?.ApplyLegacyCutOverrides(_legacyPlanInfo);
                break;
        }

        AfterSegmentEdit();
    }

    private void ReplaceEffectNode(EffectNode oldNode, string defId)
    {
        int index = _effects.Nodes.IndexOf(oldNode);
        if (index < 0) return;

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
            Content = L.T("应用时间"),
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

    private FrameworkElement SegmentParamRow(string key, double value)
    {
        double min = -4, max = 4;
        if (_segmentTarget?.Effect is { } en
            && EffectCatalog.Get(en.DefId)?.Params.FirstOrDefault(p => p.Key == key) is { } p)
        {
            min = p.Min;
            max = p.Max;
        }
        else if (_segmentTarget?.Shot is not null)
        {
            if (string.Equals(key, "gain", StringComparison.Ordinal))
            {
                min = 0;
                max = Math.Max(2, Math.Abs(value) * 2);
            }
            else
            {
                double mag = Math.Max(Math.Abs(value), 0.005);
                min = -mag * 4;
                max = mag * 4;
            }
        }

        var addr = new ParamAddress(
            _segmentTarget?.Effect is not null ? ParamAddressKind.Effect : ParamAddressKind.Layer,
            _segmentTarget?.Effect?.InstanceId ?? "segment", key);
        return AnimParamRow(_inspectorRows, addr, ParamDisplayName(key), min, max, Math.Max(0.0001, (max - min) / 100),
            () => _segmentTarget?.Effect is { } eff && eff.Values.TryGetValue(key, out var v) ? v
                : _segmentTarget?.Shot is { } s && s.Params.TryGetValue(key, out var sv) ? sv
                : _segmentTarget?.Design is { } d && d.Params.TryGetValue(key, out var dv) ? dv : value,
            v => WriteSegmentParam(key, v));
    }

    private void BuildProjectSection()
    {
        AddSectionTitle(L.T("工程"));
        AddInspector(InfoRow(L.T("画布"), $"{_scene.Width}×{_scene.Height}"));
        AddInspector(InfoRow(L.T("帧率"), $"{_scene.Fps} fps"));
        AddInspector(InfoRow(L.T("时长"), Fmt(_audio.Duration)));
        AddInspector(InfoRow(L.T("音频"),
            string.IsNullOrWhiteSpace(_audioPath) ? L.T("未绑定") : Path.GetFileName(_audioPath), _audioPath));
        var lyrics = _lyrics is null ? L.T("未装载") : L.F("{0} 句", _lyrics.Count);
        AddInspector(InfoRow(L.T("歌词"), _tailUncalibrated ? lyrics + L.T(" · 末句待校准") : lyrics));
    }

    private void AddSectionTitle(string text)
    {
        if (_inspectorTabs is null)
        {
            _inspectorTabs = new TabControl
            {
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                Style = TryFindResource("InspectorTabs") as Style,
            };
            _inspectorTabs.SelectionChanged += (_, _) =>
            {
                if (!_buildingInspector && _inspectorTabs?.SelectedItem is TabItem { Header: string header })
                    _inspectorTab = header;
            };
            InspectorHost.Children.Add(_inspectorTabs);
        }

        var content = new StackPanel();
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(0, 4, 4, 8),
            Content = content,
        };
        _inspectorTabs.Items.Add(new TabItem
        {
            Header = text,
            Content = scroll,
            Style = TryFindResource("InspectorTabItem") as Style,
        });
        _inspectorTarget = content;
    }

    private void AddInspector(UIElement element) => _inspectorTarget.Children.Add(element);

    private FrameworkElement InfoRow(string label, string value, string? tip = null)
    {
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
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
        "offsetX" => L.T("位移X"),
        "offsetY" => L.T("位移Y"),
        "scale" => L.T("缩放"),
        "rotation" => L.T("旋转"),
        "opacity" => L.T("不透明"),
        "gain" => L.T("强度"),
        "a" => L.T("幅度"),
        "dir" => L.T("方向"),
        "sp" => L.T("速度"),
        "per" => L.T("周期"),
        "d" => L.T("时长"),
        "b" => L.T("模糊"),
        "at" => L.T("触发点"),
        "f" => L.T("频率"),
        "jerk" => L.T("抖动"),
        "z" => L.T("缩放"),
        "r" => L.T("旋转"),
        "s0" => L.T("种子"),
        "vert" => L.T("垂直"),
        "dx" => L.T("X 幅度"),
        "dy" => L.T("Y 幅度"),
        "a0" => L.T("相位"),
        "side" => L.T("方向"),
        "amp" => L.T("幅度"),
        "amount" => L.T("强度"),
        "shotScale" => L.T("景别"),
        "shotX" => L.T("构图X"),
        "shotY" => L.T("构图Y"),
        "shotRot" => L.T("倾斜"),
        _ => param,
    };

    private string TrackLabel(in ParamAddress addr)
    {
        var name = ParamDisplayName(addr.Param);
        switch (addr.Kind)
        {
            case ParamAddressKind.Camera:
                return L.F("镜头 · {0}", name);
            case ParamAddressKind.Vts:
                return L.F("角色移动 · {0}", addr.Param switch
                {
                    "x" => L.T("位移X"),
                    "y" => L.T("位移Y"),
                    "size" => L.T("缩放Z"),
                    "rotation" => L.T("旋转"),
                    _ => addr.Param,
                });
            case ParamAddressKind.Layer:
                foreach (var layer in _scene.Layers)
                    if (string.Equals(layer.Id, addr.Target, StringComparison.Ordinal))
                    {
                        var prefix = _scene.IsCharacterLayer(layer)
                            ? L.T("角色")
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
    Project,
    Layer,
}

public static class ValueSourceBadge
{
    public static string Curve => L.T("曲线");
    public static string Constant => L.T("常量");

    public static string Label(bool hasCurve) => hasCurve ? Curve : Constant;
}

public static class InspectorLayout
{
    public static IReadOnlyList<InspectorSectionKind> Sections(bool hasLayerSelection) =>
        hasLayerSelection
            ? [InspectorSectionKind.Layer, InspectorSectionKind.Project]
            : [InspectorSectionKind.Camera, InspectorSectionKind.Project];
}
