using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using JizuraLive.App.Controls;
using JizuraLive.Pv;
using JizuraLive.Render.Effects;

namespace JizuraLive.App.PvStage;

/// <summary>
/// 属性检查器：相机四参 + 选中图层八参（五个可动画 + 深度/环境光/边缘光三个常量）+ 工程级信息。
/// 每行可动画参数都带 ◆K 与值来源徽标；效果架里的 Number 参数行走同一套（只把行登记进 <see cref="_rackRows"/>）。
/// </summary>
public partial class PvStageWindow
{
    /// <summary>一行可动画参数。重建时整批换掉，刷新路径只认这一份引用。</summary>
    private sealed class AnimRow
    {
        public ParamAddress Address;
        public string Label = "";
        public double Min, Max;
        public Func<double> ReadBase = null!;   // 没有曲线时的文档常量
        public Action<double> WriteBase = null!;
        public Slider Slider = null!;
        public TextBlock ValueText = null!;
        public JzBadge Badge = null!;
        public TextBlock ClampMark = null!;
        public double EditTime;                 // 滑杆编辑时刻：按下滑杆那一刻冻结（同刻替换一条 key）
    }

    private readonly List<AnimRow> _inspectorRows = new();
    private readonly List<AnimRow> _rackRows = new();
    private bool _syncingRows;  // 程序化回写滑杆时为 true：那条 ValueChanged 不是用户输入，不能当编辑写回去

    private void SelectLayer(PvLayerConfig? layer)
    {
        _selectedLayer = layer;
        HighlightLayerCards();
        BuildInspector();
    }

    /// <summary>图层卡的选中底（PvAccentSoft）与检查器标题。</summary>
    private void HighlightLayerCards()
    {
        foreach (var card in LayersHost.Children.OfType<Border>())
        {
            var active = ReferenceEquals(card.Tag, _selectedLayer);
            card.Background = (System.Windows.Media.Brush)FindResource(active ? "PvAccentSoft" : "PvRaised");
        }

        TblInspectorTitle.Text = _selectedLayer is null ? "工程" : LayerTitle(_selectedLayer);
    }

    private static string LayerTitle(PvLayerConfig layer) =>
        string.IsNullOrWhiteSpace(layer.SpoutSenderName) ? layer.Name : layer.SpoutSenderName!;

    private void BuildInspector()
    {
        // 重建前先丢引用：旧的滑杆与徽标不能再被刷新路径写到
        _inspectorRows.Clear();
        _cmbLyricStyle = null;
        InspectorHost.Children.Clear();

        // 段落计划由 InspectorLayout 给：工程段（歌词样式下拉的唯一入口）与选中无关，始终在。
        // 这里不再按"有没有选中图层"二选一——那样点过图层卡之后本次会话就回不到工程段。
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

    // ---------------- 可动画参数行 ----------------

    /// <summary>相机四参（地址 <c>camera.&lt;param&gt;</c>）。</summary>
    private void BuildCameraSection()
    {
        InspectorHost.Children.Add(SectionTitle("相机"));
        InspectorHost.Children.Add(CameraRow("offsetX", "位移X", 1));
        InspectorHost.Children.Add(CameraRow("offsetY", "位移Y", 1));
        InspectorHost.Children.Add(CameraRow("scale", "缩放", 0.01));
        InspectorHost.Children.Add(CameraRow("rotation", "旋转", 1));
    }

    /// <summary>相机一行：没有曲线时读写文档常量（与 PvPipeline.BaseCamera 的回落是同一份事实）。</summary>
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

    /// <summary>选中图层的参数：五个可动画的变换行（地址 <c>layer:&lt;id&gt;.&lt;param&gt;</c>）+ 三个观感常量行。</summary>
    private void BuildLayerSection(PvLayerConfig layer)
    {
        InspectorHost.Children.Add(SectionTitle($"图层 · {LayerKindLabel(layer.Kind)}"));
        InspectorHost.Children.Add(LayerRow(layer, "offsetX", "位移X", 0.01));
        InspectorHost.Children.Add(LayerRow(layer, "offsetY", "位移Y", 0.01));
        InspectorHost.Children.Add(LayerRow(layer, "scale", "缩放", 0.01));
        InspectorHost.Children.Add(LayerRow(layer, "opacity", "不透明", 0.02));
        InspectorHost.Children.Add(LayerRow(layer, "rotation", "旋转", 1));
        // 深度上限 3 与 PvParallax 的夹取范围一致；环境光/边缘光在合成里按 0~1 用
        InspectorHost.Children.Add(ConstRow("深度", 0, 3, 0.01,
            () => layer.Parallax.Depth, v => layer.Parallax.Depth = v));
        InspectorHost.Children.Add(ConstRow("环境光", 0, 1, 0.02,
            () => layer.VisualBlend.AmbientTintIntensity, v => layer.VisualBlend.AmbientTintIntensity = v));
        InspectorHost.Children.Add(ConstRow("边缘光", 0, 1, 0.02,
            () => layer.VisualBlend.RimGlow, v => layer.VisualBlend.RimGlow = v));
    }

    /// <summary>
    /// 只有常量的编辑行：这三个参数不在参数注册表里，帧内没有曲线/绑定通道，
    /// 所以没有 ◆K 也没有值来源徽标——滑杆写的就是文档字段。
    /// </summary>
    private FrameworkElement ConstRow(string label, double min, double max, double step,
        Func<double> read, Action<double> write)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(52) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });

        var lab = new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("PvRowLabel"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(lab, 0);
        row.Children.Add(lab);

        var value = new TextBlock
        {
            Style = (Style)FindResource("PvValue"),
            Text = read().ToString("0.###", CultureInfo.InvariantCulture),
            TextAlignment = TextAlignment.Right,
        };
        Grid.SetColumn(value, 2);
        row.Children.Add(value);

        var slider = new Slider
        {
            Style = (Style)FindResource("PvSlider"),
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            Value = Math.Clamp(read(), min, max),
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, e) =>
        {
            write(e.NewValue);
            value.Text = e.NewValue.ToString("0.###", CultureInfo.InvariantCulture);
            // 拖动中不落盘：存盘是整份工程 JSON 的原子写，一次拖动能触发上百次（与动画行同一条约定）
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

    /// <summary>名称 | 值控件 | ↦ | ◆K | 值来源徽标。<paramref name="readBase"/>/<paramref name="writeBase"/> 是"没有曲线时"的文档常量读写。</summary>
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

        var lab = new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("PvRowLabel"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(lab, 0);
        row.Children.Add(lab);

        var slider = new Slider
        {
            Style = (Style)FindResource("PvSlider"),
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            Value = Math.Clamp(readBase(), min, max),
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += (_, e) => OnRowValueChanged(state, e.NewValue);
        state.Slider = slider;
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);

        var value = new TextBlock
        {
            Style = (Style)FindResource("PvValue"),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        };
        state.ValueText = value;
        Grid.SetColumn(value, 2);
        row.Children.Add(value);

        var clamp = new TextBlock
        {
            Text = "↦",
            FontSize = (double)FindResource("PvTextSm"),
            Foreground = (System.Windows.Media.Brush)FindResource("PvWarn"),
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        state.ClampMark = clamp;
        Grid.SetColumn(clamp, 3);
        row.Children.Add(clamp);

        var key = new Button
        {
            Style = (Style)FindResource("PvBtnGhost"),
            Content = "◆K",
            FontSize = (double)FindResource("PvTextXs"),
            Padding = new Thickness(1, 1, 1, 1),
            ToolTip = "在当前时刻打一个关键帧（该时刻已有则替换）",
        };
        key.Click += (_, _) => KeyRowAtPlayhead(state);
        Grid.SetColumn(key, 4);
        row.Children.Add(key);

        var badge = new JzBadge { Cursor = Cursors.Hand };
        badge.MouseLeftButtonDown += (_, _) => JumpToRowTrack(state);
        state.Badge = badge;
        Grid.SetColumn(badge, 5);
        row.Children.Add(badge);

        return row;
    }

    /// <summary>在播放头时刻给这一行打帧：同刻替换，一次 PushUndo。</summary>
    private void KeyRowAtPlayhead(AnimRow row)
    {
        var t = _audio.Time;
        // 值不能拿滑杆显示值：滑杆只在 500ms 定时器上跟播放头同步，seek 之后立刻打帧会把上一位置的值钉到新时刻。
        // 曲线存在就取"这一刻本来该是什么"（也是管线实际生效的值，不夹显示域——夹过再写回会把取景静默改动）
        var v = _evaluator.TryEvaluate(row.Address, t, out var eval) ? eval : row.Slider.Value;

        if (!_evaluator.Curves.TryGetValue(row.Address, out var curve))
        {
            curve = new FloatCurve();
            _evaluator.SetCurve(row.Address, curve);
        }

        // 同一时刻只许有一个 key，所以"替换"用 Remove + TryAdd 表达；替换沿用原 key 的插值模式（模式是用户的选择）
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

    /// <summary>把值写进该地址的曲线：时刻取播放头，同刻替换。给"文档里没有常量字段"的参数用。</summary>
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

    /// <summary>同刻窗口（1µs，与 FloatCurve 一致）内已有 key 的插值模式；没有返回 null。</summary>
    private static CurveInterp? ModeOfKeyAt(FloatCurve curve, double time)
    {
        foreach (var k in curve.Keys)
            if (Math.Abs(k.Time - time) < 1e-6) return k.Mode;
        return null;
    }

    private void OnRowValueChanged(AnimRow row, double value)
    {
        // 挡程序化回写那一次（RefreshRow 把"当前生效值"写回滑杆）
        if (_syncingRows) return;

        var t = _audio.Time;
        if (_evaluator.Curves.TryGetValue(row.Address, out var curve))
        {
            // 有曲线时滑杆改的是"这一刻的曲线值"。编辑时刻在按下滑杆那一刻冻结：
            // 播放中设备钟每帧推进，相邻 ValueChanged 的时刻差远大于 1µs 的同刻窗口，
            // 不冻结的话 Remove 找不到上一条 key、TryAdd 每次都插新的，一次拖动堆出上百条 key。
            if (!row.Slider.IsMouseCaptureWithin) row.EditTime = t;
            var at = row.EditTime;
            var mode = ModeOfKeyAt(curve, at) ?? CurveInterp.SmoothStep;
            curve.Remove(at);
            curve.TryAdd(at, value, mode);
            PublishDirector();
        }
        else
        {
            // 还没有曲线：滑杆改的是文档里的常量（与求值器回落的那一份是同一份事实）
            row.WriteBase(value);
            row.EditTime = t;
        }

        RefreshTimeline();
        RefreshParamRows();

        // 存盘不在每个 ValueChanged 里做：这是整份工程 JSON 的序列化 + 原子写，拖动一次能触发上百次。
        // 拖动中只更新内存模型，松手时压一份撤销并落盘。
        if (!row.Slider.IsMouseCaptureWithin)
        {
            PushUndo();
            _saveError = SaveProjectNow();
        }
    }

    /// <summary>点徽标：把时间轴上该轨最接近播放头的 key 选中（时间轴没有纵向滚动，"跳轨"能做的就是落选区）。</summary>
    private void JumpToRowTrack(AnimRow row)
    {
        if (!_evaluator.Curves.TryGetValue(row.Address, out var curve) || curve.Count == 0) return;

        var t = _audio.Time;
        var best = curve.Keys[0].Time;
        foreach (var k in curve.Keys)
            if (Math.Abs(k.Time - t) < Math.Abs(best - t)) best = k.Time;
        TheTimeline.SelectKey(row.Address, best);
    }

    // ---------------- 值来源徽标 ----------------

    /// <summary>所有参数行跟着播放头刷新：值取当前生效值，徽标取当前生效来源。</summary>
    private void RefreshParamRows()
    {
        if (_inspectorRows.Count == 0 && _rackRows.Count == 0) return;

        var t = _audio.Time;
        foreach (var row in _inspectorRows) RefreshRow(row, t);
        foreach (var row in _rackRows) RefreshRow(row, t);
    }

    /// <summary>
    /// 合成顺序只有两级：文档常量 → 曲线 base(t)。
    /// 显示的是曲线原值（不夹），越界由 ↦ 标出来——目标侧写入时才按注册范围限幅。
    /// </summary>
    private void RefreshRow(AnimRow row, double time)
    {
        var hasCurve = _evaluator.Curves.TryGetValue(row.Address, out var curve) && curve.Count > 0;
        var value = hasCurve ? curve!.Evaluate(time) : row.ReadBase();
        var clamped = value < row.Min || value > row.Max;

        var source = ValueSourceBadge.Label(hasCurve);
        var brush = (System.Windows.Media.Brush)FindResource(
            source == ValueSourceBadge.Curve ? "PvAccent" : "PvFgFaint");
        row.Badge.Text = source;
        row.Badge.Foreground = brush;
        row.Badge.BorderBrush = brush;
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

    // ---------------- 工程段 ----------------

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
        InspectorHost.Children.Add(LyricStyleRow());
    }

    /// <summary>歌词样式（场景级设置）：整份重建检查器时会连它一起换，所以事件在这里挂。</summary>
    private FrameworkElement LyricStyleRow()
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var label = new TextBlock { Text = "歌词样式", Style = (Style)FindResource("PvRowLabel") };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var picker = new ComboBox
        {
            Style = (Style)FindResource("SelSm"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        foreach (var name in LyricStyleNames) picker.Items.Add(name);
        picker.SelectionChanged += LyricStyle_SelectionChanged;
        Grid.SetColumn(picker, 1);
        row.Children.Add(picker);

        _cmbLyricStyle = picker;
        SyncLyricStylePicker();
        return row;
    }

    private TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        Style = (Style)FindResource("PvSectionTitle"),
    };

    private FrameworkElement InfoRow(string label, string value, string? tip = null)
    {
        var row = new Grid { Margin = new Thickness(0, 1, 0, 1) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lab = new TextBlock { Text = label, Style = (Style)FindResource("PvRowLabel") };
        Grid.SetColumn(lab, 0);
        row.Children.Add(lab);

        var val = new TextBlock
        {
            Text = value,
            FontSize = (double)FindResource("PvTextSm"),
            Foreground = (System.Windows.Media.Brush)FindResource("PvFg"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = tip,
        };
        Grid.SetColumn(val, 1);
        row.Children.Add(val);
        return row;
    }

    // ---------------- 参数命名（检查器行与时间轴轨共用） ----------------

    private static string ParamDisplayName(string param) => param switch
    {
        "offsetX" => "位移X",
        "offsetY" => "位移Y",
        "scale" => "缩放",
        "rotation" => "旋转",
        "opacity" => "不透明",
        _ => param,
    };

    /// <summary>时间轴轨的标签：「对象名 · 参数名」。</summary>
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

    /// <summary>效果参数的显示名：按 DefId 的 schema 取 LabelKey；schema 里没有这个 key 时原样显示。</summary>
    private static string EffectParamLabel(EffectNode node, string key) =>
        EffectCatalog.Get(node.DefId)?.Params.FirstOrDefault(p => p.Key == key) is { } p ? ParamLabel(p) : key;
}

/// <summary>检查器里的一段。声明顺序即期望的渲染顺序。</summary>
public enum InspectorSectionKind
{
    Camera,
    Project,
    Layer,
}

/// <summary>
/// 值来源徽标的文字判定（纯函数，与 <see cref="InspectorLayout"/> 同款：拎出来是为了可断言）。
///
/// 只有两级来源：该地址有曲线就是「曲线」，否则是文档里的「常量」。
/// </summary>
public static class ValueSourceBadge
{
    public const string Curve = "曲线";
    public const string Constant = "常量";

    /// <param name="hasCurve">该地址有曲线。</param>
    public static string Label(bool hasCurve) => hasCurve ? Curve : Constant;
}

/// <summary>
/// 检查器渲染哪些段（PvStageWindow.BuildInspector 实际消费的就是它）。
///
/// 工程段承载歌词样式下拉，而那是改歌词样式的唯一入口，所以它**不能**挂在"有没有选中图层"上：
/// 一旦按选中与否二选一，点过任意图层卡之后本次会话就再也回不到工程段。
/// 这个纯函数存在就是为了让这条可达性有回归测试可钉（见 InspectorLayoutTests）。
/// </summary>
public static class InspectorLayout
{
    public static IReadOnlyList<InspectorSectionKind> Sections(bool hasLayerSelection) =>
        hasLayerSelection
            ? [InspectorSectionKind.Camera, InspectorSectionKind.Project, InspectorSectionKind.Layer]
            : [InspectorSectionKind.Camera, InspectorSectionKind.Project];
}
