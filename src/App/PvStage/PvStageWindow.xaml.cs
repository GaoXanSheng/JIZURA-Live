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
using JizuraLive.App.Controls;
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

/// <summary>
/// PV 工作台主窗口：舞台直呈 + 图层栈 + 效果架 + 传输条。
/// 纯 Vortice D3D11 现代管线驱动，零旧引擎包袱。
/// </summary>
public partial class PvStageWindow : Window
{
    // 工程文档三件套 + 传输时钟：**不是** readonly——工程新建/打开要整份换掉它们。
    // 场景与效果图是按引用吃进管线与渲染器的（设计画布还在管线构造期定死），所以换工程是
    // "换字段 + 让渲染线程重建管线"，而不是就地改旧对象的列表（就地改会与渲染线程每帧的枚举撞上）。
    private DirectorProject _project;
    private PvScene _scene;
    private EffectGraph _effects;

    // 传输时钟：UI 线程换（换工程时整份换掉），渲染线程每帧读 _audio.Time/Prepare 用——所以是 volatile。
    // 换掉旧实例的那一帧，渲染线程最多多读到一次旧位置；已释放的驱动 Time/Duration 返回零值，不会炸。
    private volatile AudioClockDriver _audio = new();
    private volatile PvPipeline? _pipe;

    // 工程切换请求：UI 线程换完文档对象后置位，渲染线程在下一帧 Prepare 里重建管线。
    // volatile：它同时是那批文档字段的发布栅栏（写入先于置位，渲染线程读到 true 就一定读得到新对象）。
    private volatile bool _rebuildPipe;

    // 音频与歌词的绑定：回写进 _project 后随工程一起落盘，下次打开自动恢复
    private string? _audioPath;
    private string? _lrcText;

    // 歌词（UI 侧）：与交给管线的同一份 KrcLine 列表，时间轴的歌词轨由它转成 TClip。
    private IReadOnlyList<KrcLine>? _lyrics;

    // 歌词条的编辑真相：_lyrics 是导入产物（KrcLine 只有时长），这里冻结成 Clip 序列——
    // 歌词轨的条与动效歌词层都从这一份来，拖动改的就是它。下标与轨上的条一一对应（控件按下标报编辑）。
    private List<LyricClip> _lyricClips = new();

    // 末句是不是"待校准的临时区间"（导入那一刻既没有下一个时间戳、也没有音频时长）。
    // 与 _lyrics 同一次导入产生、同一次导入更新：分开存是因为 KrcLine 里只有时长，装不下这个判定。
    private bool _tailUncalibrated;
    private ComboBox? _cmbLyricStyle;

    private int _saveTick;
    private string? _saveError;
    private System.Windows.Threading.DispatcherTimer? _uiTimer;
    private readonly List<PvLayerConfig> _uiLayers = new();
    private string[] _uiLayerIds = Array.Empty<string>();
    private System.Windows.Point _dragStart;
    private PvLayerConfig? _dragLayer;
    private EffectNode? _dragEffect;

    private EffectNode? _colorNode;
    private EffectParam? _colorParam;
    private Border? _colorSwatch;
    private bool _draggingTime;

    private bool _exporting;        // 导出中：按钮禁用防重入，状态条上的百分比由 RefreshStatus 末尾兜住
    private double _exportPercent;

    // 关键帧曲线：编辑只发生在这一份（UI 线程）上，渲染线程拿到的是 PublishDirector 发布的不可变快照。
    // 换工程时整份重来（旧工程的曲线留在里面，会按同 Id 的新实例地址继续生效）。
    private DirectorEvaluator _evaluator = new();

    // camera.scale 的地址：时间轴在没有曲线时也给它留一条空白轨（双击打第一个 key 的入口）
    private static readonly ParamAddress _camScaleAddr = new(ParamAddressKind.Camera, "", "scale");

    // 时间轴（TimelineView）宿主侧状态：快照只在模型变了时整份重建，播放头每 500ms 原位刷一次
    private TSnapshot? _snap;
    private WaveformPeaks? _peaks;

    // 离线音频装载（波形 + 频带分析）：换音频 / 换工程时取消上一次，两份结果一起作废
    private CancellationTokenSource? _audioLoadCts;

    // 离线频带序列：分析算完发布给管线（蓝图画布的「能量」节点读它）。null = 分析未就绪
    private AudioBandSeries? _audioBands;

    // 一键成片运行态与临时状态条回执
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

    // 属性检查器：null = 没有选中对象，显示工程级信息
    private PvLayerConfig? _selectedLayer;

    private const double TimelineExpandedHeight = 220;
    private const double TimelineCollapsedHeight = 40;
    private bool TimelineExpanded => TimelineRow.Height.Value >= TimelineExpandedHeight;

    // 撤销/重做：栈里放的是工程 JSON 串（与 ProjectStore.Save 同一序列化），深度上限由 UndoLog 管
    private readonly UndoLog _undo = new();

    // 歌词样式下拉的固定项：与 LyricStyle 的四个预设同名（FromName 按名大小写不敏感地取）
    private static readonly string[] LyricStyleNames = ["Classic", "Neon", "Outline", "Minimal"];
    private bool _syncingLyricStyle;  // 程序化回填下拉时为 true：那次 SelectionChanged 不是用户选择

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

        TheTimeline.SeekRequested += Timeline_SeekRequested;
        TheTimeline.EditCommitted += Timeline_EditCommitted;
        TheTimeline.Fps = _scene.Fps;

        BlueprintCanvas.EditCommitted += Blueprint_EditCommitted;

        BuildLayerList();
        BuildEffectRack();
        BuildPickers();
        BuildInspector();
        RefreshTimeline();
        RefreshBlueprint();
        ApplyBlueprintLayout(); // 上次退出时的画布缩放/平移/选中集

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

        // 撤销栈的基线：没有它，第一次编辑之前的那份状态就无处可退
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

    /// <summary>
    /// 把内存工程落盘（.jzpv）。UI 线程专用（调用点都在 UI 线程），失败原因交状态条显示。
    /// _scene / _effects 与 _project 本来就是同一批引用，只需回写两个字符串字段。
    /// </summary>
    private string? SaveProjectNow()
    {
        _project.AudioPath = _audioPath;
        _project.LrcText = _lrcText;
        SyncCurvesToProject();
        SaveBlueprintLayout(); // 画布缩放/平移/选中集顺带落盘（布局不计 dirty、不触发编译）
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
    /// 把工程的信号图整份发布给管线（引用交换，渲染线程下一帧编译）。调用点全在 UI 线程：
    /// 发布出去的那份文档此后谁也不许再改，后续编辑一律"克隆 → 改 → 换引用"。
    /// </summary>
    private void PublishSignalGraph() => _pipe?.SetSignalGraph(_project.SignalGraph);

    /// <summary>
    /// 把离线频带序列整份发布给管线（引用交换，渲染线程下一帧读到）。传 null = 分析未就绪：
    /// 频带输入失效（绑定的目标回落基础值），不是"静音"。
    /// </summary>
    private void PublishAudioBands() => _pipe?.SetAudioAnalysis(_audioBands);

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

    /// <summary>
    /// 建管线并把工程恢复上去。**只在渲染线程调用**（Ready 回调、以及换工程时的 Prepare 回调）：
    /// 管线的 D2D/DWrite/native 资源只许在渲染线程建，旧管线的释放也必须在同一条线程上。
    /// </summary>
    private void BuildPipeline(PresentHost host)
    {
        _pipe?.Dispose();
        _pipe = null;

        _pipe = new PvPipeline(host.Gpu!, _scene, _effects);
        // 工程里存的歌词样式在这里落到提供者上：换工程与撤销走的都是重建管线这条路
        _pipe.Renderer.Lyric?.SetStyle(ProjectLyricStyle());
        // 建完管线就把工程里已有的曲线与信号图带上，否则要等第一次编辑才生效。
        // 绕一下 UI 线程：本回调跑在渲染线程上，而曲线字典与工程文档归 UI 线程所有——
        // 在这里直接读会与用户编辑撞上（枚举中改 = 抛异常）。
        Dispatcher.BeginInvoke(new Action(() =>
        {
            PublishDirector();
            PublishSignalGraph();
            PublishAudioBands();
        }));

        // 万一 VTS 会话在管线之前就开着（不太可能，但保持自洽）：把它的探针接上
        if (_vts is not null) _pipe.SetTracking(_vts.Tracking);

        UpdateBroadcastButton();
        // 沿用设置：上次开着广播的这次开窗自动接上（发送端由管线在渲染线程建）
        if (_broadcastOn) _pipe.RequestBroadcast(true, _spoutName);

        _pipe.LayersChanged += () => Dispatcher.BeginInvoke(() =>
        {
            BuildLayerList();
            PushUndo(); // 管线只在图层集合真的变了时发这个事件（增 / 删 / 重排）
            _saveError = SaveProjectNow();
        });

        // 恢复工程里绑定的音频与歌词。放在这里而不是 OnLoaded：OnLoaded 会 _audio.Play()，
        // 而恢复要停在暂停态，等用户自己点播放。
        //
        // 顺序是硬的：**音频先、歌词后**。末句区间要用音频总长才能收在音频尾，
        // 反过来的话歌词只能拿一个 +3 秒的临时值（并标成待校准）。
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
            else
            {
                // 波形与频带分析都在后台线程做，这里只发起（本回调跑在渲染线程上，不在这里碰 UI）
                var path = _audioPath;
                Dispatcher.BeginInvoke(() =>
                {
                    BuildInspector(); // 检查器音频/时长行是静态内容，恢复音源后重建（否则挂着上一首的读数）
                    StartAudioLoad(path);
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(_lrcText))
        {
            try
            {
                // 解析是纯函数，在哪条线程跑都一样；但时间轴与检查器归 UI 线程，赋值必须绕回去
                // （本回调也在渲染线程上，与上面 PublishDirector 同理）。
                var (lines, tailUncalibrated) = ParseLyrics(_lrcText);
                _pipe.SetLyrics(lines);
                Dispatcher.BeginInvoke(() =>
                {
                    _lyrics = lines;
                    _lyricClips = LrcParser.ToClips(lines);
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

        var msg = $"[PV 舞台] 渲染就绪：{host.Gpu!.AdapterName}";
        Log.Info(msg);
        Console.WriteLine(msg);

        Dispatcher.BeginInvoke(() => TblStageHint.Visibility = Visibility.Collapsed);
    }

    /// <summary>
    /// 渲染线程上换管线（工程新建/打开）。UI 线程只换文档对象 + 置位请求，
    /// 资源的建与拆都留在本线程——与 Stage.Ready/Shutdown 同一条规则。
    /// </summary>
    private void RebuildPipeline(PresentHost host)
    {
        _rebuildPipe = false;
        try
        {
            BuildPipeline(host);
        }
        catch (Exception ex)
        {
            Log.Error("[PV 舞台] 换工程时重建管线失败：" + ex.Message, ex);
            Dispatcher.BeginInvoke(() => TblStatus.Text = "换工程失败：" + ex.Message);
        }
    }

    private void OnStagePrepare(PresentHost host)
    {
        // 换工程：文档对象已经换好，整条管线（提供者表、设计画布、场景与效果图的引用）都是构造期绑定的，
        // 只能在本线程重建一次——UI 线程那一侧只置了一个旗标。
        if (_rebuildPipe) RebuildPipeline(host);

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

        // 选中的图层可能已经不在（删除 / 换工程）：检查器不能继续指着一个不存在的对象
        if (_selectedLayer is not null && !_uiLayers.Contains(_selectedLayer)) _selectedLayer = null;

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

            // 所有图层都给删除入口：旧的「仅 src* 编号才有 ✕」门槛会把模板/旧工程导入的图层
            // （bg/character/lyrics_pv 等固定 id）变成永远删不掉的孤儿，用户明确要能删。
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
                _evaluator.RemoveCurvesForTarget(idRef);
                // 信号绑定同理，与效果实例那条（效果架 ✕）对称：layer 域的存在性只在编译期查一次
                // （SignalTargets 的 HasLayer），图层被摘除后残留的 layer:<id>.* 绑定会让**整张图**
                // 编译失败（target-missing-instance），所有域的信号一起停，还会跟着存盘变成悬空绑定。
                // RemoveTargetCascade 按地址 Target 字符串匹配，对 layer 地址同样成立。
                CascadeSignalTarget(idRef);
                PublishDirector();
                _pipe?.RequestRemove(idRef);
            };
            headRight.Children.Add(del);

            Grid.SetColumn(headRight, 1);
            head.Children.Add(headRight);
            stack.Children.Add(head);

            var meta = new TextBlock
            {
                Text = LayerKindLabel(layer.Kind),
                FontSize = (double)FindResource("PvTextXs"),
                Foreground = (System.Windows.Media.Brush)FindResource("PvFgFaint"),
                Margin = new Thickness(0, 2, 0, 0),
            };
            stack.Children.Add(meta);

            LayersHost.Children.Add(card);
        }

        TblLayerCount.Text = $"{_scene.Layers.Count} 层";
        HighlightLayerCards();
        BuildInspector();
        RefreshBlueprint(); // 图层域的地址候选跟着图层集合走（层增删 = 绑定目标清单变了）
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
        var layer = ((Border)sender).Tag as PvLayerConfig;
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
        _rackRows.Clear();
        // 效果图变了（增删实例 / 换预设）：徽标判定用的编译计划按引用缓存，这里必须整份作废
        _badgeGraph = null;
        _badgePlan = null;
        foreach (var node in _effects.Nodes)
        {
            var def = EffectCatalog.Get(node.DefId);
            if (def is null) continue;

            var card = new Border
            {
                Style = (Style)FindResource("PvCard"),
                AllowDrop = true,
                Cursor = Cursors.SizeAll,
            };
            card.Tag = node;
            card.PreviewMouseLeftButtonDown += EffectCard_MouseDown;
            card.PreviewMouseMove += EffectCard_MouseMove;
            card.DragOver += EffectCard_DragOver;
            card.Drop += EffectCard_Drop;
            card.DragLeave += (s2, _) => ((Border)s2!).BorderBrush = null;
            var stack = new StackPanel();
            card.Child = stack;

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var name = new TextBlock
            {
                Text = EffectName(def),
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
                // 信号绑定同理：留着指向已删实例的绑定，整张图会因目标失效而编译不过，所有信号一起停
                CascadeSignalTarget(nRef.InstanceId);
                PublishDirector();
                PushUndo();
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
                        var addr = new ParamAddress(ParamAddressKind.Effect, node.InstanceId, p.Key);
                        stack.Children.Add(AnimParamRow(_rackRows, addr, ParamLabel(p), p.Min, p.Max, p.Step,
                            () => nodeRef.Value(p), v => nodeRef.Set(p, (float)v)));
                        break;
                    }
                    case EffectParamKind.Bool:
                    {
                        var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        var lab = new TextBlock { Text = ParamLabel(p), Style = (Style)FindResource("PvRowLabel") };
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
                        var lab = new TextBlock { Text = ParamLabel(p), Style = (Style)FindResource("PvRowLabel") };
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

        RefreshParamRows();
        RefreshBlueprint(); // 地址下拉的候选来自效果实例：效果架变了，画布的绑定目标清单就得跟着变
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

        TblColorTarget.Text = $"{EffectName(node)} · {ParamLabel(param)}";
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

    /// <summary>效果显示名：<see cref="EffectDef.NameKey"/> 走词库（界面上一律用它，不显示裸 Id）。</summary>
    private static string EffectName(EffectDef def) => LocName(def.NameKey, def.Id);

    /// <summary>节点显示名；定义已不在目录里（旧存档）时回落到 DefId 本身。</summary>
    private static string EffectName(EffectNode node) =>
        EffectCatalog.Get(node.DefId) is { } def ? EffectName(def) : node.DefId;

    /// <summary>参数标签：<see cref="EffectParam.LabelKey"/> 同理走词库。</summary>
    private static string ParamLabel(EffectParam p) => LocName(p.LabelKey, p.Key);

    /// <summary>
    /// 词库查名。<c>L.T</c> 缺键时原样返回键名（并记一条 Warn），这时退回「去掉域前缀 + 拆驼峰」
    /// 的形态：<c>Fx.GlassShatter</c> → 「Glass Shatter」。缺词库条目（补词库是 i18n 那边的事）时，
    /// 界面至少还是人看得懂的名字，而不是机器键或裸 Id。
    /// </summary>
    private static string LocName(string key, string fallback)
    {
        if (string.IsNullOrWhiteSpace(key)) return Humanize(fallback);

        var text = L.T(key);
        if (!string.Equals(text, key, StringComparison.Ordinal)) return text;
        return Humanize(key[(key.LastIndexOf('.') + 1)..]);
    }

    /// <summary>chromatic → Chromatic；BlockSize → Block Size。</summary>
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

    private void EffectCard_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // 卡片里铺着滑杆与开关：在它们身上按下是改参数，不是拖卡片
        if (e.OriginalSource is DependencyObject d && FindAncestor<System.Windows.Controls.Primitives.RangeBase>(d) is not null) return;
        _dragStart = e.GetPosition(this);
        _dragEffect = ((Border)sender).Tag as EffectNode;
    }

    private void EffectCard_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragEffect is null) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        DragDrop.DoDragDrop((DependencyObject)sender, _dragEffect, DragDropEffects.Move);
        _dragEffect = null;
    }

    private void EffectCard_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(EffectNode)))
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        e.Effects = DragDropEffects.Move;
        ((Border)sender).BorderBrush = (System.Windows.Media.Brush)FindResource("PvAccent");
    }

    /// <summary>落到哪张卡就搬到它的位置（与图层卡同一口径）。顺序 = 执行顺序，改的是效果图本身。</summary>
    private void EffectCard_Drop(object sender, DragEventArgs e)
    {
        var targetCard = (Border)sender;
        targetCard.BorderBrush = null;
        if (!e.Data.GetDataPresent(typeof(EffectNode))) return;
        if (e.Data.GetData(typeof(EffectNode)) is not EffectNode source) return;
        if (targetCard.Tag is not EffectNode target) return;

        // 没动位置就不入撤销栈：一次拖动要么一条历史，要么一条都没有
        if (!_effects.Move(source, _effects.Nodes.IndexOf(target))) return;

        PushUndo();
        _saveError = SaveProjectNow();
        BuildEffectRack();
        TblStatus.Text = $"效果顺序已调整：{EffectName(source)}";
    }

    private void BuildPickers()
    {
        foreach (var def in EffectCatalog.All)
            CmbAddFx.Items.Add($"{EffectName(def)}  ·  {CategoryLabel(def.Category)}");

        // 预设下拉框首项是占位项（不是任何预设）。必须有这么一项：程序化选中是构造期唯一能做的
        // "当前状态"回填，若只能落在某个预设上，那一选就会清空工程里已存的效果图并立刻存盘——
        // 用户上次存的效果图直接消失（EffectPresets.PickerInitialIndex 是同一约定）。
        //
        // 下标映射只认 EffectPresets.PickerLabels/PickerItemAt 这一对（差一个占位项的偏移写在那边），
        // 这里只把显示文本换成解析后的名字——自己再拼一份项列表就等于把偏移规则写了两遍。
        var index = 0;
        foreach (var label in EffectPresets.PickerLabels())
        {
            var preset = EffectPresets.PickerItemAt(index++);
            CmbPreset.Items.Add(preset is null ? label : LocName(preset.LabelKey, preset.Id));
        }

        CmbPreset.SelectedIndex = EffectPresets.PickerInitialIndex;
    }

    private void CmbAddFx_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbAddFx.SelectedIndex < 0) return;
        var def = EffectCatalog.All[CmbAddFx.SelectedIndex];
        CmbAddFx.SelectedIndex = -1;
        _effects.Add(def.Id);
        PushUndo();
        BuildEffectRack();
    }

    private void CmbPreset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 占位项/越界 → 不改动效果图（启动时的初始选中就是这一项）
        var preset = EffectPresets.PickerItemAt(CmbPreset.SelectedIndex);
        if (preset is null) return;

        // 换实例之前先记下旧实例 Id：换完之后它们一个都不在了，挂在它们身上的曲线与信号绑定必须跟着走。
        // 删单个效果那条路径（效果架的 ✕）已经这么做了，预设切换是同一件事的批量版——
        // 漏掉曲线，它们会被下一个同 Id 的新实例继承（还会跟着存盘）；漏掉绑定，
        // 整张信号图会因目标失效而编译不过，所有信号一起停（EffectGraph.Add 每次发新号，旧 Id 必成孤儿）。
        var stale = _effects.Nodes
            .Select(n => n.InstanceId)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList();

        var g = preset.Build();
        _effects.Nodes.Clear();
        foreach (var n in g.Nodes) _effects.Nodes.Add(n);

        foreach (var id in stale)
        {
            _evaluator.RemoveCurvesForTarget(id);
            // 有牵连才重发布（无改动时同一引用重发是空操作）；发布前铺键在管线侧，这里不必重复
            CascadeSignalTarget(id);
        }

        PublishDirector();
        PushUndo();
        _saveError = SaveProjectNow();
        BuildEffectRack();
        TblStatus.Text = SignalStatusText(); // 结论当场可见，不等下一次 500ms 状态刷新
    }

    // ---------------- 歌词样式 ----------------

    /// <summary>工程里生效的歌词样式：自定义优先，否则按名取预设（名字未知/为空回落 Classic）。</summary>
    private LyricStyle ProjectLyricStyle() => _scene.LyricStyle ?? LyricStyle.FromName(_scene.LyricStyleName);

    /// <summary>把下拉回填成工程当前样式名。这是程序化路径，不是一次用户选择。</summary>
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
        _scene.LyricStyle = null; // 选了预设就不再是自定义样式（自定义只可能来自手改的工程文件）
        // 立即生效：管线还没建起来时由建管线那条路带上，不必在这里等
        _pipe?.Renderer.Lyric?.SetStyle(ProjectLyricStyle());

        PushUndo();
        _saveError = SaveProjectNow();
        TblStatus.Text = $"歌词样式：{_scene.LyricStyleName}";
    }

    // ---------------- 信号图（蓝图工作区与状态条共用） ----------------

    /// <summary>
    /// 工程的信号图 → 可改副本。逐条走公开的 TryAddXxx 重建（源图已被 ProjectStore 治理过），
    /// 于是副本自带一份自己的结构版本，而工程的文档对象在采用之前一字不改。
    /// 坐标一并带上：它是布局不是内容，但漏一次就整张图的节点叠到原点。
    /// </summary>
    private static SignalGraph CloneSignalGraph(SignalGraph source)
    {
        var draft = new SignalGraph();

        foreach (var node in source.Nodes)
        {
            if (node is null) continue;
            var copy = new SignalNode
            {
                Id = node.Id,
                Kind = node.Kind,
                Enabled = node.Enabled,
                X = node.X,
                Y = node.Y,
            };
            foreach (var (key, value) in node.Params) copy.Params[key] = value;
            foreach (var (key, text) in node.Texts) copy.Texts[key] = text;
            draft.TryAddNode(copy, out _);
        }

        foreach (var edge in source.Edges)
        {
            if (edge is null) continue;
            draft.TryAddEdge(new SignalEdge
            {
                Id = edge.Id,
                FromNode = edge.FromNode,
                FromPort = edge.FromPort,
                ToNode = edge.ToNode,
                ToPort = edge.ToPort,
            }, out _);
        }

        foreach (var binding in source.Bindings)
        {
            if (binding is null) continue;
            draft.TryAddBinding(new SignalBinding
            {
                Id = binding.Id,
                NodeId = binding.NodeId,
                Address = binding.Address,
            }, out _);
        }

        return draft;
    }

    /// <summary>
    /// 效果实例被删时的信号级联（引擎的 RemoveTargetCascade，对齐效果架里那条 RemoveCurvesForTarget）。
    /// 走"克隆 → 改 → 换引用"而不是就地改：就地改会与渲染线程的编译枚举同一批 List 撞上。
    /// </summary>
    private void CascadeSignalTarget(string instanceId)
    {
        var graph = _project.SignalGraph;
        if (graph is null) return;

        var draft = CloneSignalGraph(graph);
        if (draft.RemoveTargetCascade(instanceId) == 0) return; // 没牵连就不动引用，省掉一次重编

        _project.SignalGraph = draft;
        PublishSignalGraph();
    }

    /// <summary>
    /// 状态条上的信号编译结论。编辑入口（蓝图编辑、预设切换、删效果）改完图必须当场给回执——
    /// 只写日志等于没说：log 不在用户眼前，而"信号是不是全停了"正是他此刻最需要知道的。
    ///
    /// 编的是**工程里已发布**的那张图（渲染线程真正在跑的那一份），与画布上正在编辑的那份分开算。
    /// </summary>
    private string SignalStatusText()
    {
        var graph = _project.SignalGraph;
        if (graph is null || graph.Nodes.Count == 0) return "信号 无";

        var result = SignalCompiler.Compile(graph, _effects, _scene);
        var errors = result.Issues.Where(i => i.Severity == SignalIssueSeverity.Error).ToList();

        if (result.Plan is null)
        {
            // 码是稳定契约，文案可以改：状态条只放第一条码，全量清单在画布的状态条与日志里
            if (errors.Count == 0) return "信号 未通过";
            return errors.Count == 1
                ? $"信号 未通过：{errors[0].Code}"
                : $"信号 未通过：{errors[0].Code} 等 {errors.Count} 条";
        }

        var warnings = result.Issues.Count(i => i.Severity == SignalIssueSeverity.Warning);
        return warnings == 0 ? "信号 正常" : $"信号 正常（注意 {warnings} 条）";
    }

    /// <summary>
    /// 可绑定的参数地址：镜头已注册地址 + 每个图层的已注册地址 + 效果实例的 Number 参数。
    /// 地址文本按 <see cref="ParamAddress"/> 的往返形态拼（镜头 <c>camera.scale</c>、图层 <c>layer:&lt;id&gt;.opacity</c>、
    /// 效果用**实例** Id：同一种效果可以有多个实例，DefId 指不到"哪一个"）。
    /// 只给裸地址：画布把选项文本原样写进绑定，带任何后缀都会变成一个解析不了的地址。
    /// </summary>
    private List<string> SignalAddressOptions()
    {
        var addresses = new List<string>();

        foreach (var name in CameraParamNames)
            AddTargetChoice(addresses, new ParamAddress(ParamAddressKind.Camera, "", name));

        foreach (var layer in _scene.Layers)
            foreach (var name in LayerParamNames)
                AddTargetChoice(addresses, new ParamAddress(ParamAddressKind.Layer, layer.Id, name));

        foreach (var node in _effects.Nodes)
        {
            if (node is null || string.IsNullOrEmpty(node.InstanceId)) continue;
            var def = EffectCatalog.Get(node.DefId);
            if (def is null) continue;

            foreach (var p in def.Params)
                if (p.Kind == EffectParamKind.Number)
                    AddTargetChoice(addresses, new ParamAddress(ParamAddressKind.Effect, node.InstanceId, p.Key));
        }

        return addresses;
    }

    // 注册表的静态参数表（ParamRegistry 认不认由 AddTargetChoice 当场判，这里只负责给候选）
    private static readonly string[] CameraParamNames = ["offsetX", "offsetY", "scale", "rotation"];
    private static readonly string[] LayerParamNames = ["offsetX", "offsetY", "scale", "opacity", "rotation"];

    /// <summary>注册表认过的地址才进下拉——免得"选得到、编译不过"。</summary>
    private void AddTargetChoice(List<string> addresses, in ParamAddress addr)
    {
        if (!ParamRegistry.TryResolve(addr, _effects, _scene, out _, out _)) return;
        addresses.Add(addr.ToString());
    }

    private static string NewSignalId(string prefix) => $"{prefix}_{Guid.NewGuid().ToString("N")[..6]}";

    // ---------------- 撤销 / 重做 ----------------

    /// <summary>编辑动作（打/删/拖 key、滑杆、图层与效果增删、样式切换）之后压一份当前工程快照。</summary>
    private void PushUndo()
    {
        _undo.Push(ProjectSnapshot());
        UpdateUndoButtons();
    }

    /// <summary>
    /// 当前工程的 JSON 串：序列化调用与 ProjectStore.Save 逐参数相同，只是不落盘。
    /// 还原不能借 ProjectStore.Load——它收的是文件路径，而栈里躺的是一段内存字符串。
    /// </summary>
    private string ProjectSnapshot()
    {
        _project.AudioPath = _audioPath;
        _project.LrcText = _lrcText;
        SyncCurvesToProject();
        return JsonConvert.SerializeObject(_project, Formatting.Indented);
    }

    /// <summary>
    /// 快照串 → 工程文档。读不出来返回 null，并把原因写到状态条（<paramref name="what"/> 是"撤销"/"导出"这类前缀）。
    ///
    /// 与 ProjectStore.Load 同一条反序列化（同类型、同默认设置）：Load 收的是文件路径，这里只有内存串。
    /// 快照由本进程自己写出，不再过 Load 的治理与版本闸门。
    /// </summary>
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

    // ---------------- 工程：新建 / 打开 ----------------

    /// <summary>
    /// 新建工程：把当前工作台重置为默认工程。
    ///
    /// 没有"选个路径存默认工程"这一步是有理由的：工作台只会打开固定的会话工程文件
    /// （<see cref="ProjectPath"/> = AppDirs.ConfigDir/pv_project.jzpv），把默认工程写到别的路径，
    /// 等于造一个下次启动永远不会被打开的文件。所以这里是"重置内存工程 + 覆盖会话工程文件"。
    /// </summary>
    private void BtnNewProject_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                "新建工程会把当前工作台重置为默认工程（场景 / 效果 / 曲线 / 信号图全部回到默认）。\n\n"
                + $"当前工程会先备份为 {Path.GetFileName(ProjectPath)}.bak，然后被默认工程覆盖。继续？",
                "PV 工作台", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        AdoptProject(new DirectorProject(), "已新建工程");
    }

    /// <summary>
    /// 打开工程：选一个 .jzpv，整份装进当前工作台。
    ///
    /// 源文件只读不写：装进来之后本会话的存盘落到会话工程文件（<see cref="ProjectPath"/>），
    /// 与"打开歌词/音频"同一条约定（工程里的 AudioPath 同样只存路径原文，不搬文件）。
    /// </summary>
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
            // 打不开要说清原因（版本过新 / JSON 损坏 / 读不出来）：静默什么都不发生，用户只会以为按钮坏了
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

    /// <summary>
    /// 把一份工程装进工作台（新建 / 打开共用；撤销重做也走这条路）。UI 线程专用。
    ///
    /// 为什么不是"就地改旧对象的列表"：场景与效果图按引用吃进管线与渲染器，渲染线程每帧都在枚举它们，
    /// UI 线程就地 Clear/Add 就是与那个枚举撞上（与信号图"克隆 → 换引用"是同一条理由）。
    /// 所以换的是**整份文档对象**，再请渲染线程重建管线——设计画布尺寸也在管线构造期定死，就地改不到。
    /// </summary>
    private void AdoptProject(DirectorProject project, string note, bool resetUndo = true)
    {
        if (!BackupProjectFile()) return;

        var prevAudio = _audioPath;
        _project = project;
        _scene = project.Scene;
        _effects = project.Effects;
        _audioPath = project.AudioPath;
        _lrcText = project.LrcText;

        // 曲线求值器整份重来：旧工程的曲线留在里面，会按同 Id 的新实例地址继续生效（还会跟着存盘）
        _evaluator = new DirectorEvaluator();
        foreach (var save in project.Curves)
        {
            if (save?.Curve is null) continue;
            if (!ParamAddress.TryParse(save.Address, out var addr)) continue;
            _evaluator.SetCurve(addr, save.Curve);
        }

        // 撤销/重做同一音源时不重建时钟，避免 Ctrl+Z 把正在播的音频与已就绪的频带打掉
        bool sameAudio = !resetUndo && !string.IsNullOrWhiteSpace(_audioPath)
                         && string.Equals(prevAudio, _audioPath, StringComparison.OrdinalIgnoreCase)
                         && _audio.Duration > 0;

        if (!sameAudio)
        {
            // 传输时钟换一个干净的：新工程没绑音频时，上一个工程的音频不该接着播。
            // （AudioClockDriver 没有"卸载"接口；它释放后 Time/Duration 返回零值，渲染线程最多多读到一帧旧位置。）
            _audio.Dispose();
            _audio = new AudioClockDriver();

            _peaks = null;
            _audioLoadCts?.Cancel();
            // 频带序列属于旧音频：立刻作废并发布 null，别让渲染线程拿旧频带去配新时钟
            _audioBands = null;
            PublishAudioBands();

            if (!string.IsNullOrWhiteSpace(_audioPath) && File.Exists(_audioPath))
            {
                if (_audio.LoadAudio(_audioPath))
                    StartAudioLoad(_audioPath);
            }
        }

        // UI 侧先清干净：留着旧工程的歌词行/图层卡/画布投影，等于两套工程同时显示在屏幕上
        _lyrics = null;
        _lyricClips = new List<LyricClip>();
        _tailUncalibrated = false;
        if (!string.IsNullOrWhiteSpace(_lrcText))
        {
            try
            {
                var (lines, tailUncalibrated) = ParseLyrics(_lrcText);
                _lyrics = lines;
                _lyricClips = LrcParser.ToClips(lines);
                _tailUncalibrated = tailUncalibrated;
                _pipe?.SetLyrics(lines);
            }
            catch { }
        }

        _uiLayerIds = Array.Empty<string>();
        _selectedLayer = null;
        _colorNode = null;
        _colorParam = null;
        _colorSwatch = null;
        _saveError = null;

        BuildEffectRack();
        BuildLayerList();
        RefreshBlueprint(); // 画布上的节点/连线/绑定与选中集都属于旧工程，必须整份重投影
        TheTimeline.Fps = _scene.Fps;
        RefreshTimeline();
        BuildInspector();

        if (_cmbLyricStyle is not null)
        {
            _syncingLyricStyle = true;
            _cmbLyricStyle.SelectedItem = _scene.LyricStyleName;
            _syncingLyricStyle = false;
        }

        // 管线在跑就请渲染线程重建；还没建起来（Ready 未到）的话它本来就会拿新文档建，不必重复一次
        if (_pipe is not null) _rebuildPipe = true;

        // 换工程不跨工程撤销：栈里那些快照属于上一份文档，留着它们会让 Ctrl+Z 把旧工程整份搬回来
        if (resetUndo)
        {
            _undo.Clear();
            // 只有"新建/打开"恢复画布视图态：布局不参与撤销语义，撤销重做时画布不该跟着跳视图
            ApplyBlueprintLayout();
            PushUndo();
        }

        _saveError = SaveProjectNow(); // 会话工程文件立刻与内存一致，避免"界面上是这个、磁盘上是那个"
        TblStatus.Text = _saveError is null ? note : $"{note}（工程文件写入失败：{_saveError}）";
        Log.Info($"[PV 工作台] {note}（存盘落到 {ProjectPath}）");
    }

    /// <summary>
    /// 覆盖会话工程文件之前留一份 .bak。备份失败要问过用户再继续：
    /// 紧接着就是不可逆的覆盖，静默跳过备份等于把"还能找回来"这件事悄悄拿走。
    /// </summary>
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

    // ---------------- 传输条 ----------------

    /// <summary>
    /// 歌词导入：把音频时长一并交给解析器。
    ///
    /// 为什么必须带时长：末句没有"下一个时间戳"可收边，解析器只能用 <c>start + 3s</c> 这个临时值
    /// （并标 OutOfRange）；只有知道音频总长，末句才能收在音频尾。时长未知（未载入音频）时传 null，
    /// 行为与旧路径逐字段相同，只是末句会被标成待校准。
    ///
    /// 返回的 KrcLine 与 <see cref="LrcParser.Parse"/> 的产物同构（Start + End-Start），
    /// 所以管线既有的消费路径一字不用改。
    /// </summary>
    private (List<KrcLine> Lines, bool TailUncalibrated) ParseLyrics(string text)
    {
        var duration = _audio.Duration;
        var result = LrcParser.ParseClips(text, duration > 0 ? duration : null);

        var lines = new List<KrcLine>(result.Clips.Count);
        foreach (var clip in result.Clips)
            lines.Add(new KrcLine(clip.StartMs, clip.EndMs - clip.StartMs, clip.Text));

        // OutOfRange 只可能落在末句：LrcParser 只在最后一组没有下一个时间戳时才走 TailEnd
        return (lines, result.Clips.Count > 0 && result.Clips[^1].OutOfRange);
    }

    /// <summary>装载一次歌词：解析 → 时间轴歌词轨 + 检查器 → 管线，几处吃同一份，不在别处再解析一遍。</summary>
    private void LoadLyrics(string text)
    {
        var (lines, tailUncalibrated) = ParseLyrics(text);
        _lrcText = text;                    // 绑定进工程：下次打开自动恢复
        _lyrics = lines;
        _lyricClips = LrcParser.ToClips(lines);
        _tailUncalibrated = tailUncalibrated;
        _pipe?.SetLyrics(lines);
        BuildInspector();
        RefreshTimeline();
    }

    private void BtnOpenLyrics_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开歌词文件",
            Filter = "歌词文件 (*.lrc)|*.lrc|所有文件 (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            LoadLyrics(File.ReadAllText(dlg.FileName));
            TblStatus.Text = $"已装载歌词：{_lyrics?.Count ?? 0} 行"
                             + (_tailUncalibrated ? "（末句待校准：未载入音频）" : "");
            Log.Info($"[工作台] 已装载歌词：{_lyrics?.Count ?? 0} 行 ({dlg.FileName})，末句待校准={_tailUncalibrated}");
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
            BuildInspector(); // 检查器的音频/时长行是静态内容，换音源后必须重建（否则挂着上一首的读数）
            StartAudioLoad(dlg.FileName); // 波形轨 + 频带分析：后台解码，到位后自己刷新
            var lrcPath = Path.ChangeExtension(dlg.FileName, ".lrc");
            if (File.Exists(lrcPath))
            {
                try
                {
                    // 音频先就位再解析歌词：末句要用 Duration 收在音频尾，顺序反了只能拍一个 +3s 的临时区间
                    LoadLyrics(File.ReadAllText(lrcPath));
                    Log.Info($"[工作台] 自动加载同名歌词：{_lyrics?.Count ?? 0} 行，末句待校准={_tailUncalibrated}");
                }
                catch { }
            }
            else if (_tailUncalibrated && !string.IsNullOrWhiteSpace(_lrcText))
            {
                // 工程里已有的歌词也重解析一次：时长刚刚才有，末句那个临时区间现在能收准了。
                // 只补待校准的末句——已对齐的歌词不该因为换了个音源就被重排。
                LoadLyrics(_lrcText);
                TblStatus.Text = $"音频已加载 · 末句已对齐音频（{_lyrics?.Count ?? 0} 行歌词）";
            }
        }
        else
        {
            // 失败必须可见：此前无 else，界面永远停在"正在加载音频…"，用户只看到「不能播放」
            TblStatus.Text = $"音频加载失败：{Path.GetFileName(dlg.FileName)}（格式不支持或文件损坏；当前支持 wav/mp3/m4a/aac/wma）";
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

    // BuildPipeline 在渲染线程建管线时也会调到这里：按钮归 UI 线程，跨线程 SetValue 直接抛，
    // 曾让整个 PV 舞台初始化失败——统一在入口编组，调用方不必各自记得绕。
    private void UpdateBroadcastButton()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(UpdateBroadcastButton);
            return;
        }
        BtnBroadcast.Content = _broadcastOn ? "广播 开" : "广播 关";
    }

    // ---------------- 导出 MP4 ----------------

    /// <summary>导出对话框：路径 + 帧率 + 时长都在这一个弹窗里定。确认后交后台线程逐帧渲染。</summary>
    private void BtnExport_Click(object sender, RoutedEventArgs e)
    {
        if (_exporting) return;

        var audioPath = _audioPath;
        var dir = string.IsNullOrWhiteSpace(audioPath) ? null : Path.GetDirectoryName(audioPath);
        var defaultDir = string.IsNullOrEmpty(dir) ? AppDirs.ConfigDir : dir;
        var path = new TextBox
        {
            Text = Path.Combine(defaultDir,
                (string.IsNullOrWhiteSpace(audioPath) ? "pv_export" : Path.GetFileNameWithoutExtension(audioPath)) + ".mp4"),
            Margin = new Thickness(0, 0, 6, 0),
        };

        var browse = new Button { Content = "浏览…", Padding = new Thickness(10, 2, 10, 2) };
        browse.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "导出 MP4",
                Filter = "MP4 视频 (*.mp4)|*.mp4",
                DefaultExt = ".mp4",
                AddExtension = true,
                FileName = Path.GetFileName(path.Text),
                InitialDirectory = Path.GetDirectoryName(path.Text) ?? defaultDir,
            };
            if (dlg.ShowDialog(this) == true) path.Text = dlg.FileName;
        };

        // 无音源时时长为空：那一路要用户自己填秒（帧率同理，默认 60）
        var fps = new TextBox { Text = "60", Width = 72 };
        var seconds = new TextBox
        {
            Text = _audio.Duration > 0 ? _audio.Duration.ToString("F2", CultureInfo.InvariantCulture) : "",
            Width = 72,
        };

        var extra = new StackPanel();
        extra.Children.Add(ExportRow("输出", path, browse));
        extra.Children.Add(ExportRow("帧率", fps));
        extra.Children.Add(ExportRow("时长(秒)", seconds));

        // 默认不勾：预览侧没有字幕源（舞台的图层栈与广播里都没有这一层），勾上就多出一层预览里看不到的东西。
        // 「预览 = 成片」是这个工作台的前提，所以烧入必须是显式的一次选择，而不是默认塞进去。
        var burnSubtitles = new CheckBox
        {
            Content = "烧入清晰字幕（预览不显示字幕层）",
            IsChecked = false,
        };
        extra.Children.Add(burnSubtitles);

        MessageDialog.Confirm(this, "导出 MP4",
            "逐帧离屏渲染当前工程，导出期间界面照常用。含图层与效果、曲线/信号驱动的镜头与图层动画、"
            + "动效歌词；字幕默认不烧入（预览里也没有字幕层，见下方开关）；VTS 姿态追踪不参与导出。",
            "开始导出",
            () =>
            {
                // 弹窗是先关再回调，所以这里校验不出结果就没法把错误留在框里：交状态条说
                if (string.IsNullOrWhiteSpace(path.Text)
                    || !int.TryParse(fps.Text.Trim(), out var framesPerSecond)
                    || !double.TryParse(seconds.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var sec))
                {
                    TblStatus.Text = "导出未开始：输出路径不能为空，帧率与时长都必须是数字";
                    return;
                }

                StartExport(path.Text.Trim(), framesPerSecond, sec, burnSubtitles.IsChecked == true);
            },
            extra);
    }

    private async void StartExport(string outPath, int fps, double seconds, bool burnSubtitles)
    {
        _exporting = true;
        BtnExport.IsEnabled = false;
        _exportPercent = 0;
        TblStatus.Text = ExportText();

        // 快照交给后台：导出要跑几分钟，活动文档不能让导出线程去枚举（这期间 UI 照样能改图层）
        var copy = ProjectFromSnapshot(ProjectSnapshot(), "导出");
        var inputs = copy is null ? null : ExportInputs(copy, burnSubtitles);
        var progress = new Progress<double>(p =>
        {
            _exportPercent = p;
            if (_exporting) TblStatus.Text = ExportText();
        });

        var fault = copy is null
            ? "导出失败：工程快照读不出来"
            : await Task.Run(() => PvExporter.Export(copy.Scene, copy.Effects, seconds, fps, outPath, progress, inputs));

        _exporting = false;
        BtnExport.IsEnabled = true;
        TblStatus.Text = fault ?? $"导出完成：{outPath}";
    }

    /// <summary>
    /// 导出要消费的帧状态链：与舞台同一批来源——快照里的曲线/信号图（导出线程独占，不必再拷）、
    /// 本会话的歌词条与频带序列（换引用发布后不再改，跨线程只读）、以及设置里的字幕配置。
    /// 快照读不出来（<paramref name="copy"/> 为 null）时调用方不会走到这里。
    /// <paramref name="burnSubtitles"/> = false 时整层不建（字幕不是默认内容，见导出弹窗的开关）。
    /// </summary>
    private PvExportInputs ExportInputs(DirectorProject copy, bool burnSubtitles)
    {
        var curves = new DirectorEvaluator();
        foreach (var save in copy.Curves)
        {
            if (save?.Curve is null) continue;
            if (!ParamAddress.TryParse(save.Address, out var addr)) continue;
            curves.SetCurve(addr, save.Curve);
        }

        return new PvExportInputs(
            Curves: curves,
            Signals: copy.SignalGraph,
            Bands: _audioBands,
            Clips: _lyricClips,
            Subtitles: burnSubtitles ? App.SettingsOf()?.Subtitle : null);
    }

    private string ExportText() => $"导出中… {_exportPercent * 100:F0}%";

    /// <summary>导出一行：标签 + 输入控件（+ 可选尾按钮）。</summary>
    private FrameworkElement ExportRow(string label, FrameworkElement input, FrameworkElement? tail = null)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock
        {
            Text = label,
            Style = (Style)FindResource("PvRowLabel"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(text, 0);
        row.Children.Add(text);

        Grid.SetColumn(input, 1);
        row.Children.Add(input);

        if (tail is not null)
        {
            tail.Margin = new Thickness(6, 0, 0, 0);
            Grid.SetColumn(tail, 2);
            row.Children.Add(tail);
        }

        return row;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        // 焦点在输入框里就不拦：那里的空格 / Ctrl+S / Ctrl+Z / Ctrl+Y 是文本框自己的行为
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


    // ---------------- VTS 会话 ----------------

    /// <summary>
    /// 一个按钮包办会话全周期：关 → 连接中 → 授权中 → 追踪中；任意状态下再点一次都是关。
    /// 只有授权中那两态例外——那里「再点一次」是重发授权请求（VTS 侧弹框，用户点「允许」才继续）。
    /// 连接失败不占一个状态：会话开着就一直在退避重连（VtsApi.RetryMs），失败原因由状态条说出来，
    /// 用户不用为了重试先关一次。
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
        else if (vts.State is VtsState.NeedAuth or VtsState.AwaitApproval)
        {
            vts.RequestAuth();
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

    /// <summary>追踪是否正在出姿态：探针新鲜（≤ StaleMs）且认出人脸。判龄口径与渲染帧同源。</summary>
    private static bool PoseLive(VtsApi vts) =>
        vts.Tracking.TryRead(VtsTrackingProbe.StaleMs, out var st) && st.FaceFound;

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
                if (string.IsNullOrWhiteSpace(err)) return "VTS 连接中…";
                return $"VTS 连接失败（自动重试）：{(err.Length > 60 ? err[..60] : err)}";
            }
            case VtsState.NeedAuth:
                return "VTS 待授权：点 VTS 按钮，再在 VTube Studio 里点「允许」";
            case VtsState.AwaitApproval:
                return "VTS 等待授权…（在 VTube Studio 里点「允许」）";
            case VtsState.Ready:
                return PoseLive(vts)
                    ? "VTS 追踪中"
                    : $"VTS 追踪失效（丢脸或停更 >{VtsTrackingProbe.StaleMs}ms）· 镜头回基础取景";
            default:
                return "VTS 未连接";
        }
    }

    private string VtsButtonText()
    {
        var vts = _vts;
        if (vts is null) return "VTS 关";
        return vts.State switch
        {
            VtsState.NeedAuth or VtsState.AwaitApproval => "VTS 授权中",
            VtsState.Ready => PoseLive(vts) ? "VTS 追踪中" : "VTS 已连接",
            _ => "VTS 连接中",
        };
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

        // 图片与视频入口都与 Spout 段并列，且**不受**"没有发送端"影响：它们不需要发送端。
        // 原来整张菜单在没有 Spout 源时直接不弹，这些层就永远加不进来。
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

    /// <summary>
    /// 加一层磁盘图片：只投递路径，解码与位图建立由管线在渲染线程上做（D2D 位图必须与绘制同线程建）。
    /// 图层配置由管线按同一条注册路径生成（Id 接着 src_&lt;n&gt; 编号、默认为视觉素材层），
    /// 这里再造一份配置就是第二个真相；加完由 LayersChanged 回调重列图层并落盘。
    /// </summary>
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

    /// <summary>加一层磁盘视频：与图片层同一条投递路径（解码器与纹理都由管线在渲染线程上建）。</summary>
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
        PlayIcon.Data = (System.Windows.Media.Geometry)FindResource(_audio.IsPlaying ? "IconPause" : "IconPlay");

    private void SldTime_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_draggingTime) return;
        _audio.Seek(e.NewValue);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _draggingTime = false;
        // 松手即结束拖动手势。不清的话，一次「按下卡片就松开」会把待拖对象留在字段里，
        // 之后在卡片内的滑杆上按下（MouseDown 被滑杆挡掉、字段没被改写）再一移动就够到拖动阈值，
        // 于是改参数变成重排卡片。
        _dragEffect = null;
        _dragLayer = null;
        base.OnPreviewMouseLeftButtonUp(e);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (SldTime.IsMouseOver)
        {
            _draggingTime = true;
            // 点轨道=直接跳到该位置：PvSlider 模板把轨道做成 RepeatButton，点击会被吃成 LargeChange 步进（一次只挪一小步）
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

        // 检查器每行都显示"当前生效值"：曲线/信号随播放头走，没有的显示文档常量。
        // 用户正按着滑杆时不同步（否则刚拖到一半就被拉回去）。
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
            // 信号结论并进这条常驻状态行：编辑时写一次会被下一次 500ms 刷新抹掉，只有并进来才是"一直看得见"
            TblStatus.Text = $"效果 {pipe.Renderer.EffectCount} 段 · 图层 {_scene.Layers.Count} · {srcTxt}"
                             + $" · {SignalStatusText()}";

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

        // 临时状态（如「可一键成片」、「一键成片完成」等回执）：倒计时内优先展示
        if (_transientStatus is not null)
        {
            if (--_transientStatusTicks <= 0)
                _transientStatus = null;
            else
                TblStatus.Text = _transientStatus;
        }

        // 导出进度写在这条链的最后：上面每 500ms 一次的状态行刷新会把刚显示的百分比抹掉
        if (_exporting) TblStatus.Text = ExportText();

        UpdatePlayIcon();
    }

    private static string Fmt(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)ts.TotalMinutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 100}";
    }

    // ---------------- 工具栏命令 ----------------

    /// <summary>保存：与 5 秒自动存盘同一条路径，失败原因留在状态条上。</summary>
    private void SaveNow()
    {
        _saveError = SaveProjectNow();
        TblStatus.Text = _saveError is null ? "工程已保存" : $"保存失败：{_saveError}";
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e) => SaveNow();

    private void BtnUndo_Click(object sender, RoutedEventArgs e) => UndoRedo(true);

    private void BtnRedo_Click(object sender, RoutedEventArgs e) => UndoRedo(false);

    /// <summary>撤销/重做按钮的禁用态跟着栈走。</summary>
    private void UpdateUndoButtons()
    {
        BtnUndo.IsEnabled = _undo.CanUndo;
        BtnRedo.IsEnabled = _undo.CanRedo;
    }

    private void BtnFold_Click(object sender, RoutedEventArgs e)
    {
        var expanded = !TimelineExpanded;
        TimelineRow.Height = new GridLength(expanded ? TimelineExpandedHeight : TimelineCollapsedHeight);
        FoldIcon.Data = System.Windows.Media.Geometry.Parse(expanded
            ? "M3.6 6.4 L8 10.8 L12.4 6.4"
            : "M3.6 9.6 L8 5.2 L12.4 9.6");
        BtnFold.ToolTip = expanded ? "折叠时间轴" : "展开时间轴";
    }

    private void BtnAuto_Click(object sender, RoutedEventArgs e)
    {
        AutoDirectorPop.IsOpen = !AutoDirectorPop.IsOpen;
    }

    private void SldAutoIntensity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TblAutoIntensity is not null)
            TblAutoIntensity.Text = e.NewValue.ToString("F2", CultureInfo.InvariantCulture);
    }

    /// <summary>一键成片：后台节拍与能量分析 → 自动导演生成 → 单次可撤销事务 → 刷新时间轴与检查器。</summary>
    private async void BtnGenerateAuto_Click(object sender, RoutedEventArgs e)
    {
        if (_autoGenerating) return;

        var audioPath = _audioPath;
        var duration = _audio.Duration;
        var existingAnalysis = _audioBands;

        if (string.IsNullOrWhiteSpace(audioPath) && existingAnalysis == null)
        {
            ShowStatus("一键成片失败：请先导入音频");
            return;
        }

        _autoGenerating = true;
        BtnGenerateAuto.IsEnabled = false;
        BtnGenerateAuto.Content = "生成中…";
        ShowStatus("一键成片：正在分析音频节拍与能量…");

        string style = "dynamic";
        if (CmbAutoStyle.SelectedItem is ComboBoxItem item && item.Tag is string s)
            style = s;
        double intensity = SldAutoIntensity.Value;
        var options = new AutoOptions { Style = style, Intensity = intensity };

        try
        {
            var (beatMap, analysisResult, error) = await Task.Run<(BeatMap? BeatMap, AudioAnalysis? Analysis, string? Error)>(() =>
            {
                try
                {
                    AudioAnalysis? analysis = existingAnalysis;
                    if (analysis is null)
                    {
                        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
                            return (null, null, "音频文件不存在，无法分析");
                        analysis = AudioFileAnalyzer.Analyze(audioPath, CancellationToken.None);
                        if (analysis is null)
                            return (null, null, "音频频带分析失败");
                    }

                    double d = duration;
                    if (d <= 0 && analysis.Count > 0 && analysis.StepSeconds > 0)
                        d = analysis.Count * analysis.StepSeconds;

                    if (d <= 0)
                        return (null, analysis, "音频时长为 0，无法分析节拍");

                    var map = BeatAnalysis.Analyze(analysis, d);
                    return (map, analysis, null);
                }
                catch (Exception ex)
                {
                    return (null, null, ex.Message);
                }
            });

            if (beatMap is null)
            {
                ShowStatus($"一键成片失败：{error ?? "未知错误"}");
                return;
            }

            if (_audioBands is null && analysisResult is AudioBandSeries abs)
            {
                _audioBands = abs;
                PublishAudioBands();
                RefreshParamRows();
            }

            // UI 线程调 AutoDirector.Generate 写进工程
            var report = AutoDirector.Generate(_project, beatMap, options);

            // 同步工程曲线至 _evaluator 并发布给管线
            _evaluator = new DirectorEvaluator();
            foreach (var save in _project.Curves)
            {
                if (save?.Curve is null) continue;
                if (!ParamAddress.TryParse(save.Address, out var addr)) continue;
                _evaluator.SetCurve(addr, save.Curve);
            }
            PublishDirector();
            PublishSignalGraph();

            // 刷新效果架、图层、蓝图画布、时间轴与检查器
            BuildEffectRack();
            BuildLayerList();
            RefreshBlueprint();
            TheTimeline.Fps = _scene.Fps;
            RefreshTimeline();
            BuildInspector();

            if (_cmbLyricStyle is not null)
            {
                _syncingLyricStyle = true;
                _cmbLyricStyle.SelectedItem = _scene.LyricStyleName;
                _syncingLyricStyle = false;
            }

            // 一次 PushUndo（整次生成一条撤销）
            PushUndo();

            // 关闭小面板并给出状态条回执
            AutoDirectorPop.IsOpen = false;
            var note = $"一键成片完成：相机 {report.CameraKeys} 键 · 特效 {report.EffectInstances} 个 · 曲线 {report.CurveKeys} 键";
            ShowStatus(note);
            Log.Info($"[PV 工作台] {note}");
        }
        catch (Exception ex)
        {
            ShowStatus($"一键成片失败：{ex.Message}");
            Log.Warn("[PV 工作台] 一键成片异常：" + ex);
        }
        finally
        {
            _autoGenerating = false;
            BtnGenerateAuto.IsEnabled = true;
            BtnGenerateAuto.Content = "生成";
        }
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
