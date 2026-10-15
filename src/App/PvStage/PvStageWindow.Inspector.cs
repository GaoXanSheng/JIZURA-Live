using System.IO;
using System.Windows;
using System.Windows.Controls;
using JizuraLive.App.Controls;
using JizuraLive.Pv;

namespace JizuraLive.App.PvStage;

/// <summary>
/// 属性检查器占位：相机（全局，不依赖选中对象）+ 选中图层的四参数。
/// 无选中对象时显示工程级信息（画布 / 帧率 / 时长 / 音频 / 歌词）。
/// </summary>
public partial class PvStageWindow
{
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
        SldCamScale = null!;
        _camBadge = null;
        _camScaleText = null;
        _cmbLyricStyle = null;
        InspectorHost.Children.Clear();

        // 段落计划由 InspectorLayout 给：工程段（歌词样式下拉的唯一入口）与选中无关，始终在。
        // 这里不再按"有没有选中图层"二选一——那样点过图层卡之后本次会话就回不到工程段。
        foreach (var section in InspectorLayout.Sections(_selectedLayer is not null))
        {
            switch (section)
            {
                case InspectorSectionKind.Camera:
                    InspectorHost.Children.Add(SectionTitle("相机"));
                    InspectorHost.Children.Add(CameraScaleRow());
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

    /// <summary>选中图层的四参数。只有选中图层时才被 <see cref="BuildInspector"/> 调到。</summary>
    private void BuildLayerSection(PvLayerConfig layer)
    {
        InspectorHost.Children.Add(SectionTitle($"图层 · {LayerKindLabel(layer.Kind)}"));
        InspectorHost.Children.Add(ParamRow("深度", layer.Parallax.Depth, 0, 2, 0.05,
            v => layer.Parallax.Depth = v, keySlot: true));
        InspectorHost.Children.Add(ParamRow("不透明", layer.Opacity, 0, 1, 0.02,
            v => layer.Opacity = v, keySlot: true));
        InspectorHost.Children.Add(ParamRow("环境光", layer.VisualBlend.AmbientTintIntensity, 0, 1, 0.02,
            v => layer.VisualBlend.AmbientTintIntensity = v, keySlot: true));
        InspectorHost.Children.Add(ParamRow("边缘光", layer.VisualBlend.RimGlow, 0, 1, 0.02,
            v => layer.VisualBlend.RimGlow = v, keySlot: true));
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

    /// <summary>相机缩放行：值来自曲线（有 key）或场景基础值，与打帧按钮共用同一份 handler。</summary>
    private FrameworkElement CameraScaleRow()
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock { Text = "缩放", Style = (Style)FindResource("PvRowLabel") };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);

        var slider = new Slider
        {
            Style = (Style)FindResource("PvSlider"),
            Minimum = ScaleMin,
            Maximum = ScaleMax,
            SmallChange = 0.01,
            Value = Math.Clamp(_scene.Camera?.Scale ?? 1.0, ScaleMin, ScaleMax),
            VerticalAlignment = VerticalAlignment.Center,
        };
        slider.ValueChanged += SldCamScale_ValueChanged;
        SldCamScale = slider;
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);

        var value = new TextBlock
        {
            Style = (Style)FindResource("PvValue"),
            Text = slider.Value.ToString("F2"),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
        };
        _camScaleText = value;
        Grid.SetColumn(value, 2);
        row.Children.Add(value);

        var key = new Button
        {
            Style = (Style)FindResource("PvBtnGhost"),
            Content = "◆K",
            Padding = new Thickness(6, 1, 6, 1),
            Margin = new Thickness(6, 0, 6, 0),
            ToolTip = "在当前时刻打一个镜头缩放关键帧（已存在则替换）",
        };
        key.Click += BtnKeyCamera_Click;
        Grid.SetColumn(key, 3);
        row.Children.Add(key);

        var badge = new JzBadge { VerticalAlignment = VerticalAlignment.Center };
        _camBadge = badge;
        Grid.SetColumn(badge, 4);
        row.Children.Add(badge);
        RefreshCamBadge();
        return row;
    }

    /// <summary>值来源徽标占位：有 key = 曲线，否则 = 常量（连线 / 失效回落 / 限幅标记还没接）。</summary>
    private void RefreshCamBadge()
    {
        if (_camScaleText is not null) _camScaleText.Text = SldCamScale.Value.ToString("F2");

        var badge = _camBadge;
        if (badge is null) return;

        var hasCurve = _evaluator.Curves.TryGetValue(_camScaleAddr, out var curve) && curve.Count > 0;
        badge.Text = hasCurve ? "曲线" : "常量";
        var brush = (System.Windows.Media.Brush)FindResource(hasCurve ? "PvAccent" : "PvFgFaint");
        badge.Foreground = brush;
        badge.BorderBrush = brush;
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
}

/// <summary>检查器里的一段。声明顺序即期望的渲染顺序。</summary>
public enum InspectorSectionKind
{
    Camera,
    Project,
    Layer,
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
