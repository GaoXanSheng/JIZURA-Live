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
using JizuraLive.Logging;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using JizuraLive.Render.Text;
using JizuraLive.Spout;
using JizuraLive.Vts;
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

    private bool _resumeAfterScrub;

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
    private System.Windows.Threading.DispatcherTimer? _paramTimer;
    private System.Windows.Threading.DispatcherTimer? _vtsTimer;
    private readonly List<PvLayerConfig> _uiLayers = new();
    private int _projectEpoch;
    private System.Windows.Point _dragStart;
    private PvLayerConfig? _dragLayer;
    private bool _draggingTime;

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

    private const double InspectorWidth = 380;
    private bool _inspectorCollapsed;
    private bool _leftCollapsed;
    private bool _timelineCollapsed;

    private int _step = -1;
    private bool _syncingNav;

    private bool _syncingToggles;
    private bool _syncingSettings;

    private string _mediaAudioCache = "";
    private string _mediaMetaCache = "";
    private string _mediaLyricCache = "";

    private BeatMap? _beatMap;
    private CancellationTokenSource? _musicCts;

    private readonly UndoLog _undo = new();

    private bool _broadcastOn;
    private bool _spoutTransparent;
    private readonly string _spoutName;

    public PvStageWindow()
    {
        InitializeComponent();
        Left.Owner = this;
        InitVts();
        LeftSplitter.DragCompleted += (_, _) => SaveWindowLayout();
        InspectorSplitter.DragCompleted += (_, _) => SaveWindowLayout();
        TimelineSplitter.DragCompleted += (_, _) => SaveWindowLayout();
        TheTimeline.HeaderWidthChanged += SaveWindowLayout;

        var settings = App.SettingsOf();
        _broadcastOn = settings?.SpoutEnabled ?? false;
        _spoutTransparent = settings?.SpoutTransparent ?? false;
        _spoutName = settings?.SpoutName ?? "JIZURA-live-PV";
        Stage.VSync = settings?.VSync ?? true;
        InitVolume();

        var boot = ProjectStore.LoadOrCreate(ProjectPath);
        var primaryFault = boot.Fault;
        var recoveredFromAutosave = false;
        var autosavePath = ProjectPath + ".autosave.json";
        if (File.Exists(autosavePath)
            && (!File.Exists(ProjectPath) || File.GetLastWriteTimeUtc(autosavePath) > File.GetLastWriteTimeUtc(ProjectPath)))
        {
            var recovered = ProjectStore.Load(autosavePath);
            if (recovered.Project is not null)
            {
                boot = new ProjectBootstrap(recovered.Project, primaryFault);
                recoveredFromAutosave = true;
            }
        }

        if (primaryFault is not null)
        {
            BackupCorruptProject(ProjectPath);
            if (!recoveredFromAutosave) _projectPath = ProjectPath + ".recovered.jzpv";
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
        Stage.PointerPressed += Stage_PointerPressed;
        Stage.PointerMoved += Stage_PointerMoved;
        Stage.PointerReleased += Stage_PointerReleased;
        Stage.MoveCursorHitTest = StageCursorHitTest;

        TheTimeline.SeekRequested += Timeline_SeekRequested;
        TheTimeline.LyricClipMenuRequested += Timeline_LyricClipMenu;
        TheTimeline.ScrubStarted += Timeline_ScrubStarted;
        TheTimeline.ScrubEnded += Timeline_ScrubEnded;
        TheTimeline.EditCommitted += Timeline_EditCommitted;
        TheTimeline.SegmentSelected += Timeline_SegmentSelected;
        TheTimeline.SegmentDeselected += Timeline_SegmentDeselected;
        TheTimeline.SegmentTimeEdited += Timeline_SegmentTimeEdited;
        TheTimeline.SegmentDeleteRequested += Timeline_SegmentDeleteRequested;
        TheTimeline.VtsCueDeleteRequested += Timeline_VtsCueDeleteRequested;
        TheTimeline.VtsCueEdited += Timeline_VtsCueEdited;
        TheTimeline.TrackVisibilityChanged += Timeline_TrackVisibilityChanged;
        TheTimeline.TrackHeightsChanged += Timeline_TrackHeightsChanged;
        TheTimeline.OwnerSelected += Timeline_OwnerSelected;
        TheTimeline.ZoomChanged += SyncTimelineZoom;
        TheTimeline.VtsTrackClearRequested += Timeline_VtsTrackClear;
        TheTimeline.CurveClearRequested += Timeline_CurveClear;
        TheTimeline.DesignTrackClearRequested += Timeline_DesignTrackClear;
        TheTimeline.ValuePrompt = PromptTimelineValueAsync;
        TheTimeline.Fps = _scene.Fps;

        SyncLyricStylePicker();
        UpdateBroadcastButton();
        SyncStageToggles();
        SyncCameraSwitchControls();
        ApplySavedLayout(settings);
        SetStep(Math.Clamp(settings?.Layout?.LeftTab ?? 0, 0, 4));

        TblInspectorTitle.Cursor = Cursors.Hand;
        TblInspectorTitle.ToolTip = L.T("StageUi.Window.BackToOverallCameraTooltip");
        TblInspectorTitle.MouseLeftButtonDown += (_, _) => SelectLayer(null);

        BuildLayerList();
        BuildInspector();
        RefreshTimeline();
        SyncSettingsPanel();
        SyncAutoControls();

        _uiTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _playheadTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33),
        };
        _playheadTimer.Tick += (_, _) => TickTimeline();
        _playheadTimer.Start();
        _paramTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200),
        };
        _paramTimer.Tick += (_, _) => RefreshParamRows();
        _paramTimer.Start();
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

        if (primaryFault is { } bootFault)
        {
            Log.Warn($"[PV 工作台] 工程未能完整载入：{bootFault}；主文件已备份为 .corrupt.bak");
            TblStatus.Text = recoveredFromAutosave
                ? L.F("StageUi.Window.MainProjectLoadFailedRecovered", bootFault)
                : L.F("StageUi.Window.LoadFailedChangesSavedElsewhere",
                    bootFault, Path.GetFileName(_projectPath));
        }

        PushUndo();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _audio.Play();
    }

    private string _projectPath = ProjectPath;
    private static string ProjectPath => Path.Combine(AppDirs.ConfigDir, "pv_project.jzpv");
    private string AutosavePath => _projectPath + ".autosave.json";

    private static void BackupCorruptProject(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            var backup = path + ".corrupt.bak";
            if (!File.Exists(backup)) File.Copy(path, backup);
            Log.Warn($"[PV 工作台] 已备份无法载入的工程：{backup}");
        }
        catch (Exception ex)
        {
            Log.Warn("[PV 工作台] 备份损坏工程失败：" + ex.Message);
        }
    }

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
            Title = L.T("StageUi.Window.SaveProjectAsTitle"),
            Filter = L.T("StageUi.Window.PvProjectFileFilter"),
            DefaultExt = ".jzpv",
            AddExtension = true,
            FileName = string.IsNullOrWhiteSpace(_project.Name) ? L.T("StageUi.Window.DefaultProjectFileName") : _project.Name,
            InitialDirectory = AppDirs.ConfigDir,
        };
        if (dlg.ShowDialog(this) != true) return;

        var target = dlg.FileName;
        var dir = Path.GetDirectoryName(target);
        if (string.IsNullOrWhiteSpace(dir))
        {
            TblStatus.Text = L.T("StageUi.Window.SaveAsFailedInvalidDirectory");
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
                TblStatus.Text = L.T("StageUi.Window.SaveAsFailedAudioReadPrefix") + ex.Message + "）";
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
                   ?? throw new InvalidOperationException(L.T("StageUi.Window.ProjectSnapshotEmpty"));
        }
        catch (Exception ex)
        {
            TblStatus.Text = L.T("StageUi.Window.SaveAsFailedPrefix") + ex.Message;
            return;
        }

        copy.Name = Path.GetFileNameWithoutExtension(target);
        copy.LrcText = _lrcText ?? "";
        copy.Scene = _scene;
        copy.Effects = _effects;

        var fault = ProjectStore.Save(copy, target, audio);
        if (fault is not null)
        {
            TblStatus.Text = L.T("StageUi.Window.SaveAsFailedPrefix") + fault;
            return;
        }

        _projectPath = target;
        _project.Name = copy.Name;
        DeleteAutosave();

        string audioNote = audio is { Length: > 0 } ? L.T("StageUi.Window.AudioEmbeddedNote") : L.T("StageUi.Window.NoAudioNote");
        TblStatus.Text = L.F("StageUi.Window.SavedAs", target, audioNote);
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
                    L.F("StageUi.Window.ProjectSaveFailedClosePrompt", fault),
                    L.T("StageUi.Window.PvWorkbenchTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _uiTimer?.Stop();
        _playheadTimer?.Stop();
        _paramTimer?.Stop();
        StopTechniquePreview();
        _vtsTimer?.Stop();

        var pipe = _pipe;
        pipe?.StopPumping();
        var maxRecv = pipe?.MaxReceiveMs ?? 0;
        _audio.Dispose();

        if (!Stage.StopRender())
        {
            Log.Error("[PV 工作台] 停机握手未完成，管线留给渲染线程收尾");
            return;
        }

        _vts.Dispose();

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
            Dispatcher.BeginInvoke(() => TblStatus.Text = L.T("StageUi.Window.PreviewStoppedPrefix") + host.Error);
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
            Dispatcher.BeginInvoke(() => TblStatus.Text = L.T("StageUi.Window.InitializationFailed"));
        }
    }

    private void BuildPipeline(PresentHost host, bool keepTransport = false)
    {
        _pipe?.Dispose();
        _pipe = null;

        var pipe = new PvPipeline(host.Gpu!, _scene, _pipeEffects, SubtitleSource, LegacyOptions());
        _pipe = pipe;
        pipe.SelectedLayerId = _selectedLayer?.Id;
        pipe.SetSpoutTransparent(_spoutTransparent);
        pipe.StageCameraEnabled = _project.LegacyStageCamera;
        pipe.StageTransitionsEnabled = _project.LegacyStageTransitions;
        int epoch = _projectEpoch;
        pipe.LegacyPlanReady += info =>
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (epoch == _projectEpoch && ReferenceEquals(_pipe, pipe)) ApplyLegacyPlan(info);
            }));
        pipe.CompositeInvalidated += () => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (epoch != _projectEpoch || !ReferenceEquals(_pipe, pipe)) return;
            _rebuildPipeKeepTransport = true;
            _rebuildPipe = true;
            ShowStatus(L.T("StageUi.Window.LayerOrderRequiresLayeredMode"), 4);
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
                Dispatcher.BeginInvoke(() => TblStatus.Text = L.F("StageUi.Window.ProjectAudioNotFound", _audioPath));
            }
            else if (!_audio.LoadAudio(_audioPath))
            {
                Dispatcher.BeginInvoke(() => TblStatus.Text = L.F("StageUi.Window.ProjectAudioLoadFailed", _audioPath, _audio.Error));
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
                    TheTimeline.ResetView();
                    RefreshTimeline();
                });
                var last = lines.Count > 0 ? lines[^1].StartMs + lines[^1].DurMs : 0;
                Log.Info($"[PV 工作台] 已恢复工程歌词：{lines.Count} 行，末句结束 {last}ms，末句待校准={tailUncalibrated}");
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
            Dispatcher.BeginInvoke(() => TblStatus.Text = L.T("StageUi.Window.SwitchProjectFailedPrefix") + ex.Message);
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

    private void BuildLayerList()
    {
        var layers = _scene.Layers;
        bool composite = _pipe?.LegacyComposite == true;

        _uiLayers.Clear();
        for (var i = layers.Count - 1; i >= 0; i--)
        {
            var layer = layers[i];
            if (layer is null) continue;
            if (composite && IsLegacyFront(layer.Id)) continue;
            _uiLayers.Add(layer);
        }

        if (_selectedLayer is not null && !_uiLayers.Contains(_selectedLayer)) _selectedLayer = null;
        SyncStageSelection();

        LayersHost.Children.Clear();
        foreach (var layer in _uiLayers)
        {
            var card = new Border
            {
                Padding = new Thickness(8, 6, 8, 6),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                Background = Brushes.Transparent,
                AllowDrop = !composite,
                Cursor = composite ? Cursors.Arrow : Cursors.SizeAll,
            };
            card.MouseEnter += (_, _) => card.SetResourceReference(Border.BackgroundProperty, SelectFillKey);
            card.MouseLeave += (_, _) => HighlightLayerCards();
            card.Tag = layer;
            card.PreviewMouseLeftButtonDown += LayerCard_MouseDown;
            card.PreviewMouseMove += LayerCard_MouseMove;
            card.DragOver += LayerCard_DragOver;
            card.Drop += LayerCard_Drop;
            card.DragLeave += (_, _) => HighlightLayerCards();

            var body = new Grid();
            card.Child = body;

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = ThemeText(
                layer.Kind == PvLayerKind.VisualAsset && !string.IsNullOrWhiteSpace(layer.SpoutSenderName)
                    ? layer.SpoutSenderName!
                    : layer.Name,
                TextPrimaryKey, 12);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            name.VerticalAlignment = VerticalAlignment.Center;
            name.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(name, 0);
            head.Children.Add(name);

            var meta = ThemeText(LayerMetaLabel(layer), TextTertiaryKey, 10.5);
            meta.VerticalAlignment = VerticalAlignment.Center;
            meta.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(meta, 1);
            head.Children.Add(meta);

            var headRight = new StackPanel { Orientation = Orientation.Horizontal };
            var toggle = new CheckBox
            {
                IsChecked = layer.Enabled,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = L.T("StageUi.Window.ToggleLayerVisibilityTooltip"),
            };
            toggle.Checked += (_, _) => layer.Enabled = true;
            toggle.Unchecked += (_, _) => layer.Enabled = false;
            headRight.Children.Add(toggle);

            if (!IsEngineLayer(layer.Id))
            {
                var del = new Wpf.Ui.Controls.Button
                {
                    Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(6, 0, 0, 0),
                    Content = "✕",
                    ToolTip = L.T("StageUi.Window.DeleteLayerTooltip"),
                };
                del.Click += (_, _) => DeleteLayer(layer);
                headRight.Children.Add(del);
            }

            Grid.SetColumn(headRight, 2);
            head.Children.Add(headRight);
            body.Children.Add(head);

            LayersHost.Children.Add(card);
        }

        TblLayerCount.Text = L.F("StageUi.Window.LayerCount", _uiLayers.Count);
        if (composite)
        {
            var hint = ThemeText(
                L.T("StageUi.Window.LegacyLayersMergedHint"),
                TextTertiaryKey, 11, wrap: true);
            hint.Margin = new Thickness(0, 4, 0, 0);
            LayersHost.Children.Add(hint);
        }

        HighlightLayerCards();
        BuildInspector();
        if (_snap is not null) RefreshTimeline();
    }

    private static string LayerKindLabel(PvLayerKind k) => k switch
    {
        PvLayerKind.Background => L.T("StageUi.Window.LayerKindBackground"),
        PvLayerKind.VisualAsset => L.T("StageUi.Window.LayerKindVisualAsset"),
        PvLayerKind.ForegroundFx => L.T("StageUi.Window.LayerKindForegroundFx"),
        PvLayerKind.Subtitle => L.T("StageUi.Window.LayerKindSubtitle"),
        _ => k.ToString(),
    };

    private static string LayerKindLabel(PvLayerConfig layer) => layer.Id switch
    {
        LegacyPvProvider.FrontId => L.T("StageUi.Window.LayerKindLyricText"),
        LegacyPvProvider.FrontLayoutId => L.T("StageUi.Window.LayerKindLyricLayout"),
        LegacyPvProvider.FrontDecorId => L.T("StageUi.Window.LayerKindLyricDecor"),
        LegacyPvProvider.FrontHudId => L.T("StageUi.Window.LayerKindLyricHud"),
        LegacyPvProvider.BackId => L.T("StageUi.Window.LayerKindPvBackground"),
        _ => LayerKindLabel(layer.Kind),
    };

    private static string LayerMetaLabel(PvLayerConfig layer) => LayerKindLabel(layer);

    private static bool IsEngineLayer(string id) => id is
        LegacyPvProvider.BackId or LegacyPvProvider.FrontId or LegacyPvProvider.FrontLayoutId
        or LegacyPvProvider.FrontDecorId or LegacyPvProvider.FrontHudId;

    private static bool IsLegacyFront(string id) => id is
        LegacyPvProvider.FrontId or LegacyPvProvider.FrontLayoutId
        or LegacyPvProvider.FrontDecorId or LegacyPvProvider.FrontHudId;

    private void LayerCard_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d
            && (FindAncestor<RangeBase>(d) is not null
                || FindAncestor<ButtonBase>(d) is not null
                || FindAncestor<Wpf.Ui.Controls.ToggleSwitch>(d) is not null)) return;
        Keyboard.ClearFocus();
        var layer = ((Border)sender).Tag as PvLayerConfig;
        SelectLayer(layer);
        _dragStart = e.GetPosition(this);
        _dragLayer = layer;
    }

    private void LayerCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (_pipe?.LegacyComposite == true) return;
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
        ((Border)sender).SetResourceReference(Border.BackgroundProperty, SelectFillKey);
    }

    private void LayerCard_Drop(object sender, DragEventArgs e)
    {
        var targetCard = (Border)sender;
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

    private static readonly string[] LegacyAllIds =
    [
        LegacyPvProvider.BackId,
        LegacyPvProvider.FrontId,
        LegacyPvProvider.FrontLayoutId,
        LegacyPvProvider.FrontDecorId,
        LegacyPvProvider.FrontHudId,
    ];

    private bool AllowsLegacyComposite(DirectorProject p)
    {
        if (p.EditorLayout?.LegacyComposite != true) return false;

        if (ReferenceEquals(p, _project))
        {
            foreach (var address in _evaluator.Curves.Keys)
                if (address.Kind == ParamAddressKind.Layer
                    && LegacyAllIds.Contains(address.Target, StringComparer.Ordinal))
                    return false;
        }
        else
        {
            foreach (var save in p.Curves ?? new List<CurveSave>())
            {
                if (save is null || !ParamAddress.TryParse(save.Address, out var address)) continue;
                if (address.Kind == ParamAddressKind.Layer
                    && LegacyAllIds.Contains(address.Target, StringComparer.Ordinal))
                    return false;
            }
        }

        if (p.LayerCameraTracks is { } tracks)
            foreach (var id in LegacyAllIds)
                if (tracks.ContainsKey(id)) return false;

        if (p.Effects is { } effects)
            foreach (var node in effects.Nodes)
                if (node is not null && node.Enabled
                    && LegacyAllIds.Contains(node.Target, StringComparer.Ordinal))
                    return false;

        return true;
    }

    private bool EffectiveLegacyComposite(DirectorProject p) =>
        AllowsLegacyComposite(p) && PvPipeline.CanUseLegacyComposite(p.Scene, p.Effects);

    private LegacyPvOptions LegacyOptions(DirectorProject? project = null, int? fps = null)
    {
        var p = project ?? _project;
        var composite = EffectiveLegacyComposite(p);
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
            Fps = fps ?? _scene.Fps,
            Composite = composite,
            NoPost = !composite,
            CutOverrides = p.LegacyCutOverrides,
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

            TblStatus.Text = L.F("StageUi.Window.ActionFailedSnapshotEmpty", what);
            return null;
        }
        catch (Exception ex)
        {
            TblStatus.Text = L.F("StageUi.Window.ActionFailedSnapshotUnreadable", what, ex.Message);
            return null;
        }
    }

    private void UndoRedo(bool undo)
    {
        var text = undo ? _undo.Undo() : _undo.Redo();
        if (text is null)
        {
            TblStatus.Text = undo ? L.T("StageUi.Window.NothingToUndo") : L.T("StageUi.Window.NothingToRedo");
            return;
        }

        if (ProjectFromSnapshot(text, undo ? L.T("StageUi.Window.UndoLabel") : L.T("StageUi.Window.RedoLabel")) is { } project)
            AdoptProject(project, undo ? L.T("StageUi.Window.Undone") : L.T("StageUi.Window.Redone"), resetUndo: false);
    }

    private void BtnNewProject_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                L.F("StageUi.Window.NewProjectConfirm",
                    Path.GetFileName(_projectPath)),
                L.T("StageUi.Window.PvWorkbenchTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        AdoptProject(new DirectorProject(), L.T("StageUi.Window.ProjectCreated"), newPath: ProjectPath);
    }

    private void BtnOpenProject_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("StageUi.Window.OpenProjectTitle"),
            Filter = L.T("StageUi.Window.PvProjectFileFilter"),
            InitialDirectory = AppDirs.ConfigDir,
        };
        if (dlg.ShowDialog(this) != true) return;

        var loaded = LoadProjectWithAutosave(dlg.FileName);
        if (loaded.Project is null)
        {
            var fault = loaded.Fault ?? L.T("StageUi.Window.UnknownReason");
            MessageBox.Show(this, L.T("StageUi.Window.OpenProjectFailedPrefix") + fault, L.T("StageUi.Window.PvWorkbenchTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            Log.Warn("[PV 工作台] 打开工程失败：" + fault);
            return;
        }

        if (MessageBox.Show(this,
                L.F("StageUi.Window.OpenProjectConfirm",
                    loaded.Project.Name, dlg.FileName, Path.GetFileName(_projectPath)),
                L.T("StageUi.Window.PvWorkbenchTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        AdoptProject(loaded.Project, L.F("StageUi.Window.ProjectOpened", loaded.Project.Name), newPath: dlg.FileName);
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

        if (resetUndo)
        {
            _vtsBaseSynced = false;
            _vtsSaveBasePending = false;
            _vtsRestX = 0;
            _vtsRestY = 0;
            _vtsRestSize = 0;
            _vtsRestRot = 0;
            _vtsSent = (double.NaN, double.NaN, double.NaN, double.NaN);
            SyncVtsBaseFromProject();
        }

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
        TheTimeline.ResetView();
        RefreshTimeline();
        BuildInspector();
        SyncLyricStylePicker();
        SyncPresetControls();
        SyncAutoControls();
        SyncCameraSwitchControls();
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
        TblStatus.Text = _saveError is null ? note : L.F("StageUi.Window.NoteWithSaveFailure", note, _saveError);
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
                L.F("StageUi.Window.BackupProjectFailedConfirm", ex.Message),
                L.T("StageUi.Window.PvWorkbenchTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }
    }

    private (List<KrcLine> Lines, bool TailUncalibrated) ParseLyrics(string text)
    {
        var duration = _audio.Duration;
        var result = LrcParser.ParseClips(text, duration > 0 ? duration : null);
        _project.LyricEnds ??= new Dictionary<int, long>();
        var clips = LrcParser.ApplyEndOverrides(result.Clips, _project.LyricEnds);

        var lines = new List<KrcLine>(clips.Count);
        foreach (var clip in clips)
            lines.Add(new KrcLine(clip.StartMs, clip.EndMs - clip.StartMs, clip.Text, clip.Words,
                clip.SourceIndex));

        return (lines, clips.Count > 0 && clips[^1].OutOfRange);
    }

    private void LoadLyrics(string text)
    {
        var (lines, tailUncalibrated) = ParseLyrics(text);
        _lrcText = text;
        _lyrics = lines;
        _tailUncalibrated = tailUncalibrated;
        PublishLyricClips(LrcParser.ToClips(lines));
        BuildInspector();
        TheTimeline.ResetView();
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
            _project.LyricEnds ??= new Dictionary<int, long>();
            _project.LyricEnds.Clear();
            LoadLyrics(ReadLyricFileText(path));
            TblStatus.Text = L.F("StageUi.Window.LyricsLoadedLineCount", _lyrics?.Count ?? 0)
                             + (_tailUncalibrated ? L.T("StageUi.Window.TailUncalibratedNoAudio") : "");
            Log.Info($"[工作台] 已装载歌词：{_lyrics?.Count ?? 0} 行 ({path})，末句待校准={_tailUncalibrated}");
        }
        catch (Exception ex)
        {
            TblStatus.Text = L.F("StageUi.Window.OpenLyricsFailed", ex.Message);
            MessageBox.Show(this, L.T("StageUi.Window.OpenLyricsFailedPrefix") + ex.Message, L.T("StageUi.Window.PvStageTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal void BtnOpenLyrics_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("StageUi.Window.OpenLyricsFileTitle"),
            Filter = L.T("StageUi.Window.LyricsFileFilter"),
        };
        if (dlg.ShowDialog(this) != true) return;
        LoadLyricsFile(dlg.FileName);
    }

    internal void BtnOpenAudio_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("StageUi.Window.OpenAudioTitle"),
            Filter = L.T("StageUi.Window.AudioFileFilter"),
        };
        if (dlg.ShowDialog(this) != true) return;
        LoadAudioFile(dlg.FileName);
    }

    private void LoadAudioFile(string path)
    {
        TblStatus.Text = L.T("StageUi.Window.LoadingAudio");

        if (_audio.LoadAudio(path))
        {
            _audioPath = path;
            TblStatus.Text = L.T("StageUi.Window.AudioLoaded");
            BuildInspector();
            UpdateAutoButton();
            TheTimeline.ResetView();
            RefreshTimeline();
            StartAudioLoad(path);
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
                    _project.LyricEnds ??= new Dictionary<int, long>();
                    _project.LyricEnds.Clear();
                    LoadLyrics(ReadLyricFileText(lrcPath));
                    Log.Info($"[工作台] 自动加载同名歌词：{_lyrics?.Count ?? 0} 行，末句待校准={_tailUncalibrated}");
                }
                catch { }
            }
            else if (_tailUncalibrated && !string.IsNullOrWhiteSpace(_lrcText))
            {
                LoadLyrics(_lrcText);
                TblStatus.Text = L.F("StageUi.Window.AudioLoadedTailAligned", _lyrics?.Count ?? 0);
            }
        }
        else
        {
            TblStatus.Text = L.F("StageUi.Window.AudioLoadFailed", Path.GetFileName(path), _audio.Error);
        }
    }

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
            ? L.F("StageUi.Window.BroadcastOn", _spoutName, (_spoutTransparent ? L.T("StageUi.Window.BroadcastTransparentSuffix") : ""))
            : L.T("StageUi.Window.BroadcastOff");
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.S && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            SaveNow();
            base.OnPreviewKeyDown(e);
            return;
        }

        if (e.Key == Key.F1)
        {
            e.Handled = true;
            MenuShortcuts_Click(this, new RoutedEventArgs());
            base.OnPreviewKeyDown(e);
            return;
        }

        if (Keyboard.FocusedElement is TextBoxBase)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        if (e.Key == Key.Space)
        {
            if (ConsumesSpaceKeyboard(Keyboard.FocusedElement))
            {
                base.OnPreviewKeyDown(e);
                return;
            }

            e.Handled = true;
            if (!e.IsRepeat) TogglePlay();
            base.OnPreviewKeyDown(e);
            return;
        }

        var mods = Keyboard.Modifiers;
        bool plain = mods == ModifierKeys.None || mods == ModifierKeys.Shift;
        if (plain)
        {
            switch (e.Key)
            {
                case Key.J:
                    e.Handled = true;
                    TransportPlayReverse();
                    return;
                case Key.K:
                    e.Handled = true;
                    TransportPause();
                    return;
                case Key.L:
                    e.Handled = true;
                    TransportPlayForward();
                    return;
                case Key.Home:
                    e.Handled = true;
                    StopShuttle();
                    _audio.Seek(0);
                    return;
                case Key.End:
                    e.Handled = true;
                    StopShuttle();
                    _audio.Seek(Math.Max(0, _audio.Duration));
                    return;
                case Key.Up:
                case Key.Down:
                    if (ConsumesArrowKeyboard(Keyboard.FocusedElement)) break;
                    e.Handled = true;
                    TheTimeline.SeekAdjacentCut(e.Key == Key.Up ? -1 : 1);
                    return;
                case Key.S when (mods & ModifierKeys.Shift) == 0:
                    e.Handled = true;
                    ToggleSnap();
                    return;
                case Key.Z when (mods & ModifierKeys.Shift) != 0:
                    e.Handled = true;
                    TheTimeline.ZoomToFit();
                    return;
                case Key.PageUp:
                    e.Handled = true;
                    TheTimeline.SeekAdjacentKey(-1);
                    return;
                case Key.PageDown:
                    e.Handled = true;
                    TheTimeline.SeekAdjacentKey(1);
                    return;
                case Key.Left:
                case Key.Right:
                    if (ConsumesArrowKeyboard(Keyboard.FocusedElement)) break;
                    e.Handled = true;
                    SeekFrames((e.Key == Key.Left ? -1 : 1)
                               * ((mods & ModifierKeys.Shift) != 0 ? 10 : 1));
                    return;
                case Key.OemPlus:
                case Key.Add:
                    e.Handled = true;
                    TheTimeline.ZoomIn();
                    return;
                case Key.OemMinus:
                case Key.Subtract:
                    e.Handled = true;
                    TheTimeline.ZoomOut();
                    return;
                case Key.OemQuestion:
                    if ((mods & ModifierKeys.Shift) == 0) break;
                    e.Handled = true;
                    TheTimeline.ZoomToFit();
                    return;
            }
        }

        if (!ctrl)
        {
            base.OnPreviewKeyDown(e);
            return;
        }

        switch (e.Key)
        {
            case Key.Z:
                e.Handled = true;
                UndoRedo(!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
                break;
            case Key.Y:
                e.Handled = true;
                UndoRedo(false);
                break;
            case Key.Left:
                e.Handled = true;
                StopShuttle();
                _audio.Seek(Math.Clamp(_audio.Time - 1, 0, Math.Max(0, _audio.Duration)));
                break;
            case Key.Right:
                e.Handled = true;
                StopShuttle();
                _audio.Seek(Math.Clamp(_audio.Time + 1, 0, Math.Max(0, _audio.Duration)));
                break;
            case Key.OemPlus:
            case Key.Add:
                e.Handled = true;
                TheTimeline.ZoomIn();
                break;
            case Key.OemMinus:
            case Key.Subtract:
                e.Handled = true;
                TheTimeline.ZoomOut();
                break;
            case Key.D0:
                e.Handled = true;
                TheTimeline.ZoomToFit();
                break;
            case Key.N:
                e.Handled = true;
                BtnNewProject_Click(this, new RoutedEventArgs());
                break;
            case Key.O:
                e.Handled = true;
                BtnOpenProject_Click(this, new RoutedEventArgs());
                break;
            case Key.S when (Keyboard.Modifiers & ModifierKeys.Shift) != 0:
                e.Handled = true;
                SaveProjectAs();
                break;
            case Key.I:
                e.Handled = true;
                BtnOpenAudio_Click(this, new RoutedEventArgs());
                break;
            case Key.L:
                e.Handled = true;
                BtnOpenLyrics_Click(this, new RoutedEventArgs());
                break;
        }

        base.OnPreviewKeyDown(e);
    }

    private static bool ConsumesArrowKeyboard(IInputElement? element)
    {
        for (var node = element as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ComboBox or ComboBoxItem or Slider or TabItem) return true;
        return false;
    }

    private static bool ConsumesSpaceKeyboard(IInputElement? element)
    {
        for (var node = element as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ComboBox combo) return combo.IsDropDownOpen;
            else if (node is ComboBoxItem) return true;
        return false;
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

        var import = new MenuItem { Header = L.T("StageUi.Window.ImportImageMenuItem") };
        import.Click += (_, _) => ImportImageLayer();
        menu.Items.Add(import);
        menu.Items.Add(new Separator());

        if (known.Count > 0)
        {
            foreach (var name in known)
            {
                var item = new MenuItem { Header = name };
                var n = name;
                item.Click += (_, _) =>
                {
                    _pipe?.RequestAdd(n);
                    TblStatus.Text = L.F("StageUi.Window.AddSourceStatus", n);
                };
                menu.Items.Add(item);
            }
        }
        else
        {
            menu.Items.Add(new MenuItem
            {
                Header = senders.Count == 0 ? L.T("StageUi.Window.NoActiveSpoutSenders") : L.T("StageUi.Window.AllSendersInLayers"),
                IsEnabled = false,
            });
        }

        menu.IsOpen = true;
    }

    private void ImportImageLayer()
    {
        var pipe = _pipe;
        if (pipe is null)
        {
            TblStatus.Text = L.T("StageUi.Window.RenderNotReadyImportLater");
            return;
        }

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = L.T("StageUi.Window.ImportImageTitle"),
            Filter = L.T("StageUi.Window.ImageFileFilter"),
        };
        if (dlg.ShowDialog(this) != true) return;

        pipe.RequestAddAsset(dlg.FileName);
        TblStatus.Text = L.F("StageUi.Window.ImportingImage", Path.GetFileName(dlg.FileName));
    }

    private void BtnPlay_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_shuttleActive)
        {
            StopShuttle();
            UpdatePlayIcon();
            return;
        }

        if (!_audio.IsPlaying && _audio.Duration <= 0)
        {
            ShowStatus(L.T("StageUi.Window.ImportAudioFirst"), 3);
            return;
        }

        if (_audio.IsPlaying) _audio.Pause();
        else
        {
            _audio.Play();
            TheTimeline.FollowPlayhead();
        }
        UpdatePlayIcon();
    }

    private void UpdatePlayIcon()
    {
        PlayIcon.Symbol = _audio.IsPlaying || _shuttleActive
            ? Wpf.Ui.Controls.SymbolRegular.Pause24
            : Wpf.Ui.Controls.SymbolRegular.Play24;
    }

    private void SldTime_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_draggingTime) return;
        _audio.Seek(e.NewValue);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_draggingTime)
        {
            _draggingTime = false;
            _audio.Seek(SldTime.Value);
            if (SldTime.IsMouseCaptured) SldTime.ReleaseMouseCapture();
            if (_resumeAfterScrub)
            {
                _resumeAfterScrub = false;
                _audio.Play();
                TheTimeline.FollowPlayhead();
            }
        }

        _dragLayer = null;
        base.OnPreviewMouseLeftButtonUp(e);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (SldTime.IsMouseOver)
        {
            _draggingTime = true;
            if (_audio.IsPlaying)
            {
                _resumeAfterScrub = true;
                _audio.Pause();
            }

            if (SldTime.Template?.FindName("PART_Track", SldTime) is System.Windows.Controls.Primitives.Track track
                && track.ActualWidth > 0 && !IsThumbSource(e.OriginalSource as DependencyObject))
            {
                double ratio = Math.Clamp(e.GetPosition(track).X / track.ActualWidth, 0, 1);
                SldTime.Value = SldTime.Minimum + ratio * (SldTime.Maximum - SldTime.Minimum);
                SldTime.CaptureMouse();
                e.Handled = true;
            }
        }
        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (_draggingTime && SldTime.IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed
            && SldTime.Template?.FindName("PART_Track", SldTime) is System.Windows.Controls.Primitives.Track track
            && track.ActualWidth > 0)
        {
            double ratio = Math.Clamp(e.GetPosition(track).X / track.ActualWidth, 0, 1);
            SldTime.Value = SldTime.Minimum + ratio * (SldTime.Maximum - SldTime.Minimum);
            e.Handled = true;
        }
        base.OnPreviewMouseMove(e);
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
        var d = ProjectDuration();
        TblTime.Text = $"{Fmt(t)} / {Fmt(d)}";
        if (TblCompInfo is not null && _scene is not null)
            TblCompInfo.Text = $"{_scene.Width} × {_scene.Height} · {_scene.Fps} fps";
        if (!_draggingTime)
        {
            SldTime.Maximum = Math.Max(0.01, d);
            SldTime.LargeChange = Math.Max(0.5, d / 20);
            SldTime.SmallChange = Math.Max(0.1, d / 200);
            SldTime.Value = Math.Clamp(t, 0, SldTime.Maximum);
        }

        TickTimeline();

        if (_step == 4) UpdateRasterLabel();

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
            TblFps.Text = L.F("StageUi.Window.PreviewFps", effective, frameMs);
            TblFps.ToolTip =
                L.T("StageUi.Window.FpsTooltipEffective") +
                L.F("StageUi.Window.FpsTooltipPresentRate", present) +
                L.F("StageUi.Window.FpsTooltipLegacyEngine", (pipe.LegacyGpu ? "GPU" : "CPU"), legacy) +
                L.F("StageUi.Window.FpsTooltipStagePrepare", prepare) +
                L.F("StageUi.Window.FpsTooltipStageComposite", composite);

            var (live, total) = pipe.SpoutState;
            var srcTxt = total == 0 ? L.T("StageUi.Window.NoExternalSources") : L.F("StageUi.Window.SourcesConnected", live, total);
            var engineLayers = _scene.Layers.Count(l => l is not null && IsEngineLayer(l.Id));
            var layerText = engineLayers > 0
                ? L.F("StageUi.Window.EditableAndEngineLayers", _uiLayers.Count, engineLayers)
                : L.F("StageUi.Window.LayerCountShort", _uiLayers.Count);
            var status = L.F("StageUi.Window.StatusEffectNodes", pipe.Renderer.EffectCount, layerText, srcTxt);
            if (pipe.LegacyMode)
                status += L.F("StageUi.Window.StatusLegacyCuts", pipe.LegacyCutCount);
            if (pipe.LegacyComposite) status += L.T("StageUi.Window.StatusMergedComposite");
            if (pipe.LegacyError is { } legacyError)
                status += L.F("StageUi.Window.StatusLegacyError", legacyError);

            var b = pipe.BroadcastInfo;
            if (b.Enabled)
            {
                string seg;
                if (b.LastError is { } berr) seg = L.F("StageUi.Window.BroadcastFailed", berr);
                else if (b.Sent > 0) seg = L.T("StageUi.Window.Broadcasting");
                else seg = L.T("StageUi.Window.BroadcastReady");
                status += " · " + seg;
            }

            TblStatus.Text = status;
        }
        else
        {
            if (Stage.Error is not null)
                TblStatus.Text = L.F("StageUi.Window.PreviewStopped", Stage.Error);
            else
                TblStatus.Text = L.T("StageUi.Window.RenderStarting");
        }

        if (_audio.Error is { } fault) TblStatus.Text = L.F("StageUi.Window.AudioPaused", fault);
        if (_saveError is { } saveFault) TblStatus.Text = L.F("StageUi.Window.ProjectNotSaved", saveFault);

        if (_transientStatus is not null)
        {
            if (--_transientStatusTicks <= 0)
                _transientStatus = null;
            else
                TblStatus.Text = _transientStatus;
        }

        AppTitleBar.Title = L.F("Stage.WindowTitle",
            string.IsNullOrWhiteSpace(_project.Name) ? L.T("StageUi.Window.DefaultProjectName") : _project.Name);
        if (_step == 0) RefreshMediaPanel();
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
        TblStatus.Text = _saveError is null ? L.T("StageUi.Window.ProjectSaved") : L.F("StageUi.Window.SaveFailed", _saveError);
    }

    internal void SldAutoIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TblAutoIntensity is not null)
            TblAutoIntensity.Text = e.NewValue.ToString("F2", CultureInfo.InvariantCulture);
        if (_syncingAuto || _project is null) return;

        var layout = _project.EditorLayout ??= new EditorLayout();
        if (Math.Abs(layout.AutoIntensity - e.NewValue) < 1e-4) return;
        layout.AutoIntensity = Math.Clamp(e.NewValue, 0, 1);
        _saveError = SaveProjectNow();
    }

    private void UpdateAutoButton()
    {
        var ready = !string.IsNullOrWhiteSpace(_audioPath) && File.Exists(_audioPath)
                    && !string.IsNullOrWhiteSpace(_lrcText);
        BtnGenerateAuto.IsEnabled = !_autoGenerating && ready;
        TblAutoHint.Text = ready
            ? L.T("StageUi.Window.AutoGenerateHintReady")
            : L.T("StageUi.Window.AutoGenerateHintNotReady");
    }

    internal void Nav_Click(object sender, RoutedEventArgs e)
    {
        var index = ReferenceEquals(sender, NavStyle) ? 1
            : ReferenceEquals(sender, NavTech) ? 2
            : ReferenceEquals(sender, NavDirector) ? 3
            : ReferenceEquals(sender, NavSettings) ? 4
            : 0;
        SetStep(index);
    }

    private void SetStep(int index)
    {
        if (PanelMedia is null) return;
        _step = index;

        PanelMedia.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelStyle.Visibility = index == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanelTech.Visibility = index == 2 ? Visibility.Visible : Visibility.Collapsed;
        PanelDirector.Visibility = index == 3 ? Visibility.Visible : Visibility.Collapsed;
        PanelSettings.Visibility = index == 4 ? Visibility.Visible : Visibility.Collapsed;

        if (!_syncingNav)
        {
            _syncingNav = true;
            try
            {
                NavMedia.IsChecked = index == 0;
                NavStyle.IsChecked = index == 1;
                NavTech.IsChecked = index == 2;
                NavDirector.IsChecked = index == 3;
                NavSettings.IsChecked = index == 4;
            }
            finally
            {
                _syncingNav = false;
            }
        }

        if (index == 0) RefreshMediaPanel();
        if (index == 1) EnsureStylePanel();
        if (index == 2) EnsureTechPanel();
        if (index == 3) SyncAutoControls();
        if (index == 4) SyncSettingsPanel();
        if (IsLoaded) SaveWindowLayout();
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
            var gpuName = Stage.Gpu?.AdapterName ?? L.T("StageUi.Window.GpuNotReady");
            var paren = gpuName.IndexOf(" (", StringComparison.Ordinal);
            TblGpuName.Text = paren > 0 ? gpuName[..paren] : gpuName;
            TblGpuName.ToolTip = gpuName;
            TglVSync.IsChecked = Stage.VSync;
            TglLegacyComposite.IsChecked = _project.EditorLayout?.LegacyComposite == true;
            TblLegacyComposite.Text = _project.EditorLayout?.LegacyComposite != true
                ? L.T("StageUi.Window.CompositeStateLayered")
                : _pipe is null
                    ? L.T("StageUi.Window.CompositeStateWaiting")
                    : _pipe.LegacyComposite
                        ? L.T("StageUi.Window.CompositeStateMerged")
                        : L.T("StageUi.Window.CompositeStateLayeredReason");

            var settings = App.SettingsOf();
            if (settings is not null)
            {
                TglPreviewSubtitles.IsChecked = settings.PreviewSubtitles;
                SelectComboTag(CmbSubContent, settings.Subtitle.Content switch
                {
                    SubContent.Off => "off",
                    SubContent.Trans => "trans",
                    SubContent.Roma => "roma",
                    _ => "both",
                });
                SelectComboTag(CmbSubPos, settings.Subtitle.Pos == SubPos.Top ? "top" : "bottom");
            }

            TglCenterFree.IsChecked = _project.Preset.CenterFree;
            SldCenterGap.Value = Math.Clamp(_project.Preset.CenterGap ?? 30, 10, 70);
            TblCenterGap.Text = $"{Math.Round(SldCenterGap.Value):0}%";
            SelectCenterZone(_project.Preset.CenterZone);

            SelectCanvasPreset(_scene.Width, _scene.Height);
            SyncPvRenderOption();
            UpdateVtsUi();
            RefreshCacheStats();
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

        UpdateRasterLabel();
    }

    private void UpdateRasterLabel()
    {
        if (TblPvRender is null) return;

        var scale = Math.Clamp(_project.LegacyRenderScale, 0.25, 1.0);
        var (w, h) = PvRenderSize(scale);
        string mode;
        string? reason = null;

        if (_pipe is null)
        {
            mode = L.T("StageUi.Window.RasterModePending");
        }
        else if (_pipe.LegacyGpu)
        {
            mode = L.T("StageUi.Window.RasterModeGpu");
        }
        else
        {
            reason = _pipe.LegacyRasterFallback;
            mode = string.IsNullOrWhiteSpace(reason)
                ? L.T("StageUi.Window.RasterModeCpu")
                : L.F("StageUi.Window.RasterModeCpuGpuFallback", reason);
        }

        TblPvRender.Text = L.F("StageUi.Window.RenderSizeSummary", w, h, scale * 100, mode);
        TblPvRender.ToolTip = string.IsNullOrWhiteSpace(reason) ? null : reason;
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
        ShowStatus(L.F("StageUi.Window.RenderResolutionRebuilding", w, h), 5);
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
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 8),
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

            var panel = new StackPanel { Margin = new Thickness(20) };
            panel.Children.Add(title);
            panel.Children.Add(box);
            panel.Children.Add(buttons);

            var dialog = new Wpf.Ui.Controls.FluentWindow
            {
                Owner = this,
                Title = L.T("Dialog.Value"),
                Width = 360,
                MinWidth = 0,
                MinHeight = 0,
                SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                WindowCornerPreference = Wpf.Ui.Controls.WindowCornerPreference.Round,
                Content = panel,
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

            return Task.FromResult(dialog.ShowDialog() == true ? box.Value : (double?)null);
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
            TblCanvasState.Text = L.T("StageUi.Window.CanvasSizeMustBeInteger");
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
            TblCanvasState.Text = L.T("StageUi.Window.CanvasSizeFpsMustBeInteger");
            return;
        }

        w = Math.Clamp(w, 64, 7680) & ~1;
        h = Math.Clamp(h, 64, 7680) & ~1;
        fps = Math.Clamp(fps, 1, 240);

        if (w == _scene.Width && h == _scene.Height && fps == _scene.Fps)
        {
            TblCanvasState.Text = L.F("StageUi.Window.CanvasAlreadyAt", w, h, fps);
            return;
        }

        _scene.Width = w;
        _scene.Height = h;
        _scene.Fps = fps;
        _project.Scene = _scene;
        if (_project.CameraTrack is { } cameraTrack)
        {
            cameraTrack.Fps = fps;
            cameraTrack.Width = w;
            cameraTrack.Height = h;
        }

        foreach (var track in _project.LayerCameraTracks.Values)
            if (track is not null)
            {
                track.Fps = fps;
                track.Width = w;
                track.Height = h;
            }

        TheTimeline.Fps = fps;
        _rebuildPipeKeepTransport = true;
        _rebuildPipe = true;

        var fault = SaveProjectNow();
        TblCanvasState.Text = fault is null
            ? L.F("StageUi.Window.CanvasAppliedRebuilding", w, h, fps)
            : L.F("StageUi.Window.CanvasAppliedSaveFailed", fault);
        ShowStatus(TblCanvasState.Text, 5);
        SyncSettingsPanel();
        SyncPresetControls();
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

    internal void TglLegacyComposite_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingSettings || _project is null) return;

        var layout = _project.EditorLayout ??= new EditorLayout();
        bool on = TglLegacyComposite.IsChecked == true;
        if (layout.LegacyComposite == on) return;

        layout.LegacyComposite = on;
        _saveError = SaveProjectNow();
        _rebuildPipeKeepTransport = true;
        _rebuildPipe = true;
        ShowStatus(on ? L.T("StageUi.Window.MergingLegacyLayers") : L.T("StageUi.Window.SwitchedToLayeredRendering"), 4);
    }

    internal void TglPreviewSubtitles_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncingSettings) return;
        var settings = App.SettingsOf();
        if (settings is null) return;

        settings.PreviewSubtitles = TglPreviewSubtitles.IsChecked == true;
        settings.SaveDebounced();
        ShowStatus(settings.PreviewSubtitles ? L.T("StageUi.Window.SubtitlePreviewEnabled") : L.T("StageUi.Window.SubtitlePreviewDisabled"), 3);
    }

    internal void CmbSubContent_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSettings || CmbSubContent.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        var settings = App.SettingsOf();
        if (settings is null) return;

        settings.Subtitle.Content = tag switch
        {
            "trans" => SubContent.Trans,
            "roma" => SubContent.Roma,
            "both" => SubContent.Both,
            _ => SubContent.Off,
        };
        settings.SaveDebounced();
    }

    internal void CmbSubPos_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingSettings || CmbSubPos.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        var settings = App.SettingsOf();
        if (settings is null) return;

        settings.Subtitle.Pos = string.Equals(tag, "top", StringComparison.Ordinal)
            ? SubPos.Top
            : SubPos.Bottom;
        settings.SaveDebounced();
    }

    private void ApplySavedLayout(Settings? settings)
    {
        var layout = settings?.Layout;
        if (layout is null) return;

        _leftCollapsed = layout.LeftCollapsed;
        _inspectorCollapsed = layout.InspectorCollapsed;
        _timelineCollapsed = layout.TimelineCollapsed;

        LeftColumn.MinWidth = _leftCollapsed ? 0 : 240;
        LeftColumn.Width = new GridLength(_leftCollapsed ? 0 : Math.Clamp(layout.LeftWidth, 240, 1200));
        Left.Visibility = _leftCollapsed ? Visibility.Collapsed : Visibility.Visible;
        LeftSplitter.Visibility = _leftCollapsed ? Visibility.Collapsed : Visibility.Visible;
        LeftSplitterColumn.Width = new GridLength(_leftCollapsed ? 0 : 4);

        InspectorColumn.MinWidth = _inspectorCollapsed ? 0 : 340;
        InspectorColumn.Width = new GridLength(_inspectorCollapsed
            ? 0
            : Math.Clamp(layout.InspectorWidth, 340, 1200));
        InspectorPane.Visibility = _inspectorCollapsed ? Visibility.Collapsed : Visibility.Visible;
        InspectorSplitter.Visibility = _inspectorCollapsed ? Visibility.Collapsed : Visibility.Visible;
        InspectorSplitterColumn.Width = new GridLength(_inspectorCollapsed ? 0 : 6);

        TimelineRow.MinHeight = _timelineCollapsed ? 34 : 80;
        TimelineRow.Height = new GridLength(_timelineCollapsed
            ? 34
            : Math.Clamp(layout.TimelineHeight, 80, 1600));
        TheTimeline.Visibility = _timelineCollapsed ? Visibility.Collapsed : Visibility.Visible;

        TheTimeline.SetHeaderWidth(layout.TimelineHeaderWidth);
    }

    private void SaveWindowLayout()
    {
        var settings = App.SettingsOf();
        if (settings is null) return;
        var layout = settings.Layout ??= new WindowLayout();

        layout.LeftCollapsed = _leftCollapsed;
        layout.InspectorCollapsed = _inspectorCollapsed;
        layout.TimelineCollapsed = _timelineCollapsed;
        if (!_leftCollapsed && LeftColumn.Width.IsAbsolute && LeftColumn.Width.Value > 0)
            layout.LeftWidth = LeftColumn.Width.Value;
        if (!_inspectorCollapsed && InspectorColumn.Width.IsAbsolute && InspectorColumn.Width.Value > 0)
            layout.InspectorWidth = InspectorColumn.Width.Value;
        if (!_timelineCollapsed && TimelineRow.Height.IsAbsolute && TimelineRow.Height.Value > 0)
            layout.TimelineHeight = TimelineRow.Height.Value;
        if (_step >= 0) layout.LeftTab = Math.Clamp(_step, 0, 4);
        layout.TimelineHeaderWidth = TheTimeline.HeaderWidth;
        settings.SaveDebounced();
    }

    private void ToggleInspector()
    {
        _inspectorCollapsed = !_inspectorCollapsed;
        var width = Math.Clamp(App.SettingsOf()?.Layout?.InspectorWidth ?? InspectorWidth, 340, 1200);
        InspectorColumn.MinWidth = _inspectorCollapsed ? 0 : 340;
        InspectorColumn.Width = new GridLength(_inspectorCollapsed ? 0 : width);
        InspectorPane.Visibility = _inspectorCollapsed ? Visibility.Collapsed : Visibility.Visible;
        InspectorSplitter.Visibility = _inspectorCollapsed ? Visibility.Collapsed : Visibility.Visible;
        InspectorSplitterColumn.Width = new GridLength(_inspectorCollapsed ? 0 : 6);
        SaveWindowLayout();
    }

    private void ToggleLeftPanel()
    {
        _leftCollapsed = !_leftCollapsed;
        var width = Math.Clamp(App.SettingsOf()?.Layout?.LeftWidth ?? 320, 240, 1200);
        LeftColumn.MinWidth = _leftCollapsed ? 0 : 240;
        LeftColumn.Width = new GridLength(_leftCollapsed ? 0 : width);
        Left.Visibility = _leftCollapsed ? Visibility.Collapsed : Visibility.Visible;
        LeftSplitter.Visibility = _leftCollapsed ? Visibility.Collapsed : Visibility.Visible;
        LeftSplitterColumn.Width = new GridLength(_leftCollapsed ? 0 : 4);
        SaveWindowLayout();
    }

    private void ToggleTimeline()
    {
        _timelineCollapsed = !_timelineCollapsed;
        var height = Math.Clamp(App.SettingsOf()?.Layout?.TimelineHeight ?? 300, 80, 1600);
        TimelineRow.MinHeight = _timelineCollapsed ? 34 : 80;
        TimelineRow.Height = new GridLength(_timelineCollapsed ? 34 : height);
        TheTimeline.Visibility = _timelineCollapsed ? Visibility.Collapsed : Visibility.Visible;
        SaveWindowLayout();
    }

    private void RefreshMediaPanel()
    {
        var name = string.IsNullOrWhiteSpace(_audioPath) ? L.T("StageUi.Window.NoMusicImported") : Path.GetFileName(_audioPath);
        var meta = _audio.Duration > 0 ? L.F("StageUi.Window.Duration", Fmt(_audio.Duration)) : L.T("StageUi.Window.DropAudioToStart");
        if (_beatMap is { Bpm: > 0 } map)
            meta += L.F("StageUi.Window.BeatInfo", map.Bpm, map.Sections.Count, ChorusCount(map));
        var lyric = _lyrics is null
            ? L.T("StageUi.Window.NoLyricsLoaded")
            : L.F("StageUi.Window.LyricLineCount", _lyrics.Count) + (_tailUncalibrated ? L.T("StageUi.Window.TailUncalibratedNote") : "")
              + L.F("StageUi.Window.PerCharacterMode", (HasBuiltinWords() ? L.T("StageUi.Window.WordsFromFile") : L.T("StageUi.Window.WordsEvenSplit")));

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
