using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using JizuraLive.App.I18n;
using JizuraLive.App.PvStage.Timeline;
using JizuraLive.Engine;
using JizuraLive.Engine.Music;
using JizuraLive.Logging;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using JizuraLive.Render.Text;
using JizuraLive.Spout;
using Newtonsoft.Json;
using Vortice.Mathematics;

namespace JizuraLive.App.PvStage;

public partial class PvStageWindow : Wpf.Ui.Controls.FluentWindow
{
    private DirectorProject _project;
    private PvScene _scene;
    private EffectGraph _effects;

    private volatile AudioClockDriver _audio = new();
    private volatile PvPipeline? _pipe;

    private volatile bool _rebuildPipe;

    private volatile bool _rebuildPipeKeepTransport;

    private volatile EffectGraph _pipeEffects = new();

    private string? _audioPath;
    private string? _lrcText;

    private IReadOnlyList<KrcLine>? _lyrics;

    private List<LyricClip> _lyricClips = new();

    private bool _tailUncalibrated;
    private ComboBox? _cmbLyricStyle;

    private int _saveTick;
    private string? _saveError;
    private System.Windows.Threading.DispatcherTimer? _uiTimer;
    private System.Windows.Threading.DispatcherTimer? _playheadTimer;
    private readonly List<PvLayerConfig> _uiLayers = new();
    private string[] _uiLayerIds = Array.Empty<string>();
    private System.Windows.Point _dragStart;
    private PvLayerConfig? _dragLayer;
    private bool _draggingTime;

    private bool _exporting;
    private double _exportPercent;

    private DirectorEvaluator _evaluator = new();

    private static readonly ParamAddress _camScaleAddr = new(ParamAddressKind.Camera, "", "scale");

    private TSnapshot? _snap;
    private WaveformPeaks? _peaks;

    private CancellationTokenSource? _audioLoadCts;

    private AudioBandSeries? _audioBands;

    private bool _autoGenerating;
    private string? _transientStatus;
    private int _transientStatusTicks;

    private void ShowStatus(string message, int durationSeconds = 5)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => ShowStatus(message, durationSeconds));
            return;
        }
        _transientStatus = message;
        _transientStatusTicks = Math.Max(1, durationSeconds * 2);
        TblStatus.Text = message;
    }

    private PvLayerConfig? _selectedLayer;

    private const double InspectorWidth = 320;
    private bool _inspectorCollapsed;

    private int _step = -1;

    private bool _syncingToggles;
    private bool _syncingExportForm;
    private bool _exportFormEdited;

    private string _mediaAudioCache = "";
    private string _mediaMetaCache = "";
    private string _mediaLyricCache = "";

    private BeatMap? _beatMap;
    private CancellationTokenSource? _musicCts;

    private readonly UndoLog _undo = new();

    private bool _syncingLyricStyle;

    private bool _broadcastOn;
    private readonly string _spoutName;

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
        _pipeEffects = _effects.Clone();
        _audioPath = _project.AudioPath;
        _lrcText = _project.LrcText;
        if (string.IsNullOrWhiteSpace(_scene.AmbientTint)) _scene.AmbientTint = "#8899ff";

        _evaluator = DirectorEvaluator.FromSaves(_project.Curves, _project.CameraTrack);

        Loaded += OnLoaded;
        Closing += OnClosing;
        Stage.Ready += OnStageReady;
        Stage.Prepare += OnStagePrepare;
        Stage.Frame += OnStageFrame;
        Stage.Shutdown += OnStageShutdown;

        TheTimeline.SeekRequested += Timeline_SeekRequested;
        TheTimeline.EditCommitted += Timeline_EditCommitted;
        TheTimeline.Fps = _scene.Fps;

        BuildLyricStylePicker();
        UpdateBroadcastButton();
        SetStep(0);

        BuildLayerList();
        BuildInspector();
        RefreshTimeline();

        _uiTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _playheadTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _playheadTimer.Tick += (_, _) => TickTimeline();
        _playheadTimer.Start();
        _uiTimer.Tick += (_, _) =>
        {
            RefreshStatus();
            if (++_saveTick >= 10)
            {
                _saveTick = 0;
                _saveError = SaveProjectNow();
            }
        };
        _uiTimer.Start();

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

        PushUndo();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _audio.Play();
        if (_ready) TblStageHint.Visibility = Visibility.Collapsed;
    }

    private static string ProjectPath => Path.Combine(AppDirs.ConfigDir, "pv_project.jzpv");
    private static string LegacyScenePath => Path.Combine(AppDirs.ConfigDir, "pv_scene.json");
    private static string LegacyEffectsPath => Path.Combine(AppDirs.ConfigDir, "pv_effects.json");

    private string? SaveProjectNow()
    {
        _project.AudioPath = _audioPath;
        _project.LrcText = _lrcText;
        SyncCurvesToProject();
        return ProjectStore.Save(_project, ProjectPath);
    }

    private void SyncCurvesToProject()
    {
        _project.Curves = _evaluator.ToSaves();
    }

    private void PublishDirector() => _pipe?.SetDirector(_evaluator.Snapshot());

    private void EffectsChanged()
    {
        var snapshot = _effects.Clone();
        _pipeEffects = snapshot;
        _pipe?.PublishEffects(snapshot);
    }

    private void PublishAudioBands() => _pipe?.SetAudioAnalysis(_audioBands);

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
                e.Cancel = true;
                return;
            }
        }

        _uiTimer?.Stop();
        _playheadTimer?.Stop();

        var pipe = _pipe;
        pipe?.StopPumping();
        var maxRecv = pipe?.MaxReceiveMs ?? 0;
        _audio.Dispose();

        if (!Stage.StopRender())
        {
            Log.Error("[PV 工作台] 停机握手未完成，管线留给渲染线程收尾");
            return;
        }

        if (pipe is not null)
            Log.Info($"[PV 工作台] 已按握手关闭（{_scene.Layers.Count} 层，Spout 取帧最长 {maxRecv:F1} ms）");
    }

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
            BuildPipeline(host);
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

    private void BuildPipeline(PresentHost host, bool keepTransport = false)
    {
        _pipe?.Dispose();
        _pipe = null;

        _pipe = new PvPipeline(host.Gpu!, _scene, _pipeEffects);
        _pipe.Renderer.Lyric?.SetStyle(ProjectLyricStyle());
        Dispatcher.BeginInvoke(new Action(() =>
        {
            PublishDirector();
            PublishAudioBands();
        }));

        UpdateBroadcastButton();
        if (_broadcastOn) _pipe.RequestBroadcast(true, _spoutName);

        _pipe.LayersChanged += () => Dispatcher.BeginInvoke(() =>
        {
            BuildLayerList();
            PushUndo();
            _saveError = SaveProjectNow();
        });

        if (!keepTransport && !string.IsNullOrWhiteSpace(_audioPath))
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
            else
            {
                var path = _audioPath;
                Dispatcher.BeginInvoke(() =>
                {
                    BuildInspector();
                    StartAudioLoad(path);
                    StartMusicAnalysis(path);
                });
            }
        }

        if (!keepTransport && !string.IsNullOrWhiteSpace(_lrcText))
        {
            try
            {
                var (lines, tailUncalibrated) = ParseLyrics(_lrcText);
                _pipe.SetLyrics(lines);
                Dispatcher.BeginInvoke(() =>
                {
                    _lyrics = lines;
                    PublishLyricClips(LrcParser.ToClips(lines));
                    _tailUncalibrated = tailUncalibrated;
                    BuildInspector();
                    RefreshTimeline();
                });
                Log.Info($"[PV 工作台] 已恢复工程歌词：{lines.Count} 行，末句待校准={tailUncalibrated}");
            }
            catch (Exception ex)
            {
                Log.Warn("[PV 工作台] 工程歌词解析失败：" + ex.Message);
            }
        }
        else if (keepTransport)
        {
            _pipe.Renderer.Lyric?.SetClips(_lyricClips);
        }

        var msg = $"[PV 舞台] 渲染就绪：{host.Gpu!.AdapterName}";
        Log.Info(msg);
        Console.WriteLine(msg);

        Dispatcher.BeginInvoke(() => TblStageHint.Visibility = Visibility.Collapsed);
    }

    private void RebuildPipeline(PresentHost host)
    {
        _rebuildPipe = false;
        var keepTransport = _rebuildPipeKeepTransport;
        _rebuildPipeKeepTransport = false;
        try
        {
            BuildPipeline(host, keepTransport);
        }
        catch (Exception ex)
        {
            Log.Error("[PV 舞台] 换工程时重建管线失败：" + ex.Message, ex);
            Dispatcher.BeginInvoke(() => TblStatus.Text = "换工程失败：" + ex.Message);
        }
    }

    private void OnStagePrepare(PresentHost host)
    {
        if (_rebuildPipe) RebuildPipeline(host);

        var pipe = _pipe;
        if (pipe is null || !pipe.Ready || host.Target is null) return;
        try
        {
            pipe.Prepare(_audio.Time);
        }
        catch (Exception ex)
        {
            Log.Error("[PV 舞台] 帧准备失败：" + ex.Message, ex);
        }
    }

    private void OnStageFrame(PresentHost host, double dt)
    {
        var pipe = _pipe;
        if (pipe is null || !pipe.Ready || host.Target is null) return;

        try
        {
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
        catch (Exception ex)
        {
            Log.Error("[PV 舞台] 帧合成失败：" + ex.Message, ex);
        }
    }

    private const string TextPrimaryKey = "TextFillColorPrimaryBrush";
    private const string TextSecondaryKey = "TextFillColorSecondaryBrush";
    private const string TextTertiaryKey = "TextFillColorTertiaryBrush";
    private const string TextAccentKey = "AccentTextFillColorPrimaryBrush";
    private const string CardFillKey = "CardBackgroundFillColorDefaultBrush";
    private const string CardStrokeKey = "CardStrokeColorDefaultBrush";
    private const string SelectFillKey = "ControlFillColorSecondaryBrush";
    private const string SelectStrokeKey = "AccentFillColorSecondaryBrush";
    private const string CautionKey = "SystemFillColorCautionBrush";

    private static readonly System.Windows.Media.FontFamily MonoFontFamily =
        new("Consolas, Cascadia Mono, Microsoft YaHei UI");

    private static T Themed<T>(T element, DependencyProperty property, string themeKey)
        where T : FrameworkElement
    {
        element.SetResourceReference(property, themeKey);
        return element;
    }

    private static TextBlock ThemeText(string text, string themeKey, double size, bool wrap = false) =>
        Themed(new TextBlock
        {
            Text = text,
            FontSize = size,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalAlignment = VerticalAlignment.Center,
        }, TextBlock.ForegroundProperty, themeKey);

    private static TextBlock ThemeValue(string text) =>
        Themed(new TextBlock
        {
            Text = text,
            FontFamily = MonoFontFamily,
            FontSize = 11,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
        }, TextBlock.ForegroundProperty, TextPrimaryKey);

    private static void RestoreCardStroke(Wpf.Ui.Controls.Card card) =>
        card.SetResourceReference(Control.BorderBrushProperty, CardStrokeKey);

    private static void MarkCardStroke(Wpf.Ui.Controls.Card card, string themeKey) =>
        card.SetResourceReference(Control.BorderBrushProperty, themeKey);

    private void BuildLayerList()
    {
        var layers = _scene.Layers;

        var ids = layers.Select(l => l.Id).ToArray();
        if (!_uiLayerIds.SequenceEqual(ids))
        {
            _uiLayerIds = ids;
            _uiLayers.Clear();
            for (var i = layers.Count - 1; i >= 0; i--) _uiLayers.Add(layers[i]);
        }

        if (_selectedLayer is not null && !_uiLayers.Contains(_selectedLayer)) _selectedLayer = null;

        LayersHost.Children.Clear();
        foreach (var layer in _uiLayers)
        {
            var card = new Wpf.Ui.Controls.Card
            {
                Padding = new Thickness(10, 9, 10, 9),
                Margin = new Thickness(0, 0, 0, 6),
                AllowDrop = true,
                Cursor = Cursors.SizeAll,
            };
            card.Tag = layer;
            card.PreviewMouseLeftButtonDown += LayerCard_MouseDown;
            card.PreviewMouseMove += LayerCard_MouseMove;
            card.DragOver += LayerCard_DragOver;
            card.Drop += LayerCard_Drop;
            card.DragLeave += (_, _) => RestoreCardStroke(card);

            var stack = new StackPanel();
            card.Content = stack;

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = ThemeText(
                string.IsNullOrWhiteSpace(layer.SpoutSenderName) ? layer.Name : layer.SpoutSenderName!,
                TextPrimaryKey, 13);
            Grid.SetColumn(name, 0);
            head.Children.Add(name);

            var headRight = new StackPanel { Orientation = Orientation.Horizontal };
            var toggle = new Wpf.Ui.Controls.ToggleSwitch
            {
                IsChecked = layer.Enabled,
                ToolTip = "显示 / 隐藏本图层",
            };
            toggle.Checked += (_, _) => layer.Enabled = true;
            toggle.Unchecked += (_, _) => layer.Enabled = false;
            headRight.Children.Add(toggle);

            var del = new Wpf.Ui.Controls.Button
            {
                Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(6, 0, 0, 0),
                Content = "✕",
                ToolTip = "删除本图层",
            };
            var idRef = layer.Id;
            del.Click += (_, _) =>
            {
                _evaluator.RemoveCurvesForTarget(idRef);
                PublishDirector();
                _pipe?.RequestRemove(idRef);
            };
            headRight.Children.Add(del);

            Grid.SetColumn(headRight, 1);
            head.Children.Add(headRight);
            stack.Children.Add(head);

            var meta = ThemeText(LayerKindLabel(layer.Kind), TextTertiaryKey, 11);
            meta.Margin = new Thickness(0, 2, 0, 0);
            stack.Children.Add(meta);

            LayersHost.Children.Add(card);
        }

        TblLayerCount.Text = $"{_scene.Layers.Count} 层";
        HighlightLayerCards();
        BuildInspector();
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
        var layer = ((Wpf.Ui.Controls.Card)sender).Tag as PvLayerConfig;
        if (!ReferenceEquals(layer, _selectedLayer)) SelectLayer(layer);
        _dragStart = e.GetPosition(this);
        _dragLayer = layer;
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
        MarkCardStroke((Wpf.Ui.Controls.Card)sender, SelectStrokeKey);
    }

    private void LayerCard_Drop(object sender, DragEventArgs e)
    {
        var targetCard = (Wpf.Ui.Controls.Card)sender;
        RestoreCardStroke(targetCard);
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

    private static string EffectName(EffectDef def) => LocName(def.NameKey, def.Id);

    private static string EffectName(EffectNode node) =>
        EffectCatalog.Get(node.DefId) is { } def ? EffectName(def) : node.DefId;

    private static string ParamLabel(EffectParam p) => LocName(p.LabelKey, p.Key);

    private static string LocName(string key, string fallback)
    {
        if (string.IsNullOrWhiteSpace(key)) return Humanize(fallback);

        var text = L.T(key);
        if (!string.Equals(text, key, StringComparison.Ordinal)) return text;
        return Humanize(key[(key.LastIndexOf('.') + 1)..]);
    }

    private static string Humanize(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1])) sb.Append(' ');
            sb.Append(i == 0 ? char.ToUpperInvariant(c) : c);
        }
        return sb.ToString();
    }

    private LyricStyle ProjectLyricStyle() => _scene.LyricStyle ?? LyricStyle.FromName(_scene.LyricStyleName);

    private void SyncLyricStylePicker()
    {
        var picker = _cmbLyricStyle;
        if (picker is null) return;

        _syncingLyricStyle = true;
        try
        {
            picker.SelectedIndex = Math.Max(0, Array.FindIndex(LyricStyleNames,
                n => string.Equals(n, _scene.LyricStyleName, StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            _syncingLyricStyle = false;
        }
    }

    private void LyricStyle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var picker = _cmbLyricStyle;
        if (_syncingLyricStyle || picker is null || picker.SelectedIndex < 0) return;

        _scene.LyricStyleName = LyricStyleNames[picker.SelectedIndex];
        _scene.LyricStyle = null;
        _pipe?.Renderer.Lyric?.SetStyle(ProjectLyricStyle());

        PushUndo();
        _saveError = SaveProjectNow();
        TblStatus.Text = $"歌词样式：{LyricStyleLabels[picker.SelectedIndex]}（{_scene.LyricStyleName}）";
    }

    private void PushUndo()
    {
        _undo.Push(ProjectSnapshot());
        UpdateUndoButtons();
    }

    private string ProjectSnapshot()
    {
        _project.AudioPath = _audioPath;
        _project.LrcText = _lrcText;
        SyncCurvesToProject();
        return JsonConvert.SerializeObject(_project, Formatting.None);
    }

    private DirectorProject? ProjectFromSnapshot(string text, string what)
    {
        try
        {
            var project = JsonConvert.DeserializeObject<DirectorProject>(text);
            if (project is not null) return project;

            TblStatus.Text = $"{what}失败：快照解析为空";
            return null;
        }
        catch (Exception ex)
        {
            TblStatus.Text = $"{what}失败：快照读不出来（{ex.Message}）";
            return null;
        }
    }

    private void UndoRedo(bool undo)
    {
        var text = undo ? _undo.Undo() : _undo.Redo();
        UpdateUndoButtons();
        if (text is null)
        {
            TblStatus.Text = undo ? "没有可撤销的改动" : "没有可重做的改动";
            return;
        }

        if (ProjectFromSnapshot(text, undo ? "撤销" : "重做") is { } project)
            AdoptProject(project, undo ? "已撤销" : "已重做", resetUndo: false);
    }

    private void BtnNewProject_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "新建工程会把当前工作台重置为默认工程（场景 / 效果 / 曲线全部回到默认）。\n\n"
                + $"当前工程会先备份为 {Path.GetFileName(ProjectPath)}.bak，然后被默认工程覆盖。继续？",
                "PV 工作台", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        AdoptProject(new DirectorProject(), "已新建工程");
    }

    private void BtnOpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开工程",
            Filter = "PV 工程 (*.jzpv)|*.jzpv|所有文件 (*.*)|*.*",
            InitialDirectory = AppDirs.ConfigDir,
        };
        if (dlg.ShowDialog(this) != true) return;

        var loaded = ProjectStore.Load(dlg.FileName);
        if (loaded.Project is null)
        {
            var fault = loaded.Fault ?? "未知原因";
            MessageBox.Show(this, "打开工程失败：\n" + fault, "PV 工作台",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Log.Warn("[PV 工作台] 打开工程失败：" + fault);
            return;
        }

        if (MessageBox.Show(this,
                $"打开工程：{loaded.Project.Name}\n{dlg.FileName}\n\n"
                + $"当前工作台会被它整份替换（当前工程先备份为 {Path.GetFileName(ProjectPath)}.bak）。继续？",
                "PV 工作台", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        AdoptProject(loaded.Project, $"已打开工程：{loaded.Project.Name}");
    }

    private void AdoptProject(DirectorProject project, string note, bool resetUndo = true)
    {
        if (!BackupProjectFile()) return;

        var prevAudio = _audioPath;
        _project = project;
        _scene = project.Scene;
        _effects = project.Effects;
        _pipeEffects = _effects.Clone();
        _audioPath = project.AudioPath;
        _lrcText = project.LrcText;

        _evaluator = DirectorEvaluator.FromSaves(project.Curves, project.CameraTrack);

        bool sameAudio = !resetUndo && !string.IsNullOrWhiteSpace(_audioPath)
                         && string.Equals(prevAudio, _audioPath, StringComparison.OrdinalIgnoreCase)
                         && _audio.Duration > 0;

        if (!sameAudio)
        {
            _audio.Dispose();
            _audio = new AudioClockDriver();

            _peaks = null;
            _audioLoadCts?.Cancel();
            _musicCts?.Cancel();
            _audioBands = null;
            _beatMap = null;
            PublishAudioBands();

            if (!string.IsNullOrWhiteSpace(_audioPath) && File.Exists(_audioPath))
            {
                if (_audio.LoadAudio(_audioPath))
                {
                    StartAudioLoad(_audioPath);
                    StartMusicAnalysis(_audioPath);
                }
            }
        }

        _lyrics = null;
        _lyricClips = new List<LyricClip>();
        _tailUncalibrated = false;
        if (!string.IsNullOrWhiteSpace(_lrcText))
        {
            try
            {
                var (lines, tailUncalibrated) = ParseLyrics(_lrcText);
                _lyrics = lines;
                _tailUncalibrated = tailUncalibrated;
                PublishLyricClips(LrcParser.ToClips(lines));
            }
            catch { }
        }

        _uiLayerIds = Array.Empty<string>();
        _selectedLayer = null;
        _saveError = null;

        BuildLayerList();
        TheTimeline.Fps = _scene.Fps;
        RefreshTimeline();
        BuildInspector();
        SyncLyricStylePicker();

        if (_pipe is not null)
        {
            _rebuildPipeKeepTransport = false;
            _rebuildPipe = true;
        }

        if (resetUndo)
        {
            _undo.Clear();
            PushUndo();
        }

        _saveError = SaveProjectNow();
        TblStatus.Text = _saveError is null ? note : $"{note}（工程文件写入失败：{_saveError}）";
        _exportFormEdited = false;
        Log.Info($"[PV 工作台] {note}（存盘落到 {ProjectPath}）");
    }

    private bool BackupProjectFile()
    {
        if (!File.Exists(ProjectPath)) return true;

        try
        {
            File.Copy(ProjectPath, ProjectPath + ".bak", overwrite: true);
            Log.Info($"[PV 工作台] 已备份当前工程：{ProjectPath}.bak");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("[PV 工作台] 备份当前工程失败：" + ex.Message);
            return MessageBox.Show(this,
                $"备份当前工程失败：{ex.Message}\n\n没有备份就继续的话，当前工程文件会被直接覆盖且找不回来。继续？",
                "PV 工作台", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }
    }

    private (List<KrcLine> Lines, bool TailUncalibrated) ParseLyrics(string text)
    {
        var duration = _audio.Duration;
        var result = LrcParser.ParseClips(text, duration > 0 ? duration : null);

        var lines = new List<KrcLine>(result.Clips.Count);
        foreach (var clip in result.Clips)
            lines.Add(new KrcLine(clip.StartMs, clip.EndMs - clip.StartMs, clip.Text, clip.Words));

        return (lines, result.Clips.Count > 0 && result.Clips[^1].OutOfRange);
    }

    private void LoadLyrics(string text)
    {
        var (lines, tailUncalibrated) = ParseLyrics(text);
        _lrcText = text;
        _lyrics = lines;
        _tailUncalibrated = tailUncalibrated;
        PublishLyricClips(LrcParser.ToClips(lines));
        BuildInspector();
        RefreshTimeline();
    }

    private void LoadLyricsFile(string path)
    {
        try
        {
            LoadLyrics(ReadLyricFileText(path));
            TblStatus.Text = $"已装载歌词：{_lyrics?.Count ?? 0} 行"
                             + (_tailUncalibrated ? "（末句待校准：未载入音频）" : "");
            Log.Info($"[工作台] 已装载歌词：{_lyrics?.Count ?? 0} 行 ({path})，末句待校准={_tailUncalibrated}");
        }
        catch (Exception ex)
        {
            TblStatus.Text = $"打开歌词失败：{ex.Message}";
            MessageBox.Show(this, "打开歌词失败：" + ex.Message, "PV 舞台", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnOpenLyrics_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开歌词文件",
            Filter = "歌词 (*.lrc;*.krc)|*.lrc;*.krc",
        };
        if (dlg.ShowDialog(this) != true) return;
        LoadLyricsFile(dlg.FileName);
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
        LoadAudioFile(dlg.FileName);
    }

    private void LoadAudioFile(string path)
    {
        TblStatus.Text = "正在加载音频…";

        if (_audio.LoadAudio(path))
        {
            _audioPath = path;
            TblStatus.Text = "音频已加载";
            BuildInspector();
            StartAudioLoad(path);
            StartMusicAnalysis(path);
            _exportFormEdited = false;
            var lrcPath = Path.ChangeExtension(path, ".lrc");
            if (File.Exists(lrcPath))
            {
                try
                {
                    LoadLyrics(File.ReadAllText(lrcPath));
                    Log.Info($"[工作台] 自动加载同名歌词：{_lyrics?.Count ?? 0} 行，末句待校准={_tailUncalibrated}");
                }
                catch { }
            }
            else if (_tailUncalibrated && !string.IsNullOrWhiteSpace(_lrcText))
            {
                LoadLyrics(_lrcText);
                TblStatus.Text = $"音频已加载 · 末句已对齐音频（{_lyrics?.Count ?? 0} 行歌词）";
            }
        }
        else
        {
            TblStatus.Text = $"音频加载失败：{Path.GetFileName(path)}（格式不支持或文件损坏；当前支持 wav/mp3/m4a/aac/wma）";
        }
    }

    private void StartMusicAnalysis(string path)
    {
        _musicCts?.Cancel();
        var cts = new CancellationTokenSource();
        _musicCts = cts;
        _beatMap = null;

        Task.Run(() =>
        {
            BeatMap? map = null;
            try
            {
                map = SongAnalyzer.AnalyzeFile(path, cts.Token);
            }
            catch (Exception ex)
            {
                if (!cts.IsCancellationRequested) Log.Warn("[工作台] 音乐分析失败：" + ex.Message);
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(_musicCts, cts)) return;
                if (map is null) return;

                _beatMap = map;
                TblAutoReport.Text =
                    $"BPM {map.Bpm:F1} · 段落 {map.Sections.Count} 段 · 下拍 {map.Downbeats.Count} 个\n"
                    + $"段落序列 {SectionSequence(map)}";
            });
        });
    }

    private static string SectionSequence(BeatMap map) =>
        string.Join("→", map.Sections.Where(s => s is not null).Select(s => SectionKindLabel(s.Kind)));

    private static string SectionKindLabel(string? kind) => (kind ?? "").Trim().ToLowerInvariant() switch
    {
        "intro" => "前奏",
        "verse" => "主歌",
        "chorus" => "副歌",
        "bridge" => "桥段",
        "outro" => "尾奏",
        "break" => "间奏",
        var other => other,
    };

    private static int ChorusCount(BeatMap map) =>
        map.Sections.Count(s => s is not null && s.Kind.Equals("chorus", StringComparison.OrdinalIgnoreCase));

    private void TglBroadcast_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingToggles) return;

        _broadcastOn = TglBroadcast.IsChecked == true;
        _pipe?.RequestBroadcast(_broadcastOn, _spoutName);

        var s = App.SettingsOf();
        if (s is not null)
        {
            s.SpoutEnabled = _broadcastOn;
            s.Save();
        }

        UpdateBroadcastButton();
    }

    private void UpdateBroadcastButton()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateBroadcastButton);
            return;
        }

        _syncingToggles = true;
        try { TglBroadcast.IsChecked = _broadcastOn; }
        finally { _syncingToggles = false; }

        TblBroadcastState.Text = _broadcastOn ? $"广播 开（发送端 {_spoutName}）" : "广播 关";
    }

    private void SyncExportForm()
    {
        SyncExportAudioToggle();
        if (_exportFormEdited) return;

        var audioPath = _audioPath;
        var dir = string.IsNullOrWhiteSpace(audioPath) ? null : Path.GetDirectoryName(audioPath);
        var defaultDir = string.IsNullOrEmpty(dir) ? AppDirs.ConfigDir : dir;
        var name = (string.IsNullOrWhiteSpace(audioPath) ? "pv_export" : Path.GetFileNameWithoutExtension(audioPath)) + ".mp4";
        var path = Path.Combine(defaultDir, name);
        var seconds = _audio.Duration > 0 ? _audio.Duration.ToString("F2", CultureInfo.InvariantCulture) : "";

        _syncingExportForm = true;
        try
        {
            if (!string.Equals(TxtExportPath.Text, path, StringComparison.Ordinal)) TxtExportPath.Text = path;
            if (!string.Equals(TxtExportSeconds.Text, seconds, StringComparison.Ordinal)) TxtExportSeconds.Text = seconds;
        }
        finally
        {
            _syncingExportForm = false;
        }
    }

    private void ExportField_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingExportForm) return;
        _exportFormEdited = true;
    }

    private void BtnExportBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出 MP4",
            Filter = "MP4 视频 (*.mp4)|*.mp4",
            DefaultExt = ".mp4",
            AddExtension = true,
            FileName = Path.GetFileName(TxtExportPath.Text),
            InitialDirectory = Path.GetDirectoryName(TxtExportPath.Text) ?? AppDirs.ConfigDir,
        };
        if (dlg.ShowDialog(this) == true) TxtExportPath.Text = dlg.FileName;
    }

    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting) return;

        if (string.IsNullOrWhiteSpace(TxtExportPath.Text)
            || !int.TryParse(TxtExportFps.Text.Trim(), out var fps)
            || !double.TryParse(TxtExportSeconds.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
        {
            TblExportState.Text = "导出未开始：输出路径不能为空，帧率与时长都必须是数字";
            return;
        }

        StartExport(TxtExportPath.Text.Trim(), fps, sec, ChkBurnSubtitles.IsChecked == true,
            ChkIncludeAudio.IsChecked == true);
    }

    private async void StartExport(string outPath, int fps, double seconds, bool burnSubtitles, bool includeAudio)
    {
        _exporting = true;
        BtnExport.IsEnabled = false;
        PrgExport.Progress = 0;
        PrgExport.Visibility = Visibility.Visible;
        _exportPercent = 0;
        TblExportState.Text = ExportText();
        TblStatus.Text = ExportText();

        var copy = ProjectFromSnapshot(ProjectSnapshot(), "导出");
        var inputs = copy is null ? null : ExportInputs(copy, burnSubtitles, includeAudio);
        var progress = new Progress<double>(p =>
        {
            _exportPercent = p;
            PrgExport.Progress = p * 100;
            if (_exporting)
            {
                TblExportState.Text = ExportText();
                TblStatus.Text = ExportText();
            }
        });

        var fault = copy is null
            ? "导出失败：工程快照读不出来"
            : await Task.Run(() => PvExporter.Export(copy.Scene, copy.Effects, seconds, fps, outPath, progress, inputs));

        _exporting = false;
        BtnExport.IsEnabled = true;
        PrgExport.Visibility = Visibility.Collapsed;
        TblExportState.Text = fault ?? $"导出完成：{outPath}";
        ShowStatus(fault ?? $"导出完成：{outPath}");
    }

    private PvExportInputs ExportInputs(DirectorProject copy, bool burnSubtitles, bool includeAudio)
    {
        var curves = DirectorEvaluator.FromSaves(copy.Curves, copy.CameraTrack);

        return new PvExportInputs(
            Curves: curves,
            Bands: _audioBands,
            Clips: _lyricClips,
            Subtitles: burnSubtitles ? App.SettingsOf()?.Subtitle : null,
            AudioPath: includeAudio ? _audioPath : null,
            AudioOffsetSec: ExportAudioOffsetSec);
    }

    private string ExportText() => $"导出中… {_exportPercent * 100:F0}%";

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBox)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        if (e.Key == Key.Space)
        {
            e.Handled = true;
            TogglePlay();
            base.OnPreviewKeyDown(e);
            return;
        }

        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        switch (e.Key)
        {
            case Key.S:
                e.Handled = true;
                SaveNow();
                break;
            case Key.Z:
                e.Handled = true;
                UndoRedo(true);
                break;
            case Key.Y:
                e.Handled = true;
                UndoRedo(false);
                break;
        }

        base.OnPreviewKeyDown(e);
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

        var menu = new ContextMenu { PlacementTarget = BtnAddLayer };

        var imageItem = new MenuItem { Header = "图片文件…", ToolTip = "png / jpg / webp / bmp" };
        imageItem.Click += (_, _) => AddImageLayer();
        menu.Items.Add(imageItem);

        var videoItem = new MenuItem { Header = "视频文件…", ToolTip = "mp4 / mkv / avi / wmv，循环播放" };
        videoItem.Click += (_, _) => AddVideoLayer();
        menu.Items.Add(videoItem);

        if (known.Count > 0)
        {
            menu.Items.Add(new Separator());
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
        }
        else
        {
            menu.Items.Add(new MenuItem
            {
                Header = senders.Count == 0 ? "（没有活动的 Spout 发送端）" : "（发送端都已在图层里）",
                IsEnabled = false,
            });
        }

        menu.IsOpen = true;
    }

    private void AddImageLayer()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "添加图片图层",
            Filter = "图片 (*.png;*.jpg;*.jpeg;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.bmp|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        var pipe = _pipe;
        if (pipe is null)
        {
            TblStatus.Text = "舞台未就绪：图片图层未加入";
            return;
        }

        pipe.RequestAddImage(dlg.FileName);
        TblStatus.Text = $"添加图片图层 {Path.GetFileName(dlg.FileName)}…";
        Log.Info($"[工作台] 添加图片图层：{dlg.FileName}");
    }

    private void AddVideoLayer()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "添加视频图层",
            Filter = "视频 (*.mp4;*.mkv;*.avi;*.wmv)|*.mp4;*.mkv;*.avi;*.wmv|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        var pipe = _pipe;
        if (pipe is null)
        {
            TblStatus.Text = "舞台未就绪：视频图层未加入";
            return;
        }

        pipe.RequestAddVideo(dlg.FileName);
        TblStatus.Text = $"添加视频图层 {Path.GetFileName(dlg.FileName)}…";
        Log.Info($"[工作台] 添加视频图层：{dlg.FileName}");
    }

    private void BtnPlay_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_audio.IsPlaying) _audio.Pause();
        else _audio.Play();
        UpdatePlayIcon();
    }

    private void UpdatePlayIcon() =>
        PlayIcon.Symbol = _audio.IsPlaying
            ? Wpf.Ui.Controls.SymbolRegular.Pause24
            : Wpf.Ui.Controls.SymbolRegular.Play24;

    private void SldTime_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_draggingTime) return;
        _audio.Seek(e.NewValue);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _draggingTime = false;
        _dragLayer = null;
        base.OnPreviewMouseLeftButtonUp(e);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (SldTime.IsMouseOver)
        {
            _draggingTime = true;
            if (SldTime.Template?.FindName("PART_Track", SldTime) is System.Windows.Controls.Primitives.Track track
                && track.ActualWidth > 0 && !IsThumbSource(e.OriginalSource as DependencyObject))
            {
                double ratio = Math.Clamp(e.GetPosition(track).X / track.ActualWidth, 0, 1);
                SldTime.Value = SldTime.Minimum + ratio * (SldTime.Maximum - SldTime.Minimum);
                e.Handled = true;
            }
        }
        base.OnPreviewMouseLeftButtonDown(e);
    }

    private static bool IsThumbSource(DependencyObject? src)
    {
        for (var d = src; d is not null; d = (d as FrameworkElement)?.TemplatedParent as DependencyObject)
            if (d is System.Windows.Controls.Primitives.Thumb) return true;
        return false;
    }

    private void RefreshStatus()
    {
        var t = _audio.Time;
        var d = _audio.Duration;
        TblTime.Text = $"{Fmt(t)} / {Fmt(d)}";
        if (!_draggingTime)
        {
            SldTime.Maximum = Math.Max(0.01, d);
            SldTime.LargeChange = Math.Max(0.5, d / 20);
            SldTime.SmallChange = Math.Max(0.1, d / 200);
            SldTime.Value = Math.Clamp(t, 0, SldTime.Maximum);
        }

        RefreshParamRows();
        TickTimeline();

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
            var status = $"效果 {pipe.Renderer.EffectCount} 段 · 图层 {_scene.Layers.Count} · {srcTxt}";

            var b = pipe.BroadcastInfo;
            if (b.Enabled)
            {
                string seg;
                if (b.Sent > 0) seg = b.Failed > 0 ? $"广播 {b.Sent} 帧 · 丢 {b.Failed}" : $"广播 {b.Sent} 帧";
                else if (b.LastError is { } berr) seg = $"广播失败：{berr}";
                else seg = "广播就绪";
                status += $" · {seg}";
            }

            TblStatus.Text = status;
        }
        else
        {
            if (Stage.Error is not null)
                TblStatus.Text = $"预览已停止：{Stage.Error}";
            else
                TblStatus.Text = "渲染就绪中…";
        }

        if (_audio.Error is { } fault) TblStatus.Text = $"音频已暂停：{fault}";
        if (_saveError is { } saveFault) TblStatus.Text = $"工程未保存：{saveFault}";

        if (_transientStatus is not null)
        {
            if (--_transientStatusTicks <= 0)
                _transientStatus = null;
            else
                TblStatus.Text = _transientStatus;
        }

        if (_exporting) TblStatus.Text = ExportText();

        TblProjectName.Text = string.IsNullOrWhiteSpace(_project.Name) ? "默认工程" : _project.Name;
        if (_step == 0) RefreshMediaPanel();
        SyncExportForm();
        UpdateAutoButton();
        UpdatePlayIcon();
    }

    private static string Fmt(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 100}";
    }

    private void SaveNow()
    {
        _saveError = SaveProjectNow();
        TblStatus.Text = _saveError is null ? "工程已保存" : $"保存失败：{_saveError}";
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e) => SaveNow();

    private void BtnUndo_Click(object sender, RoutedEventArgs e) => UndoRedo(true);

    private void BtnRedo_Click(object sender, RoutedEventArgs e) => UndoRedo(false);

    private void UpdateUndoButtons()
    {
        BtnUndo.IsEnabled = _undo.CanUndo;
        BtnRedo.IsEnabled = _undo.CanRedo;
    }

    private void SldAutoIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TblAutoIntensity is not null)
            TblAutoIntensity.Text = e.NewValue.ToString("F2", CultureInfo.InvariantCulture);
    }

    private void UpdateAutoButton()
    {
        var ready = !string.IsNullOrWhiteSpace(_audioPath) || _audioBands is not null;
        BtnGenerateAuto.IsEnabled = !_autoGenerating && ready;
        BtnRegenerateAuto.IsEnabled = !_autoGenerating && ready;
        TblAutoHint.Text = ready
            ? "生成会整份重写自动生成的曲线与效果实例，一次生成压一条撤销。"
            : "先导入音乐，生成才会解锁。";
    }

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        var index = ReferenceEquals(sender, NavDirector) ? 1
            : ReferenceEquals(sender, NavOutput) ? 2
            : 0;
        SetStep(index);
    }

    private void SetStep(int index)
    {
        _step = index;

        PanelMedia.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelDirector.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanelOutput.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;

        NavMedia.Appearance = AppearanceOf(index == 0);
        NavDirector.Appearance = AppearanceOf(index == 1);
        NavOutput.Appearance = AppearanceOf(index == 2);

        if (index == 0) RefreshMediaPanel();

        static Wpf.Ui.Controls.ControlAppearance AppearanceOf(bool selected) =>
            selected ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Transparent;
    }

    private void BtnInspector_Click(object sender, RoutedEventArgs e)
    {
        _inspectorCollapsed = !_inspectorCollapsed;
        InspectorColumn.MinWidth = _inspectorCollapsed ? 0 : 280;
        InspectorColumn.Width = new GridLength(_inspectorCollapsed ? 0 : InspectorWidth);
        InspectorPane.Visibility = _inspectorCollapsed ? Visibility.Collapsed : Visibility.Visible;
    }

    private void BuildLyricStylePicker()
    {
        for (var i = 0; i < LyricStyleNames.Length; i++)
            CmbLyricStyle.Items.Add(LyricStyleLabels[i]);
        CmbLyricStyle.SelectionChanged += LyricStyle_SelectionChanged;
        _cmbLyricStyle = CmbLyricStyle;
        SyncLyricStylePicker();
    }

    private void RefreshMediaPanel()
    {
        var name = string.IsNullOrWhiteSpace(_audioPath) ? "未导入音乐" : Path.GetFileName(_audioPath);
        var meta = _audio.Duration > 0 ? $"时长 {Fmt(_audio.Duration)}" : "拖入音频文件即可开始";
        if (_beatMap is { Bpm: > 0 } map)
            meta += $" · BPM {map.Bpm:F0} · 段落 {map.Sections.Count}（副歌 ×{ChorusCount(map)}）";
        var lyric = _lyrics is null
            ? "未装载歌词"
            : $"{_lyrics.Count} 句" + (_tailUncalibrated ? "（末句待校准）" : "")
              + $" · 逐字：{(HasBuiltinWords() ? "文件自带" : "按字均分")}";

        if (name != _mediaAudioCache)
        {
            _mediaAudioCache = name;
            TblAudioName.Text = name;
        }

        if (meta != _mediaMetaCache)
        {
            _mediaMetaCache = meta;
            TblAudioMeta.Text = meta;
        }

        if (lyric != _mediaLyricCache)
        {
            _mediaLyricCache = lyric;
            TblLyricInfo.Text = lyric;
        }
    }

    private static readonly string[] AudioExtensions =
        [".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".wma"];

    private static bool IsDroppable(DragEventArgs e) => DroppedFiles(e).Length > 0;

    private static bool IsLyricExtension(string path)
    {
        var ext = Path.GetExtension(path);
        return string.Equals(ext, ".lrc", StringComparison.OrdinalIgnoreCase)
               || string.Equals(ext, ".krc", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] DroppedFiles(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return [];
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return [];

        return files.Where(f =>
        {
            var ext = Path.GetExtension(f);
            return AudioExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)
                   || IsLyricExtension(f);
        }).ToArray();
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(PvLayerConfig))) return;

        e.Effects = IsDroppable(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        var files = DroppedFiles(e);
        if (files.Length == 0) return;

        SetStep(0);
        foreach (var file in files)
        {
            if (IsLyricExtension(file))
                LoadLyricsFile(file);
            else
                LoadAudioFile(file);
        }

        e.Handled = true;
    }
}
