using System.Collections.Concurrent;
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
    private readonly ConcurrentQueue<string> _pendingDecorAdds = new();
    private readonly ConcurrentQueue<string> _pendingRemoves = new();
    private volatile string[]? _pendingOrder;

    private readonly List<SpoutSourceProvider> _spoutProviders = new();
    private readonly List<DecorLayerProvider> _decor = new();

    private volatile bool _stopPumping;
    private SubtitleProvider? _subtitle;
    private SpoutSourceProvider? _character;
    private int _layerSeq;
    private bool _disposed;

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
        IPvSubtitleSource? subtitleSource = null)
    {
        _scene = scene;
        _effects = new SnapshotSlot<EffectGraph>(effects.Clone());
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

    public void SetDirector(DirectorEvaluator? d) => _director = d;

    public void PublishEffects(EffectGraph snapshot) => _effects.Publish(snapshot);

    public void SetAudioAnalysis(AudioBandSeries? series) => _audioBands = series;

    public static AudioBandSnapshot SampleAudioBands(AudioBandSeries? series, double transportSeconds)
    {
        if (series is null || !series.Sample(transportSeconds, out var bands)) return default;

        return new AudioBandSnapshot(bands.Valid, bands.Rms, bands.Bass, bands.Mid, bands.High);
    }

    public void Prepare(double time)
    {
        var effects = _effects.Snapshot;

        ApplyLayerOps();
        ApplyBroadcast();

        if (!_stopPumping)
            foreach (var p in _spoutProviders) p.PumpFrame();

        var frame = new PvFrameState();
        ApplyCamera(time, frame);
        ApplyLayerFrame(time, frame);
        ApplyEffectCurves(effects, _director, time);
        _scene.PublishFrame(frame);

        _renderer.Stage.Camera = frame.Camera;

        _subtitle?.Render(time, _renderer.DesignWidth, _renderer.DesignHeight);
        _renderer.Lyric?.SetScale(1.0);

        foreach (var p in _decor) p.Render(time);

        _renderer.Prepare(time, effects);
    }

    public void Composite(ID2D1Bitmap1 target, int width, int height) =>
        _renderer.Composite(target, width, height);

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

    public void RequestAddDecor(string kind)
    {
        if (string.IsNullOrWhiteSpace(kind)) return;
        _pendingDecorAdds.Enqueue(kind);
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

    public static bool WantsDecor(PvLayerConfig layer) =>
        layer.Decor is { Kinds.Count: > 0 };

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

    public static PvLayerConfig NewDecorLayer(string id, string kind) => new()
    {
        Id = id,
        Name = DecorCatalog.Label(kind),
        Kind = PvLayerKind.Decor,
        Decor = DecorCatalog.CreateConfig(kind),
        Parallax = new PvParallaxConfig { Depth = 1.25 },
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
            _renderer.Overlay = _subtitle;
        }
    }

    private void RegisterLayerProvider(PvLayerConfig layer)
    {
        if (WantsDecor(layer))
        {
            var provider = new DecorLayerProvider(layer.Id, _renderer.Device, _d2d, layer.Decor!,
                _renderer.DesignWidth, _renderer.DesignHeight);
            _renderer.Stage.Register(provider);
            _decor.Add(provider);
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

        while (_pendingDecorAdds.TryDequeue(out var kind))
        {
            var id = $"src_{Interlocked.Increment(ref _layerSeq)}";
            var layer = NewDecorLayer(id, kind);
            _scene.Layers = WithLayerAdded(_scene.Layers, layer);
            RegisterLayerProvider(layer);
            changed = true;
            Log.Info($"[PV 管线] 新建装饰图层 '{id}' <- '{kind}'");
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
                var fx = _decor.FirstOrDefault(x => x.Id == id);
                if (fx is not null) _decor.Remove(fx);
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

    public static List<PvLayerConfig> WithLayerAdded(IReadOnlyList<PvLayerConfig> layers, PvLayerConfig layer) =>
        [.. layers, layer];

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
            frame.SetLayer(layers[i].Id, BaseLayer(layers[i], _director, time));
    }

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
        _decor.Clear();
        _character = null;
        _renderer.Overlay = null;
        _subtitle?.Dispose();
        _subtitle = null;

        _renderer.SetBroadcast(null);
        _broadcast?.Dispose();
        _broadcast = null;

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
