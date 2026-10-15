using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using JizuraLive.Spout;
using Vortice.Mathematics;

namespace JizuraLive.App.PvStage;

/// <summary>
/// PV 工作台主窗口：舞台直呈 + 图层栈 + 效果架 + 传输条。
/// 纯 Vortice D3D11 现代管线驱动，零旧引擎包袱。
/// </summary>
public partial class PvStageWindow : Window
{
    private readonly PvScene _scene;
    private readonly EffectGraph _effects;
    private readonly AudioClockDriver _audio = new();
    private volatile PvPipeline? _pipe;

    private int _saveTick;
    private readonly List<PvLayerConfig> _uiLayers = new();
    private System.Windows.Point _dragStart;
    private PvLayerConfig? _dragLayer;

    private EffectNode? _colorNode;
    private EffectParam? _colorParam;
    private Border? _colorSwatch;
    private bool _draggingTime;

    private bool _ready => _pipe is { Ready: true };

    public PvStageWindow()
    {
        InitializeComponent();

        _scene = PvPipeline.LoadScene(ScenePath);
        if (string.IsNullOrWhiteSpace(_scene.AmbientTint)) _scene.AmbientTint = "#8899ff";
        _effects = new EffectGraph();

        Loaded += OnLoaded;
        Closed += OnClosed;
        Stage.Ready += OnStageReady;
        Stage.Prepare += OnStagePrepare;
        Stage.Frame += OnStageFrame;

        BuildLayerList();
        BuildEffectRack();
        BuildPickers();

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        timer.Tick += (_, _) =>
        {
            RefreshStatus();
            if (++_saveTick >= 10)
            {
                _saveTick = 0;
                _pipe?.RequestSave();
            }
        };
        timer.Start();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _audio.Play();
        if (_ready) TblStageHint.Visibility = Visibility.Collapsed;
    }

    private static string ScenePath => Path.Combine(AppDirs.ConfigDir, "pv_scene.json");

    private void OnClosed(object? sender, EventArgs e)
    {
        _pipe?.SaveSceneNow();
        Log.Info($"[PV 舞台] 场景已保存：{_scene.Layers.Count} 层（{ScenePath}）");

        _audio.Dispose();
        _pipe?.Dispose();
    }

    private void OnStageReady(PresentHost host)
    {
        try
        {
            _pipe?.Dispose();
            _pipe = null;

            _pipe = new PvPipeline(host.Gpu!, ScenePath, _scene, _effects, 1920, 1080);
            _pipe.Renderer.Stage.Driver = _audio;

            _pipe.LayersChanged += () => Dispatcher.BeginInvoke(new Action(BuildLayerList));
            _pipe.EffectsChanged += () => Dispatcher.BeginInvoke(new Action(BuildEffectRack));

            var msg = $"[PV 舞台] 渲染就绪：{host.Gpu!.AdapterName}";
            Log.Info(msg);
            Console.WriteLine(msg);

            Dispatcher.BeginInvoke(() => TblStageHint.Visibility = Visibility.Collapsed);
        }
        catch (Exception ex)
        {
            Log.Error("[PV 舞台] 初始化失败：" + ex.Message, ex);
            Dispatcher.BeginInvoke(() =>
            {
                TblStageHint.Visibility = Visibility.Visible;
                TblStageHint.Text = "渲染初始化失败：" + ex.Message;
                TblStatus.Text = "初始化失败";
            });
        }
    }

    private void OnStagePrepare(PresentHost host)
    {
        var pipe = _pipe;
        if (pipe is null || !pipe.Ready || host.Target is null) return;
        pipe.Prepare(_audio.Time, host.Target.Width, host.Target.Height);
    }

    private void OnStageFrame(PresentHost host, double dt)
    {
        var pipe = _pipe;
        if (pipe is null || !pipe.Ready || host.Target is null) return;

        var w = host.Target.Width;
        var h = host.Target.Height;
        using var back = host.Target.GetBackBuffer();
        using var surface = back.QueryInterface<Vortice.DXGI.IDXGISurface>();
        using var target = pipe.D2DContext.CreateBitmapFromDxgiSurface(surface,
            new Vortice.Direct2D1.BitmapProperties1(
                new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm,
                    Vortice.DCommon.AlphaMode.Premultiplied),
                96f, 96f, Vortice.Direct2D1.BitmapOptions.Target | Vortice.Direct2D1.BitmapOptions.CannotDraw));

        pipe.Composite(target, w, h);
    }

    // ---------------- 图层栈 ----------------

    private void BuildLayerList()
    {
        if (_uiLayers.Count != _scene.Layers.Count)
        {
            _uiLayers.Clear();
            for (var i = _scene.Layers.Count - 1; i >= 0; i--) _uiLayers.Add(_scene.Layers[i]);
        }

        LayersHost.Children.Clear();
        foreach (var layer in _uiLayers)
        {
            var card = new Border
            {
                Style = (Style)FindResource("PvCard"),
                AllowDrop = true,
                Cursor = Cursors.SizeAll,
            };
            card.Tag = layer;
            card.PreviewMouseLeftButtonDown += LayerCard_MouseDown;
            card.PreviewMouseMove += LayerCard_MouseMove;
            card.DragOver += LayerCard_DragOver;
            card.Drop += LayerCard_Drop;
            card.DragLeave += (s2, _) => ((Border)s2!).BorderBrush = null;
            var stack = new StackPanel();
            card.Child = stack;

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(layer.SpoutSenderName) ? layer.Name : layer.SpoutSenderName!,
                FontSize = (double)FindResource("PvTextBase"),
                Foreground = (System.Windows.Media.Brush)FindResource("PvFg"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 0);
            head.Children.Add(name);

            var headRight = new StackPanel { Orientation = Orientation.Horizontal };
            var toggle = new System.Windows.Controls.Primitives.ToggleButton
            {
                Style = (Style)FindResource("PvSwitch"),
                IsChecked = layer.Enabled,
            };
            toggle.Checked += (_, _) => layer.Enabled = true;
            toggle.Unchecked += (_, _) => layer.Enabled = false;
            headRight.Children.Add(toggle);

            if (layer.Id.StartsWith("src", StringComparison.Ordinal))
            {
                var del = new Button
                {
                    Style = (Style)FindResource("PvBtnGhost"),
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(6, 0, 0, 0),
                    Content = "✕",
                    ToolTip = "删除本图层",
                };
                var idRef = layer.Id;
                del.Click += (_, _) => _pipe?.RequestRemove(idRef);
                headRight.Children.Add(del);
            }

            Grid.SetColumn(headRight, 1);
            head.Children.Add(headRight);
            stack.Children.Add(head);

            var meta = new TextBlock
            {
                Text = $"{LayerKindLabel(layer.Kind)} · 深度 {layer.Parallax.Depth:F2}",
                FontSize = (double)FindResource("PvTextXs"),
                Foreground = (System.Windows.Media.Brush)FindResource("PvFgFaint"),
                Margin = new Thickness(0, 2, 0, 6),
            };
            stack.Children.Add(meta);

            var lRef = layer;
            stack.Children.Add(ParamRow("深度", layer.Parallax.Depth, 0, 2, 0.05,
                v => lRef.Parallax.Depth = v));
            stack.Children.Add(ParamRow("不透明", layer.Opacity, 0, 1, 0.02,
                v => lRef.Opacity = v));
            stack.Children.Add(ParamRow("环境光", layer.VisualBlend.AmbientTintIntensity, 0, 1, 0.02,
                v => lRef.VisualBlend.AmbientTintIntensity = v));
            stack.Children.Add(ParamRow("边缘光", layer.VisualBlend.RimGlow, 0, 1, 0.02,
                v => lRef.VisualBlend.RimGlow = v));

            LayersHost.Children.Add(card);
        }

        TblLayerCount.Text = $"{_scene.Layers.Count} 层";
    }

    private static string LayerKindLabel(PvLayerKind k) => k switch
    {
        PvLayerKind.Background => "背景",
        PvLayerKind.VisualAsset => "素材/角色",
        PvLayerKind.LyricPv => "歌词动效",
        PvLayerKind.ForegroundFx => "前景特效",
        PvLayerKind.Subtitle => "字幕",
        _ => k.ToString(),
    };

    private void LayerCard_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindAncestor<System.Windows.Controls.Primitives.RangeBase>(d) is not null) return;
        _dragStart = e.GetPosition(this);
        _dragLayer = ((Border)sender).Tag as PvLayerConfig;
    }

    private void LayerCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragLayer is null) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        DragDrop.DoDragDrop((DependencyObject)sender, _dragLayer, DragDropEffects.Move);
        _dragLayer = null;
    }

    private void LayerCard_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(PvLayerConfig)))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Move;
        ((Border)sender).BorderBrush = (System.Windows.Media.Brush)FindResource("PvAccent");
    }

    private void LayerCard_Drop(object sender, DragEventArgs e)
    {
        var targetCard = (Border)sender;
        targetCard.BorderBrush = null;
        if (!e.Data.GetDataPresent(typeof(PvLayerConfig))) return;
        var source = (PvLayerConfig)e.Data.GetData(typeof(PvLayerConfig));
        var target = (PvLayerConfig)targetCard.Tag;
        if (ReferenceEquals(source, target)) return;

        var srcIdx = _uiLayers.IndexOf(source);
        var dstIdx = _uiLayers.IndexOf(target);
        if (srcIdx < 0 || dstIdx < 0) return;

        _uiLayers.RemoveAt(srcIdx);
        _uiLayers.Insert(dstIdx, source);

        _pipe?.RequestOrder(_uiLayers.Select(l => l.Id).ToList());
        BuildLayerList();
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = System.Windows.Media.VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    // ---------------- 效果架 ----------------

    private void BuildEffectRack()
    {
        EffectsHost.Children.Clear();
        foreach (var node in _effects.Nodes)
        {
            var def = EffectCatalog.Get(node.DefId);
            if (def is null) continue;

            var card = new Border { Style = (Style)FindResource("PvCard") };
            var stack = new StackPanel();
            card.Child = stack;

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = def.Id,
                FontSize = (double)FindResource("PvTextBase"),
                Foreground = (System.Windows.Media.Brush)FindResource("PvFg"),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(name, 0);
            head.Children.Add(name);

            var headRight = new StackPanel { Orientation = Orientation.Horizontal };
            var toggle = new System.Windows.Controls.Primitives.ToggleButton
            {
                Style = (Style)FindResource("PvSwitch"),
                IsChecked = node.Enabled,
            };
            var nRef = node;
            toggle.Checked += (_, _) => nRef.Enabled = true;
            toggle.Unchecked += (_, _) => nRef.Enabled = false;
            headRight.Children.Add(toggle);

            var del = new Button
            {
                Style = (Style)FindResource("PvBtnGhost"),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(6, 0, 0, 0),
                Content = "✕",
                ToolTip = "删除该效果",
            };
            del.Click += (_, _) =>
            {
                _effects.Nodes.Remove(nRef);
                BuildEffectRack();
            };
            headRight.Children.Add(del);

            Grid.SetColumn(headRight, 1);
            head.Children.Add(headRight);
            stack.Children.Add(head);

            var meta = new TextBlock
            {
                Text = $"{StageLabel(def.Stage)} · {CategoryLabel(def.Category)}",
                FontSize = (double)FindResource("PvTextXs"),
                Foreground = (System.Windows.Media.Brush)FindResource("PvFgFaint"),
                Margin = new Thickness(0, 2, 0, 6),
            };
            stack.Children.Add(meta);

            foreach (var p in def.Params)
            {
                switch (p.Kind)
                {
                    case EffectParamKind.Number:
                    {
                        var nodeRef = node;
                        stack.Children.Add(ParamRow(p.Key, nodeRef.Value(p), p.Min, p.Max, p.Step,
                            v => nodeRef.Set(p, (float)v)));
                        break;
                    }
                    case EffectParamKind.Bool:
                    {
                        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        var lab = new TextBlock { Text = p.Key, Style = (Style)FindResource("PvRowLabel") };
                        Grid.SetColumn(lab, 0);
                        row.Children.Add(lab);
                        var sw = new System.Windows.Controls.Primitives.ToggleButton
                        {
                            Style = (Style)FindResource("PvSwitch"),
                            IsChecked = node.Value(p) > 0.5f,
                        };
                        var nodeRef = node;
                        sw.Checked += (_, _) => nodeRef.Set(p, 1f);
                        sw.Unchecked += (_, _) => nodeRef.Set(p, 0f);
                        Grid.SetColumn(sw, 1);
                        row.Children.Add(sw);
                        stack.Children.Add(row);
                        break;
                    }
                    case EffectParamKind.Color:
                    {
                        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        var lab = new TextBlock { Text = p.Key, Style = (Style)FindResource("PvRowLabel") };
                        Grid.SetColumn(lab, 0);
                        row.Children.Add(lab);

                        var hex = node.ColorValue(p);
                        var swatch = new Border
                        {
                            Width = 34,
                            Height = 16,
                            CornerRadius = (CornerRadius)FindResource("PvRadiusSm"),
                            Background = BrushFromHex(hex),
                            BorderBrush = (System.Windows.Media.Brush)FindResource("PvLine"),
                            BorderThickness = new Thickness(1),
                            Cursor = Cursors.Hand,
                        };
                        var nodeRef = node;
                        var paramRef = p;
                        var swatchRef = swatch;
                        swatch.MouseLeftButtonDown += (_, _) =>
                            OpenColorPicker(nodeRef, paramRef, swatchRef);
                        Grid.SetColumn(swatch, 1);
                        row.Children.Add(swatch);
                        stack.Children.Add(row);
                        break;
                    }
                }
            }

            EffectsHost.Children.Add(card);
        }

        var count = _effects.Nodes.Count(n => n.Enabled);
        TblStatus.Text = $"效果 {count} 段";
    }

    private static readonly string[] Swatches =
        ["#ffffff", "#8ad4ff", "#ffd24a", "#ff2080", "#38e1c4", "#ff7a45"];

    private void OpenColorPicker(EffectNode node, EffectParam param, Border swatch)
    {
        _colorNode = node;
        _colorParam = param;
        _colorSwatch = swatch;

        TblColorTarget.Text = $"{node.DefId} · {param.Key}";
        TxtHex.Text = node.ColorValue(param);

        SwatchGrid.Items.Clear();
        foreach (var hex in Swatches)
        {
            var b = new Border
            {
                Width = 26,
                Height = 20,
                Margin = new Thickness(0, 0, 3, 3),
                CornerRadius = (CornerRadius)FindResource("PvRadiusSm"),
                Background = BrushFromHex(hex),
                BorderBrush = (System.Windows.Media.Brush)FindResource("PvLine"),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
            };
            var hexRef = hex;
            b.MouseLeftButtonDown += (_, _) => TxtHex.Text = hexRef;
            SwatchGrid.Items.Add(b);
        }

        Dispatcher.BeginInvoke(new Action(() => ColorPop.IsOpen = true),
            System.Windows.Threading.DispatcherPriority.Input);
    }

    private void TxtHex_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_colorNode is null || _colorParam is null) return;
        var hex = TxtHex.Text.Trim();
        if (hex.Length is not (4 or 7)) return;
        if (!hex.StartsWith('#')) return;
        foreach (var c in hex.AsSpan(1))
            if (!Uri.IsHexDigit(c)) return;

        _colorNode.SetColor(_colorParam, hex);
        if (_colorSwatch is not null) _colorSwatch.Background = BrushFromHex(hex);
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

    private static string StageLabel(EffectStage stage) => stage switch
    {
        EffectStage.Source => "源",
        EffectStage.Layer => "图层",
        EffectStage.Composite => "合成",
        EffectStage.GlobalPost => "整帧后期",
        EffectStage.Transition => "转场",
        EffectStage.Output => "输出",
        _ => stage.ToString(),
    };

    private static string CategoryLabel(EffectCategory c) => c switch
    {
        EffectCategory.ColorGrade => "调色",
        EffectCategory.LightGlow => "光效",
        EffectCategory.DistortGlitch => "扭曲故障",
        EffectCategory.Geometry => "几何",
        EffectCategory.Texture => "纹理噪点",
        EffectCategory.Particle => "粒子",
        EffectCategory.Typography => "文字",
        EffectCategory.Camera => "镜头",
        _ => c.ToString(),
    };

    private FrameworkElement ParamRow(string label, double value, double min, double max,
        double step, Action<double> onChange)
    {
        var row = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(42) });

        var lab = new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("PvRowLabel"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(lab, 0);
        row.Children.Add(lab);

        var slider = new Slider
        {
            Style = (Style)FindResource("PvSlider"),
            Minimum = min,
            Maximum = max,
            SmallChange = step,
            Value = value,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var valText = new TextBlock
        {
            Style = (Style)FindResource("PvValue"),
            Text = value.ToString("F2"),
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
        };

        slider.ValueChanged += (_, e) =>
        {
            valText.Text = e.NewValue.ToString("F2");
            onChange(e.NewValue);
        };

        Grid.SetColumn(slider, 1);
        Grid.SetColumn(valText, 2);
        row.Children.Add(slider);
        row.Children.Add(valText);
        return row;
    }

    private void BuildPickers()
    {
        foreach (var def in EffectCatalog.All)
            CmbAddFx.Items.Add($"{def.Id}  ·  {CategoryLabel(def.Category)}");

        foreach (var preset in EffectPresets.All)
            CmbPreset.Items.Add(preset.Id);

        var idx = 0;
        for (var i = 0; i < EffectPresets.All.Count; i++)
            if (EffectPresets.All[i].Id == "cinematic") idx = i;
        CmbPreset.SelectedIndex = idx;
    }

    private void CmbAddFx_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbAddFx.SelectedIndex < 0) return;
        var def = EffectCatalog.All[CmbAddFx.SelectedIndex];
        CmbAddFx.SelectedIndex = -1;
        _effects.Add(def.Id);
        BuildEffectRack();
    }

    private void CmbPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbPreset.SelectedIndex < 0) return;
        var preset = EffectPresets.All[CmbPreset.SelectedIndex];
        var g = preset.Build();
        _effects.Nodes.Clear();
        foreach (var n in g.Nodes) _effects.Nodes.Add(n);
        BuildEffectRack();
    }

    // ---------------- 传输条 ----------------

    private void BtnOpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开歌词文件",
            Filter = "歌词文件 (*.lrc)|*.lrc|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var text = File.ReadAllText(dlg.FileName);
            var lines = LrcParser.Parse(text);
            _pipe?.SetLyrics(lines);
            TblStatus.Text = $"已装载歌词：{lines.Count} 行";
            Log.Info($"[工作台] 已装载歌词：{lines.Count} 行 ({dlg.FileName})");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开歌词失败：" + ex.Message, "PV 舞台", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnOpenAudio_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开音频",
            Filter = "音频 (*.mp3;*.flac;*.wav;*.m4a;*.aac;*.ogg)|*.mp3;*.flac;*.wav;*.m4a;*.aac;*.ogg"
                     + "|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        TblStatus.Text = "正在加载音频…";

        if (_audio.LoadAudio(dlg.FileName))
        {
            TblStatus.Text = "音频已加载";
            var lrcPath = Path.ChangeExtension(dlg.FileName, ".lrc");
            if (File.Exists(lrcPath))
            {
                try
                {
                    var lines = LrcParser.Parse(File.ReadAllText(lrcPath));
                    _pipe?.SetLyrics(lines);
                    Log.Info($"[工作台] 自动加载同名歌词：{lines.Count} 行");
                }
                catch { }
            }
        }
    }

    private void BtnAddLayer_Click(object sender, RoutedEventArgs e)
    {
        IReadOnlyList<string> senders;
        try
        {
            senders = SpoutNames.Enumerate(refresh: true);
        }
        catch (Exception ex)
        {
            senders = Array.Empty<string>();
            Log.Warn("[PV 舞台] 枚举 Spout 发送端失败：" + ex.Message);
        }

        var known = senders.Where(n => !_scene.Layers.Any(l => l.SpoutSenderName == n)).ToList();
        if (known.Count == 0)
        {
            TblStatus.Text = senders.Count == 0
                ? "没有发现活动的 Spout 发送端"
                : "所有发送端都已在图层里";
            return;
        }

        var menu = new ContextMenu { PlacementTarget = BtnAddLayer };
        foreach (var name in known)
        {
            var item = new MenuItem { Header = name };
            var n = name;
            item.Click += (_, _) =>
            {
                _pipe?.RequestAdd(n);
                TblStatus.Text = $"添加源 {n}…";
            };
            menu.Items.Add(item);
        }

        menu.IsOpen = true;
    }

    private void BtnPlay_Click(object sender, RoutedEventArgs e)
    {
        if (_audio.IsPlaying) _audio.Pause();
        else _audio.Play();
        UpdatePlayIcon();
    }

    private void UpdatePlayIcon()
    {
        PlayIcon.Data = _audio.IsPlaying
            ? System.Windows.Media.Geometry.Parse("M0,0 H4 V12 H0 Z M7,0 H11 V12 H7 Z")
            : System.Windows.Media.Geometry.Parse("M0,0 L11,6 L0,12 Z");
    }

    private void SldTime_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_draggingTime) return;
        _audio.Seek(e.NewValue);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _draggingTime = false;
        base.OnPreviewMouseLeftButtonUp(e);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (SldTime.IsMouseOver) _draggingTime = true;
        base.OnPreviewMouseLeftButtonDown(e);
    }

    private void RefreshStatus()
    {
        var t = _audio.Time;
        var d = _audio.Duration;
        TblTime.Text = $"{Fmt(t)} / {Fmt(d)}";
        if (!_draggingTime)
        {
            SldTime.Maximum = Math.Max(0.01, d);
            SldTime.Value = Math.Clamp(t, 0, SldTime.Maximum);
        }

        var pipe = _pipe;
        if (pipe is not null)
        {
            var (eng, fx, comp) = pipe.Renderer.LastTiming;
            TblStageInfo.Text = $"{Stage.Target?.Width ?? 0}×{Stage.Target?.Height ?? 0}"
                                + $"  {Stage.Fps:F1} fps"
                                + $"  歌词 {eng:F1} + 特效 {fx:F1} + 合成 {comp:F1} ms";
            TblFps.Text = $"{Stage.Fps:F1} fps";

            var (live, total) = pipe.SpoutState;
            var srcTxt = total == 0 ? "无外部源" : $"{live}/{total} 路源已连";
            TblStatus.Text = $"效果 {pipe.Renderer.EffectCount} 段 · 图层 {_scene.Layers.Count} · {srcTxt}";
        }

        UpdatePlayIcon();
    }

    private static string Fmt(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 100}";
    }

    // ---------------- 窗口外壳 ----------------

    private void TitleBar_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void BtnMin_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void BtnMax_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        BorderThickness = WindowState == WindowState.Maximized ? new Thickness(6) : new Thickness(0);
    }
}
