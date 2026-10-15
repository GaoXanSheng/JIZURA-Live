using System.IO;
using JizuraLive.Logging;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using JizuraLive.Vts;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JizuraLive.Pv;

/// <summary>
/// PV 管线的所有者：场景文档 + 图层栈 + 效果图 + 各图层提供者 + 存盘。
/// 全链路纯 GPU，零线程锁，支持工作台直呈和 Spout 外部广播。
/// </summary>
public sealed class PvPipeline : IDisposable
{
    private readonly D2DHost _d2d;
    private readonly PvStageRenderer _renderer;
    private readonly PvScene _scene;
    private readonly EffectGraph _effects;

    private readonly Queue<string> _pendingAdds = new();
    private readonly Queue<string> _pendingRemoves = new();
    private volatile string[]? _pendingOrder;
    private volatile bool _saveRequested;

    private readonly List<SpoutSourceProvider> _spoutProviders = new();
    private SubtitleProvider? _subtitle;
    private SpoutSourceProvider? _character;
    private int _layerSeq;
    private string? _lastSaved;
    private bool _disposed;

    private readonly VtsTrackingProbe? _tracking;
    private VtsTrackingOptions _trackOptions = new();
    private bool _prevBlink;

    private const double TrackingCameraFrac = 0.10;
    private readonly bool _watchSceneFile;
    private DateTime _lastWriteUtc;
    private long _lastWatchAt;
    private DateTime _effectsWriteUtc;

    public string ScenePath { get; }
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

    public string EffectsPath { get; }

    public event Action? LayersChanged;
    public event Action? EffectsChanged;

    public PvPipeline(D3DDevice gpu, string scenePath, PvScene scene, EffectGraph effects,
        int designWidth = 1920, int designHeight = 1080,
        IPvSubtitleSource? subtitleSource = null, bool watchSceneFile = false,
        VtsTrackingProbe? tracking = null)
    {
        ScenePath = scenePath;
        _scene = scene;
        _effects = effects;
        _watchSceneFile = watchSceneFile;
        _tracking = tracking;
        _lastWriteUtc = CurrentWriteUtc();

        EffectsPath = Path.Combine(Path.GetDirectoryName(scenePath) ?? ".", "pv_effects.json");
        _effectsWriteUtc = EffectsWriteUtc();
        LoadEffects();

        try
        {
            _d2d = D2DHost.Create(gpu);
            _renderer = new PvStageRenderer(gpu, _d2d, scene, effects, designWidth, designHeight);
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

    private int _frameW, _frameH;

    public void Prepare(double time, int width, int height)
    {
        ApplyLayerOps();

        if (_saveRequested)
        {
            _saveRequested = false;
            SaveSceneNow();
        }

        if (_watchSceneFile)
        {
            var nowMs = Environment.TickCount64;
            if (nowMs - _lastWatchAt >= SceneWatchMs)
            {
                _lastWatchAt = nowMs;
                ReloadSceneIfChanged();
                ReloadEffectsIfChanged();
            }
        }

        foreach (var p in _spoutProviders) p.PumpFrame();

        _frameW = width;
        _frameH = height;
        ApplyTracking();

        _subtitle?.Render(time, width, height);
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
    public void RequestSave() => _saveRequested = true;

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
            if (layer.Id == CharacterLayerId) _character = provider;
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
                _renderer.Stage.Remove(id);
                var p = _spoutProviders.FirstOrDefault(x => x.Id == id);
                if (p is not null)
                {
                    _spoutProviders.Remove(p);
                    p.Dispose();
                }
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
            RequestSave();
            LayersChanged?.Invoke();
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

        _renderer.Stage.Camera = new PvCamState(
            drive.CameraX * _frameW * TrackingCameraFrac,
            drive.CameraY * _frameH * TrackingCameraFrac,
            Math.Clamp(drive.Zoom, 0.5, 2.0),
            0, 0, null, null);
        _renderer.Stage.Energy = drive.MouthEnergy;
    }

    public void SaveSceneNow()
    {
        try
        {
            var json = _scene.ToJson();
            if (string.Equals(json, _lastSaved, StringComparison.Ordinal)) return;
            var dir = Path.GetDirectoryName(ScenePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(ScenePath, json);
            _lastSaved = json;
            _lastWriteUtc = CurrentWriteUtc();
        }
        catch (Exception ex)
        {
            Log.Warn("[PV 管线] 保存场景失败：" + ex.Message);
        }
    }

    public void SaveEffectsNow()
    {
        try
        {
            var json = EffectGraphJson.Serialize(_effects);
            var dir = Path.GetDirectoryName(EffectsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(EffectsPath, json);
            _effectsWriteUtc = EffectsWriteUtc();
        }
        catch (Exception ex)
        {
            Log.Warn("[PV 管线] 保存效果图失败：" + ex.Message);
        }
    }

    private void LoadEffects()
    {
        if (!File.Exists(EffectsPath)) return;
        try
        {
            var json = File.ReadAllText(EffectsPath);
            var restored = EffectGraphJson.Deserialize(json);
            _effects.Nodes.Clear();
            _effects.Nodes.AddRange(restored.Nodes);
        }
        catch (Exception ex)
        {
            Log.Warn("[PV 管线] 读取效果图失败，使用空图：" + ex.Message);
        }
    }

    private const int SceneWatchMs = 1000;
    private const string CharacterLayerId = "actor";

    private DateTime CurrentWriteUtc()
    {
        try { return File.Exists(ScenePath) ? File.GetLastWriteTimeUtc(ScenePath) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    private DateTime EffectsWriteUtc()
    {
        try { return File.Exists(EffectsPath) ? File.GetLastWriteTimeUtc(EffectsPath) : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    private void ReloadSceneIfChanged()
    {
        var cur = CurrentWriteUtc();
        if (cur <= _lastWriteUtc || cur == DateTime.MinValue) return;
        _lastWriteUtc = cur;

        try
        {
            var disk = PvPipeline.LoadScene(ScenePath);
            _scene.Width = disk.Width;
            _scene.Height = disk.Height;
            _scene.AmbientTint = disk.AmbientTint;
            _scene.AmbientBlendEnabled = disk.AmbientBlendEnabled;

            var diskById = disk.Layers.ToDictionary(l => l.Id);
            foreach (var mem in _scene.Layers)
            {
                if (diskById.TryGetValue(mem.Id, out var d))
                {
                    mem.Enabled = d.Enabled;
                    mem.Scale = d.Scale;
                    mem.Cover = d.Cover;
                    mem.Parallax.Depth = d.Parallax.Depth;
                    mem.VisualBlend.AmbientTintIntensity = d.VisualBlend.AmbientTintIntensity;
                    mem.VisualBlend.RimGlow = d.VisualBlend.RimGlow;
                    mem.VisualBlend.DropShadow = d.VisualBlend.DropShadow;
                    mem.VisualBlend.AntiBleedMargin = d.VisualBlend.AntiBleedMargin;
                }
            }

            var diskIds = disk.Layers.Select(l => l.Id).ToList();
            var memIds = _scene.Layers.Select(l => l.Id).ToList();
            if (!diskIds.SequenceEqual(memIds))
            {
                _scene.Layers.Sort((a, b) => diskIds.IndexOf(a.Id).CompareTo(diskIds.IndexOf(b.Id)));
            }

            LayersChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("[PV 管线] 重载场景文件失败：" + ex.Message);
        }
    }

    private void ReloadEffectsIfChanged()
    {
        var cur = EffectsWriteUtc();
        if (cur <= _effectsWriteUtc || cur == DateTime.MinValue) return;
        _effectsWriteUtc = cur;

        try
        {
            LoadEffects();
            EffectsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("[PV 管线] 重载效果图文件失败：" + ex.Message);
        }
    }

    public static PvScene LoadScene(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                var scene = PvScene.FromJson(File.ReadAllText(path));
                if (scene is not null) return scene;
            }
            catch (Exception ex)
            {
                Log.Warn($"[PV 管线] 场景文件读取失败，重置为默认：{ex.Message}");
            }
        }

        var def = PvScene.CreateDefault();
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, def.ToJson());
        }
        catch { }
        return def;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SaveSceneNow();
        SaveEffectsNow();

        foreach (var p in _spoutProviders) p.Dispose();
        _spoutProviders.Clear();

        _subtitle?.Dispose();
        _renderer.Dispose();
        _d2d.Dispose();
    }
}
