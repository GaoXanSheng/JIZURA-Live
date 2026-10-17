using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    }

    private FrameworkElement ConstRow(string label, double min, double max, double step,
        Func<double> read, Action<double> write)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

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
        var state = new AnimRow
        {
            Address = address,
            Label = label,
            Min = min,
            Max = max,
            ReadBase = readBase,
            WriteBase = writeBase,
        };
        owner.Add(state);

        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lab = ThemeText(label, TextSecondaryKey, 12);
        lab.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(lab, 0);
        row.Children.Add(lab);

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            Value = Math.Clamp(readBase(), min, max),
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
        if (target.Span is { } span) return _project.LyricStyles.Contains(span);
        return false;
    }

    private void BuildSegmentSection(SegmentTarget target)
    {
        string technique;
        string source;
        bool locked;
        string title;
        double[] times;
        Dictionary<string, double> segParams;

        switch (target)
        {
            case { Effect: { } node }:
                var win = node.Windows.Count > 0 ? node.Windows[0] : default;
                technique = node.DefId;
                times = [win.IsValid ? win.Start : 0, win.IsValid ? win.End : 0, win.IsValid ? win.FadeIn : 0, win.IsValid ? win.FadeOut : 0];
                source = node.Source;
                locked = node.Locked;
                segParams = new Dictionary<string, double>(node.Values.ToDictionary(p => p.Key, p => (double)p.Value), StringComparer.Ordinal);
                title = EffectName(node);
                break;
            case { Shot: { } shot }:
                technique = shot.Preset;
                times = [shot.Start, shot.End, 0, 0];
                source = shot.Source;
                locked = shot.Locked;
                segParams = new Dictionary<string, double>(shot.Params ?? new Dictionary<string, double>(), StringComparer.Ordinal);
                title = "运镜 · " + shot.Preset;
                break;
            case { Span: { } span }:
                technique = span.Style;
                times = [span.Start, span.End, 0, 0];
                source = span.Source;
                locked = span.Locked;
                segParams = new Dictionary<string, double>(StringComparer.Ordinal);
                title = "文字处理 · " + span.Style;
                break;
            default:
                return;
        }

        InspectorHost.Children.Add(SectionTitle(title));
        InspectorHost.Children.Add(InfoRow("手法", technique));
        InspectorHost.Children.Add(InfoRow("来源", string.Equals(source, "user", StringComparison.OrdinalIgnoreCase) ? "user" : "auto"));

        InspectorHost.Children.Add(SegmentTimeRow("开始", () => times[0], v => times[0] = v));
        InspectorHost.Children.Add(SegmentTimeRow("结束", () => times[1], v => times[1] = v));
        InspectorHost.Children.Add(SegmentTimeRow("淡入", () => times[2], v => times[2] = v));
        InspectorHost.Children.Add(SegmentTimeRow("淡出", () => times[3], v => times[3] = v));
        InspectorHost.Children.Add(SegmentApplyRow(times));

        if (segParams.Count > 0)
        {
            InspectorHost.Children.Add(SectionTitle("参数"));
            foreach (var (key, value) in segParams.OrderBy(p => p.Key, StringComparer.Ordinal))
                InspectorHost.Children.Add(SegmentParamRow(key, value));
        }

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

    private FrameworkElement SegmentApplyRow(double[] times)
    {
        var apply = new Wpf.Ui.Controls.Button
        {
            Content = "应用时间",
            Margin = new Thickness(0, 6, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        apply.Click += (_, _) => WriteSegmentTime(times[0], times[1], times[2], times[3]);
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

        var addr = new ParamAddress(ParamAddressKind.Layer, _segmentTarget?.Effect?.InstanceId ?? "segment", key);
        return AnimParamRow(_inspectorRows, addr, key, min, max, Math.Max(0.001, (max - min) / 100),
            () => _segmentTarget?.Effect is { } eff && eff.Values.TryGetValue(key, out var v) ? v
                : _segmentTarget?.Shot is { } s && s.Params.TryGetValue(key, out var sv) ? sv : value,
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
