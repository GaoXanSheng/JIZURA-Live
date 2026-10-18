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
using System.Windows.Media;
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

    private Vortice.Direct2D1.ID2D1Bitmap1? _backBufferBitmap;
    private IntPtr _backBufferHandle;
    private int _backBufferWidth;
    private int _backBufferHeight;

    private volatile EffectGraph _pipeEffects = new();

    private string? _audioPath;
    private string? _lrcText;

    private IReadOnlyList<KrcLine>? _lyrics;

    private List<LyricClip> _lyricClips = new();

    private bool _tailUncalibrated;

    private int _saveTick;
    private string? _saveError;
    private System.Windows.Threading.DispatcherTimer? _uiTimer;
    private System.Windows.Threading.DispatcherTimer? _playheadTimer;
    private readonly List<PvLayerConfig> _uiLayers = new();
    private int _projectEpoch;
    private System.Windows.Point _dragStart;
    private PvLayerConfig? _dragLayer;
    private bool _draggingTime;

    private bool _exporting;
    private double _exportPercent;

    private DirectorEvaluator _evaluator = new();

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

    private const double InspectorWidth = 360;
    private bool _inspectorCollapsed;

    private int _step = -1;

    private bool _syncingToggles;
    private bool _syncingExportForm;
    private bool _syncingSettings;
    private bool _exportFormEdited;

    private string _mediaAudioCache = "";
    private string _mediaMetaCache = "";
    private string _mediaLyricCache = "";

    private BeatMap? _beatMap;
    private CancellationTokenSource? _musicCts;

    private readonly UndoLog _undo = new();

    private bool _broadcastOn;
    private bool _spoutTransparent;
    private readonly string _spoutName;

    private bool _ready => _pipe is { Ready: true };

    public PvStageWindow()
    {
        InitializeComponent();
        Left.Owner = this;

        var settings = App.SettingsOf();
        _broadcastOn = settings?.SpoutEnabled ?? false;
        _spoutTransparent = settings?.SpoutTransparent ?? false;
        _spoutName = settings?.SpoutName ?? "JIZURA-live-PV";
        Stage.VSync = settings?.VSync ?? true;
        InitVolume();

        var boot = ProjectStore.LoadOrImport(ProjectPath, LegacyScenePath, LegacyEffectsPath);
        var autosavePath = ProjectPath + ".autosave.json";
        if (File.Exists(autosavePath)
            && (!File.Exists(ProjectPath) || File.GetLastWriteTimeUtc(autosavePath) > File.GetLastWriteTimeUtc(ProjectPath)))
        {
            var recovered = ProjectStore.Load(autosavePath);
            if (recovered.Project is not null) boot = new ProjectBootstrap(recovered.Project, null, false);
        }

        _project = boot.Project;
        _scene = _project.Scene;
        _effects = _project.Effects;
        _pipeEffects = _effects.Clone();
        _audioPath = _project.AudioPath;
        _lrcText = _project.LrcText;
        if (string.IsNullOrWhiteSpace(_scene.AmbientTint)) _scene.AmbientTint = "#8899ff";

        _evaluator = DirectorEvaluator.FromSaves(_project.Curves, _project.CameraTrack, _project.LayerCameraTracks);

        Loaded += OnLoaded;
        Closing += OnClosing;
        Stage.Ready += OnStageReady;
        Stage.BeforeResize += _ => DisposeBackBufferBitmap();
        Stage.Prepare += OnStagePrepare;
        Stage.Frame += OnStageFrame;
        Stage.Shutdown += OnStageShutdown;

        TheTimeline.SeekRequested += Timeline_SeekRequested;
        TheTimeline.EditCommitted += Timeline_EditCommitted;
        TheTimeline.SegmentSelected += Timeline_SegmentSelected;
        TheTimeline.SegmentDeselected += Timeline_SegmentDeselected;
        TheTimeline.SegmentTimeEdited += Timeline_SegmentTimeEdited;
        TheTimeline.SegmentDeleteRequested += Timeline_SegmentDeleteRequested;
        TheTimeline.ValuePrompt = PromptTimelineValueAsync;
        TheTimeline.Fps = _scene.Fps;

        SyncLyricStylePicker();
        UpdateBroadcastButton();
        SyncStageToggles();
        SetStep(0);

        TblInspectorTitle.Cursor = Cursors.Hand;
        TblInspectorTitle.ToolTip = "点击回到工程与整体相机";
        TblInspectorTitle.MouseLeftButtonDown += (_, _) => SelectLayer(null);

        BuildLayerList();
        BuildInspector();
        RefreshTimeline();
        SyncSettingsPanel();

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
    }

    private string _projectPath = ProjectPath;
    private static string ProjectPath => Path.Combine(AppDirs.ConfigDir, "pv_project.jzpv");
    private static string LegacyScenePath => Path.Combine(AppDirs.ConfigDir, "pv_scene.json");
    private static string LegacyEffectsPath => Path.Combine(AppDirs.ConfigDir, "pv_effects.json");

    private string AutosavePath => _projectPath + ".autosave.json";

    private string? SaveProjectNow()
    {
        _project.AudioPath = _audioPath;
        _project.LrcText = _lrcText;
        _project.Scene = _scene;
        SyncCurvesToProject();
        return ProjectStore.SaveJson(_project, AutosavePath);
    }

    private string? SaveProjectToCurrent()
    {
        _project.AudioPath = _audioPath;
        _project.LrcText = _lrcText;
        _project.Scene = _scene;
        SyncCurvesToProject();

        byte[]? audio = null;
        if (!string.IsNullOrWhiteSpace(_audioPath) && File.Exists(_audioPath))
        {
            try
            {
                audio = File.ReadAllBytes(_audioPath);
            }
            catch (Exception ex)
            {
                Log.Warn("[PV 工作台] 读取音频失败，工程将不内嵌音频：" + ex.Message);
            }
        }

        var fault = ProjectStore.Save(_project, _projectPath, audio);
        if (fault is null) DeleteAutosave();
        return fault;
    }

    private void DeleteAutosave()
    {
        try
        {
            if (File.Exists(AutosavePath)) File.Delete(AutosavePath);
        }
        catch (Exception ex)
        {
            Log.Debug("[PV 工作台] 清理自动保存失败：" + ex.Message);
        }
    }

    private void SaveProjectAs()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "工程另存为",
            Filter = "PV 工程 (*.jzpv)|*.jzpv|所有文件 (*.*)|*.*",
            DefaultExt = ".jzpv",
            AddExtension = true,
            FileName = string.IsNullOrWhiteSpace(_project.Name) ? "PV工程" : _project.Name,
            InitialDirectory = AppDirs.ConfigDir,
        };
        if (dlg.ShowDialog(this) != true) return;

        var target = dlg.FileName;
        var dir = Path.GetDirectoryName(target);
        if (string.IsNullOrWhiteSpace(dir))
        {
            TblStatus.Text = "另存为失败：目标目录无效";
            return;
        }

        byte[]? audio = null;
        if (!string.IsNullOrWhiteSpace(_audioPath) && File.Exists(_audioPath))
        {
            try
            {
                audio = File.ReadAllBytes(_audioPath);
            }
            catch (Exception ex)
            {
                Log.Warn("[PV 工作台] 另存为读取音频失败：" + ex.Message);
                TblStatus.Text = "另存为失败：音频读取失败（" + ex.Message + "）";
                return;
            }
        }

        _project.AudioPath = _audioPath;
        _project.LrcText = _lrcText;
        _project.Scene = _scene;
        SyncCurvesToProject();

        DirectorProject copy;
        try
        {
            var json = JsonConvert.SerializeObject(_project, Formatting.Indented);
            copy = JsonConvert.DeserializeObject<DirectorProject>(json)
                   ?? throw new InvalidOperationException("工程快照解析为空");
        }
        catch (Exception ex)
        {
            TblStatus.Text = "另存为失败：" + ex.Message;
            return;
        }

        copy.Name = Path.GetFileNameWithoutExtension(target);
        copy.LrcText = _lrcText ?? "";
        copy.Scene = _scene;
        copy.Effects = _effects;

        var fault = ProjectStore.Save(copy, target, audio);
        if (fault is not null)
        {
            TblStatus.Text = "另存为失败：" + fault;
            return;
        }

        _projectPath = target;
        _project.Name = copy.Name;
        DeleteAutosave();

        string audioNote = audio is { Length: > 0 } ? "，音频已内嵌" : "，工程未关联音频";
        TblStatus.Text = $"已另存为：{target}{audioNote}";
        Log.Info($"[PV 工作台] 工程已另存为：{target}{audioNote}");
    }

    private void SyncCurvesToProject()
    {
        _project.Curves = _evaluator.ToSaves();
    }

    private void PublishDirector()
    {
        _pipe?.SetDirector(_evaluator.Snapshot());
        _pipe?.SetDesignSegments(_project.DesignSegments);
    }

    private void EffectsChanged()
    {
        var snapshot = _effects.Clone();
        _pipeEffects = snapshot;
        _pipe?.PublishEffects(snapshot);
    }

    private void PublishAudioBands() => _pipe?.SetAudioAnalysis(_audioBands);

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        var fault = SaveProjectToCurrent();
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
        DisposeBackBufferBitmap();
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
            Dispatcher.BeginInvoke(() => TblStatus.Text = "初始化失败");
        }
    }

    private void BuildPipeline(PresentHost host, bool keepTransport = false)
    {
        _pipe?.Dispose();
        _pipe = null;

        var pipe = new PvPipeline(host.Gpu!, _scene, _pipeEffects, null, LegacyOptions());
        _pipe = pipe;
        pipe.SetSpoutTransparent(_spoutTransparent);
        pipe.StageCameraEnabled = _project.LegacyStageCamera;
        pipe.StageTransitionsEnabled = _project.LegacyStageTransitions;
        int epoch = _projectEpoch;
        pipe.LegacyPlanReady += info =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (epoch == _projectEpoch && ReferenceEquals(_pipe, pipe)) ApplyLegacyPlan(info);
            }));
        if (pipe.LegacyPlan is { } plan)
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (epoch == _projectEpoch && ReferenceEquals(_pipe, pipe)) ApplyLegacyPlan(plan);
            }));
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
                Dispatcher.BeginInvoke(() => TblStatus.Text = $"工程音频加载失败：{_audioPath}：{_audio.Error}");
            }
            else
            {
                var path = _audioPath;
                Dispatcher.BeginInvoke(() =>
                {
                    BuildInspector();
                    StartAudioLoad(path);
                });
            }
        }

        if (!keepTransport && !string.IsNullOrWhiteSpace(_lrcText))
        {
            try
            {
                var (lines, tailUncalibrated) = ParseLyrics(_lrcText);
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
            PublishLyricClips(_lyricClips);
        }

        var msg = $"[PV 舞台] 渲染就绪：{host.Gpu!.AdapterName}";
        Log.Info(msg);
        Console.WriteLine(msg);

        Dispatcher.BeginInvoke(SyncSettingsPanel);
    }

    private void RebuildPipeline(PresentHost host)
    {
        _rebuildPipe = false;
        var keepTransport = _rebuildPipeKeepTransport;
        _rebuildPipeKeepTransport = false;
        DisposeBackBufferBitmap();
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
            if (_backBufferBitmap is null
                || _backBufferHandle != back.NativePointer
                || _backBufferWidth != w
                || _backBufferHeight != h)
            {
                DisposeBackBufferBitmap();
                using var surface = back.QueryInterface<Vortice.DXGI.IDXGISurface>();
                _backBufferBitmap = pipe.D2DContext.CreateBitmapFromDxgiSurface(surface,
                    new Vortice.Direct2D1.BitmapProperties1(
                        new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm,
                            Vortice.DCommon.AlphaMode.Premultiplied),
                        96f, 96f, Vortice.Direct2D1.BitmapOptions.Target | Vortice.Direct2D1.BitmapOptions.CannotDraw));
                _backBufferHandle = back.NativePointer;
                _backBufferWidth = w;
                _backBufferHeight = h;
            }

            pipe.Composite(_backBufferBitmap, w, h);
        }
        catch (Exception ex)
        {
            Log.Error("[PV 舞台] 帧合成失败：" + ex.Message, ex);
        }
    }

    private void DisposeBackBufferBitmap()
    {
        _pipe?.D2DContext.Target = null;
        _backBufferBitmap?.Dispose();
        _backBufferBitmap = null;
        _backBufferHandle = IntPtr.Zero;
        _backBufferWidth = 0;
        _backBufferHeight = 0;
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

        _uiLayers.Clear();
        for (var i = layers.Count - 1; i >= 0; i--) _uiLayers.Add(layers[i]);

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

            var body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            card.Content = body;

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = ThemeText(
                layer.Kind == PvLayerKind.VisualAsset && !string.IsNullOrWhiteSpace(layer.SpoutSenderName)
                    ? layer.SpoutSenderName!
                    : layer.Name,
                TextPrimaryKey, 13);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.Margin = new Thickness(0, 0, 6, 0);
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
                var pipe = _pipe;
                if (pipe is not null)
                {
                    pipe.RequestRemove(idRef);
                    return;
                }

                var index = _scene.Layers.FindIndex(l => l.Id == idRef);
                if (index < 0) return;
                _scene.Layers = PvPipeline.WithLayerRemovedAt(_scene.Layers, index);
                BuildLayerList();
            };
            headRight.Children.Add(del);

            Grid.SetColumn(headRight, 1);
            head.Children.Add(headRight);
            body.Children.Add(head);

            var meta = ThemeText(LayerMetaLabel(layer), TextTertiaryKey, 11);
            meta.Margin = new Thickness(0, 2, 0, 0);
            Grid.SetRow(meta, 1);
            body.Children.Add(meta);

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
        PvLayerKind.Decor => "装饰",
        PvLayerKind.Subtitle => "字幕",
        _ => k.ToString(),
    };

    private static string LayerKindLabel(PvLayerConfig layer) => layer.Id switch
    {
        LegacyPvProvider.FrontId => "歌词文字",
        LegacyPvProvider.BackId => "PV 背景",
        _ => LayerKindLabel(layer.Kind),
    };

    private static string LayerMetaLabel(PvLayerConfig layer) => LayerKindLabel(layer);

    private void LayerCard_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d
            && (FindAncestor<RangeBase>(d) is not null
                || FindAncestor<ButtonBase>(d) is not null
                || FindAncestor<Wpf.Ui.Controls.ToggleSwitch>(d) is not null)) return;
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

    private LegacyPvOptions LegacyOptions(DirectorProject? project = null)
    {
        var p = project ?? _project;
        return new LegacyPvOptions
        {
            Enabled = true,
            LrcText = _lrcText ?? "",
            AudioPath = _audioPath,
            Style = p.LegacyStyle,
            Seed = p.LegacySeed,
            RenderScale = p.LegacyRenderScale,
            NoCutTrans = p.LegacyStageTransitions,
            StageCamera = p.LegacyStageCamera,
            StageTransitions = p.LegacyStageTransitions,
            Preset = p.Preset,
        };
    }

    private static string LegacyStyleToLyric(string style)
    {
        var key = (style ?? "").Trim();
        return key.Length == 0 ? "Noir" : char.ToUpperInvariant(key[0]) + key[1..].ToLowerInvariant();
    }

    private void SyncLyricStylePicker()
    {
        if (string.IsNullOrWhiteSpace(_scene.LyricStyleName))
            _scene.LyricStyleName = LegacyStyleToLyric(_project.LegacyStyle);

        SyncThemeCards();
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
                + $"当前工程会先备份为 {Path.GetFileName(_projectPath)}.bak，然后被默认工程覆盖。继续？",
                "PV 工作台", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        AdoptProject(new DirectorProject(), "已新建工程", newPath: ProjectPath);
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

        var loaded = LoadProjectWithAutosave(dlg.FileName);
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
                + $"当前工作台会被它整份替换（当前工程先备份为 {Path.GetFileName(_projectPath)}.bak）。继续？",
                "PV 工作台", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        AdoptProject(loaded.Project, $"已打开工程：{loaded.Project.Name}", newPath: dlg.FileName);
    }

    private static ProjectLoadResult LoadProjectWithAutosave(string path)
    {
        var loaded = ProjectStore.Load(path);
        if (loaded.Project is null) return loaded;

        var autosave = path + ".autosave.json";
        if (!File.Exists(autosave)) return loaded;
        if (File.Exists(path) && File.GetLastWriteTimeUtc(autosave) <= File.GetLastWriteTimeUtc(path)) return loaded;

        var recovered = ProjectStore.Load(autosave);
        return recovered.Project is not null ? recovered : loaded;
    }

    private void AdoptProject(DirectorProject project, string note, bool resetUndo = true, string? newPath = null)
    {
        if (!BackupProjectFile()) return;

        if (!string.IsNullOrWhiteSpace(newPath)) _projectPath = newPath;

        var prevAudio = _audioPath;
        _projectEpoch++;
        _legacyPlanInfo = null;
        _uiLayers.Clear();
        _project = project;
        _scene = project.Scene;
        _effects = project.Effects;
        _pipeEffects = _effects.Clone();
        _audioPath = project.AudioPath;
        _lrcText = project.LrcText;

        _evaluator = DirectorEvaluator.FromSaves(project.Curves, project.CameraTrack, project.LayerCameraTracks);

        bool sameAudio = !resetUndo && !string.IsNullOrWhiteSpace(_audioPath)
                         && string.Equals(prevAudio, _audioPath, StringComparison.OrdinalIgnoreCase)
                         && _audio.Duration > 0;

        if (!sameAudio)
        {
            _audio.Dispose();
            _audio = new AudioClockDriver();
            ApplyVolume();

            _peaks = null;
            _audioLoadCts?.Cancel();
            _audioLoadCts = null;
            _musicCts?.Cancel();
            _musicCts = null;
            _audioBands = null;
            _beatMap = null;
            PublishAudioBands();

            if (!string.IsNullOrWhiteSpace(_audioPath) && File.Exists(_audioPath))
            {
                if (_audio.LoadAudio(_audioPath))
                {
                    StartAudioLoad(_audioPath);
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

        _uiLayers.Clear();
        _selectedLayer = null;
        _saveError = null;

        BuildLayerList();
        TheTimeline.Fps = _scene.Fps;
        RefreshTimeline();
        BuildInspector();
        SyncLyricStylePicker();
        SyncPresetControls();
        RefreshMediaPanel();
        UpdateAutoButton();
        TblAutoReport.Text = "";

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
        Log.Info($"[PV 工作台] {note}（存盘落到 {_projectPath}）");
    }

    private bool BackupProjectFile()
    {
        if (!File.Exists(_projectPath)) return true;

        try
        {
            File.Copy(_projectPath, _projectPath + ".bak", overwrite: true);
            Log.Info($"[PV 工作台] 已备份当前工程：{_projectPath}.bak");
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
        UpdateAutoButton();

        if (_pipe is not null)
        {
            _rebuildPipeKeepTransport = true;
            _rebuildPipe = true;
        }
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

    internal void BtnOpenLyrics_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开歌词文件",
            Filter = "歌词 (*.lrc;*.krc)|*.lrc;*.krc",
        };
        if (dlg.ShowDialog(this) != true) return;
        LoadLyricsFile(dlg.FileName);
    }

    internal void BtnOpenAudio_Click(object sender, RoutedEventArgs e)
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
            UpdateAutoButton();
            StartAudioLoad(path);
            _exportFormEdited = false;
            if (_pipe is not null)
            {
                _rebuildPipeKeepTransport = true;
                _rebuildPipe = true;
            }

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
            TblStatus.Text = $"音频加载失败：{Path.GetFileName(path)}：{_audio.Error}";
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
                if (_legacyPlanInfo is { } legacyPlan)
                    ApplyLegacyPlan(legacyPlan, force: true);
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

    internal void TglBroadcast_Changed(object sender, RoutedEventArgs e)
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

    internal void TglSpoutTransparent_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingToggles) return;

        _spoutTransparent = TglSpoutTransparent.IsChecked == true;
        _pipe?.SetSpoutTransparent(_spoutTransparent);

        var s = App.SettingsOf();
        if (s is not null)
        {
            s.SpoutTransparent = _spoutTransparent;
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
        try
        {
            TglBroadcast.IsChecked = _broadcastOn;
            TglSpoutTransparent.IsChecked = _spoutTransparent;
        }
        finally { _syncingToggles = false; }

        TblBroadcastState.Text = _broadcastOn
            ? $"广播 开（发送端 {_spoutName}{(_spoutTransparent ? " · 透明背景" : "")}）"
            : "广播 关";
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

    internal void ExportField_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingExportForm) return;
        _exportFormEdited = true;
    }

    internal void BtnExportBrowse_Click(object sender, RoutedEventArgs e)
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

    internal void BtnExport_Click(object sender, RoutedEventArgs e)
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
        var curves = DirectorEvaluator.FromSaves(copy.Curves, copy.CameraTrack, copy.LayerCameraTracks);

        return new PvExportInputs(
            Curves: curves,
            Bands: _audioBands,
            Segments: copy.DesignSegments,
            Clips: _lyricClips,
            Subtitles: burnSubtitles ? App.SettingsOf()?.Subtitle : null,
            AudioPath: includeAudio ? _audioPath : null,
            AudioOffsetSec: ExportAudioOffsetSec,
            Legacy: LegacyOptions(copy));
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

    internal void BtnAddLayer_Click(object sender, RoutedEventArgs e)
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
            var present = Stage.Fps;
            var prepare = pipe.LastPrepareMs;
            var composite = pipe.LastCompositeMs;
            var legacy = pipe.LegacyRenderMs;
            var frameMs = Math.Max(legacy, prepare + composite);
            var effective = frameMs > 0.01
                ? Math.Min(present > 0 ? present : 999, 1000.0 / frameMs)
                : present;
            TblFps.Text = $"{effective:F1} fps · {frameMs:F1}ms";
            TblFps.ToolTip =
                "有效帧率按最慢环节估算\n" +
                $"呈现节奏 {present:F1} Hz（受 VSync/显示器刷新限制）\n" +
                $"原版引擎（{(pipe.LegacyGpu ? "GPU" : "CPU")} 光栅）{legacy:F1} ms\n" +
                $"舞台准备 {prepare:F1} ms\n" +
                $"舞台合成 {composite:F1} ms";

            var (live, total) = pipe.SpoutState;
            var srcTxt = total == 0 ? "无外部源" : $"{live}/{total} 路源已连";
            var status = $"效果 {pipe.Renderer.EffectCount} 段 · 图层 {_scene.Layers.Count} · {srcTxt}";
            if (pipe.LegacyMode)
                status += $" · 原版引擎 {pipe.LegacyCutCount} 切分";

            var b = pipe.BroadcastInfo;
            if (b.Enabled)
            {
                string seg;
                if (b.LastError is { } berr) seg = $"广播失败：{berr}";
                else if (b.Sent > 0) seg = "广播中";
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
        _saveError = SaveProjectToCurrent();
        TblStatus.Text = _saveError is null ? "工程已保存" : $"保存失败：{_saveError}";
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e) => SaveNow();

    private void BtnSaveAs_Click(object sender, RoutedEventArgs e) => SaveProjectAs();

    private void BtnUndo_Click(object sender, RoutedEventArgs e) => UndoRedo(true);

    private void BtnRedo_Click(object sender, RoutedEventArgs e) => UndoRedo(false);

    private void UpdateUndoButtons()
    {
        BtnUndo.IsEnabled = _undo.CanUndo;
        BtnRedo.IsEnabled = _undo.CanRedo;
    }

    internal void SldAutoIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TblAutoIntensity is not null)
            TblAutoIntensity.Text = e.NewValue.ToString("F2", CultureInfo.InvariantCulture);
    }

    private void UpdateAutoButton()
    {
        var ready = !string.IsNullOrWhiteSpace(_audioPath) && File.Exists(_audioPath)
                    && !string.IsNullOrWhiteSpace(_lrcText);
        BtnGenerateAuto.IsEnabled = !_autoGenerating && ready;
        BtnRegenerateAuto.IsEnabled = !_autoGenerating && ready;
        TblAutoHint.Text = ready
            ? "生成会整份重写自动生成的曲线与效果实例，一次生成压一条撤销。"
            : "先导入音频和歌词，生成才会解锁。";
    }

    internal void Nav_Click(object sender, RoutedEventArgs e)
    {
        var index = ReferenceEquals(sender, NavStyle) ? 1
            : ReferenceEquals(sender, NavTech) ? 2
            : ReferenceEquals(sender, NavDirector) ? 3
            : ReferenceEquals(sender, NavOutput) ? 4
            : ReferenceEquals(sender, NavSettings) ? 5
            : 0;
        SetStep(index);
    }

    private void SetStep(int index)
    {
        _step = index;

        PanelMedia.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelStyle.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanelTech.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PanelDirector.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        PanelOutput.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;
        PanelSettings.Visibility = index == 5 ? Visibility.Visible : Visibility.Collapsed;

        NavMedia.Appearance = AppearanceOf(index == 0);
        NavStyle.Appearance = AppearanceOf(index == 1);
        NavTech.Appearance = AppearanceOf(index == 2);
        NavDirector.Appearance = AppearanceOf(index == 3);
        NavOutput.Appearance = AppearanceOf(index == 4);
        NavSettings.Appearance = AppearanceOf(index == 5);

        if (index == 0) RefreshMediaPanel();
        if (index == 1) EnsureStylePanel();
        if (index == 2) EnsureTechPanel();
        if (index == 5) SyncSettingsPanel();

        static Wpf.Ui.Controls.ControlAppearance AppearanceOf(bool selected) =>
            selected ? Wpf.Ui.Controls.ControlAppearance.Primary : Wpf.Ui.Controls.ControlAppearance.Transparent;
    }

    private void SyncSettingsPanel()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(SyncSettingsPanel);
            return;
        }

        _syncingSettings = true;
        try
        {
            TxtCanvasW.Text = _scene.Width.ToString(CultureInfo.InvariantCulture);
            TxtCanvasH.Text = _scene.Height.ToString(CultureInfo.InvariantCulture);
            TxtCanvasFps.Text = _scene.Fps.ToString(CultureInfo.InvariantCulture);
            var gpuName = Stage.Gpu?.AdapterName ?? "未就绪";
            var paren = gpuName.IndexOf(" (", StringComparison.Ordinal);
            TblGpuName.Text = paren > 0 ? gpuName[..paren] : gpuName;
            TblGpuName.ToolTip = gpuName;
            TblVideoDecode.Text = "D3D11 硬解优先";
            TblVideoDecode.ToolTip = "D3D11 硬解优先，失败自动回退";
            TglVSync.IsChecked = Stage.VSync;

            TglCenterFree.IsChecked = _project.Preset.CenterFree;
            SldCenterGap.Value = Math.Clamp(_project.Preset.CenterGap ?? 30, 10, 70);
            TblCenterGap.Text = $"{Math.Round(SldCenterGap.Value):0}%";
            SelectCenterZone(_project.Preset.CenterZone);

            SelectCanvasPreset(_scene.Width, _scene.Height);
            SyncPvRenderOption();
        }
        finally
        {
            _syncingSettings = false;
        }
    }

    private void SyncPvRenderOption()
    {
        var scale = Math.Clamp(_project.LegacyRenderScale, 0.25, 1.0);
        var best = -1;
        var bestDiff = double.MaxValue;
        for (var i = 0; i < CmbPvRender.Items.Count; i++)
        {
            if (CmbPvRender.Items[i] is not ComboBoxItem item) continue;
            if (item.Tag is not string tag
                || !double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
                continue;
            var diff = Math.Abs(value - scale);
            if (diff >= bestDiff) continue;
            bestDiff = diff;
            best = i;
        }

        if (bestDiff > 0.0005) best = -1;
        CmbPvRender.SelectedIndex = best;

        var (w, h) = PvRenderSize(scale);
        var mode = _pipe is null ? "待启动" : _pipe.LegacyGpu ? "GPU 光栅" : "CPU 光栅";
        TblPvRender.Text = $"当前 {w} × {h}（{scale * 100:0.#}%）· {mode}";
    }

    private (int Width, int Height) PvRenderSize(double scale)
    {
        var sceneWidth = Math.Max(1, _scene.Width);
        var target = (int)Math.Round(sceneWidth * Math.Clamp(scale, 0.25, 1.0));
        target = sceneWidth <= 320 ? sceneWidth : Math.Clamp(target, 320, sceneWidth);
        var height = Math.Max(1, (int)Math.Round(_scene.Height * (target / (double)sceneWidth)));
        return (target, height);
    }

    internal void CmbPvRender_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSettings) return;
        if (CmbPvRender.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not string tag
            || !double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale))
            return;

        scale = Math.Clamp(scale, 0.25, 1.0);
        if (Math.Abs(_project.LegacyRenderScale - scale) < 0.0001)
        {
            SyncPvRenderOption();
            return;
        }

        _project.LegacyRenderScale = scale;
        _saveError = SaveProjectNow();
        _rebuildPipeKeepTransport = true;
        _rebuildPipe = true;

        var (w, h) = PvRenderSize(scale);
        ShowStatus($"PV 渲染分辨率：{w} × {h}，正在重建渲染管线", 5);
        SyncPvRenderOption();
    }

    private Task<double?> PromptTimelineValueAsync(double current)
    {
        try
        {
            var box = new Wpf.Ui.Controls.NumberBox
            {
                Value = current,
                MinWidth = 220,
                MaxDecimalPlaces = 3,
                SmallChange = 0.1,
            };
            var title = new TextBlock
            {
                Text = L.T("Dialog.Value"),
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 12),
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");

            var ok = new Wpf.Ui.Controls.Button
            {
                Content = "OK",
                Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
                MinWidth = 88,
                IsDefault = true,
            };
            var cancel = new Wpf.Ui.Controls.Button
            {
                Content = L.T("Dialog.Cancel"),
                MinWidth = 88,
                IsCancel = true,
                Margin = new Thickness(8, 0, 0, 0),
            };
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 16, 0, 0),
            };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var panel = new StackPanel();
            panel.Children.Add(title);
            panel.Children.Add(box);
            panel.Children.Add(buttons);

            var card = new Border
            {
                Child = panel,
                Padding = new Thickness(22),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Width = 360,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            card.SetResourceReference(Border.BackgroundProperty, "SolidBackgroundFillColorSecondaryBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

            var overlay = new Grid
            {
                Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb(150, 0, 0, 0)),
            };
            overlay.Children.Add(card);

            var dialog = new Window
            {
                Owner = this,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = ActualWidth,
                Height = ActualHeight,
                Content = overlay,
                FontFamily = FontFamily,
            };

            ok.Click += (_, _) => dialog.DialogResult = true;
            cancel.Click += (_, _) => dialog.DialogResult = false;
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    dialog.DialogResult = true;
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    dialog.DialogResult = false;
                    e.Handled = true;
                }
            };

            var accepted = dialog.ShowDialog() == true;
            return Task.FromResult(accepted ? box.Value : (double?)null);
        }
        catch (Exception ex)
        {
            Log.Warn("[PV 工作台] 数值输入失败：" + ex.Message);
            return Task.FromResult<double?>(null);
        }
    }

    private void SelectCanvasPreset(int width, int height)
    {
        var tag = $"{width}x{height}";
        for (var i = 0; i < CmbCanvasPreset.Items.Count; i++)
        {
            if (CmbCanvasPreset.Items[i] is ComboBoxItem item
                && string.Equals(item.Tag as string, tag, StringComparison.Ordinal))
            {
                CmbCanvasPreset.SelectedIndex = i;
                return;
            }
        }

        CmbCanvasPreset.SelectedIndex = CmbCanvasPreset.Items.Count - 1;
    }

    internal void CmbCanvasPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSettings) return;
        if (CmbCanvasPreset.SelectedItem is not ComboBoxItem item) return;
        if (item.Tag is not string tag || tag == "custom") return;
        var parts = tag.Split('x');
        if (parts.Length != 2) return;
        TxtCanvasW.Text = parts[0];
        TxtCanvasH.Text = parts[1];
    }

    internal void CanvasField_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingSettings) return;
        if (!int.TryParse(TxtCanvasW.Text.Trim(), out var w)
            || !int.TryParse(TxtCanvasH.Text.Trim(), out var h))
        {
            TblCanvasState.Text = "画布宽高必须是整数";
            return;
        }

        SelectCanvasPreset(w, h);
        TblCanvasState.Text = "";
    }

    internal void BtnApplyCanvas_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(TxtCanvasW.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var w)
            || !int.TryParse(TxtCanvasH.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var h)
            || !int.TryParse(TxtCanvasFps.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fps))
        {
            TblCanvasState.Text = "画布宽高与帧率都必须是整数";
            return;
        }

        w = Math.Clamp(w, 64, 7680) & ~1;
        h = Math.Clamp(h, 64, 7680) & ~1;
        fps = Math.Clamp(fps, 1, 240);

        if (w == _scene.Width && h == _scene.Height && fps == _scene.Fps)
        {
            TblCanvasState.Text = $"当前已是 {w}×{h}@{fps}";
            return;
        }

        _scene.Width = w;
        _scene.Height = h;
        _scene.Fps = fps;
        _project.Scene = _scene;

        var settings = App.SettingsOf();
        if (settings is not null)
        {
            settings.Width = w;
            settings.Height = h;
            settings.RenderFps = fps;
            settings.SaveDebounced();
        }

        TheTimeline.Fps = fps;
        _rebuildPipeKeepTransport = true;
        _rebuildPipe = true;

        var fault = SaveProjectNow();
        TblCanvasState.Text = fault is null
            ? $"已应用 {w}×{h}@{fps}，正在重建渲染管线"
            : $"画布已应用，工程保存失败：{fault}";
        ShowStatus(TblCanvasState.Text, 5);
        SyncSettingsPanel();
    }

    internal void TglVSync_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingSettings) return;
        var on = TglVSync.IsChecked == true;
        Stage.VSync = on;
        var settings = App.SettingsOf();
        if (settings is not null)
        {
            settings.VSync = on;
            settings.SaveDebounced();
        }
    }

    private void BtnInspector_Click(object sender, RoutedEventArgs e)
    {
        _inspectorCollapsed = !_inspectorCollapsed;
        InspectorColumn.MinWidth = _inspectorCollapsed ? 0 : 320;
        InspectorColumn.Width = new GridLength(_inspectorCollapsed ? 0 : InspectorWidth);
        InspectorPane.Visibility = _inspectorCollapsed ? Visibility.Collapsed : Visibility.Visible;
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
