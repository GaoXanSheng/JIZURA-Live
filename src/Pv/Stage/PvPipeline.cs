using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using JizuraLive.App.I18n;
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
    private readonly ConcurrentQueue<string> _pendingAssets = new();
    private readonly ConcurrentQueue<string> _pendingRemoves = new();
    private volatile string[]? _pendingOrder;

    private readonly List<SpoutSourceProvider> _spoutProviders = new();

    private volatile bool _stopPumping;
    private SubtitleProvider? _subtitle;
    private readonly bool _legacyMode;
    private LegacyPvProvider? _legacy;
    private int _layerSeq;
    private bool _disposed;

    private volatile bool _stageCamera = true;
    private volatile bool _stageTransitions = true;
    private bool _legacyComposite;

    public bool LegacyComposite => _legacyComposite;

    public event Action? CompositeInvalidated;

    private static readonly string[] LegacyLayerIds =
    [
        LegacyPvProvider.BackId,
        LegacyPvProvider.FrontLayoutId,
        LegacyPvProvider.FrontId,
        LegacyPvProvider.FrontDecorId,
        LegacyPvProvider.FrontHudId,
    ];
    private List<DesignSegment>? _designSegments;

    public double LastPrepareMs { get; private set; }

    public double LastCompositeMs { get; private set; }

    public double LegacyRenderMs => _legacy?.LastRenderMs ?? 0;

    public bool LegacyGpu => _legacy?.GpuMode ?? false;

    public string? LegacyRasterFallback => _legacy?.RasterFallbackReason;

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
            if (_legacy is not null) _legacy.NoCutTrans = value;
            _renderer.SetDesignSegments(value ? _designSegments : null);
        }
    }

    public LegacyPvPlanInfo? LegacyPlan => _legacy?.PlanInfo;

    public string? SelectedLayerId
    {
        get => _renderer.SelectedLayerId;
        set => _renderer.SelectedLayerId = value;
    }

    public PvSelectionInfo? Selection => _renderer.Selection;

    public PvHitQuad? Pick(Vector2 designPoint) => _renderer.Pick(designPoint);

    public PvLayerImage CaptureLayerImage(string id)
    {
        if (!_renderer.Stage.LastImages.TryGetValue(id, out var image) || !image.HasContent)
            return PvLayerImage.Empty;
        if (image.Alpha is { Length: > 0 }) return image;

        var provider = _spoutProviders.FirstOrDefault(p =>
            string.Equals(p.Id, id, StringComparison.Ordinal));
        if (provider is null || !provider.TryCaptureAlpha(128, out var alpha, out var alphaWidth, out var alphaHeight))
            return image;

        return image with { Alpha = alpha, AlphaWidth = alphaWidth, AlphaHeight = alphaHeight };
    }

    public PvVtsControl VtsControl
    {
        get => _renderer.VtsControl;
        set => _renderer.VtsControl = value;
    }

    public int DesignWidth => _renderer.DesignWidth;

    public int DesignHeight => _renderer.DesignHeight;

    public event Action<LegacyPvPlanInfo>? LegacyPlanReady;

    private SpoutOut? _broadcast;
    private volatile BroadcastRequest? _pendingBroadcast;

    private sealed record BroadcastRequest(bool Enable, string Name);

    public readonly record struct BroadcastState(bool Enabled, bool Active, long Sent, long Failed, string? LastError);

    private volatile DirectorEvaluator? _director;

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

    public PvStageRenderer Renderer => _renderer;
    public bool LegacyMode => _legacyMode;
    public int LegacyCutCount => _legacy?.CutCount ?? 0;

    public bool Ready { get; private set; }
    public string? Error { get; private set; }
    public string? LegacyError => _legacy?.Error;

    public (int Live, int Total) SpoutState =>
        (_spoutProviders.Count(p => p.Connected), _spoutProviders.Count);

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

    public void SetDesignSegments(IEnumerable<DesignSegment>? segments)
    {
        _designSegments = segments?.Where(s => s is not null).ToList();
        _renderer.SetDesignSegments(_stageTransitions ? _designSegments : null);
    }

    public void ApplyLegacyCutOverrides(LegacyPvPlanInfo info) => _legacy?.ApplyCutOverrides(info);

    public void PublishEffects(EffectGraph snapshot)
    {
        _effects.Publish(snapshot);
        if (_legacyComposite
            && snapshot.Nodes.Any(n => n is not null && n.Enabled
                && LegacyLayerIds.Contains(n.Target, StringComparer.Ordinal)))
            CompositeInvalidated?.Invoke();
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

    public ID2D1DeviceContext D2DContext => _renderer.D2DContext;

    public void RequestAdd(string sender) => _pendingAdds.Enqueue(sender);

    public void RequestAddAsset(string path) => _pendingAssets.Enqueue(path);

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

    public static bool WantsAsset(PvLayerConfig layer) =>
        !string.IsNullOrWhiteSpace(layer.AssetPath);

    public static PvLayerConfig NewAssetLayer(string id, string path) => new()
    {
        Id = id,
        Name = Path.GetFileName(path),
        Kind = PvLayerKind.Background,
        AssetPath = path,
        SpoutSenderName = null,
        Cover = true,
        Parallax = new PvParallaxConfig { Depth = 1, RotationFactor = 1, ScaleFactor = 1, Defocus = 1 },
        VisualBlend = new PvVisualBlendConfig
        {
            AmbientTintIntensity = 0,
            RimGlow = 0,
            DropShadow = 0,
            ShadowOffsetY = 0,
            AntiBleedMargin = 0.06,
        },
    };

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

    private void RegisterProviders(D3DDevice gpu, IPvSubtitleSource? subtitleSource)
    {
        _scene.Layers.RemoveAll(l => l is null
                                     || (l.Kind == PvLayerKind.Subtitle && subtitleSource is null)
                                     || (!_legacyMode && l.Id is LegacyPvProvider.BackId
                                         or LegacyPvProvider.FrontId
                                         or LegacyPvProvider.FrontLayoutId
                                         or LegacyPvProvider.FrontDecorId
                                         or LegacyPvProvider.FrontHudId));
        foreach (var layer in _scene.Layers) RegisterLayerProvider(layer);

        if (subtitleSource is not null)
        {
            var subtitleId = _scene.Layers.FirstOrDefault(l => l is not null && l.Kind == PvLayerKind.Subtitle)?.Id
                             ?? "subtitle_top";
            _subtitle = new SubtitleProvider(subtitleId, gpu, _d2d.Ctx, subtitleSource);
            _renderer.Stage.Register(_subtitle);
        }
    }

    private void RegisterLegacy(D3DDevice gpu, LegacyPvOptions options)
    {
        var renderWidth = options.Synchronous
            ? _scene.Width
            : Math.Clamp(
                (int)Math.Round(_scene.Width * Math.Clamp(options.RenderScale, 0.25, 1.0)),
                Math.Min(320, _scene.Width), _scene.Width);
        var fps = options.Fps > 0 ? options.Fps : _scene.Fps;
        _legacy = new LegacyPvProvider("legacy", gpu, _d2d.Ctx, options,
            _scene.Width, _scene.Height, fps, renderWidth);
        _legacy.NoCutTrans = _stageTransitions;
        _legacy.NoCamera = _stageCamera;
        _legacy.PlanReady += info => LegacyPlanReady?.Invoke(info);

        var existingBack = ExistingLayer(LegacyPvProvider.BackId);
        var existingText = ExistingLayer(LegacyPvProvider.FrontId);
        var existingLayout = ExistingLayer(LegacyPvProvider.FrontLayoutId);
        var existingDecor = ExistingLayer(LegacyPvProvider.FrontDecorId);
        var existingHud = ExistingLayer(LegacyPvProvider.FrontHudId);
        var back = existingBack ?? LegacyLayer(LegacyPvProvider.BackId, L.T("StageUi.Layer.BackgroundLayer"), PvLayerKind.Background);
        var text = existingText ?? LegacyLayer(LegacyPvProvider.FrontId, L.T("StageUi.Layer.LyricsTextLayer"), PvLayerKind.ForegroundFx);
        var layout = existingLayout ?? LegacyLayer(LegacyPvProvider.FrontLayoutId, L.T("StageUi.Layer.LyricsLayoutLayer"), PvLayerKind.ForegroundFx);
        var decor = existingDecor ?? LegacyLayer(LegacyPvProvider.FrontDecorId, L.T("StageUi.Layer.LyricsDecorationLayer"), PvLayerKind.ForegroundFx);
        var hud = existingHud ?? LegacyLayer(LegacyPvProvider.FrontHudId, L.T("StageUi.Layer.LyricsInterfaceLayer"), PvLayerKind.ForegroundFx);
        back.Name = L.T("StageUi.Layer.BackgroundLayer");
        back.Kind = PvLayerKind.Background;
        text.Name = L.T("StageUi.Layer.LyricsTextLayer");
        text.Kind = PvLayerKind.ForegroundFx;
        layout.Name = L.T("StageUi.Layer.LyricsLayoutLayer");
        layout.Kind = PvLayerKind.ForegroundFx;
        decor.Name = L.T("StageUi.Layer.LyricsDecorationLayer");
        decor.Kind = PvLayerKind.ForegroundFx;
        hud.Name = L.T("StageUi.Layer.LyricsInterfaceLayer");
        hud.Kind = PvLayerKind.ForegroundFx;

        EnsureLegacyCamera(back, true);
        EnsureLegacyCamera(text, false);
        EnsureLegacyCamera(layout, false);
        EnsureLegacyCamera(decor, false);
        EnsureLegacyCamera(hud, false);

        if (existingLayout is null) CopyLayerTransform(text, layout);
        if (existingDecor is null) CopyLayerTransform(text, decor);
        if (existingHud is null) CopyLayerTransform(text, hud);

        if (existingBack is null)
        {
            var insert = 0;
            for (var i = 0; i < _scene.Layers.Count; i++)
            {
                var layer = _scene.Layers[i];
                var backgroundMedia = !ReferenceEquals(layer, _scene.CharacterLayer)
                                      && layer.Kind == PvLayerKind.Background;
                if (backgroundMedia) insert = i + 1;
            }

            _scene.Layers.Insert(insert, back);
        }

        EnsureLyricLayer(layout, existingLayout is not null,
            [], [LegacyPvProvider.FrontId, LegacyPvProvider.FrontDecorId, LegacyPvProvider.FrontHudId]);
        EnsureLyricLayer(text, existingText is not null,
            [LegacyPvProvider.FrontLayoutId], [LegacyPvProvider.FrontDecorId, LegacyPvProvider.FrontHudId]);
        EnsureLyricLayer(decor, existingDecor is not null,
            [LegacyPvProvider.FrontId, LegacyPvProvider.FrontLayoutId], [LegacyPvProvider.FrontHudId]);
        EnsureLyricLayer(hud, existingHud is not null,
            [LegacyPvProvider.FrontDecorId, LegacyPvProvider.FrontId, LegacyPvProvider.FrontLayoutId], []);

        if (options.CutOverrides is { Count: > 0 } overrides)
        {
            var info = new LegacyPvPlanInfo();
            foreach (var o in overrides)
            {
                if (o is null || !double.IsFinite(o.Start)) continue;
                info.Cuts.Add(new LegacyPvCutInfo
                {
                    Start = o.Start,
                    Line = o.Line,
                    Layout = o.Layout ?? "",
                    Enter = o.Enter ?? "",
                    Exit = o.Exit ?? "",
                    Treat = o.Treat ?? "",
                    LayoutParams = new Dictionary<string, double>(o.LayoutParams ?? new(), StringComparer.Ordinal),
                    TreatParams = new Dictionary<string, double>(o.TreatParams ?? new(), StringComparer.Ordinal),
                    BgParams = new Dictionary<string, double>(o.BgParams ?? new(), StringComparer.Ordinal),
                    CamParams = new Dictionary<string, double>(o.CamParams ?? new(), StringComparer.Ordinal),
                    LayoutTexts = new Dictionary<string, string>(o.LayoutTexts ?? new(), StringComparer.Ordinal),
                    TreatTexts = new Dictionary<string, string>(o.TreatTexts ?? new(), StringComparer.Ordinal),
                    BgTexts = new Dictionary<string, string>(o.BgTexts ?? new(), StringComparer.Ordinal),
                    CamTexts = new Dictionary<string, string>(o.CamTexts ?? new(), StringComparer.Ordinal),
                });
            }

            if (info.Cuts.Count > 0) _legacy.ApplyCutOverrides(info);
        }

        _legacyComposite = options.Composite && CanUseLegacyComposite();
        _legacy.CompositeMode = _legacyComposite;
        back.Name = _legacyComposite ? L.T("StageUi.Layer.LegacyComposite") : L.T("StageUi.Layer.BackgroundLayer");

        if (_legacyComposite)
        {
            _renderer.Stage.Register(_legacy.Composite);
        }
        else
        {
            _renderer.Stage.Register(_legacy.Back);
            _renderer.Stage.Register(_legacy.Front);
            _renderer.Stage.Register(_legacy.FrontLayout);
            _renderer.Stage.Register(_legacy.FrontDecor);
            _renderer.Stage.Register(_legacy.FrontHud);
        }

        Log.Info($"[PV 原版引擎] 图层顺序：{string.Join(" → ", _scene.Layers.Select(l => l.Id))}");
        Log.Info($"[PV 原版引擎] 启动中：{renderWidth}px 渲染（{(_legacyComposite ? "合并层" : "背景/布局/文字/装饰/界面分层")}，NoPost={_legacy.NoPost}）");
    }

    private bool CanUseLegacyComposite() => CanUseLegacyComposite(_scene, _effects.Snapshot);

    public static bool CanUseLegacyComposite(PvScene scene, EffectGraph effects)
    {
        PvLayerConfig? Find(string id) =>
            scene.Layers.FirstOrDefault(l => l is not null && l.Id == id);

        var back = Find(LegacyPvProvider.BackId);
        if (back is null || !IsDefaultLegacyBack(back)) return false;

        var fronts = new[]
        {
            LegacyPvProvider.FrontLayoutId,
            LegacyPvProvider.FrontId,
            LegacyPvProvider.FrontDecorId,
            LegacyPvProvider.FrontHudId,
        };
        foreach (var id in fronts)
        {
            var layer = Find(id);
            if (layer is null || !IsDefaultLegacyFront(layer)) return false;
        }

        var indices = new int[LegacyLayerIds.Length];
        for (var i = 0; i < LegacyLayerIds.Length; i++)
        {
            indices[i] = scene.Layers.FindIndex(l => l is not null && l.Id == LegacyLayerIds[i]);
            if (indices[i] < 0) return false;
        }

        int min = indices.Min();
        int max = indices.Max();
        if (max - min + 1 != LegacyLayerIds.Length) return false;
        for (var i = min; i <= max; i++)
            if (!LegacyLayerIds.Contains(scene.Layers[i].Id, StringComparer.Ordinal))
                return false;

        foreach (var node in effects.Nodes)
            if (node is not null && node.Enabled
                && LegacyLayerIds.Contains(node.Target, StringComparer.Ordinal))
                return false;

        return true;
    }

    private static bool IsDefaultLegacyFront(PvLayerConfig layer)
    {
        if (!layer.Enabled || layer.MirrorX || !layer.Cover) return false;
        if (Math.Abs(layer.Opacity - 1) > 1e-6 || Math.Abs(layer.Scale - 1) > 1e-6
            || Math.Abs(layer.OffsetX) > 1e-6 || Math.Abs(layer.OffsetY) > 1e-6
            || Math.Abs(layer.Rotation) > 1e-6) return false;

        var parallax = layer.Parallax;
        if (parallax is null
            || Math.Abs(parallax.Depth - 1) > 1e-6
            || Math.Abs(parallax.RotationFactor - 1) > 1e-6
            || Math.Abs(parallax.ScaleFactor - 1) > 1e-6
            || Math.Abs(parallax.Defocus - 1) > 1e-6
            || (parallax.Follow is { } follow && Math.Abs(follow - 1) > 1e-6)) return false;

        var blend = layer.VisualBlend;
        return blend is not null
               && blend.AmbientTintIntensity <= 1e-3
               && blend.RimGlow <= 1e-3
               && blend.DropShadow <= 1e-3
               && Math.Abs(blend.AntiBleedMargin - 0.02) <= 1e-3;
    }

    private static bool IsDefaultLegacyBack(PvLayerConfig layer)
    {
        if (!layer.Enabled || layer.MirrorX || !layer.Cover) return false;
        if (Math.Abs(layer.Opacity - 1) > 1e-6 || Math.Abs(layer.Scale - 1) > 1e-6
            || Math.Abs(layer.OffsetX) > 1e-6 || Math.Abs(layer.OffsetY) > 1e-6
            || Math.Abs(layer.Rotation) > 1e-6) return false;

        var parallax = layer.Parallax;
        if (parallax is null
            || Math.Abs(parallax.Depth - 1) > 1e-6
            || Math.Abs(parallax.RotationFactor - 1) > 1e-6
            || Math.Abs(parallax.ScaleFactor - 1) > 1e-6
            || Math.Abs(parallax.Defocus - 1) > 1e-6
            || (parallax.Follow is { } follow && Math.Abs(follow - 1) > 1e-6)) return false;

        var blend = layer.VisualBlend;
        return blend is not null
               && blend.AmbientTintIntensity <= 1e-3
               && blend.RimGlow <= 1e-3
               && blend.DropShadow <= 1e-3
               && blend.AntiBleedMargin is >= 0.19 and <= 0.21;
    }

    private void EnsureLyricLayer(PvLayerConfig layer, bool exists, string[] afterIds, string[] beforeIds)
    {
        if (exists) return;

        foreach (var id in afterIds)
        {
            var index = _scene.Layers.FindIndex(l => l?.Id == id);
            if (index < 0) continue;
            _scene.Layers.Insert(index + 1, layer);
            return;
        }

        foreach (var id in beforeIds)
        {
            var index = _scene.Layers.FindIndex(l => l?.Id == id);
            if (index < 0) continue;
            _scene.Layers.Insert(index, layer);
            return;
        }

        _scene.Layers.Add(layer);
    }

    private static void CopyLayerTransform(PvLayerConfig from, PvLayerConfig to)
    {
        to.Enabled = from.Enabled;
        to.Opacity = from.Opacity;
        to.Scale = from.Scale;
        to.OffsetX = from.OffsetX;
        to.OffsetY = from.OffsetY;
        to.Rotation = from.Rotation;
        to.MirrorX = from.MirrorX;
        to.Cover = from.Cover;
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
        Parallax = new PvParallaxConfig { Depth = 1, RotationFactor = 1, ScaleFactor = 1, Defocus = 1 },
        VisualBlend = new PvVisualBlendConfig
        {
            AmbientTintIntensity = 0,
            RimGlow = 0,
            DropShadow = 0,
            ShadowOffsetY = 0,
            AntiBleedMargin = 0,
        },
    };

    private static void EnsureLegacyCamera(PvLayerConfig layer, bool background)
    {
        layer.Parallax ??= new PvParallaxConfig();
        var p = layer.Parallax;
        if (p.Depth <= 0.001 && p.RotationFactor <= 0.001 && p.ScaleFactor <= 0.001
            && p.Defocus <= 0.001 && (p.Follow is null || p.Follow <= 0.001))
        {
            p.Depth = 1;
            p.RotationFactor = 1;
            p.ScaleFactor = 1;
            p.Defocus = 1;
        }

        layer.VisualBlend ??= new PvVisualBlendConfig();
        if (layer.VisualBlend.AntiBleedMargin <= 0.001)
            layer.VisualBlend.AntiBleedMargin = background ? 0.2 : 0.02;
    }

    private void RegisterLayerProvider(PvLayerConfig layer)
    {
        if (WantsAsset(layer))
        {
            try
            {
                var provider = new ImageLayerProvider(layer.Id, _renderer.Device, _d2d.Ctx, layer.AssetPath!);
                _renderer.Stage.Register(provider);
                Log.Info($"[PV 管线] 图片图层已就绪：'{layer.Id}' <- '{layer.AssetPath}'");
            }
            catch (Exception ex)
            {
                Log.Warn($"[PV 管线] 图片图层加载失败：{layer.Id} <- {layer.AssetPath}：{ex.Message}");
            }
            return;
        }

        if (WantsSpout(layer))
        {
            var provider = new SpoutSourceProvider(layer.Id, _renderer.Device, _d2d.Ctx, layer.SpoutSenderName!,
                layer.Premultiplied);
            _renderer.Stage.Register(provider);
            _spoutProviders.Add(provider);
            return;
        }

    }

    private void ApplyLayerOps()
    {
        var changed = false;

        while (_pendingAdds.TryDequeue(out var sender))
        {
            var id = $"src_{Interlocked.Increment(ref _layerSeq)}";
            var layer = NewSourceLayer(id, sender);
            _scene.Layers = WithLayerAdded(_scene.Layers, layer, _legacyComposite);
            RegisterLayerProvider(layer);
            changed = true;
            Log.Info($"[PV 管线] 新建图层 '{id}' <- '{sender}'");
        }

        while (_pendingAssets.TryDequeue(out var path))
        {
            var id = $"img_{Interlocked.Increment(ref _layerSeq)}";
            var layer = NewAssetLayer(id, path);
            _scene.Layers = WithLayerAdded(_scene.Layers, layer, false);
            RegisterLayerProvider(layer);
            changed = true;
            Log.Info($"[PV 管线] 新建图片图层 '{id}' <- '{path}'");
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

        if (_legacyComposite && !CanUseLegacyComposite(_scene, _effects.Snapshot))
            CompositeInvalidated?.Invoke();

        if (changed)
        {
            LayersChanged?.Invoke();
        }
    }

    public static List<PvLayerConfig> WithLayerAdded(IReadOnlyList<PvLayerConfig> layers, PvLayerConfig layer,
        bool aboveComposite = false)
    {
        var next = new List<PvLayerConfig>(layers);
        if (aboveComposite)
        {
            int hud = next.FindIndex(l => l.Id == LegacyPvProvider.FrontHudId);
            if (hud >= 0)
            {
                next.Insert(hud + 1, layer);
                return next;
            }
        }

        int front = next.FindIndex(l => l.Id == LegacyPvProvider.FrontLayoutId);
        if (front < 0) front = next.FindIndex(l => l.Id == LegacyPvProvider.FrontId);
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

    public static PvCamState ClampCamera(PvCamState state, int width, int height) => state with
    {
        X = Math.Clamp(Finite(state.X, 0), -width, width),
        Y = Math.Clamp(Finite(state.Y, 0), -height, height),
        Scale = Math.Clamp(Finite(state.Scale, 1), 0.5, 2),
        Rotation = Math.Clamp(Finite(state.Rotation, 0), -180, 180),
    };

    public static PvLayerState EvaluateLayer(PvLayerConfig layer, DirectorEvaluator? curves, double time) =>
        SanitizeLayer(BaseLayer(layer, curves, time));

    public static PvCamState EvaluateCamera(PvCameraConfig camera, int width, int height, DirectorEvaluator? curves,
        double time, PvLayerConfig? background, in PvLayerState backgroundState, PvLayerImage? backgroundImage,
        bool globalParallax)
    {
        var cam = ClampCamera(BaseCamera(camera, curves, time), width, height);
        if (background is null || backgroundImage is not { } image || !image.HasContent) return cam;

        return PvParallax.LimitToLayer(width, height, background, backgroundState, cam, image, globalParallax);
    }

    public static PvCamState CharacterCameraState(PvLayerConfig character, PvCamState globalCam,
        DirectorEvaluator? curves, double time, bool cameraAffectsCharacter)
    {
        if (cameraAffectsCharacter) return globalCam;
        if (curves is not null && curves.TryLayerCameraAt(PvScene.CharacterLayerId, time, out var layerCam))
            return CharacterCamera(character, globalCam, layerCam);
        return globalCam with { X = 0, Y = 0 };
    }

    private void ApplyLayerFrame(double time, PvFrameState frame)
    {
        var layers = _scene.Layers;
        var character = _scene.CharacterLayer;
        for (var i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            var state = EvaluateLayer(layer, _director, time);

            if (ReferenceEquals(layer, character))
            {
                state = state with
                {
                    CameraOverride = CharacterCameraState(layer, frame.Camera, _director, time,
                        _scene.CameraAffectsCharacter),
                };
            }
            else if (_director is not null && _director.TryLayerCameraAt(layer.Id, time, out var layerCam))
            {
                state = ApplyLayerCamera(state, layerCam, _scene.Width, _scene.Height);
            }

            frame.SetLayer(layer.Id, state);
        }
    }

    private static PvLayerState SanitizeLayer(PvLayerState state) => state with
    {
        OffsetX = Math.Clamp(Finite(state.OffsetX, 0), -1, 1),
        OffsetY = Math.Clamp(Finite(state.OffsetY, 0), -1, 1),
        Scale = Math.Clamp(Finite(state.Scale, 1), 0.1, 5),
        Opacity = Math.Clamp(Finite(state.Opacity, 1), 0, 1),
        Rotation = Math.Clamp(Finite(state.Rotation, 0), -180, 180),
    };

    private static PvCamState CharacterCamera(PvLayerConfig layer, PvCamState globalCam, PvCamState layerCam)
    {
        var parallax = layer.Parallax;
        double follow = Math.Clamp(parallax?.Follow ?? parallax?.Depth ?? 0, 0, 1);
        double own = 1 - follow;
        return new PvCamState(
            0,
            0,
            Mix(globalCam.Scale, layerCam.Scale, own),
            Mix(globalCam.Rotation, layerCam.Rotation, own),
            Mix(globalCam.SkewX, layerCam.SkewX, own),
            MixOpt(globalCam.ScaleX, layerCam.ScaleX, own),
            MixOpt(globalCam.ScaleY, layerCam.ScaleY, own),
            Mix(globalCam.BlurSigma, layerCam.BlurSigma, own),
            Mix(globalCam.Brighten, layerCam.Brighten, own));
    }

    private static double Mix(double a, double b, double t) => a + (b - a) * t;

    private static double? MixOpt(double? a, double? b, double t) =>
        a is null && b is null ? null : Mix(a ?? 1, b ?? 1, t);

    public static PvLayerState ApplyLayerCamera(PvLayerState state, PvCamState cam, int width, int height) =>
        state with
        {
            OffsetX = state.OffsetX + cam.X / Math.Max(1, width),
            OffsetY = state.OffsetY + cam.Y / Math.Max(1, height),
            Scale = state.Scale * Math.Max(0.01, cam.Scale),
            Rotation = state.Rotation + cam.Rotation,
        };

    private PvLayerConfig? FindBackground(out PvLayerImage image)
    {
        image = default;
        var layers = _scene.Layers;
        for (var i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            if (!layer.Enabled || layer.Kind != PvLayerKind.Background) continue;
            if (_scene.IsCharacterLayer(layer)) continue;
            if (!_renderer.Stage.LastImages.TryGetValue(layer.Id, out var found) || !found.HasContent) continue;
            image = found;
            return layer;
        }

        return null;
    }

    private PvLayerState EvaluateLayerCamera(PvLayerConfig layer, double time)
    {
        var state = EvaluateLayer(layer, _director, time);
        if (_director is not null && _director.TryLayerCameraAt(layer.Id, time, out var layerCam))
            state = ApplyLayerCamera(state, layerCam, _scene.Width, _scene.Height);
        return state;
    }

    private void ApplyCamera(double time, PvFrameState frame)
    {
        var background = FindBackground(out var image);
        var backgroundState = background is null ? default : EvaluateLayerCamera(background, time);
        frame.Camera = EvaluateCamera(_scene.Camera ?? new PvCameraConfig(), _scene.Width, _scene.Height,
            _director, time, background, backgroundState, image, _scene.GlobalParallaxEnabled);
        _renderer.Stage.Energy = 0;
    }

    private static string HexFromRgb(double r, double g, double b) =>
        $"#{ToByte(r):X2}{ToByte(g):X2}{ToByte(b):X2}";

    private static int ToByte(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);

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
                if (param.Kind == EffectParamKind.Color)
                {
                    var rAddr = new ParamAddress(ParamAddressKind.Effect, node.InstanceId, param.Key + ".r");
                    var gAddr = new ParamAddress(ParamAddressKind.Effect, node.InstanceId, param.Key + ".g");
                    var bAddr = new ParamAddress(ParamAddressKind.Effect, node.InstanceId, param.Key + ".b");
                    if (curves is null
                        || (!curves.Affects(rAddr) && !curves.Affects(gAddr) && !curves.Affects(bAddr)))
                        continue;

                    var (br, bg, bb) = EffectNode.ParseHex(node.ColorValue(param));
                    var r = curves.Affects(rAddr) ? curves.Resolve(rAddr, time, br) : br;
                    var g = curves.Affects(gAddr) ? curves.Resolve(gAddr, time, bg) : bg;
                    var b = curves.Affects(bAddr) ? curves.Resolve(bAddr, time, bb) : bb;
                    node.SetColor(param, HexFromRgb(r, g, b));
                    continue;
                }

                if (param.Kind is not (EffectParamKind.Number or EffectParamKind.Bool)) continue;
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

public sealed class SnapshotSlot<T> where T : class
{
    private volatile T _snapshot;

    public SnapshotSlot(T initial) => _snapshot = initial;

    public T Snapshot => _snapshot;

    public void Publish(T snapshot) => _snapshot = snapshot;
}
