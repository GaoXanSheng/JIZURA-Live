using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JizuraLive.LegacyEngine;
using JizuraLive.Pv;
using JizuraLive.Render.Effects;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow
{
    private sealed class AnimRow
    {
        public ParamAddress Address;
        public string Label = "";
        public double Min, Max;
        public Func<double> ReadBase = null!;
        public Action<double> WriteBase = null!;
        public Slider Slider = null!;
        public TextBlock ValueText = null!;
        public Wpf.Ui.Controls.Badge Badge = null!;
        public TextBlock ClampMark = null!;
        public double EditTime;
    }

    private readonly List<AnimRow> _inspectorRows = new();
    private bool _syncingRows;

    private void SelectLayer(PvLayerConfig? layer)
    {
        _selectedLayer = layer;
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

        TblInspectorTitle.Text = _selectedLayer is null ? "工程" : LayerTitle(_selectedLayer);
    }

    private static string LayerTitle(PvLayerConfig layer) =>
        string.IsNullOrWhiteSpace(layer.SpoutSenderName) ? layer.Name : layer.SpoutSenderName!;

    private void BuildInspector()
    {
        _inspectorRows.Clear();
        InspectorHost.Children.Clear();

        if (_segmentTarget is { } target && SegmentTargetAlive(target))
        {
            BuildSegmentSection(target);
            RefreshParamRows();
            return;
        }
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

        RefreshParamRows();
    }

    private void BuildCameraSection()
    {
        InspectorHost.Children.Add(SectionTitle("相机"));
        InspectorHost.Children.Add(CameraRow("offsetX", "位移X", 1));
        InspectorHost.Children.Add(CameraRow("offsetY", "位移Y", 1));
        InspectorHost.Children.Add(CameraRow("scale", "缩放", 0.01));
        InspectorHost.Children.Add(CameraRow("rotation", "旋转", 1));
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
        InspectorHost.Children.Add(SectionTitle($"图层 · {LayerKindLabel(layer.Kind)}"));
        InspectorHost.Children.Add(LayerRow(layer, "offsetX", "位移X", 0.01));
        InspectorHost.Children.Add(LayerRow(layer, "offsetY", "位移Y", 0.01));
        InspectorHost.Children.Add(LayerRow(layer, "scale", "缩放", 0.01));
        InspectorHost.Children.Add(LayerRow(layer, "opacity", "不透明", 0.02));
        InspectorHost.Children.Add(LayerRow(layer, "rotation", "旋转", 1));
        InspectorHost.Children.Add(ConstRow("深度", 0, 3, 0.01,
            () => layer.Parallax.Depth, v => layer.Parallax.Depth = v));
        InspectorHost.Children.Add(ConstRow("环境光", 0, 1, 0.02,
            () => layer.VisualBlend.AmbientTintIntensity, v => layer.VisualBlend.AmbientTintIntensity = v));
        InspectorHost.Children.Add(ConstRow("边缘光", 0, 1, 0.02,
            () => layer.VisualBlend.RimGlow, v => layer.VisualBlend.RimGlow = v));
        InspectorHost.Children.Add(ConstRow("阴影", 0, 1, 0.02,
            () => layer.VisualBlend.DropShadow, v => layer.VisualBlend.DropShadow = v));

        if (!string.IsNullOrWhiteSpace(layer.SpoutSenderName))
            InspectorHost.Children.Add(BoolRow("预乘 Alpha", layer.Premultiplied, v =>
            {
                layer.Premultiplied = v;
                RebuildStage();
            }));

        if (layer.Decor is { } decor) BuildDecorSection(decor);
    }

    private FrameworkElement BoolRow(string label, bool value, Action<bool> write)
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

    private void BuildDecorSection(DecorConfig decor)
    {
        InspectorHost.Children.Add(SectionTitle("装饰参数"));

        InspectorHost.Children.Add(ConstRow("密度", 0, 300, 1,
            () => decor.Density, v => decor.Density = v));
        InspectorHost.Children.Add(ConstRow("速度", 0.2, 3, 0.05,
            () => decor.Speed, v => decor.Speed = v));
        InspectorHost.Children.Add(ConstRow("尺寸", 0.3, 3, 0.05,
            () => decor.Size, v => decor.Size = v));
        InspectorHost.Children.Add(ConstRow("种子", 1, 9999, 1,
            () => decor.Seed, v => decor.Seed = (int)Math.Round(v)));

        InspectorHost.Children.Add(DecorColorRow("主色", decor.Color, v => decor.Color = v));
        InspectorHost.Children.Add(DecorColorRow("辅色", decor.AccentColor, v => decor.AccentColor = v));
    }

    private FrameworkElement DecorColorRow(string label, string hex, Action<string> write)
    {
        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lab = ThemeText(label, TextSecondaryKey, 12);
        Grid.SetColumn(lab, 0);
        row.Children.Add(lab);

        var host = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(host, 1);
        row.Children.Add(host);

        var swatch = new Border
        {
            Width = 26,
            Height = 18,
            CornerRadius = new CornerRadius(4),
            Background = BrushFromHex(hex),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "当前颜色",
        };
        swatch.SetResourceReference(Border.BorderBrushProperty, CardStrokeKey);
        host.Children.Add(swatch);

        var box = new TextBox
        {
            Text = hex,
            Width = 78,
            FontSize = 11,
            Margin = new Thickness(8, 0, 0, 0),
            ToolTip = "十六进制颜色 #rrggbb",
        };
        box.TextChanged += (_, _) =>
        {
            var text = box.Text.Trim();
            if (!IsHexColor(text)) return;
            write(text);
            swatch.Background = BrushFromHex(text);
        };
        host.Children.Add(box);

        foreach (var preset in DecorSwatches)
        {
            var b = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(3),
                Background = BrushFromHex(preset),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Margin = new Thickness(4, 0, 0, 0),
                ToolTip = preset,
            };
            b.SetResourceReference(Border.BorderBrushProperty, CardStrokeKey);
            var value = preset;
            b.MouseLeftButtonDown += (_, _) => box.Text = value;
            host.Children.Add(b);
        }

        return row;
    }

    private static readonly string[] DecorSwatches =
        ["#ffffff", "#ffc0d8", "#ffd24a", "#ff8a4a", "#8ad4ff", "#38e1c4"];

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
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });

        var lab = ThemeText(label, TextSecondaryKey, 12);
        lab.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(lab, 0);
        row.Children.Add(lab);

        var value = ThemeValue(read().ToString("0.###", CultureInfo.InvariantCulture));
        Grid.SetColumn(value, 2);
        row.Children.Add(value);

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            Value = Math.Clamp(read(), min, max),
            IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, e) =>
        {
            write(e.NewValue);
            value.Text = e.NewValue.ToString("0.###", CultureInfo.InvariantCulture);
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
        double min, double max, double step, Func<double> readBase, Action<double> writeBase)
    {
        double current = readBase();
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
            ReadBase = readBase,
            WriteBase = writeBase,
        };
        owner.Add(state);

        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lab = ThemeText(label, TextSecondaryKey, 12);
        lab.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(lab, 0);
        row.Children.Add(lab);

        var slider = new Slider
        {
            Minimum = lo,
            Maximum = hi,
            SmallChange = step,
            Value = Math.Clamp(readBase(), lo, hi),
            IsMoveToPointEnabled = true,
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, e) => OnRowValueChanged(state, e.NewValue);
        state.Slider = slider;
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);

        var value = ThemeValue("");
        state.ValueText = value;
        Grid.SetColumn(value, 2);
        row.Children.Add(value);

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
            ToolTip = "在当前时刻打一个关键帧（该时刻已有则替换）",
        };
        key.Click += (_, _) => KeyRowAtPlayhead(state);
        Grid.SetColumn(key, 4);
        row.Children.Add(key);

        var badge = new Wpf.Ui.Controls.Badge
        {
            Cursor = Cursors.Hand,
            FontSize = 10,
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
        var v = _evaluator.TryEvaluate(row.Address, t, out var eval) ? eval : row.Slider.Value;

        if (!_evaluator.Curves.TryGetValue(row.Address, out var curve))
        {
            curve = new FloatCurve();
            _evaluator.SetCurve(row.Address, curve);
        }

        var mode = ModeOfKeyAt(curve, t) ?? CurveInterp.SmoothStep;
        curve.Remove(t);
        curve.TryAdd(t, v, mode);

        PublishDirector();
        RefreshParamRows();
        RefreshTimeline();
        PushUndo();
        _saveError = SaveProjectNow();
        TblStatus.Text = $"已打关键帧 t={t:F2}s · {row.Label} {v:F2}";
    }

    private void WriteCurveKeyAtPlayhead(in ParamAddress addr, double value)
    {
        var t = _audio.Time;
        if (!_evaluator.Curves.TryGetValue(addr, out var curve))
        {
            curve = new FloatCurve();
            _evaluator.SetCurve(addr, curve);
        }

        var mode = ModeOfKeyAt(curve, t) ?? CurveInterp.SmoothStep;
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

        var t = _audio.Time;
        if (_evaluator.Curves.TryGetValue(row.Address, out var curve))
        {
            if (!row.Slider.IsMouseCaptureWithin) row.EditTime = t;
            var at = row.EditTime;
            var mode = ModeOfKeyAt(curve, at) ?? CurveInterp.SmoothStep;
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
        var clamped = value < row.Min || value > row.Max;

        var source = ValueSourceBadge.Label(hasCurve);
        row.Badge.Content = source;
        row.Badge.SetResourceReference(Control.ForegroundProperty,
            hasCurve ? TextAccentKey : TextTertiaryKey);
        row.Badge.SetResourceReference(Control.BorderBrushProperty,
            hasCurve ? TextAccentKey : TextTertiaryKey);
        row.Badge.ToolTip = hasCurve ? "由曲线驱动 base(t)（点徽标跳到时间轴该轨）" : "文档里的常量";

        var range = $"[{row.Min.ToString("0.###", CultureInfo.InvariantCulture)}, {row.Max.ToString("0.###", CultureInfo.InvariantCulture)}]";
        row.ClampMark.Visibility = clamped ? Visibility.Visible : Visibility.Collapsed;
        row.ClampMark.ToolTip = clamped
            ? $"原值 {value.ToString("0.###", CultureInfo.InvariantCulture)} 越界，写入目标时按 {range} 限幅"
            : null;

        row.ValueText.Text = value.ToString("0.###", CultureInfo.InvariantCulture);

        var shown = Math.Clamp(value, row.Min, row.Max);
        if (!row.Slider.IsMouseCaptureWithin && Math.Abs(row.Slider.Value - shown) > 1e-4)
        {
            _syncingRows = true;
            try { row.Slider.Value = shown; }
            finally { _syncingRows = false; }
        }
    }

    private bool SegmentTargetAlive(SegmentTarget target)
    {
        if (target.Effect is { } node) return _effects.Nodes.Contains(node);
        if (target.Shot is { } shot) return _project.CameraTrack?.Shots.Contains(shot) == true;
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
                title = EffectName(node) + $" · 窗口 {windowTarget.WindowIndex + 1}";
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
                title = "运镜 · " + shot.Preset;
                showFades = false;
                break;
            case { Design: { } design }:
                technique = design.Technique;
                times = [design.Start, design.End, design.FadeIn, design.FadeOut];
                source = design.Source;
                locked = design.Locked;
                segParams = new Dictionary<string, double>(design.Params, StringComparer.Ordinal);
                title = DesignTrackNames.Label(design.Track) + " · " + technique;
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
                title = DesignTrackNames.Label(legacyTarget.LegacyKind) + " · " + technique;
                showFades = false;
                break;
            default:
                return;
        }

        InspectorHost.Children.Add(SectionTitle(title));
        InspectorHost.Children.Add(TechniqueRow(target, technique));
        InspectorHost.Children.Add(InfoRow("来源", string.Equals(source, "user", StringComparison.OrdinalIgnoreCase) ? "user" : "auto"));

        bool editable = target.LegacyCutIndex < 0;
        if (editable)
        {
            InspectorHost.Children.Add(SegmentTimeRow("开始", () => times[0], v => times[0] = v));
            InspectorHost.Children.Add(SegmentTimeRow("结束", () => times[1], v => times[1] = v));
            if (showFades)
            {
                InspectorHost.Children.Add(SegmentTimeRow("淡入", () => times[2], v => times[2] = v));
                InspectorHost.Children.Add(SegmentTimeRow("淡出", () => times[3], v => times[3] = v));
            }

            InspectorHost.Children.Add(SegmentApplyRow(times, showFades));
        }
        else
        {
            InspectorHost.Children.Add(InfoRow("开始", Fmt(times[0])));
            InspectorHost.Children.Add(InfoRow("结束", Fmt(times[1])));
        }

        if (segParams.Count > 0)
        {
            InspectorHost.Children.Add(SectionTitle("参数"));
            foreach (var (key, value) in segParams.OrderBy(p => p.Key, StringComparer.Ordinal))
                InspectorHost.Children.Add(SegmentParamRow(key, value));
        }

        if (editable)
        {
            var lockRow = new Grid { Margin = new Thickness(0, 6, 0, 2) };
            lockRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var lockLab = ThemeText("锁定（防自动清理）", TextSecondaryKey, 12);
            Grid.SetColumn(lockLab, 0);
            lockRow.Children.Add(lockLab);
            var lockToggle = new Wpf.Ui.Controls.ToggleSwitch { IsChecked = locked };
            lockToggle.Checked += (_, _) => WriteSegmentLocked(true);
            lockToggle.Unchecked += (_, _) => WriteSegmentLocked(false);
            lockRow.Children.Add(lockToggle);
            InspectorHost.Children.Add(lockRow);
        }
    }

    private FrameworkElement TechniqueRow(SegmentTarget target, string current)
    {
        IEnumerable<string>? options = target switch
        {
            { Shot: not null } => CameraDirection.Presets.Select(p => p.Key),
            { Effect: not null } => EffectCatalog.All.Select(d => d.Id),
            { Design: { Track: DesignTrackKind.Trans } } => TransitionCatalog.Ids,
            { LegacyCutIndex: >= 0 } => LegacyTechniques(target.LegacyKind),
            _ => null,
        };

        if (options is null) return InfoRow("手法", current);

        var items = options.ToList();
        if (!items.Contains(current, StringComparer.Ordinal)) items.Insert(0, current);

        var combo = new ComboBox { FontSize = 12, Margin = new Thickness(0, 2, 0, 2) };
        foreach (var option in items) combo.Items.Add(option);
        combo.SelectedItem = current;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is not string value || string.Equals(value, current, StringComparison.Ordinal)) return;
            current = value;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplyTechnique(target, value);
                BuildInspector();
            }));
        };

        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var label = ThemeText("手法", TextSecondaryKey, 12);
        Grid.SetColumn(label, 0);
        row.Children.Add(label);
        Grid.SetColumn(combo, 1);
        row.Children.Add(combo);
        return row;
    }

    private void ApplyTechnique(SegmentTarget target, string value)
    {
        switch (target)
        {
            case { Shot: { } shot }:
                shot.Preset = value;
                shot.Source = "user";
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

                _pipe?.ApplyLegacyCutOverrides(_legacyPlanInfo);
                break;
        }

        AfterSegmentEdit();
    }

    private static IEnumerable<string> LegacyTechniques(DesignTrackKind kind)
    {
        return kind switch
        {
            DesignTrackKind.Layout => Reg.Layouts.Keys,
            DesignTrackKind.Enter => Reg.Enter.Keys,
            DesignTrackKind.Exit => Reg.Exit.Keys,
            DesignTrackKind.Treat => new[] { "none" }.Concat(Reg.Treat.Keys),
            _ => Array.Empty<string>(),
        };
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
            Content = "应用时间",
            Margin = new Thickness(0, 6, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        apply.Click += (_, _) => WriteSegmentTime(times[0], times[1], showFades ? times[2] : 0, showFades ? times[3] : 0);
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
            _segmentTarget?.Effect is { } fx ? ParamAddressKind.Effect : ParamAddressKind.Layer,
            _segmentTarget?.Effect?.InstanceId ?? "segment", key);
        return AnimParamRow(_inspectorRows, addr, ParamDisplayName(key), min, max, Math.Max(0.0001, (max - min) / 100),
            () => _segmentTarget?.Effect is { } eff && eff.Values.TryGetValue(key, out var v) ? v
                : _segmentTarget?.Shot is { } s && s.Params.TryGetValue(key, out var sv) ? sv
                : _segmentTarget?.Design is { } d && d.Params.TryGetValue(key, out var dv) ? dv : value,
            v => WriteSegmentParam(key, v));
    }

    private void BuildProjectSection()
    {
        InspectorHost.Children.Add(SectionTitle("工程"));
        InspectorHost.Children.Add(InfoRow("画布", $"{_scene.Width}×{_scene.Height}"));
        InspectorHost.Children.Add(InfoRow("帧率", $"{_scene.Fps} fps"));
        InspectorHost.Children.Add(InfoRow("时长", Fmt(_audio.Duration)));
        InspectorHost.Children.Add(InfoRow("音频",
            string.IsNullOrWhiteSpace(_audioPath) ? "未绑定" : Path.GetFileName(_audioPath), _audioPath));
        var lyrics = _lyrics is null ? "未装载" : $"{_lyrics.Count} 句";
        InspectorHost.Children.Add(InfoRow("歌词", _tailUncalibrated ? lyrics + " · 末句待校准" : lyrics));
    }

    private TextBlock SectionTitle(string text)
    {
        var title = ThemeText(text, TextTertiaryKey, 11);
        title.FontWeight = FontWeights.SemiBold;
        title.Margin = new Thickness(2, 10, 0, 6);
        return title;
    }

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
        "offsetX" => "位移X",
        "offsetY" => "位移Y",
        "scale" => "缩放",
        "rotation" => "旋转",
        "opacity" => "不透明",
        "gain" => "强度",
        "a" => "幅度",
        "dir" => "方向",
        "sp" => "速度",
        "per" => "周期",
        "d" => "时长",
        "b" => "模糊",
        "at" => "触发点",
        "f" => "频率",
        "jerk" => "抖动",
        "z" => "缩放",
        "r" => "旋转",
        "s0" => "种子",
        "vert" => "垂直",
        "dx" => "X 幅度",
        "dy" => "Y 幅度",
        "a0" => "相位",
        "side" => "方向",
        "amp" => "幅度",
        "amount" => "强度",
        "shotScale" => "景别",
        "shotX" => "构图X",
        "shotY" => "构图Y",
        "shotRot" => "倾斜",
        _ => param,
    };

    private string TrackLabel(in ParamAddress addr)
    {
        var name = ParamDisplayName(addr.Param);
        switch (addr.Kind)
        {
            case ParamAddressKind.Camera:
                return "镜头 · " + name;
            case ParamAddressKind.Layer:
                foreach (var layer in _scene.Layers)
                    if (string.Equals(layer.Id, addr.Target, StringComparison.Ordinal))
                        return LayerTitle(layer) + " · " + name;
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
    public const string Curve = "曲线";
    public const string Constant = "常量";

    public static string Label(bool hasCurve) => hasCurve ? Curve : Constant;
}

public static class InspectorLayout
{
    public static IReadOnlyList<InspectorSectionKind> Sections(bool hasLayerSelection) =>
        hasLayerSelection
            ? [InspectorSectionKind.Camera, InspectorSectionKind.Project, InspectorSectionKind.Layer]
            : [InspectorSectionKind.Camera, InspectorSectionKind.Project];
}
