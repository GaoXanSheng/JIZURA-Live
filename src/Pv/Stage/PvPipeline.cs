using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using JizuraLive.Spout;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JizuraLive.Pv;

public sealed class PvPipeline : IDisposable
{
    private readonly D2DHost _d2d;
    private readonly PvStageRenderer _renderer;
    private readonly PvScene _scene;

    private readonly SnapshotSlot<EffectGraph> _effects;

    private readonly ConcurrentQueue<string> _pendingAdds = new();
    private readonly ConcurrentQueue<string> _pendingImageAdds = new();
    private readonly ConcurrentQueue<string> _pendingVideoAdds = new();
    private readonly ConcurrentQueue<string> _pendingRemoves = new();
    private volatile string[]? _pendingOrder;

    private readonly List<SpoutSourceProvider> _spoutProviders = new();

    private volatile bool _stopPumping;
    private SubtitleProvider? _subtitle;
    private SpoutSourceProvider? _character;
    private readonly bool _legacyMode;
    private LegacyPvProvider? _legacy;
    private int _layerSeq;
    private bool _disposed;

    private volatile bool _stageCamera = true;
    private volatile bool _stageTransitions = true;
    private List<DesignSegment>? _designSegments;

    public double LastPrepareMs { get; private set; }

    public double LastCompositeMs { get; private set; }

    public double LegacyRenderMs => _legacy?.LastRenderMs ?? 0;

    public bool LegacyGpu => _legacy?.GpuMode ?? false;

    public bool StageCameraEnabled
    {
        get => _stageCamera;
        set
        {
            _stageCamera = value;
            if (_legacy is not null) _legacy.NoCamera = value;
        }
    }

    public bool StageTransitionsEnabled
    {
        get => _stageTransitions;
        set
        {
            _stageTransitions = value;
            if (_legacy is not null) _legacy.NoCutTrans = !value;
            _renderer.SetDesignSegments(value ? _designSegments : null);
        }
    }

    public LegacyPvPlanInfo? LegacyPlan => _legacy?.PlanInfo;

    public event Action<LegacyPvPlanInfo>? LegacyPlanReady;

    private SpoutOut? _broadcast;
    private volatile BroadcastRequest? _pendingBroadcast;

    private sealed record BroadcastRequest(bool Enable, string Name);

    public readonly record struct BroadcastState(bool Enabled, bool Active, long Sent, long Failed, string? LastError);

    private volatile DirectorEvaluator? _director;

    private volatile AudioBandSeries? _audioBands;

    private static readonly ParamAddress CameraScaleAddr =
        new(ParamAddressKind.Camera, "", "scale");

    private static readonly ParamAddress CameraOffsetXAddr =
        new(ParamAddressKind.Camera, "", "offsetX");

    private static readonly ParamAddress CameraOffsetYAddr =
        new(ParamAddressKind.Camera, "", "offsetY");

    private static readonly ParamAddress CameraRotationAddr =
        new(ParamAddressKind.Camera, "", "rotation");

    private static readonly ParamAddress CameraBlurAddr =
        new(ParamAddressKind.Camera, "", "blur");

    private static readonly ParamAddress CameraBrightenAddr =
        new(ParamAddressKind.Camera, "", "brighten");

    public PvScene Scene => _scene;
    public EffectGraph Effects => _effects.Snapshot;
    public PvStageRenderer Renderer => _renderer;
    public bool LegacyMode => _legacyMode;
    public int LegacyCutCount => _legacy?.CutCount ?? 0;

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

    public void RequestBroadcast(bool enable, string name) =>
        _pendingBroadcast = new BroadcastRequest(enable, name?.Trim() ?? "");

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
        IPvSubtitleSource? subtitleSource = null, LegacyPvOptions? legacy = null)
    {
        _scene = scene;
        _effects = new SnapshotSlot<EffectGraph>(effects.Clone());
        _legacyMode = legacy is { Enabled: true }
                      && !string.IsNullOrWhiteSpace(legacy.LrcText)
                      && !string.IsNullOrWhiteSpace(legacy.AudioPath)
                      && File.Exists(legacy.AudioPath);
        _stageCamera = legacy?.StageCamera ?? true;
        _stageTransitions = legacy?.StageTransitions ?? true;
        SeedLayerSeq();

        try
        {
            _d2d = D2DHost.Create(gpu);
            _renderer = new PvStageRenderer(gpu, _d2d, scene, _effects.Snapshot,
                scene.Width, scene.Height);
            RegisterProviders(gpu, subtitleSource);
            if (_legacyMode) RegisterLegacy(gpu, legacy!);
            Ready = true;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Error("[PV 管线] 初始化失败：" + ex.Message, ex);
            throw;
        }
    }

    public void SetDirector(DirectorEvaluator? d)
    {
        _director = d;
    }

    public void SetSpoutTransparent(bool on) => _renderer.SpoutTransparent = on;

    public void SetLyricsAvoid(bool on, double minScale)
    {
        _renderer.LyricsAvoid = on;
        _renderer.LyricsAvoidMinScale = minScale;
    }

    public void SetDesignSegments(IEnumerable<DesignSegment>? segments)
    {
        _designSegments = segments?.Where(s => s is not null).ToList();
        _renderer.SetDesignSegments(_stageTransitions ? _designSegments : null);
    }

    public void ApplyLegacyCutOverrides(LegacyPvPlanInfo info) => _legacy?.ApplyCutOverrides(info);

    public void PublishEffects(EffectGraph snapshot) => _effects.Publish(snapshot);

    public void SetAudioAnalysis(AudioBandSeries? series) => _audioBands = series;

    public static AudioBandSnapshot SampleAudioBands(AudioBandSeries? series, double transportSeconds)
    {
        if (series is null || !series.Sample(transportSeconds, out var bands)) return default;

        return new AudioBandSnapshot(bands.Valid, bands.Rms, bands.Bass, bands.Mid, bands.High);
    }

    public void Prepare(double time)
    {
        var sw = Stopwatch.StartNew();
        var effects = _effects.Snapshot;

        ApplyLayerOps();
        ApplyBroadcast();

        if (!_stopPumping)
            foreach (var p in _spoutProviders) p.PumpFrame();

        var frame = new PvFrameState();
        if (_legacyMode && !_stageCamera) frame.Camera = PvCamState.Identity;
        else ApplyCamera(time, frame);
        ApplyLayerFrame(time, frame);
        ApplyEffectCurves(effects, _director, time);
        _scene.PublishFrame(frame);

        _renderer.Stage.Camera = frame.Camera;

        _subtitle?.Render(time, _renderer.DesignWidth, _renderer.DesignHeight);

        _renderer.Prepare(time, effects);
        LastPrepareMs = sw.Elapsed.TotalMilliseconds;
    }

    public void Composite(ID2D1Bitmap1 target, int width, int height)
    {
        var sw = Stopwatch.StartNew();
        _renderer.Composite(target, width, height);
        LastCompositeMs = sw.Elapsed.TotalMilliseconds;
    }

    public PvStageRenderer.SharedTarget OpenSharedTarget(IntPtr shareHandle, int w, int h) =>
        _renderer.OpenSharedTarget(shareHandle, w, h);

    public ID2D1DeviceContext D2DContext => _renderer.D2DContext;

    public void RequestAdd(string sender) => _pendingAdds.Enqueue(sender);

    public void RequestAddImage(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return;
        _pendingImageAdds.Enqueue(imagePath);
    }

    public void RequestAddVideo(string videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath)) return;
        _pendingVideoAdds.Enqueue(videoPath);
    }

    public void RequestRemove(string id) => _pendingRemoves.Enqueue(id);
    public void RequestOrder(IReadOnlyList<string> idsTopFirst) =>
        _pendingOrder = [.. idsTopFirst];

    public void StopPumping()
    {
        _stopPumping = true;
        _renderer.FreezeFrames();
    }

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

    public static bool WantsVideo(PvLayerConfig layer) =>
        layer.Kind == PvLayerKind.VisualAsset
        && string.IsNullOrWhiteSpace(layer.SpoutSenderName)
        && !string.IsNullOrWhiteSpace(layer.VideoPath);

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
        Premultiplied = false,
        Cover = true,
        Parallax = new PvParallaxConfig { Depth = 0, RotationFactor = 0, ScaleFactor = 0 },
        VisualBlend = new PvVisualBlendConfig { AmbientTintIntensity = 0.18, RimGlow = 0.12 },
    };

    public static PvLayerConfig NewImageLayer(string id, string imagePath) => new()
    {
        Id = id,
        Name = Path.GetFileNameWithoutExtension(imagePath),
        Kind = PvLayerKind.VisualAsset,
        ImagePath = imagePath,
        SpoutSenderName = null,
        Cover = true,
        Parallax = new PvParallaxConfig { Depth = 0.9 },
        VisualBlend = new PvVisualBlendConfig { AmbientTintIntensity = 0.18, RimGlow = 0.12 },
    };

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
        _scene.Layers.RemoveAll(l => l.Id is "legacy_pv"
                                     || l.Id == "lyrics_pv"
                                     || l.Id == "foreground_fx"
                                     || l.Id == "bg"
                                     || l.Kind == PvLayerKind.LyricPv
                                     || l.Kind == PvLayerKind.Decor
                                     || l.Decor is not null
                                     || (l.Kind == PvLayerKind.Subtitle && subtitleSource is null)
                                     || (!_legacyMode && l.Id is LegacyPvProvider.BackId or LegacyPvProvider.FrontId));

        foreach (var layer in _scene.Layers) MigrateVisualDefaults(layer);
        foreach (var layer in _scene.Layers) RegisterLayerProvider(layer);

        if (subtitleSource is not null)
        {
            _subtitle = new SubtitleProvider("subtitle_top", gpu, _d2d.Ctx, subtitleSource);
            _renderer.Overlay = _subtitle;
        }
    }

    private void RegisterLegacy(D3DDevice gpu, LegacyPvOptions options)
    {
        var renderWidth = options.Synchronous
            ? _scene.Width
            : Math.Clamp(
                (int)Math.Round(_scene.Width * Math.Clamp(options.RenderScale, 0.25, 1.0)),
                320, _scene.Width);
        _legacy = new LegacyPvProvider("legacy", gpu, _d2d.Ctx, options,
            _scene.Width, _scene.Height, _scene.Fps, renderWidth);
        _legacy.NoCutTrans = _stageTransitions;
        _legacy.NoCamera = _stageCamera;
        _legacy.PlanReady += info => LegacyPlanReady?.Invoke(info);
        _renderer.Stage.Register(_legacy.Back);
        _renderer.Stage.Register(_legacy.Front);

        var existingBack = ExistingLayer(LegacyPvProvider.BackId);
        var existingFront = ExistingLayer(LegacyPvProvider.FrontId);
        var back = existingBack ?? LegacyLayer(LegacyPvProvider.BackId, "PV 背景层", PvLayerKind.Background);
        var front = existingFront ?? LegacyLayer(LegacyPvProvider.FrontId, "歌词文字层", PvLayerKind.ForegroundFx);
        back.Name = "PV 背景层";
        back.Kind = PvLayerKind.Background;
        front.Name = "歌词文字层";
        front.Kind = PvLayerKind.ForegroundFx;

        if (existingBack is null)
        {
            var insert = 0;
            for (var i = 0; i < _scene.Layers.Count; i++)
            {
                var layer = _scene.Layers[i];
                var backgroundMedia = layer.Id != PvScene.CharacterLayerId
                                      && (layer.Kind == PvLayerKind.Background
                                          || (layer.Kind == PvLayerKind.VisualAsset
                                              && string.IsNullOrWhiteSpace(layer.SpoutSenderName)
                                              && (!string.IsNullOrWhiteSpace(layer.VideoPath)
                                                  || !string.IsNullOrWhiteSpace(layer.ImagePath))));
                if (backgroundMedia) insert = i + 1;
            }

            _scene.Layers.Insert(insert, back);
        }

        // 只在首次创建时置顶；之后保留用户在图层列表里拖出来的顺序。
        if (existingFront is null)
            _scene.Layers.Add(front);

        Log.Info($"[PV 原版引擎] 图层顺序：{string.Join(" → ", _scene.Layers.Select(l => l.Id))}");
        Log.Info($"[PV 原版引擎] 启动中：{renderWidth}px 渲染（背景/文字分层）");
    }

    private PvLayerConfig? ExistingLayer(string id) =>
        _scene.Layers.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.Ordinal));

    private static PvLayerConfig LegacyLayer(string id, string name, PvLayerKind kind) => new()
    {
        Id = id,
        Name = name,
        Kind = kind,
        Enabled = true,
        Cover = true,
        Opacity = 1,
        Scale = 1,
        SpoutSenderName = null,
        Parallax = new PvParallaxConfig { Depth = 0, RotationFactor = 0, ScaleFactor = 0, Defocus = 0 },
        VisualBlend = new PvVisualBlendConfig
        {
            AmbientTintIntensity = 0,
            RimGlow = 0,
            DropShadow = 0,
            ShadowOffsetY = 0,
            AntiBleedMargin = 0,
        },
    };

    private static void MigrateVisualDefaults(PvLayerConfig layer)
    {
        if (layer.Id != PvScene.CharacterLayerId || string.IsNullOrWhiteSpace(layer.SpoutSenderName)) return;

        var blend = layer.VisualBlend;

        if (layer.Premultiplied
            && Math.Abs(blend.RimGlow - 0.15) < 0.001
            && Math.Abs(blend.DropShadow - 0.35) < 0.001)
        {
            layer.Premultiplied = false;
            blend.RimGlow = 0;
            blend.DropShadow = 0;
        }
    }

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

        while (_pendingRemoves.TryDequeue(out var id))
        {
            var idx = _scene.Layers.FindIndex(l => l.Id == id);
            if (idx >= 0)
            {
                _scene.Layers = WithLayerRemovedAt(_scene.Layers, idx);
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
            _scene.Layers = WithLayerOrder(_scene.Layers, order);
            changed = true;
        }

        if (changed)
        {
            LayersChanged?.Invoke();
        }
    }

    public static List<PvLayerConfig> WithLayerAdded(IReadOnlyList<PvLayerConfig> layers, PvLayerConfig layer)
    {
        var next = new List<PvLayerConfig>(layers);
        int front = next.FindIndex(l => l.Id == LegacyPvProvider.FrontId);
        if (front >= 0) next.Insert(front, layer);
        else next.Add(layer);
        return next;
    }

    public static List<PvLayerConfig> WithLayerRemovedAt(IReadOnlyList<PvLayerConfig> layers, int index)
    {
        var next = new List<PvLayerConfig>(layers);
        next.RemoveAt(index);
        return next;
    }

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

    public static PvCamState BaseCamera(PvCameraConfig cam, DirectorEvaluator? curves, double time)
    {
        var plan = PvCamState.Identity;
        if (curves is not null) curves.TryCameraAt(time, out plan);

        var x = plan.X + cam.X;
        var y = plan.Y + cam.Y;
        var rotation = plan.Rotation + cam.Rotation;
        var scale = Math.Max(0.01, plan.Scale * cam.Scale);
        double blur = plan.BlurSigma;
        double brighten = plan.Brighten;
        double skewX = plan.SkewX;
        double? scaleX = plan.ScaleX;
        double? scaleY = plan.ScaleY;

        if (curves is not null)
        {
            x = Finite(curves.Resolve(CameraOffsetXAddr, time, x), x);
            y = Finite(curves.Resolve(CameraOffsetYAddr, time, y), y);
            scale = Math.Max(0.01, Finite(curves.Resolve(CameraScaleAddr, time, scale), scale));
            rotation = Finite(curves.Resolve(CameraRotationAddr, time, rotation), rotation);
            blur = Finite(curves.Resolve(CameraBlurAddr, time, blur), blur);
            brighten = Finite(curves.Resolve(CameraBrightenAddr, time, brighten), brighten);
        }

        return new PvCamState(x, y, scale, rotation, skewX, scaleX, scaleY,
            Math.Clamp(blur, 0, 40), Math.Clamp(brighten, 0, 1));
    }

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

    private static double Finite(double v, double fallback) => double.IsFinite(v) ? v : fallback;

    private static ParamAddress LayerAddr(string layerId, string param) =>
        new(ParamAddressKind.Layer, layerId, param);

    private void ApplyLayerFrame(double time, PvFrameState frame)
    {
        var layers = _scene.Layers;
        for (var i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            var state = BaseLayer(layer, _director, time);
            if (_director is not null && _director.TryLayerCameraAt(layer.Id, time, out var layerCam))
                state = ApplyLayerCamera(state, layerCam);
            frame.SetLayer(layer.Id, state);
        }
    }

    private PvLayerState ApplyLayerCamera(PvLayerState state, PvCamState cam) => state with
    {
        OffsetX = state.OffsetX + cam.X / Math.Max(1, _scene.Width),
        OffsetY = state.OffsetY + cam.Y / Math.Max(1, _scene.Height),
        Scale = state.Scale * Math.Max(0.01, cam.Scale),
        Rotation = state.Rotation + cam.Rotation,
    };

    private void ApplyCamera(double time, PvFrameState frame)
    {
        var cam = _scene.Camera ?? new PvCameraConfig();
        frame.Camera = BaseCamera(cam, _director, time);
        _renderer.Stage.Energy = 0;
    }

    public static void ApplyEffectCurves(EffectGraph effects, DirectorEvaluator? curves, double time)
    {
        var nodes = effects.Nodes;
        for (var ni = 0; ni < nodes.Count; ni++)
        {
            var node = nodes[ni];
            if (node is null || string.IsNullOrEmpty(node.InstanceId)) continue;
            var def = EffectCatalog.Get(node.DefId);
            if (def is null) continue;

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

                double rest = node.RestValue(param);
                var value = animated ? curves!.Resolve(addr, time, rest) : rest;
                if (!double.IsFinite(value)) continue;
                if (isMix) value = identity + (value - identity) * weight;

                node.Set(param, (float)Math.Clamp(value, param.Min, param.Max));
            }
        }
    }

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

        Ready = false;

        _spoutProviders.Clear();
        _character = null;
        _renderer.Overlay = null;
        _subtitle?.Dispose();
        _subtitle = null;

        _renderer.SetBroadcast(null);
        _broadcast?.Dispose();
        _broadcast = null;

        _legacy?.Dispose();
        _legacy = null;
        _renderer.ClearFxBitmapCache();
        _renderer.Dispose();
        _d2d.Dispose();
    }
}

public readonly record struct AudioBandSnapshot(bool Valid, double Rms, double Bass, double Mid, double High);

public sealed class SnapshotSlot<T> where T : class
{
    private volatile T _snapshot;

    public SnapshotSlot(T initial) => _snapshot = initial;

    public T Snapshot => _snapshot;

    public void Publish(T snapshot) => _snapshot = snapshot;
}
