using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using JizuraLive.Spout;
using JizuraLive.Vts;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JizuraLive.Pv;

/// <summary>
/// PV 管线的所有者：场景文档 + 图层栈 + 效果图 + 各图层提供者。
/// 全链路纯 GPU，零线程锁，支持工作台直呈和 Spout 外部广播。
/// 持久化不在这里：工程文件（.jzpv）的读写归 <see cref="ProjectStore"/>，管线只管渲染态。
/// </summary>
public sealed class PvPipeline : IDisposable
{
    private readonly D2DHost _d2d;
    private readonly PvStageRenderer _renderer;
    private readonly PvScene _scene;
    private readonly EffectGraph _effects;

    // 跨线程交接只有这一条命令通道：UI 投递，渲染线程在帧首消费（图层提供者的 COM 对象必须在渲染线程建）。
    private readonly ConcurrentQueue<string> _pendingAdds = new();
    private readonly ConcurrentQueue<string> _pendingImageAdds = new();
    private readonly ConcurrentQueue<string> _pendingVideoAdds = new();
    private readonly ConcurrentQueue<string> _pendingRemoves = new();
    private volatile string[]? _pendingOrder;

    private readonly List<SpoutSourceProvider> _spoutProviders = new();
    private volatile bool _stopPumping;
    private SubtitleProvider? _subtitle;
    private SpoutSourceProvider? _character;
    private int _layerSeq;
    private bool _disposed;

    // 广播发送端：native 资源，只在渲染线程建立与释放（Prepare / Dispose）。
    // UI 线程投递请求（_pendingBroadcast）、读回执（BroadcastInfo），不碰发送端本身。
    private SpoutOut? _broadcast;
    private volatile BroadcastRequest? _pendingBroadcast;

    // 引用类型 + volatile：UI 线程写、渲染线程读的跨线程投递必须能整份发布（Nullable<struct> 不能用 volatile）
    private sealed record BroadcastRequest(bool Enable, string Name);

    /// <summary>广播回执：Enabled = 用户开着；Active = native 发送端已建立（≠ 下游已收到）。</summary>
    public readonly record struct BroadcastState(bool Enabled, bool Active, long Sent, long Failed, string? LastError);

    // 探针由 VTS 会话（VtsApi）拥有，会话一生一死、只能 new 新实例重连：
    // UI 线程换引用、渲染线程下一帧读到；引用交换原子，不做锁。
    private volatile VtsTrackingProbe? _tracking;
    private VtsTrackingOptions _trackOptions = new();
    private bool _prevBlink;

    private const double TrackingCameraFrac = 0.10;

    // 关键帧曲线：UI 线程发布不可变快照（DirectorEvaluator.Snapshot），渲染线程下一帧读到；
    // 引用交换原子，无锁。快照发布后永不修改，所以渲染线程读它是安全的。
    private volatile DirectorEvaluator? _director;

    // 音频频带序列：与 _director 同款引用交换（UI 线程发布、渲染线程每帧查询，原子无锁）。
    private volatile AudioBandSeries? _audioBands;

    // 信号图（M6）：UI 线程发布整份文档，渲染线程在 Prepare 里编译 + 求值。
    // 与 _director 同款引用交换（原子、无锁）。约定：发布出去的那份文档此后不许再改——
    // 面板改的始终是它自己的草稿，确认时才整份换引用，所以渲染线程不会读到半更新的图。
    private volatile SignalGraph? _signalGraph;
    private CompiledSignalGraph? _signalPlan;   // 渲染线程私有
    private SignalGraph? _signalPlanGraph;      // 计划的来源引用（判断是否需要重编），同样渲染线程私有

    // Time 节点的 Live 口径要一个暂停也在走的单调时钟：进程启动即开始，与设备/系统时间无关
    private static readonly Stopwatch Monotonic = Stopwatch.StartNew();

    /// <summary>镜头缩放的参数地址（文本形式 <c>camera.scale</c>）。地址是值相等类型，静态构造一次即可。</summary>
    private static readonly ParamAddress CameraScaleAddr =
        new(ParamAddressKind.Camera, "", "scale");

    public PvScene Scene => _scene;
    public EffectGraph Effects => _effects;
    public PvStageRenderer Renderer => _renderer;

    public bool Ready { get; private set; }
    public string? Error { get; private set; }

    public (int Live, int Total) SpoutState =>
        (_spoutProviders.Count(p => p.Connected), _spoutProviders.Count);

    public string? CharacterSourceSummary =>
        _character is null ? null
        : _character.Connected ? $"{_character.SourceWidth}×{_character.SourceHeight}"
        : _character.Sender;

    public string SubtitleSummary =>
        _subtitle is null ? "无字幕层" : "字幕 就绪";

    /// <summary>开关广播（任意线程）：渲染线程在下一帧 Prepare 里消费（native 对象必须在本线程建）。</summary>
    public void RequestBroadcast(bool enable, string name) =>
        // 名字在这里就定成非空（空/纯空白交给 SpoutOut 兜默认名）
        _pendingBroadcast = new BroadcastRequest(enable, name?.Trim() ?? "");

    /// <summary>广播回执快照（UI 状态条用，任意线程可读）。</summary>
    public BroadcastState BroadcastInfo
    {
        get
        {
            var b = _broadcast;
            var pending = _pendingBroadcast;
            return new BroadcastState(
                b is not null || pending is { Enable: true },
                b?.IsActive ?? false,
                b?.SentCount ?? 0,
                b?.FailedCount ?? 0,
                b?.LastError);
        }
    }

    public event Action? LayersChanged;

    public PvPipeline(D3DDevice gpu, PvScene scene, EffectGraph effects,
        IPvSubtitleSource? subtitleSource = null, VtsTrackingProbe? tracking = null)
    {
        _scene = scene;
        _effects = effects;
        _tracking = tracking;
        SeedLayerSeq();

        try
        {
            _d2d = D2DHost.Create(gpu);
            _renderer = new PvStageRenderer(gpu, _d2d, scene, effects, scene.Width, scene.Height);
            RegisterProviders(gpu, subtitleSource);
            Ready = true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Error("[PV 管线] 初始化失败：" + ex.Message, ex);
            throw;
        }
    }

    public void SetLyrics(IReadOnlyList<JizuraLive.Engine.KrcLine>? lines) =>
        _renderer.SetLyrics(lines);

    /// <summary>
    /// 换追踪探针（任意线程）。探针实例随 VTS 会话一起重建，UI 线程换引用、渲染线程下一帧读到；
    /// 引用交换原子，不做锁。传 null = 断开 VTS，镜头回场景基础值。
    /// </summary>
    public void SetTracking(VtsTrackingProbe? probe) => _tracking = probe;

    /// <summary>
    /// 换导演求值器快照（任意线程）。UI 线程在曲线上改完打一份不可变快照发布过来，渲染线程下一帧读到；
    /// 引用交换原子，无锁。传 null = 关掉曲线驱动，镜头回场景基础值。
    /// </summary>
    public void SetDirector(DirectorEvaluator? d) => _director = d;

    /// <summary>
    /// 换音频频带分析序列（任意线程）。后台把整段音频算完（见 <see cref="AudioFileAnalyzer.Analyze"/>）
    /// 再发布过来，渲染线程每帧按 transport 时刻查询；引用交换原子，无锁。
    /// 传 null = 还没有分析结果，频带输入失效（Valid=false ⇒ 绑定的目标回落到基础值），不是"静音"。
    /// </summary>
    public void SetAudioAnalysis(AudioBandSeries? series) => _audioBands = series;

    /// <summary>
    /// 取本帧的频带快照：<paramref name="series"/> 未就绪返回 Valid=false（不是四个 0）。
    ///
    /// 只读、纯函数：同一时刻在任何预览帧率下逐位相同（序列侧的窗口与插值规则见
    /// <see cref="AudioBandSeries.Sample"/>），暂停/seek 也不会因为"上次查过哪"而不同。
    /// </summary>
    public static AudioBandSnapshot SampleAudioBands(AudioBandSeries? series, double transportSeconds)
    {
        if (series is null || !series.Sample(transportSeconds, out var bands)) return default;

        return new AudioBandSnapshot(bands.Valid, bands.Rms, bands.Bass, bands.Mid, bands.High);
    }

    /// <summary>
    /// 换信号图（任意线程）。UI 线程在信号面板里确认后把整份文档发布过来，渲染线程下一帧编译并采用；
    /// 引用交换原子，无锁。传 null = 关掉信号（效果参数回常量/曲线）。
    ///
    /// 发布前先给每条绑定要写的参数**铺键**（见 <see cref="SeedBoundParamKeys"/>）：这条是"发布"的一部分，
    /// 不能留给调用方——工程恢复（OnStageReady）与效果实例级联删除两条路径不发这一步的话，
    /// 第一条被帧内写入的新键就会在渲染线程上扩容字典。
    /// </summary>
    public void SetSignalGraph(SignalGraph? graph)
    {
        SeedBoundParamKeys(graph);
        _signalGraph = graph;
    }

    public void Prepare(double time)
    {
        ApplyLayerOps();
        ApplyBroadcast();

        if (!_stopPumping)
            foreach (var p in _spoutProviders) p.PumpFrame();

        ApplyTracking(time);

        // 信号在追踪之后（能量源 Stage.Energy 是 ApplyTracking 写的），在 _renderer.Prepare 之前
        // （效果参数就是在那一句里被打包成常量缓冲的）。顺序反了，本帧读到的要么是上一帧的能量，要么是上一帧的参数。
        ApplySignals(time);

        // 字幕与歌词都在设计画布上求值，窗口尺寸不参与排版
        _subtitle?.Render(time, _renderer.DesignWidth, _renderer.DesignHeight);
        _renderer.Lyric?.SetScale(1.0);
        _renderer.Prepare(time);
    }

    public void Composite(ID2D1Bitmap1 target, int width, int height) =>
        _renderer.Composite(target, width, height);

    public PvStageRenderer.SharedTarget OpenSharedTarget(IntPtr shareHandle, int w, int h) =>
        _renderer.OpenSharedTarget(shareHandle, w, h);

    public ID2D1DeviceContext D2DContext => _renderer.D2DContext;

    public void RequestAdd(string sender) => _pendingAdds.Enqueue(sender);

    /// <summary>
    /// 新增一层磁盘图片图层（任意线程）：只投递路径，解码与位图建立留给渲染线程
    /// （D2D 位图必须与绘制同线程建）。空路径当场丢弃——建出一层永远没有内容的图层不如不建。
    /// </summary>
    public void RequestAddImage(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return;
        _pendingImageAdds.Enqueue(imagePath);
    }

    /// <summary>
    /// 新增一层视频图层（任意线程）：只投递路径，解码器与纹理的建立在渲染线程上做
    /// （D2D 位图必须与绘制同线程建）。空路径当场丢弃——同 RequestAddImage。
    /// </summary>
    public void RequestAddVideo(string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath)) return;
        _pendingVideoAdds.Enqueue(videoPath);
    }

    public void RequestRemove(string id) => _pendingRemoves.Enqueue(id);
    public void RequestOrder(IReadOnlyList<string> idsTopFirst) =>
        _pendingOrder = [.. idsTopFirst];

    /// <summary>
    /// 停止取帧（停机握手的第一步，可在任意线程调用）。
    /// 原生接收没有超时接口，一旦进去就只能等它自己出来，所以只能保证「不再进去新的」。
    /// 同时冻结效果链的末帧：合成到此为止，最后一张 fx 输出不还池，留给管线释放时统一收。
    /// </summary>
    public void StopPumping()
    {
        _stopPumping = true;
        _renderer.FreezeFrames();
    }

    /// <summary>已观察到的最长一次取帧耗时（毫秒）；停机实测用它判断原生接收会不会把收尾卡住。</summary>
    public double MaxReceiveMs
    {
        get
        {
            var max = 0.0;
            foreach (var p in _spoutProviders) max = Math.Max(max, p.MaxReceiveMs);
            return max;
        }
    }

    public static bool WantsSpout(PvLayerConfig layer) =>
        layer.Kind == PvLayerKind.VisualAsset
        && !string.IsNullOrWhiteSpace(layer.SpoutSenderName);

    /// <summary>该层是否由视频文件驱动（仅视觉素材层；三源互斥，Spout 已占用时不算）。</summary>
    public static bool WantsVideo(PvLayerConfig layer) =>
        layer.Kind == PvLayerKind.VisualAsset
        && string.IsNullOrWhiteSpace(layer.SpoutSenderName)
        && !string.IsNullOrWhiteSpace(layer.VideoPath);

    /// <summary>该层是否由磁盘图片驱动（仅视觉素材层；三源互斥，Spout/Video 已占用时不算）。</summary>
    public static bool WantsImage(PvLayerConfig layer) =>
        layer.Kind == PvLayerKind.VisualAsset
        && string.IsNullOrWhiteSpace(layer.SpoutSenderName)
        && string.IsNullOrWhiteSpace(layer.VideoPath)
        && !string.IsNullOrWhiteSpace(layer.ImagePath);

    public static PvLayerConfig NewSourceLayer(string id, string sender) => new()
    {
        Id = id,
        Name = sender,
        Kind = PvLayerKind.VisualAsset,
        SpoutSenderName = sender,
        Cover = true,
        Parallax = new PvParallaxConfig { Depth = 0.9 },
        VisualBlend = new PvVisualBlendConfig { AmbientTintIntensity = 0.18, RimGlow = 0.12 },
    };

    /// <summary>新建一层图片图层：名字取文件名（不含扩展名）；取景默认铺满（Cover），与 Spout 层同档。</summary>
    public static PvLayerConfig NewImageLayer(string id, string imagePath) => new()
    {
        Id = id,
        Name = Path.GetFileNameWithoutExtension(imagePath),
        Kind = PvLayerKind.VisualAsset,
        ImagePath = imagePath,
        // 必须显式清掉：PvLayerConfig 的 SpoutSenderName 默认值是 "VTubeStudio"，
        // 留着它会让这层在界面里显示成 VTubeStudio，还会把真正的 VTubeStudio 源挡在"已在图层里"之外
        SpoutSenderName = null,
        Cover = true,
        Parallax = new PvParallaxConfig { Depth = 0.9 },
        VisualBlend = new PvVisualBlendConfig { AmbientTintIntensity = 0.18, RimGlow = 0.12 },
    };

    /// <summary>新建一层视频图层：与图片层同档（名字取文件名、默认铺满）；同样必须显式清掉 SpoutSenderName。</summary>
    public static PvLayerConfig NewVideoLayer(string id, string videoPath) => new()
    {
        Id = id,
        Name = Path.GetFileNameWithoutExtension(videoPath),
        Kind = PvLayerKind.VisualAsset,
        VideoPath = videoPath,
        SpoutSenderName = null,
        Cover = true,
        Parallax = new PvParallaxConfig { Depth = 0.9 },
        VisualBlend = new PvVisualBlendConfig { AmbientTintIntensity = 0.18, RimGlow = 0.12 },
    };

    private void RegisterProviders(D3DDevice gpu, IPvSubtitleSource? subtitleSource)
    {
        var tint = PvScene.ParseHex(_scene.AmbientTint);
        _renderer.Stage.Register(new ColorProvider("bg",
            new Color4(tint.R * 0.16f, tint.G * 0.16f, tint.B * 0.22f, 1f)));

        foreach (var layer in _scene.Layers) RegisterLayerProvider(layer);

        if (subtitleSource is not null)
        {
            _subtitle = new SubtitleProvider("subtitle_top", gpu, _d2d.Ctx, subtitleSource);
            // 清晰字幕挂在全局后期**之后**的叠加槽上，不注册进 Stage：进了图层栈就会被视差与场景模糊带走，
            // 同一帧还会被泵两次（按层一次 + Overlay 一次）。净效果就是"按层泵图跳过字幕"——
            // 场景里那层 subtitle_top 在本帧按"没有提供者"缺席（PvStage.RenderFrame 的取提供者那一步）。
            // 所有权随之转移给本类：注册在 Stage 上的提供者都归 Stage 释放，字幕是唯一不在其中的那个。
            // 契约 PV-DIRECTOR-SYSTEM.md:399「字幕不经过视差/场景模糊」。
            _renderer.Overlay = _subtitle;
        }
    }

    /// <summary>
    /// 建某层的提供者并注册进舞台（只在渲染线程调）。场景装载与运行期新增共用这一条路径：
    /// 「三源互斥、优先级 Spout &gt; Video &gt; Image」只写一次，不会分叉成装载一套、新增另一套。
    /// </summary>
    private void RegisterLayerProvider(PvLayerConfig layer)
    {
        if (WantsSpout(layer))
        {
            var provider = new SpoutSourceProvider(layer.Id, _renderer.Device, _d2d.Ctx, layer.SpoutSenderName!,
                layer.Premultiplied);
            _renderer.Stage.Register(provider);
            _spoutProviders.Add(provider);
            if (layer.Id == PvScene.CharacterLayerId) _character = provider;
            return;
        }

        if (WantsVideo(layer))
        {
            _renderer.Stage.Register(new FileVideoProvider(layer.Id, _renderer.Device, _d2d.Ctx, layer.VideoPath!));
            return;
        }

        if (WantsImage(layer))
        {
            _renderer.Stage.Register(new FileImageProvider(layer.Id, _d2d.Ctx, layer.ImagePath!));
        }
    }

    private void ApplyLayerOps()
    {
        var changed = false;

        while (_pendingAdds.TryDequeue(out var sender))
        {
            var id = $"src_{Interlocked.Increment(ref _layerSeq)}";
            var layer = NewSourceLayer(id, sender);
            _scene.Layers.Add(layer);
            RegisterLayerProvider(layer);
            changed = true;
            Log.Info($"[PV 管线] 新建图层 '{id}' <- '{sender}'");
        }

        while (_pendingImageAdds.TryDequeue(out var imagePath))
        {
            var id = $"src_{Interlocked.Increment(ref _layerSeq)}";
            var layer = NewImageLayer(id, imagePath);
            _scene.Layers.Add(layer);
            RegisterLayerProvider(layer);
            changed = true;
            Log.Info($"[PV 管线] 新建图片图层 '{id}' <- '{imagePath}'");
        }

        while (_pendingVideoAdds.TryDequeue(out var videoPath))
        {
            var id = $"src_{Interlocked.Increment(ref _layerSeq)}";
            var layer = NewVideoLayer(id, videoPath);
            _scene.Layers.Add(layer);
            RegisterLayerProvider(layer);
            changed = true;
            Log.Info($"[PV 管线] 新建视频图层 '{id}' <- '{videoPath}'");
        }

        while (_pendingRemoves.TryDequeue(out var id))
        {
            var idx = _scene.Layers.FindIndex(l => l.Id == id);
            if (idx >= 0)
            {
                _scene.Layers.RemoveAt(idx);
                // 提供者归 Stage 拥有：Remove 已经释放它，这里只摘索引，不再二次 Dispose
                _renderer.Stage.Remove(id);
                var p = _spoutProviders.FirstOrDefault(x => x.Id == id);
                if (p is not null) _spoutProviders.Remove(p);
                if (_character?.Id == id) _character = null;
                changed = true;
                Log.Info($"[PV 管线] 移除图层 '{id}'");
            }
        }

        var order = _pendingOrder;
        if (order is not null)
        {
            _pendingOrder = null;
            var byId = _scene.Layers.ToDictionary(l => l.Id);
            var reordered = new List<PvLayerConfig>();
            for (var i = order.Length - 1; i >= 0; i--)
            {
                if (byId.TryGetValue(order[i], out var l)) reordered.Add(l);
            }
            foreach (var l in _scene.Layers)
            {
                if (!reordered.Contains(l)) reordered.Add(l);
            }

            _scene.Layers.Clear();
            _scene.Layers.AddRange(reordered);
            changed = true;
        }

        if (changed)
        {
            // 图层集合变了：只通知 UI，存盘由 UI 线程自己做（渲染帧路径不碰磁盘）
            LayersChanged?.Invoke();
        }
    }

    /// <summary>
    /// 消费广播开关请求（渲染线程，帧首）。发送端的 native 资源只在本线程建立与释放；
    /// 停机后（_stopPumping）跳过：那时只剩收尾，不再新建任何资源。
    /// </summary>
    private void ApplyBroadcast()
    {
        var request = _pendingBroadcast;
        if (request is null) return;
        _pendingBroadcast = null;
        if (_stopPumping) return;

        if (!request.Enable)
        {
            if (_broadcast is null) return;
            _renderer.SetBroadcast(null);
            _broadcast.Dispose();
            _broadcast = null;
            Log.Info("[PV 管线] Spout 广播已关闭");
            return;
        }

        // 同名重复开启：不重建。重建会先注销再注册发送端名字，下游会看到源闪断一下
        if (_broadcast is not null
            && string.Equals(_broadcast.SenderName, request.Name, StringComparison.Ordinal)) return;

        if (_broadcast is not null)
        {
            _renderer.SetBroadcast(null);
            _broadcast.Dispose();
            _broadcast = null;
        }

        try
        {
            var outlet = new SpoutOut(_renderer.Device, request.Name,
                _renderer.DesignWidth, _renderer.DesignHeight);
            // D2D 目标位图在渲染线程、用渲染用的 D2D 上下文建：Composite 直接拿它当绘制目标
            if (outlet.IsActive) outlet.EnsureD2DBitmap(_renderer.D2DContext);
            _broadcast = outlet;
            _renderer.SetBroadcast(outlet);
            Log.Info($"[PV 管线] Spout 广播已开启：'{outlet.SenderName}' ({outlet.Width}×{outlet.Height})");
        }
        catch (Exception ex)
        {
            Log.Error("[PV 管线] 建立 Spout 广播失败：" + ex.Message, ex);
        }
    }

    private void ApplyTracking(double time)
    {
        // 基础态来自场景文档，不是 Identity：追踪失效时镜头落在工程量好的取景上，
        // 而不是每次都弹回 (0,0,1)。Camera 被手改成 null 同理：回落默认。
        var cam = _scene.Camera ?? new PvCameraConfig();

        // 缩放取值曲线优先：有 scale 曲线时它**替换**基础 Scale（曲线值即绝对取景），追踪缩放仍在它之上相乘；
        // 曲线与信号图（追踪通道）的统一仲裁是 M6 的事，这里不做混合。无曲线回落场景常量。
        // 两条路都夹 Math.Max：Scale=0 会让每层缩放塌成 0、整幅空白，手改文件也不能把画面改没。
        var baseScale = Math.Max(0.01, cam.Scale);
        var director = _director;
        if (director is not null && director.TryEvaluate(CameraScaleAddr, time, out var curveScale))
            baseScale = Math.Max(0.01, curveScale);

        if (_tracking is null || !_tracking.TryRead(250, out var st))
        {
            // 每帧都写回基础态（不是"什么都不做"）：否则最后一帧的追踪姿态会一直留在镜头上，
            // 断连后画面冻在歪着的取景里。
            _renderer.Stage.Camera = new PvCamState(cam.X, cam.Y, baseScale, 0, 0, null, null);
            _renderer.Stage.Energy = 0;
            _prevBlink = false;
            return;
        }

        var drive = VtsTrackingMap.Compute(st, _trackOptions, _prevBlink);
        _prevBlink = drive.Blink;

        // 镜头位移以设计像素为单位，缩放预览窗口不改变运镜幅度。
        // Zoom 的夹持是追踪通道自身的上下界；基础值自由相乘。
        // 曲线在这里就进了 Stage.Camera，而预览与广播都从同一个 Stage 合成本帧，天然同步反映。
        _renderer.Stage.Camera = new PvCamState(
            cam.X + drive.CameraX * _renderer.DesignWidth * TrackingCameraFrac,
            cam.Y + drive.CameraY * _renderer.DesignHeight * TrackingCameraFrac,
            baseScale * Math.Clamp(drive.Zoom, 0.5, 2.0),
            0, 0, null, null);
        _renderer.Stage.Energy = drive.MouthEnergy;
    }

    /// <summary>
    /// 每帧求值信号图，并把每个绑定的最终值写回目标效果实例的参数。
    ///
    /// 帧内状态（<see cref="_signalPlan"/>、写回的参数值）全在渲染线程上：本方法在 Prepare 里调用，
    /// 读取这些值的 BuildRenderGraph 紧接着也在同一线程、同一帧顺序里跑——同帧读写同线程同顺序，
    /// 所以既不需要锁，也不存在"读到半更新的帧"。
    ///
    /// 编译只在发布的文档换了引用时发生（面板确认 = 换引用）；同一份文档内的参数改动走
    /// TryApplyParams 复用已编译拓扑。NaN / 失效值不进这条路径：求值器已经把每个输出夹进目标范围
    /// 并回落到基础值（SignalEvaluator 的三条不变量），这里拿到的每个值都是有限的。
    /// </summary>
    private void ApplySignals(double time)
    {
        var graph = _signalGraph;
        if (graph is null)
        {
            // 关掉信号：计划与来源引用一起清掉，下次发布重新编译
            _signalPlan = null;
            _signalPlanGraph = null;
            return;
        }

        if (!ReferenceEquals(graph, _signalPlanGraph))
        {
            AdoptSignalGraph(graph);
        }
        else if (_signalPlan is { } live && !live.TryApplyParams(graph, _effects, out _))
        {
            // 参数改动自己把计划弄失效了（例如 Remap 区间被改成零长）：重编一次，不拿旧值继续跑
            AdoptSignalGraph(graph);
        }

        var plan = _signalPlan;
        if (plan is null) return;

        // 音频频带来自离线分析序列（按 transport 时刻查询），分析未就绪 ⇒ Valid=false ⇒ 绑定回落基础值。
        // Stage.Energy 是另一路（VTS 口型能量），由 ApplyTracking 写、给渲染器消费，不冒充频带。
        var audio = SampleAudioBands(_audioBands, time);

        plan.Evaluate(new SignalFrameInputs(time, Monotonic.Elapsed.TotalSeconds, audio, _director));

        foreach (var resolved in plan.Resolved)
        {
            // 用编译期就解析好的目标引用（TryGetTarget），不再按 InstanceId 拼字符串回查效果图：
            // 字符串只在显示与序列化时用，这一点是 SignalTargets 的设计前提。
            // 本线只写效果实例参数：相机/图层域没有效果实例可写，它们在编译期就被
            // SignalTargets.TryResolveBinding 以 target-domain-unwired 拒掉了（解析值没人消费 = 静默无效），
            // 所以计划里不会有 Node 为 null 的目标；这个 continue 只是防御性兜底。
            if (!plan.TryGetTarget(resolved.Address, out var node, out var param) || node is null) continue;
            node.Set(param, (float)resolved.Value);
        }
    }

    /// <summary>
    /// 发布前铺键：把每条绑定要写的效果参数键先建成"已存在"。
    ///
    /// 为什么必须在发布前：渲染线程每帧写的是 <c>EffectNode.Set</c> → <c>Values[key] = v</c>，
    /// 键不存在时这一次写入会扩容字典；而 UI 线程的存盘（EffectGraphJson 遍历 Values）可能与它同时发生——
    /// 扩容换掉桶结构，正在跑的枚举随之失效。铺键之后帧内写入全是覆盖已有键，不再有结构变化。
    ///
    /// 只在"投递新文档"这条线程上做，且必须与枚举字典的那条线程错开：现在的调用点全在 UI 线程
    /// （面板确认 / 工程恢复 / 实例级联删除），存盘也在 UI 线程——同一条线程上先后发生，不重叠。
    /// 将来若有别的线程调 <see cref="SetSignalGraph"/>，铺键与帧内写入就落到两条线程上，那时得另找同步点。
    /// </summary>
    private void SeedBoundParamKeys(SignalGraph? graph)
    {
        if (graph?.Bindings is null) return;

        foreach (var binding in graph.Bindings)
        {
            if (binding is null) continue;

            // 解析不了的绑定留给编译器报 target-*/address-*：这里再做一套判定，两处结论迟早分叉
            if (!ParamAddress.TryParse(binding.Address, out var addr)) continue;
            if (!SignalTargets.TryResolve(addr, _effects, out var target, out _)) continue;

            // 值取实例现状：已存在的键一字不动（铺键只保证"键在"，绝不改用户设好的参数值）
            target.Node.Values.TryAdd(target.Param.Key, target.Node.Value(target.Param));
        }
    }

    /// <summary>编译并采用一份新发布的信号图；编译结论在这里各报一次（静态错误不会自己消失，每帧刷日志没有意义）。</summary>
    private void AdoptSignalGraph(SignalGraph graph)
    {
        // 带场景编译：相机的位移范围是 ±1 个设计画布，编译期要按本工程的画布解析注册范围
        var result = SignalCompiler.Compile(graph, _effects, _scene);
        _signalPlan = result.Plan;
        _signalPlanGraph = graph;

        foreach (var issue in result.Issues)
        {
            var line = $"[PV 管线] 信号图 {issue.Code}：{issue.Message}";
            if (issue.Severity == SignalIssueSeverity.Error) Log.Error(line);
            else Log.Warn(line);
        }
    }

    /// <summary>
    /// 从已载入场景里把 <c>src_&lt;n&gt;</c> 的最大序号找出来接着编号。
    /// 不播种的话重开工程后 _layerSeq 从 0 重来，新增图层会撞上磁盘上已有的 Id。
    /// </summary>
    private void SeedLayerSeq()
    {
        foreach (var id in _scene.Layers.Select(l => l.Id))
        {
            if (id.StartsWith("src_", StringComparison.Ordinal)
                && int.TryParse(id.AsSpan(4), out var n) && n > _layerSeq) _layerSeq = n;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 先让帧回调看见"没准备好"：守卫只查 Ready，不查 _disposed
        Ready = false;

        // 图层提供者都注册在 Stage 上，释放权归 Stage（经 _renderer.Dispose()）；这里只摘掉本类的索引，
        // 绝不第二次 Dispose 同一个对象。字幕是唯一例外：它挂在渲染器的 Overlay 槽上、不进 Stage，
        // 所以由本类释放——先把槽摘空再释放，免得 Composite 摸到已释放的位图。
        _spoutProviders.Clear();
        _character = null;
        _renderer.Overlay = null;
        _subtitle?.Dispose();
        _subtitle = null;

        // 广播发送端（渲染线程）：先摘掉渲染器的引用再释放，避免 Composite 摸到已释放的位图
        _renderer.SetBroadcast(null);
        _broadcast?.Dispose();
        _broadcast = null;

        // D2D 位图缓存（fx 临时面→位图）先于渲染器整体释放清掉：
        // 这些位图按代际绑在 D2D 上下文上，留到进程退出才回收就等于代际泄漏
        _renderer.ClearFxBitmapCache();
        _renderer.Dispose();
        _d2d.Dispose();
    }
}
