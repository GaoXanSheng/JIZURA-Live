using System.Diagnostics;
using System.ComponentModel;
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
using JizuraLive.Vts;
using Vortice.Mathematics;

namespace JizuraLive.App.PvStage;

/// <summary>
/// PV 工作台主窗口：舞台直呈 + 图层栈 + 效果架 + 传输条。
/// 纯 Vortice D3D11 现代管线驱动，零旧引擎包袱。
/// </summary>
public partial class PvStageWindow : Window
{
    private readonly DirectorProject _project;
    private readonly PvScene _scene;
    private readonly EffectGraph _effects;
    private readonly AudioClockDriver _audio = new();
    private volatile PvPipeline? _pipe;

    // 音频与歌词的绑定：回写进 _project 后随工程一起落盘，下次打开自动恢复
    private string? _audioPath;
    private string? _lrcText;

    private int _saveTick;
    private string? _saveError;
    private System.Windows.Threading.DispatcherTimer? _uiTimer;
    private readonly List<PvLayerConfig> _uiLayers = new();
    private string[] _uiLayerIds = Array.Empty<string>();
    private System.Windows.Point _dragStart;
    private PvLayerConfig? _dragLayer;

    private EffectNode? _colorNode;
    private EffectParam? _colorParam;
    private Border? _colorSwatch;
    private bool _draggingTime;

    // 关键帧曲线：编辑只发生在这一份（UI 线程）上，渲染线程拿到的是 PublishDirector 发布的不可变快照。
    // 地址解析一次存字段：它是值相等类型，每帧/每次编辑现拼字符串没有意义。
    private readonly DirectorEvaluator _evaluator = new();
    private readonly ParamAddress _camScaleAddr = new(ParamAddressKind.Camera, "", "scale");

    // 缩放的值域（与 XAML 里滑杆的 Minimum / Maximum 是同一对数）：曲线条的纵向映射也用它，两处必须同域
    private const double ScaleMin = 0.5;
    private const double ScaleMax = 2.0;

    // 关键帧圆点直径
    private const double KeyDotSize = 8;

    private bool _updatingScale;  // 程序化回写滑杆时为 true：那条 ValueChanged 不是用户输入，不能当编辑写回去
    private double _stripSpan;    // 曲线条上次重画用的时间跨度，用来发现"时长变了要及时重量程"
    private System.Windows.Shapes.Line? _playhead;  // 播放头：只挪位置，不参与整条重画
    private double _scaleEditTime;  // 滑杆编辑的时刻：按下滑杆那一刻冻结，拖动全程替换同一条 key（播放中时钟在走，不冻结会堆 key）

    // 广播开关：UI 只持有"用户意图"，native 发送端由管线在渲染线程建/拆
    private bool _broadcastOn;
    private readonly string _spoutName;

    // VTS 会话：null = 关。UI 线程建/断（Stop 单向不可逆，重连只能 new 新实例）；
    // volatile 是因为 OnStageReady（渲染线程）要把它接给新管线。
    private volatile VtsApi? _vts;

    private bool _ready => _pipe is { Ready: true };

    public PvStageWindow()
    {
        InitializeComponent();

        var settings = App.SettingsOf();
        _broadcastOn = settings?.SpoutEnabled ?? false;
        _spoutName = settings?.SpoutName ?? "JIZURA-live-PV";

        var boot = ProjectStore.LoadOrImport(ProjectPath, LegacyScenePath, LegacyEffectsPath);
        _project = boot.Project;
        _scene = _project.Scene;
        _effects = _project.Effects;
        _audioPath = _project.AudioPath;
        _lrcText = _project.LrcText;
        if (string.IsNullOrWhiteSpace(_scene.AmbientTint)) _scene.AmbientTint = "#8899ff";

        // 恢复工程里的关键帧曲线。ProjectStore.Load 已经治理过坏地址，这里再挡一次：
        // 解析失败的条目一旦进了求值器，就是一条谁也取不到、UI 上也看不见的孤儿曲线，会一直跟着存回磁盘。
        foreach (var save in _project.Curves)
        {
            if (save?.Curve is null) continue;
            if (!ParamAddress.TryParse(save.Address, out var addr)) continue;
            _evaluator.SetCurve(addr, save.Curve);
        }

        Loaded += OnLoaded;
        Closing += OnClosing;
        Stage.Ready += OnStageReady;
        Stage.Prepare += OnStagePrepare;
        Stage.Frame += OnStageFrame;
        Stage.Shutdown += OnStageShutdown;

        BuildLayerList();
        BuildEffectRack();
        BuildPickers();

        _uiTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _uiTimer.Tick += (_, _) =>
        {
            RefreshStatus();
            if (++_saveTick >= 10)
            {
                _saveTick = 0;
                // 存盘留在 UI 线程做：渲染帧路径不碰磁盘。失败留在状态条上，不静默吞掉。
                _saveError = SaveProjectNow();
            }
        };
        _uiTimer.Start();

        // 载入结论（导入 / 载入失败）必须在日志与状态条各留一句：用户不能只在心里猜工程为什么是空的
        var bootNotes = new List<string>();
        if (boot.ImportedFromLegacy)
        {
            Log.Info($"[PV 工作台] 已从旧版场景/效果导入工程：{ProjectPath}");
            bootNotes.Add("已从旧版场景/效果导入工程");
        }
        if (boot.Fault is { } bootFault)
        {
            Log.Warn($"[PV 工作台] 工程未能完整载入，磁盘文件保持原样：{bootFault}");
            bootNotes.Add($"载入异常：{bootFault}（磁盘文件保持原样）");
        }
        if (bootNotes.Count > 0) TblStatus.Text = string.Join("；", bootNotes);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _audio.Play();
        if (_ready) TblStageHint.Visibility = Visibility.Collapsed;
    }

    private static string ProjectPath => Path.Combine(AppDirs.ConfigDir, "pv_project.jzpv");
    private static string LegacyScenePath => Path.Combine(AppDirs.ConfigDir, "pv_scene.json");
    private static string LegacyEffectsPath => Path.Combine(AppDirs.ConfigDir, "pv_effects.json");

    /// <summary>
    /// 把内存工程落盘（.jzpv）。UI 线程专用（调用点都在 UI 线程），失败原因交状态条显示。
    /// _scene / _effects 与 _project 本来就是同一批引用，只需回写两个字符串字段。
    /// </summary>
    private string? SaveProjectNow()
    {
        _project.AudioPath = _audioPath;
        _project.LrcText = _lrcText;
        SyncCurvesToProject();
        return ProjectStore.Save(_project, ProjectPath);
    }

    /// <summary>
    /// 把求值器里的曲线回写进工程文档，供随后的一次序列化落盘。
    ///
    /// 为什么可以共享同一个 <see cref="FloatCurve"/> 实例：ProjectStore.Save 就在这一句之后、
    /// 在 UI 线程上同步序列化，而渲染线程只读它自己那份快照（PublishDirector 发的），
    /// 不会有人与这次序列化同时动 _project.Curves。
    /// </summary>
    private void SyncCurvesToProject()
    {
        _project.Curves.Clear();
        foreach (var (addr, curve) in _evaluator.Curves)
            _project.Curves.Add(new CurveSave { Address = addr.ToString(), Curve = curve });
    }

    /// <summary>
    /// 把当前曲线整份拷成快照交给管线（引用交换，渲染线程下一帧读到）。
    /// 每次编辑之后都必须调：渲染线程只看快照，漏掉这一句 = 这次编辑屏幕上不生效。
    /// </summary>
    private void PublishDirector() => _pipe?.SetDirector(_evaluator.Snapshot());

    /// <summary>
    /// 停机顺序按执行契约走：先完成保存/放弃决策，再停止发布（取帧、音频），
    /// 最后等渲染线程在自己的线程上释放 GPU 资源并确认退出。反过来做就是 use-after-dispose。
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var fault = SaveProjectNow();
        if (fault is not null)
        {
            Log.Error($"[PV 工作台] 关闭前保存失败：{fault}");
            if (MessageBox.Show(this,
                    $"工程没能保存：{fault}\n\n放弃这次改动并关闭？",
                    "PV 工作台", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                // 取消或没救回来都不许停活动会话：留着窗口，让用户自己处理
                e.Cancel = true;
                return;
            }
        }

        // 定时器一停，写盘的人就只剩关闭流程与渲染线程收尾，不会有两个 File.WriteAllText 撞同一文件
        _uiTimer?.Stop();

        var pipe = _pipe;
        pipe?.StopPumping();
        var maxRecv = pipe?.MaxReceiveMs ?? 0;
        _audio.Dispose();
        _vts?.Stop(); // VTS 会话（工作线程 + 命令队列）与音频一样，停机时收掉

        if (!Stage.StopRender())
        {
            // 线程还没出来：不能由 UI 线程替它释放，否则正在用的设备上下文会被拆掉
            Log.Error("[PV 工作台] 停机握手未完成，管线留给渲染线程收尾");
            return;
        }

        if (pipe is not null)
            Log.Info($"[PV 工作台] 已按握手关闭（{_scene.Layers.Count} 层，Spout 取帧最长 {maxRecv:F1} ms）");
    }

    /// <summary>渲染线程确认退出后、设备尚未释放前，在本线程回收管线资源。</summary>
    private void OnStageShutdown(PresentHost host)
    {
        var pipe = _pipe;
        _pipe = null;
        pipe?.Dispose();

        if (host.Error is not null)
            Dispatcher.BeginInvoke(() => TblStatus.Text = "预览已停止：" + host.Error);
    }

    private void OnStageReady(PresentHost host)
    {
        try
        {
            _pipe?.Dispose();
            _pipe = null;

            _pipe = new PvPipeline(host.Gpu!, _scene, _effects);
            // 建完管线就把工程里已有的曲线带上，否则要等第一次编辑才生效。
            // 绕一下 UI 线程：本回调跑在渲染线程上，而曲线字典归 UI 线程所有——
            // 在这里直接 Snapshot() 会与用户编辑撞上同一个字典（枚举中改 = 抛异常）。
            Dispatcher.BeginInvoke(new Action(PublishDirector));

            // 万一 VTS 会话在管线之前就开着（不太可能，但保持自洽）：把它的探针接上
            if (_vts is not null) _pipe.SetTracking(_vts.Tracking);

            UpdateBroadcastButton();
            // 沿用设置：上次开着广播的这次开窗自动接上（发送端由管线在渲染线程建）
            if (_broadcastOn) _pipe.RequestBroadcast(true, _spoutName);

            _pipe.LayersChanged += () => Dispatcher.BeginInvoke(() =>
            {
                BuildLayerList();
                _saveError = SaveProjectNow();
            });

            // 恢复工程里绑定的歌词与音频。放在这里而不是 OnLoaded：OnLoaded 会 _audio.Play()，
            // 而恢复要停在暂停态，等用户自己点播放。
            if (!string.IsNullOrWhiteSpace(_lrcText))
            {
                try
                {
                    var lines = LrcParser.Parse(_lrcText);
                    _pipe.SetLyrics(lines);
                    Log.Info($"[PV 工作台] 已恢复工程歌词：{lines.Count} 行");
                }
                catch (Exception ex)
                {
                    Log.Warn("[PV 工作台] 工程歌词解析失败：" + ex.Message);
                }
            }

            if (!string.IsNullOrWhiteSpace(_audioPath))
            {
                if (!File.Exists(_audioPath))
                {
                    Log.Warn($"[PV 工作台] 工程音频文件不存在：{_audioPath}");
                    Dispatcher.BeginInvoke(() => TblStatus.Text = $"找不到工程音频：{_audioPath}");
                }
                else if (!_audio.LoadAudio(_audioPath))
                {
                    Dispatcher.BeginInvoke(() => TblStatus.Text = "工程音频加载失败：" + _audioPath);
                }
            }

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
        pipe.Prepare(_audio.Time);
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
        // 按 Id 序列判断是否需要重建：层数相同但换了序/换了层（重载、上移下移、增删成对）时，
        // 只比 Count 会留着旧对象继续显示。
        var ids = _scene.Layers.Select(l => l.Id).ToArray();
        if (!_uiLayerIds.SequenceEqual(ids))
        {
            _uiLayerIds = ids;
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
                del.Click += (_, _) =>
                {
                    // 曲线跟着图层走：SeedLayerSeq 会在"删最高号 src_N + 重启"后重新发出同一个
                    // src_N，遗留的 layer:src_N.* 曲线会被下一个同名新层继承（与效果实例串味同理）。
                    // 现在还没有图层曲线的编辑入口，但级联语义先钉死，别等 UI 造得出来才补。
                    _evaluator.RemoveCurvesForTarget(idRef);
                    PublishDirector();
                    _pipe?.RequestRemove(idRef);
                };
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
                // 删实例连带删它的曲线：按实例 Id 挂的动画不能留给同 DefId 的下一个实例去继承
                _evaluator.RemoveCurvesForTarget(nRef.InstanceId);
                PublishDirector();
                _saveError = SaveProjectNow();
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
            _lrcText = text; // 绑定进工程：下次打开自动恢复
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
            _audioPath = dlg.FileName; // 绑定进工程：下次打开自动恢复（停在暂停态）
            TblStatus.Text = "音频已加载";
            var lrcPath = Path.ChangeExtension(dlg.FileName, ".lrc");
            if (File.Exists(lrcPath))
            {
                try
                {
                    var text = File.ReadAllText(lrcPath);
                    var lines = LrcParser.Parse(text);
                    _lrcText = text;
                    _pipe?.SetLyrics(lines);
                    Log.Info($"[工作台] 自动加载同名歌词：{lines.Count} 行");
                }
                catch { }
            }
        }
    }

    private void BtnBroadcast_Click(object sender, RoutedEventArgs e)
    {
        _broadcastOn = !_broadcastOn;
        _pipe?.RequestBroadcast(_broadcastOn, _spoutName);

        var s = App.SettingsOf();
        if (s is not null)
        {
            s.SpoutEnabled = _broadcastOn;
            s.Save();
        }

        // 按钮只说开关状态；发没发出去、下游接没接住，看状态条的广播回执
        UpdateBroadcastButton();
    }

    private void UpdateBroadcastButton() => BtnBroadcast.Content = _broadcastOn ? "广播 开" : "广播 关";

    // ---------------- 镜头缩放的关键帧 ----------------

    /// <summary>
    /// 给 camera.scale 打一个 key：时刻取当前播放位置，值取滑杆当前值。
    /// 曲线不存在就新建——"还没曲线时按打 key"是最自然的第一次打点方式，
    /// 逼用户先做一步"新建曲线"没有任何信息量。
    /// </summary>
    private void BtnKeyCamera_Click(object sender, RoutedEventArgs e)
    {
        var t = _audio.Time;
        // 打 key 的值不能拿滑杆显示值：滑杆只在 500ms 定时器上跟播放位置同步，
        // seek 之后立刻打 key 会把上一位置的值钉到新时刻上。曲线存在就按"这一刻曲线本来该是什么"
        // （也是管线实际生效的值，不夹显示域——曲线是替换语义，夹过再写回会把取景静默拉近/推远）来打；
        // 只在新建曲线时才用滑杆值起步。
        var v = _evaluator.TryEvaluate(_camScaleAddr, t, out var eval)
            ? eval
            : SldCamScale.Value;

        if (!_evaluator.Curves.TryGetValue(_camScaleAddr, out var curve))
        {
            curve = new FloatCurve();
            _evaluator.SetCurve(_camScaleAddr, curve);
        }

        // 同一时刻只许有一个 key（TryAdd 会拒绝重复时刻），所以"替换"用 Remove + TryAdd 表达；
        // 反复点打 key 只是刷新同一条 key 的值，不会堆出一摞同刻的 key。
        // 替换沿用原 key 的插值模式（模式是用户的选择）；新时刻才用 SmoothStep。
        var mode = ModeOfKeyAt(curve, t) ?? CurveInterp.SmoothStep;
        curve.Remove(t);
        curve.TryAdd(t, v, mode);

        PublishDirector();
        RedrawCurveStrip();
        _saveError = SaveProjectNow();
        TblStatus.Text = $"已打关键帧 t={t:F2}s · 缩放 {v:F2}";
    }

    /// <summary>同刻窗口（1µs，与 FloatCurve 一致）内已有 key 的插值模式；没有返回 null。</summary>
    private static CurveInterp? ModeOfKeyAt(FloatCurve curve, double time)
    {
        foreach (var k in curve.Keys)
            if (Math.Abs(k.Time - time) < 1e-6) return k.Mode;
        return null;
    }

    private void SldCamScale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // 挡两条非用户路径：工程装载前 InitializeComponent 定 Minimum 夹 Value 那一次（若事件已接上），
        // 以及 RefreshStatus 每 500ms 把"当前生效值"写回滑杆那一次。
        if (_updatingScale || _scene is null) return;

        var v = e.NewValue;
        if (_evaluator.Curves.TryGetValue(_camScaleAddr, out var curve))
        {
            // 有曲线时滑杆改的是"这一刻的曲线值"。编辑时刻在按下滑杆那一刻冻结：
            // 播放中设备钟每帧推进，相邻 ValueChanged 的时刻差远大于 1µs 的同刻窗口，
            // 不冻结的话 Remove 找不到上一条 key、TryAdd 每次都插新的，一次拖动堆出上百条 key。
            if (!SldCamScale.IsMouseCaptureWithin) _scaleEditTime = _audio.Time;
            var t = _scaleEditTime;
            // 替换沿用该 key 原有的插值模式：模式是用户的选择，改值不该把它静默抹成 SmoothStep。
            // 新时刻才用 SmoothStep 默认——与打 key 按钮同款。
            var mode = ModeOfKeyAt(curve, t) ?? CurveInterp.SmoothStep;
            curve.Remove(t);
            curve.TryAdd(t, v, mode);
            PublishDirector();
            RedrawCurveStrip();
        }
        else
        {
            // 还没有曲线：滑杆改的是取景基础值（与 PvScene.Camera.Scale 是同一份事实）
            _scene.Camera ??= new PvCameraConfig(); // 手改文件可能留 null，基础值得有落点
            _scene.Camera.Scale = v;
            _scaleEditTime = _audio.Time; // 无曲线路径也要记住时刻，之后第一次打 key 从它起步
        }

        // 存盘不在每个 ValueChanged 里做：这是整份工程 JSON 的序列化+原子写，拖动一次能触发上百次
        // （UiStallProbe 自己就把文件写标注为 UI 卡顿来源）。拖动中只更新内存模型，松手时落盘一次。
        if (!SldCamScale.IsMouseCaptureWithin) _saveError = SaveProjectNow();
    }

    /// <summary>曲线条的时间跨度（秒）：跟音频总长走；没音源时用 30 秒占位，好过拿 0 当除数。</summary>
    private double CurveSpan() => _audio.Duration > 0.5 ? _audio.Duration : 30;

    private double TimeToX(double t, double width) => _stripSpan <= 0 ? 0 : t / _stripSpan * width;

    /// <summary>值 → 纵向像素，固定映射 [0.5, 2.0]（与滑杆同域）：自动量程会在打一个 key 时让整条曲线跳形。</summary>
    private static double ScaleToY(double value, double height) =>
        (1 - (value - ScaleMin) / (ScaleMax - ScaleMin)) * height;

    private void CurveStrip_SizeChanged(object sender, SizeChangedEventArgs e) => RedrawCurveStrip();

    /// <summary>
    /// 重画关键帧条。整条重画只在尺寸变化与编辑之后发生；
    /// 播放头是独立元素，RefreshStatus 里只挪它的位置，播放时不会闪。
    /// </summary>
    private void RedrawCurveStrip()
    {
        CurveStrip.Children.Clear();

        var w = CurveStrip.ActualWidth;
        var h = CurveStrip.Height;
        _stripSpan = CurveSpan();

        var accent = (System.Windows.Media.Brush)FindResource("PvAccent");

        // 基线画在值域下限（0.5）上：没有这条参照线，一个圆点的高低读不出是拉近还是推远
        CurveStrip.Children.Add(new System.Windows.Shapes.Line
        {
            X1 = 0, X2 = w, Y1 = h - 0.5, Y2 = h - 0.5,
            Stroke = (System.Windows.Media.Brush)FindResource("PvLine"),
            StrokeThickness = 1,
        });

        _evaluator.Curves.TryGetValue(_camScaleAddr, out var curve);

        if (curve is null || curve.Count == 0)
        {
            var hint = new TextBlock
            {
                Text = "无关键帧",
                FontSize = (double)FindResource("PvTextXs"),
                Foreground = (System.Windows.Media.Brush)FindResource("PvFgFaint"),
            };
            Canvas.SetLeft(hint, 6);
            Canvas.SetTop(hint, (h - 14) / 2);
            CurveStrip.Children.Add(hint);
        }
        else
        {
            if (curve.Count >= 2)
            {
                // 逐 key 直连当近似：SmoothStep 的弧度在 30px 高的条上肉眼分不出来，
                // 按曲线采样反而会让每次编辑都多跑一遍求值。
                var poly = new System.Windows.Shapes.Polyline
                {
                    Stroke = accent,
                    StrokeThickness = 1.2,
                };
                foreach (var k in curve.Keys)
                    poly.Points.Add(new System.Windows.Point(
                        TimeToX(k.Time, w), Math.Clamp(ScaleToY(k.Value, h), 0, h)));
                CurveStrip.Children.Add(poly);
            }

            foreach (var key in curve.Keys)
            {
                var x = TimeToX(key.Time, w);
                // 值域外的 key 钉在上下边缘：管线的保护只拦 Scale<=0，0.5~2 之外的值会真实生效
                // （旧版导入/手改工程都能造出来）。夹进显示域但保留可点选——把它画到画布外，
                // 用户就删不掉它了；y 越贴边越说明这条 key 超域。
                var y = Math.Clamp(ScaleToY(key.Value, h), 0, h);
                var outOfRange = key.Value < ScaleMin || key.Value > ScaleMax;
                var dot = new System.Windows.Shapes.Ellipse
                {
                    Width = KeyDotSize,
                    Height = KeyDotSize,
                    Fill = accent,
                    Stroke = outOfRange ? (System.Windows.Media.Brush)FindResource("PvFg") : null, // 越界标记
                    StrokeThickness = outOfRange ? 1.5 : 0,
                    Cursor = Cursors.Hand,
                    ToolTip = outOfRange ? $"越界值 {key.Value:F2}（显示域 {ScaleMin:0.0}–{ScaleMax:0.0}）" : null,
                    Tag = key.Time, // 删除时靠它把时刻找回来
                };
                Canvas.SetLeft(dot, x - KeyDotSize / 2);
                Canvas.SetTop(dot, y - KeyDotSize / 2);

                var keyTime = key.Time;
                dot.MouseLeftButtonDown += (_, e) =>
                {
                    // 先吃掉事件：命中 key 的这一次点击不能既删了 key、又冒泡到画布背景变成一次 seek
                    e.Handled = true;
                    if (!curve.Remove(keyTime)) return;
                    // 空了就把整条曲线撤掉：求值上"空曲线"等价于"没有曲线"，存档里留个空壳只会误导
                    if (curve.Count == 0) _evaluator.RemoveCurve(_camScaleAddr);

                    PublishDirector();
                    RedrawCurveStrip();
                    _saveError = SaveProjectNow();
                    TblStatus.Text = "已删除关键帧";
                };

                CurveStrip.Children.Add(dot);
            }
        }

        _playhead = new System.Windows.Shapes.Line
        {
            X1 = 0, X2 = 0, Y1 = 0, Y2 = h,
            Stroke = (System.Windows.Media.Brush)FindResource("PvFg"),
            StrokeThickness = 1,
            IsHitTestVisible = false, // 播放头压在 key 上时，点击要落在 key 上，不能被它吃掉
        };
        CurveStrip.Children.Add(_playhead);
        UpdatePlayhead();
    }

    /// <summary>把播放头挪到当前时刻。位置用上次重画时的那根时间轴（_stripSpan），与圆点同源。</summary>
    private void UpdatePlayhead()
    {
        var play = _playhead;
        if (play is null) return;
        var w = CurveStrip.ActualWidth;
        if (w <= 0 || _stripSpan <= 0) return;
        Canvas.SetLeft(play, Math.Clamp(_audio.Time / _stripSpan * w, 0, w));
    }

    /// <summary>点曲线条空白处 = 定位（圆点会自己把事件吃掉，走不到这里）。</summary>
    private void CurveStrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var w = CurveStrip.ActualWidth;
        if (w <= 0) return;
        _audio.Seek(e.GetPosition(CurveStrip).X / w * _stripSpan);
    }

    // ---------------- VTS 会话 ----------------

    /// <summary>
    /// 一个按钮包办会话全周期：关→开；待授权→再请求一次授权；其余状态→断开。
    /// 断开为什么是"销毁"而不是"挂起"：VtsApi.Stop/Dispose 单向不可逆（工作线程与命令队列
    /// 不会复活），要用只能 new 新实例——探针随之新建，靠 SetTracking 换给管线。
    /// 不做二次确认弹窗：这是本机的可逆操作（再点一下就回来），状态条会说清楚结果。
    /// </summary>
    private void BtnVts_Click(object sender, RoutedEventArgs e)
    {
        var vts = _vts;
        if (vts is null)
        {
            _vts = vts = new VtsApi();
            vts.Start();
            _pipe?.SetTracking(vts.Tracking);
        }
        else if (vts.State == VtsState.NeedAuth)
        {
            vts.RequestAuth(); // VTS 侧会弹出插件授权框，用户点「允许」后才继续
        }
        else
        {
            _pipe?.SetTracking(null); // 先摘探针：渲染线程立刻回场景基础值
            vts.Stop();
            vts.Dispose();
            _vts = null;
        }

        RefreshStatus();
    }

    /// <summary>VTS 段的状态条文案（独立于 Spout 源状态：一个说输入，一个说输出）。</summary>
    private string VtsStatusText()
    {
        var vts = _vts;
        if (vts is null) return "VTS 关";

        switch (vts.State)
        {
            case VtsState.Disconnected:
            {
                var err = vts.LastError;
                if (string.IsNullOrWhiteSpace(err)) return "VTS 未连接";
                return $"VTS 未连接：{(err.Length > 60 ? err[..60] : err)}";
            }
            case VtsState.NeedAuth:
                return "VTS 待授权（再点 VTS 按钮）";
            case VtsState.AwaitApproval:
                return "VTS 等待授权…";
            case VtsState.Ready:
                var posed = vts.Tracking.TryRead(500, out var st) && st.FaceFound;
                return posed ? "VTS 已连接 · 姿态有效" : "VTS 已连接 · 无姿态";
            default:
                return "VTS 未连接";
        }
    }

    private string VtsButtonText() => _vts?.State switch
    {
        null => "VTS 关",
        VtsState.NeedAuth => "VTS 待授权",
        VtsState.AwaitApproval => "VTS 等待授权",
        VtsState.Ready => "VTS 已连接",
        _ => "VTS 未连接",
    };

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

        // 滑杆显示"当前生效的缩放"：有曲线是曲线值，没有就是场景基础值。
        // 用户正按着滑杆时不同步（否则刚拖到一半就被拉回去）；程序化设 Value 会再触发一次 ValueChanged，
        // 用 _updatingScale 把那一次挡在编辑路径外。
        if (!SldCamScale.IsMouseCaptureWithin)
        {
            var want = _evaluator.TryEvaluate(_camScaleAddr, t, out var curveScale)
                ? Math.Clamp(curveScale, ScaleMin, ScaleMax)
                : Math.Clamp(_scene.Camera?.Scale ?? 1.0, ScaleMin, ScaleMax);

            if (Math.Abs(SldCamScale.Value - want) > 1e-4)
            {
                _updatingScale = true;
                try { SldCamScale.Value = want; }
                finally { _updatingScale = false; }
            }
        }

        // 时长会变（载入/换音源），时间→像素的比例跟着变：重画一次让 key 与播放头重新对齐。
        // 播放头只是被挪位置，不重画整条——播放中每 500ms 清一次 Children 会闪。
        if (Math.Abs(_stripSpan - CurveSpan()) > 1e-6) RedrawCurveStrip();
        UpdatePlayhead();

        var pipe = _pipe;
        if (pipe is not null)
        {
            var (eng, fx, comp) = pipe.Renderer.LastTiming;
            TblStageInfo.Text = $"设计 {pipe.Renderer.DesignWidth}×{pipe.Renderer.DesignHeight}"
                                + $"  预览 {Stage.Target?.Width ?? 0}×{Stage.Target?.Height ?? 0}"
                                + $"  {Stage.Fps:F1} fps"
                                + $"  歌词 {eng:F1} + 特效 {fx:F1} + 合成 {comp:F1} ms";
            TblFps.Text = $"{Stage.Fps:F1} fps";

            var (live, total) = pipe.SpoutState;
            var srcTxt = total == 0 ? "无外部源" : $"{live}/{total} 路源已连";
            TblStatus.Text = $"效果 {pipe.Renderer.EffectCount} 段 · 图层 {_scene.Layers.Count} · {srcTxt}";

            var b = pipe.BroadcastInfo;
            if (b.Enabled)
            {
                string seg;
                if (b.Sent > 0) seg = b.Failed > 0 ? $"广播 {b.Sent} 帧 · 丢 {b.Failed}" : $"广播 {b.Sent} 帧";
                else if (b.LastError is { } berr) seg = $"广播失败：{berr}";
                else seg = "广播就绪";
                TblStatus.Text += $" · {seg}";
            }
        }

        // VTS 与 Spout 源状态分开两段：一个说输入（追踪姿态有没有），一个说输出（广播发没发出去）
        BtnVts.Content = VtsButtonText();
        TblStatus.Text += $" · {VtsStatusText()}";

        if (_audio.Error is { } fault) TblStatus.Text = $"音频已暂停：{fault}";
        if (_saveError is { } saveFault) TblStatus.Text = $"工程未保存：{saveFault}";

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
