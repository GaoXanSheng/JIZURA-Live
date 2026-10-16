using System.Collections.Concurrent;
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

    // 效果图：UI 线程改完文档图后克隆发布（PublishEffects），渲染线程在 Prepare 帧首取用本帧快照。
    // 发布出去的那份此后归渲染线程独占——ApplyEffectCurves 会往节点参数里写曲线值，
    // 所以那个对象上不能再有第二个写者（文档图的增删留在 UI 线程）。
    private readonly SnapshotSlot<EffectGraph> _effects;

    // 跨线程交接只有这一条命令通道：UI 投递，渲染线程在帧首消费（图层提供者的 COM 对象必须在渲染线程建）。
    private readonly ConcurrentQueue<string> _pendingAdds = new();
    private readonly ConcurrentQueue<string> _pendingImageAdds = new();
    private readonly ConcurrentQueue<string> _pendingVideoAdds = new();
    private readonly ConcurrentQueue<string> _pendingParticleAdds = new();
    private readonly ConcurrentQueue<string> _pendingRemoves = new();
    private volatile string[]? _pendingOrder;

    private readonly List<SpoutSourceProvider> _spoutProviders = new();

    // 粒子层要在合成之外先画好（它用共享的 Immediate2D 切自己的离屏面，见 ParticleLayerProvider.Render）：
    // 引用与 _spoutProviders 同档——提供者本身归 Stage 释放，这里只留索引。
    private readonly List<ParticleLayerProvider> _particles = new();
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

    /// <summary>镜头缩放的参数地址（文本形式 <c>camera.scale</c>）。地址是值相等类型，静态构造一次即可。</summary>
    private static readonly ParamAddress CameraScaleAddr =
        new(ParamAddressKind.Camera, "", "scale");

    private static readonly ParamAddress CameraOffsetXAddr =
        new(ParamAddressKind.Camera, "", "offsetX");

    private static readonly ParamAddress CameraOffsetYAddr =
        new(ParamAddressKind.Camera, "", "offsetY");

    private static readonly ParamAddress CameraRotationAddr =
        new(ParamAddressKind.Camera, "", "rotation");

    public PvScene Scene => _scene;
    public EffectGraph Effects => _effects.Snapshot;
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
        // 渲染侧从第一帧起就用自己的克隆：文档图（UI 线程手里的 _project.Effects）永不进渲染线程，
        // 否则一次界面编辑就是在与渲染线程每帧的 foreach Nodes 抢同一个列表。
        _effects = new SnapshotSlot<EffectGraph>(effects.Clone());
        _tracking = tracking;
        SeedLayerSeq();

        try
        {
            _d2d = D2DHost.Create(gpu);
            _renderer = new PvStageRenderer(gpu, _d2d, scene, _effects.Snapshot, scene.Width, scene.Height);
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
    ///
    /// 不再需要"发布前铺键"（旧 SeedCurveParamKeys）：曲线值现在写进的是渲染侧自己那份发布快照
    /// （<see cref="PublishEffects"/> 的克隆），帧内首次插键（<c>EffectNode.Set</c>）只动那个对象，
    /// 它上面没有第二个写者，也就不会再与谁的枚举撞上。
    /// </summary>
    public void SetDirector(DirectorEvaluator? d) => _director = d;

    /// <summary>
    /// 发布一份效果图快照（任意线程，UI 线程调用）。界面改完文档图后必须调：增删节点、拖拽排序、
    /// 启用/停用、改参数、换预设、撤销重做、一键生成——漏掉这一句这次改动屏幕上就不生效。
    ///
    /// 传进来的这份此后**归渲染线程独占**（曲线值写进节点参数），所以调用方只能传克隆
    /// （<see cref="EffectGraph.Clone"/>），不许把文档图直接发过来。
    /// </summary>
    public void PublishEffects(EffectGraph snapshot) => _effects.Publish(snapshot);

    /// <summary>
    /// 换音频频带分析序列（任意线程）。后台把整段音频算完（见 <see cref="AudioFileAnalyzer.Analyze"/>）
    /// 再发布过来；引用交换原子，无锁。传 null = 还没有分析结果。
    /// 取值走 <see cref="SampleAudioBands"/>（按 transport 时刻查询，未就绪返回 Valid=false，不是"静音"）。
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

    public void Prepare(double time)
    {
        // 帧首取本帧的效果图快照：曲线求值、随后的效果图构建与 Composite 只认这一份；
        // 帧中途的发布留到下一帧（换引用原子，无锁）。
        var effects = _effects.Snapshot;

        ApplyLayerOps();
        ApplyBroadcast();

        if (!_stopPumping)
            foreach (var p in _spoutProviders) p.PumpFrame();

        // 帧状态（契约 §3.3 的步骤 1/2）：本帧建一份**新的**草稿，常量与曲线先落进去，全部填完才整份发布——
        // 读者永远只看得到完整快照，看不到写了一半的字典。
        // 效果参数不走帧状态：曲线值直接写进本帧那份效果图快照，必须在 _renderer.Prepare 之前
        // （效果参数就是在那一句里被打包成常量缓冲的）。
        var frame = new PvFrameState();
        ApplyTracking(time, frame);
        ApplyLayerFrame(time, frame);
        ApplyEffectCurves(effects, _director, time);
        _scene.PublishFrame(frame);

        // 帧状态 → 舞台：预览与广播都从同一个 Stage 合成本帧，镜头只从这里取
        _renderer.Stage.Camera = frame.Camera;

        // 字幕与歌词都在设计画布上求值，窗口尺寸不参与排版
        _subtitle?.Render(time, _renderer.DesignWidth, _renderer.DesignHeight);
        _renderer.Lyric?.SetScale(1.0);

        // 粒子层同样在合成之前画好：它们要切自己的离屏面，进了合成器的绘制批次会把那一批打断
        foreach (var p in _particles) p.Render(time);

        _renderer.Prepare(time, effects);
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

    /// <summary>
    /// 新增一层粒子图层（任意线程）：只投递种类名，提供者与精灵的建立在渲染线程上做
    /// （D2D 几何必须与绘制同线程建）。空种类当场丢弃——同 RequestAddImage。
    /// </summary>
    public void RequestAddParticles(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return;
        _pendingParticleAdds.Enqueue(kind);
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

    /// <summary>
    /// 新建一层前景粒子图层：名字与默认色按种类取（樱花 #ffc0d8 / 星光 #fff4c2 / 光斑 #bfe3ff）。
    /// 同样必须显式清掉 SpoutSenderName（默认值是 "VTubeStudio"，留着会在界面上显示成角色层）。
    /// </summary>
    public static PvLayerConfig NewParticleLayer(string id, string kind) => new()
    {
        Id = id,
        Name = ParticleName(kind),
        Kind = PvLayerKind.ForegroundFx,
        SpoutSenderName = null,
        Particles = new PvParticleConfig { Kind = kind, Color = ParticleColor(kind) },
        // 前景层：视差比焦点平面更快更近（PvParallaxConfig.Depth 的前景档）
        Parallax = new PvParallaxConfig { Depth = 1.25 },
    };

    private static string ParticleName(string kind) => kind switch
    {
        "sparkle" => "星光",
        "bokeh" => "光斑",
        "sakura" => "樱花",
        _ => kind,
    };

    private static string ParticleColor(string kind) => kind switch
    {
        "sparkle" => "#fff4c2",
        "bokeh" => "#bfe3ff",
        _ => "#ffc0d8",
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
        if (layer.Particles is not null)
        {
            var provider = new ParticleLayerProvider(layer.Id, _renderer.Device, _d2d,
                layer.Particles, _renderer.DesignWidth, _renderer.DesignHeight);
            _renderer.Stage.Register(provider);
            _particles.Add(provider);
            return;
        }

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

    /// <summary>
    /// 消费图层的增 / 删 / 重排请求（渲染线程，帧首）。改结构一律换一份新列表（见
    /// <see cref="WithLayerAdded"/> 一族）：界面线程（图层卡、状态条）与存盘都在读 <see cref="PvScene.Layers"/>，
    /// 原地 Clear/Add/RemoveAt 会与那个枚举撞出「集合已修改」。
    /// </summary>
    private void ApplyLayerOps()
    {
        var changed = false;

        while (_pendingAdds.TryDequeue(out var sender))
        {
            var id = $"src_{Interlocked.Increment(ref _layerSeq)}";
            var layer = NewSourceLayer(id, sender);
            _scene.Layers = WithLayerAdded(_scene.Layers, layer);
            RegisterLayerProvider(layer);
            changed = true;
            Log.Info($"[PV 管线] 新建图层 '{id}' <- '{sender}'");
        }

        while (_pendingImageAdds.TryDequeue(out var imagePath))
        {
            var id = $"src_{Interlocked.Increment(ref _layerSeq)}";
            var layer = NewImageLayer(id, imagePath);
            _scene.Layers = WithLayerAdded(_scene.Layers, layer);
            RegisterLayerProvider(layer);
            changed = true;
            Log.Info($"[PV 管线] 新建图片图层 '{id}' <- '{imagePath}'");
        }

        while (_pendingVideoAdds.TryDequeue(out var videoPath))
        {
            var id = $"src_{Interlocked.Increment(ref _layerSeq)}";
            var layer = NewVideoLayer(id, videoPath);
            _scene.Layers = WithLayerAdded(_scene.Layers, layer);
            RegisterLayerProvider(layer);
            changed = true;
            Log.Info($"[PV 管线] 新建视频图层 '{id}' <- '{videoPath}'");
        }

        while (_pendingParticleAdds.TryDequeue(out var kind))
        {
            var id = $"src_{Interlocked.Increment(ref _layerSeq)}";
            var layer = NewParticleLayer(id, kind);
            _scene.Layers = WithLayerAdded(_scene.Layers, layer);
            RegisterLayerProvider(layer);
            changed = true;
            Log.Info($"[PV 管线] 新建粒子图层 '{id}' <- '{kind}'");
        }

        while (_pendingRemoves.TryDequeue(out var id))
        {
            var idx = _scene.Layers.FindIndex(l => l.Id == id);
            if (idx >= 0)
            {
                _scene.Layers = WithLayerRemovedAt(_scene.Layers, idx);
                // 提供者归 Stage 拥有：Remove 已经释放它，这里只摘索引，不再二次 Dispose
                _renderer.Stage.Remove(id);
                var p = _spoutProviders.FirstOrDefault(x => x.Id == id);
                if (p is not null) _spoutProviders.Remove(p);
                var fx = _particles.FirstOrDefault(x => x.Id == id);
                if (fx is not null) _particles.Remove(fx);
                if (_character?.Id == id) _character = null;
                changed = true;
                Log.Info($"[PV 管线] 移除图层 '{id}'");
            }
        }

        var order = _pendingOrder;
        if (order is not null)
        {
            _pendingOrder = null;
            _scene.Layers = WithLayerOrder(_scene.Layers, order);
            changed = true;
        }

        if (changed)
        {
            // 图层集合变了：只通知 UI，存盘由 UI 线程自己做（渲染帧路径不碰磁盘）
            LayersChanged?.Invoke();
        }
    }

    /// <summary>在图层栈末尾加一层（返回新列表；<see cref="PvScene.Layers"/> 的写入口之一）。</summary>
    public static List<PvLayerConfig> WithLayerAdded(IReadOnlyList<PvLayerConfig> layers, PvLayerConfig layer) =>
        [.. layers, layer];

    /// <summary>摘掉某一层（按位置；调用方先 FindIndex 判存在）。</summary>
    public static List<PvLayerConfig> WithLayerRemovedAt(IReadOnlyList<PvLayerConfig> layers, int index)
    {
        var next = new List<PvLayerConfig>(layers);
        next.RemoveAt(index);
        return next;
    }

    /// <summary>按「从上到下」的 Id 序列重排：列表顺序 = 绘制顺序，所以这里要把序列反过来。</summary>
    public static List<PvLayerConfig> WithLayerOrder(IReadOnlyList<PvLayerConfig> layers,
        IReadOnlyList<string> idsTopFirst)
    {
        var byId = layers.ToDictionary(l => l.Id);
        var next = new List<PvLayerConfig>(layers.Count);
        for (var i = idsTopFirst.Count - 1; i >= 0; i--)
        {
            if (byId.TryGetValue(idsTopFirst[i], out var l)) next.Add(l);
        }

        foreach (var l in layers)
        {
            if (!next.Contains(l)) next.Add(l);
        }

        return next;
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

    /// <summary>
    /// 相机基础帧状态（契约 §3.3 步骤 1/2）：文档常量 → 曲线 base(t)（有曲线就用曲线值）。
    /// 纯函数、只读：不写回 <see cref="PvScene.Camera"/>——那是工程文件里的静息取景，不能被播放过程篡改。
    /// </summary>
    public static PvCamState BaseCamera(PvCameraConfig cam, DirectorEvaluator? curves, double time)
    {
        var x = cam.X;
        var y = cam.Y;
        var rotation = cam.Rotation;
        // 夹 Math.Max：Scale=0 会让每层缩放塌成 0、整幅空白，手改文件也不能把画面改没
        var scale = Math.Max(0.01, cam.Scale);

        if (curves is not null)
        {
            x = Finite(curves.Resolve(CameraOffsetXAddr, time, x), x);
            y = Finite(curves.Resolve(CameraOffsetYAddr, time, y), y);
            scale = Math.Max(0.01, Finite(curves.Resolve(CameraScaleAddr, time, scale), scale));
            rotation = Finite(curves.Resolve(CameraRotationAddr, time, rotation), rotation);
        }

        return new PvCamState(x, y, scale, rotation, 0, null, null);
    }

    /// <summary>图层基础帧状态（契约 §3.3 步骤 1/2）：文档常量 → 曲线 base(t)。</summary>
    public static PvLayerState BaseLayer(PvLayerConfig layer, DirectorEvaluator? curves, double time)
    {
        var state = PvLayerState.From(layer);
        if (curves is null) return state;

        return state with
        {
            OffsetX = Finite(curves.Resolve(LayerAddr(layer.Id, "offsetX"), time, state.OffsetX), state.OffsetX),
            OffsetY = Finite(curves.Resolve(LayerAddr(layer.Id, "offsetY"), time, state.OffsetY), state.OffsetY),
            Scale = Finite(curves.Resolve(LayerAddr(layer.Id, "scale"), time, state.Scale), state.Scale),
            Opacity = Finite(curves.Resolve(LayerAddr(layer.Id, "opacity"), time, state.Opacity), state.Opacity),
            Rotation = Finite(curves.Resolve(LayerAddr(layer.Id, "rotation"), time, state.Rotation), state.Rotation),
        };
    }

    // 手改工程能把 NaN 写进曲线 key；镜头/图层取到非有限值就退回下层值，不让它污染整帧
    private static double Finite(double v, double fallback) => double.IsFinite(v) ? v : fallback;

    private static ParamAddress LayerAddr(string layerId, string param) =>
        new(ParamAddressKind.Layer, layerId, param);

    /// <summary>
    /// 图层帧状态（§3.3 步骤 1/2）：每层「文档常量 → 曲线 base(t)」写进本帧草稿。
    /// 草稿每帧新建，所以删掉的图层不会留残影。
    /// </summary>
    private void ApplyLayerFrame(double time, PvFrameState frame)
    {
        foreach (var layer in _scene.Layers)
            frame.SetLayer(layer.Id, BaseLayer(layer, _director, time));
    }

    private void ApplyTracking(double time, PvFrameState frame)
    {
        // 基础态来自场景文档 + 曲线（不是 Identity）：追踪失效时镜头落在工程量好的取景上，
        // 而不是每次都弹回 (0,0,1)。Camera 被手改成 null 同理：回落默认。
        var cam = _scene.Camera ?? new PvCameraConfig();

        if (_tracking is null || !_tracking.TryRead(VtsTrackingProbe.StaleMs, out var st))
        {
            // 每帧都写回基础态（不是"什么都不做"）：否则最后一帧的追踪姿态会一直留在镜头上，
            // 断连后画面冻在歪着的取景里。
            frame.Camera = BaseCamera(cam, _director, time);
            _renderer.Stage.Energy = 0;
            _prevBlink = false;
            return;
        }

        var drive = VtsTrackingMap.Compute(st, _trackOptions, _prevBlink);
        _prevBlink = drive.Blink;

        // 追踪位移/滚转/缩放叠在基础帧状态之上。
        // 镜头位移以设计像素为单位，缩放预览窗口不改变运镜幅度；Zoom 的夹持是追踪通道自身的上下界。
        // 滚转是度数、与 baseCam.Rotation 同量纲，直接相加（PvParallax 再按每层 RotationFactor/深度加权）。
        var baseCam = BaseCamera(cam, _director, time);
        frame.Camera = new PvCamState(
            baseCam.X + drive.CameraX * _renderer.DesignWidth * TrackingCameraFrac,
            baseCam.Y + drive.CameraY * _renderer.DesignHeight * TrackingCameraFrac,
            baseCam.Scale * Math.Clamp(drive.Zoom, 0.5, 2.0),
            baseCam.Rotation + drive.Rotation, 0, null, null);
        _renderer.Stage.Energy = drive.MouthEnergy;
    }

    /// <summary>
    /// 效果参数曲线求值（契约 §3.3 步骤 4）：每个效果实例的每个参数，只要地址
    /// <c>effect:&lt;InstanceId&gt;.&lt;paramKey&gt;</c> 上有曲线，就把该时刻的值写进实例参数。
    ///
    /// 相机/图层的曲线不走这里——它们在 <see cref="BaseCamera"/> / <see cref="BaseLayer"/> 里求值，
    /// 落到本帧草稿的帧状态槽。
    ///
    /// 只在渲染线程的 Prepare 里调：与随后读这些参数的 <c>BuildRenderGraph</c> 同线程同顺序。
    /// 每帧不分配集合——地址是值相等结构体（只引用既有的实例 Id 与参数 Key 字符串），
    /// 参数表来自效果目录的静态 schema。
    ///
    /// 值按参数 schema 的 min/max 夹取；非有限值（手改工程文件可以把 NaN/Infinity 写进曲线 key）
    /// 整条跳过，不能让它经 <c>EffectNode.Set</c> 的 Math.Clamp 漏进常量缓冲（NaN 会污染整帧）。
    ///
    /// public static 与 <see cref="BaseCamera"/> / <see cref="SampleAudioBands"/> 同档：纯函数，可直接单测
    /// （本程序的管线本体要 GPU，构造不出来）。
    /// </summary>
    public static void ApplyEffectCurves(EffectGraph effects, DirectorEvaluator? curves, double time)
    {
        foreach (var node in effects.Nodes)
        {
            if (node is null || string.IsNullOrEmpty(node.InstanceId)) continue;
            var def = EffectCatalog.Get(node.DefId);
            if (def is null) continue;

            // 出现区间：权重 0 整个节点本帧不进渲染图；介于 0..1 时把混合参数向无效果值插值（淡入淡出）
            var weight = node.WindowWeight(time);
            node.FrameActive = weight > 0;
            if (!node.FrameActive) continue;
            var hasMix = EffectCatalog.TryGetMix(def.Id, out var mixKey, out var identity) && node.Windows.Count > 0;

            foreach (var param in def.Params)
            {
                if (param.Kind != EffectParamKind.Number) continue;
                var addr = new ParamAddress(ParamAddressKind.Effect, node.InstanceId, param.Key);
                var isMix = hasMix && string.Equals(param.Key, mixKey, StringComparison.Ordinal);
                var animated = curves is not null && curves.Affects(addr);
                if (!animated && !isMix) continue;

                // 每帧从文档原值重新算：Values 里是上一帧写回的结果，拿它当底会让调制层逐帧累积
                double rest = node.RestValue(param);
                var value = animated ? curves!.Resolve(addr, time, rest) : rest;
                if (!double.IsFinite(value)) continue;
                if (isMix) value = identity + (value - identity) * weight;

                node.Set(param, (float)Math.Clamp(value, param.Min, param.Max));
            }
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
        _particles.Clear();
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

/// <summary>
/// 音频分析在某时刻的快照：有效位与值一起传。<see cref="Valid"/> = false 表示"没有数据"，
/// 与"数据是 0"是两件事——静音段的有效 0 照常传播，失效才回落到基础值。
/// 值从 <see cref="AudioBands"/> 的 float 加宽而来（帧内用 double 算，避免多次加宽累积）。
/// </summary>
public readonly record struct AudioBandSnapshot(bool Valid, double Rms, double Bass, double Mid, double High);

/// <summary>
/// 一份跨线程发布的不可变快照：UI 线程写、渲染线程读。换引用，原子、无锁；
/// 发布出去的那份此后不许再改——它就是读者正在读的快照。
///
/// 抽成一个类型是为了让「发布 → 取用」这条通道本身能被单测直接驱动：管线本体要 D3D 设备，
/// 测试里构造不出来（见 tests/JizuraLive.Engine.Tests/RenderThreadSafetyTests.cs）。
/// </summary>
public sealed class SnapshotSlot<T> where T : class
{
    private volatile T _snapshot;

    public SnapshotSlot(T initial) => _snapshot = initial;

    /// <summary>当前快照（任意线程可读）。</summary>
    public T Snapshot => _snapshot;

    public void Publish(T snapshot) => _snapshot = snapshot;
}
