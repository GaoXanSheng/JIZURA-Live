using System.Collections.Concurrent;
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

    // 跨线程交接只有这一条命令通道：UI 投递，渲染线程在帧首消费（图层提供者的 COM 对象必须在渲染线程建）。
    private readonly ConcurrentQueue<string> _pendingAdds = new();
    private readonly ConcurrentQueue<string> _pendingRemoves = new();
    private volatile string[]? _pendingOrder;

    private readonly List<SpoutSourceProvider> _spoutProviders = new();
    private volatile bool _stopPumping;
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

    /// <summary>
    /// 上一次 <see cref="Dispose"/> 里收尾存盘的失败原因（null = 都成功）。
    /// 关停路径上的保存失败不能吞：调用方要把它显示给用户，否则用户以为改动落盘了。
    /// </summary>
    public string? LastDisposeFault { get; private set; }

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
        IPvSubtitleSource? subtitleSource = null, bool watchSceneFile = false,
        VtsTrackingProbe? tracking = null)
    {
        ScenePath = scenePath;
        _scene = scene;
        _effects = effects;
        _watchSceneFile = watchSceneFile;
        _tracking = tracking;
        _lastWriteUtc = CurrentWriteUtc();
        SeedLayerSeq();

        EffectsPath = Path.Combine(Path.GetDirectoryName(scenePath) ?? ".", "pv_effects.json");
        _effectsWriteUtc = EffectsWriteUtc();
        LoadEffects();

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

    /// <summary>把当前场景写成 JSON。成功返回 null，失败返回原因（调用方必须让用户看见）。</summary>
    public string? SaveSceneNow()
    {
        try
        {
            var json = _scene.ToJson();
            if (string.Equals(json, _lastSaved, StringComparison.Ordinal)) return null;
            var dir = Path.GetDirectoryName(ScenePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(ScenePath, json);
            _lastSaved = json;
            _lastWriteUtc = CurrentWriteUtc();
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("[PV 管线] 保存场景失败：" + ex.Message);
            return ex.Message;
        }
    }

    /// <summary>把效果图写成 JSON。成功返回 null，失败返回原因。</summary>
    public string? SaveEffectsNow()
    {
        try
        {
            var json = EffectGraphJson.Serialize(_effects);
            var dir = Path.GetDirectoryName(EffectsPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(EffectsPath, json);
            _effectsWriteUtc = EffectsWriteUtc();
            return null;
        }
        catch (Exception ex)
        {
            Log.Warn("[PV 管线] 保存效果图失败：" + ex.Message);
            return ex.Message;
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
            // 画布尺寸不参与热重载：它是渲染资源的代际边界，改它必须重建管线（见执行契约的停机/重建），
            // 在这里悄悄赋进内存场景只会让内存与 GPU 面尺寸各说一套。
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
                Log.Warn("[PV 管线] 场景文件解析结果为空，本次会话使用默认场景（磁盘文件保持原样）");
            }
            catch (Exception ex)
            {
                // 文件存在但坏了：只在内存里回落默认，绝不写盘——旧行为会把用户场景覆盖成默认
                Log.Warn($"[PV 管线] 场景文件解析失败，本次会话使用默认场景（磁盘文件保持原样）：{ex.Message}");
            }

            return PvScene.CreateDefault();
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

        // 先让帧回调看见"没准备好"：守卫只查 Ready，不查 _disposed
        Ready = false;

        // 收尾存盘的失败要留着给调用方显示，不能像 best-effort 那样随手丢掉
        LastDisposeFault = SaveSceneNow() ?? SaveEffectsNow();

        // 所有提供者都注册在 Stage 上，释放权归 Stage（经 _renderer.Dispose()）。
        // 这里只摘掉本类的索引，绝不第二次 Dispose 同一个对象。
        _spoutProviders.Clear();
        _subtitle = null;
        _character = null;

        // D2D 位图缓存（fx 临时面→位图）先于渲染器整体释放清掉：
        // 这些位图按代际绑在 D2D 上下文上，留到进程退出才回收就等于代际泄漏
        _renderer.ClearFxBitmapCache();
        _renderer.Dispose();
        _d2d.Dispose();
    }
}
