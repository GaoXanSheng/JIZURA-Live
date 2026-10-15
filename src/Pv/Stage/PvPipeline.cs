using System.Collections.Concurrent;
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

    private readonly VtsTrackingProbe? _tracking;
    private VtsTrackingOptions _trackOptions = new();
    private bool _prevBlink;

    private const double TrackingCameraFrac = 0.10;

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

    public void Prepare(double time)
    {
        ApplyLayerOps();
        ApplyBroadcast();

        if (!_stopPumping)
            foreach (var p in _spoutProviders) p.PumpFrame();

        ApplyTracking();

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

    private void RegisterProviders(D3DDevice gpu, IPvSubtitleSource? subtitleSource)
    {
        var tint = PvScene.ParseHex(_scene.AmbientTint);
        _renderer.Stage.Register(new ColorProvider("bg",
            new Color4(tint.R * 0.16f, tint.G * 0.16f, tint.B * 0.22f, 1f)));

        foreach (var layer in _scene.Layers)
        {
            if (!WantsSpout(layer)) continue;
            var provider = new SpoutSourceProvider(layer.Id, gpu, _d2d.Ctx, layer.SpoutSenderName!);
            _renderer.Stage.Register(provider);
            _spoutProviders.Add(provider);
            if (layer.Id == PvScene.CharacterLayerId) _character = provider;
        }

        if (subtitleSource is not null)
        {
            _subtitle = new SubtitleProvider("subtitle_top", gpu, _d2d.Ctx, subtitleSource);
            _renderer.Stage.Register(_subtitle);
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

            var provider = new SpoutSourceProvider(id, _renderer.Device, _d2d.Ctx, sender);
            _renderer.Stage.Register(provider);
            _spoutProviders.Add(provider);
            changed = true;
            Log.Info($"[PV 管线] 新建图层 '{id}' <- '{sender}'");
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

    private void ApplyTracking()
    {
        if (_tracking is null) return;

        if (!_tracking.TryRead(250, out var st))
        {
            _renderer.Stage.Camera = PvCamState.Identity;
            _renderer.Stage.Energy = 0;
            _prevBlink = false;
            return;
        }

        var drive = VtsTrackingMap.Compute(st, _trackOptions, _prevBlink);
        _prevBlink = drive.Blink;

        // 镜头位移以设计像素为单位，缩放预览窗口不改变运镜幅度
        _renderer.Stage.Camera = new PvCamState(
            drive.CameraX * _renderer.DesignWidth * TrackingCameraFrac,
            drive.CameraY * _renderer.DesignHeight * TrackingCameraFrac,
            Math.Clamp(drive.Zoom, 0.5, 2.0),
            0, 0, null, null);
        _renderer.Stage.Energy = drive.MouthEnergy;
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

        // 所有提供者都注册在 Stage 上，释放权归 Stage（经 _renderer.Dispose()）。
        // 这里只摘掉本类的索引，绝不第二次 Dispose 同一个对象。
        _spoutProviders.Clear();
        _subtitle = null;
        _character = null;

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
