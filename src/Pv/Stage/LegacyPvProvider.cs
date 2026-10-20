using System.Diagnostics;
using JizuraLive.LegacyEngine;
using JizuraLive.Logging;
using JizuraLive.Render.Gpu;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using DCommonPixelFormat = Vortice.DCommon.PixelFormat;

namespace JizuraLive.Pv;

public sealed class LegacyPvProvider : IDisposable
{
    public const string BackId = "legacy_pv_back";
    public const string FrontId = "legacy_pv_front";
    public const string FrontLayoutId = "legacy_pv_front_layout";
    public const string FrontDecorId = "legacy_pv_front_decor";
    public const string FrontHudId = "legacy_pv_front_hud";

    private readonly D3DDevice _gpu;
    private readonly ID2D1DeviceContext _ctx;
    private readonly LegacyPvOptions _options;
    private readonly int _designWidth;
    private readonly int _designHeight;
    private readonly int _fps;
    private readonly int _renderWidth;
    private readonly object _frameGate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;

    private readonly Part _back;
    private readonly Part _front;
    private readonly Part _frontLayout;
    private readonly Part _frontDecor;
    private readonly Part _frontHud;
    private readonly Part[] _parts;

    private volatile bool _running = true;
    private volatile int _cutCount;
    private volatile string? _error;
    private volatile LegacyPvPlanInfo? _planInfo;
    private LegacyPvEngine? _syncEngine;
    private volatile LegacyPvEngine? _engine;
    private volatile CameraTrack? _cameraTrack;
    private LegacyPvPlanInfo? _pendingCutOverrides;
    private CameraTrack? _pendingCameraTrack;
    private GpuRasterHost? _gpuHost;
    private volatile bool _gpuMode;

    private double _requestedTime;
    private double _lastRenderedTime = double.NaN;

        public double LastRenderMs { get; private set; }

    public bool GpuMode => _gpuMode;

    public string Id { get; }
    public IPvLayerProvider Back { get; }
    public IPvLayerProvider Front { get; }
    public IPvLayerProvider FrontLayout { get; }
    public IPvLayerProvider FrontDecor { get; }
    public IPvLayerProvider FrontHud { get; }
    public int CutCount => _cutCount;
    public LegacyPvPlanInfo? PlanInfo => _planInfo;
    public event Action<LegacyPvPlanInfo>? PlanReady;

        public volatile bool NoCutTrans;

        public volatile bool NoCamera;

    public LegacyPvProvider(string id, D3DDevice gpu, ID2D1DeviceContext ctx,
        LegacyPvOptions options, int designWidth, int designHeight, int fps, int renderWidth)
    {
        Id = id;
        _gpu = gpu;
        _ctx = ctx;
        _options = options;
        _designWidth = designWidth;
        _designHeight = designHeight;
        _fps = fps;
        _renderWidth = Math.Max(320, renderWidth);
        NoCutTrans = options.NoCutTrans;
        NoCamera = options.StageCamera;

        _back = new Part(BackId, "back", transparent: false, "legacy_back");
        _front = new Part(FrontId, "front_text", transparent: true, "legacy_front_text");
        _frontLayout = new Part(FrontLayoutId, "front_layout", transparent: true, "legacy_front_layout");
        _frontDecor = new Part(FrontDecorId, "front_decor", transparent: true, "legacy_front_decor");
        _frontHud = new Part(FrontHudId, "front_hud", transparent: true, "legacy_front_hud");
        _parts = [_back, _frontLayout, _front, _frontDecor, _frontHud];

        Back = new LayerProvider(this, _back);
        Front = new LayerProvider(this, _front);
        FrontLayout = new LayerProvider(this, _frontLayout);
        FrontDecor = new LayerProvider(this, _frontDecor);
        FrontHud = new LayerProvider(this, _frontHud);

        if (options.Synchronous)
        {
            _syncEngine = new LegacyPvEngine(options.LrcText, options.AudioPath!, options.Style,
                options.Seed, designWidth, designHeight, fps, options.Preset);
            _cutCount = _syncEngine.CutCount;
            _error = _syncEngine.Error;
            _planInfo = _syncEngine.PlanInfo;
            _engine = _syncEngine;
            _thread = new Thread(() => { });
            return;
        }

        _thread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "PV.LegacyRender",
            Priority = ThreadPriority.BelowNormal,
        };
        _thread.Start();
    }

    public void ApplyCutOverrides(LegacyPvPlanInfo info)
    {
        if (_syncEngine is not null)
        {
            _syncEngine.ApplyCutOverrides(info);
            return;
        }

        Interlocked.Exchange(ref _pendingCutOverrides, SnapshotCutOverrides(info));
        _wake.Set();
    }

    private static LegacyPvPlanInfo SnapshotCutOverrides(LegacyPvPlanInfo info)
    {
        var copy = new LegacyPvPlanInfo();
        foreach (var cut in info.Cuts)
        {
            if (cut is null) continue;
            copy.Cuts.Add(new LegacyPvCutInfo
            {
                Start = cut.Start,
                Layout = cut.Layout,
                Enter = cut.Enter,
                Exit = cut.Exit,
                Treat = cut.Treat,
            });
        }

        return copy;
    }

    private PvLayerImage PumpLayer(in PvFrameContext ctx, Part part)
    {
        var target = part.Target;

        if (_syncEngine is not null)
        {
            if (ctx.Time != target.LastTime)
            {
                var sw = Stopwatch.StartNew();
                var rendered = _syncEngine.Render(ctx.Time, _renderWidth, false, NoCutTrans,
                    part.Layer, part.Transparent, NoCamera);
                sw.Stop();
                LastRenderMs = sw.Elapsed.TotalMilliseconds;
                if (rendered is not null)
                {
                    Upload(target, rendered);
                    target.LastTime = ctx.Time;
                }
            }

            return target.Bitmap is null ? PvLayerImage.Empty : new PvLayerImage(target.Bitmap, null, target.Width, target.Height);
        }

        Volatile.Write(ref _requestedTime, ctx.Time);
        _wake.Set();

        if (_gpuMode) return PumpGpu(part);

        lock (_frameGate)
        {
            if (target.Frame is not null && target.FrameVersion != target.UploadedVersion)
            {
                Upload(target, target.Frame);
                target.UploadedVersion = target.FrameVersion;
            }
        }

        return target.Bitmap is null ? PvLayerImage.Empty : new PvLayerImage(target.Bitmap, null, target.Width, target.Height);
    }

    private PvLayerImage PumpGpu(Part part)
    {
        var layer = part.Gpu;
        if (layer is null) return PvLayerImage.Empty;

        GpuFrame? frame;
        lock (_frameGate)
        {
            var index = layer.Latest;
            if (index < 0) return PvLayerImage.Empty;
            layer.Shown = index;
            frame = layer.Frames[index];
        }

        if (frame.BitmapFailed) return PvLayerImage.Empty;
        var bitmap = frame.Bitmap;
        if (bitmap is null)
        {
            try
            {
                using var surface = frame.Surface.Texture.QueryInterface<IDXGISurface>();
                bitmap = _ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
                    new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
                    96f, 96f, BitmapOptions.None));
                frame.Bitmap = bitmap;
            }
            catch (Exception ex)
            {
                frame.BitmapFailed = true;
                Log.Warn("[PV/原版引擎] GPU 帧转 D2D 位图失败：" + ex.Message);
                return PvLayerImage.Empty;
            }
        }

        return new PvLayerImage(bitmap, null, frame.Surface.Width, frame.Surface.Height);
    }

    private void RenderLoop()
    {
        LegacyPvEngine? engine = null;
        GpuRasterHost? host = null;
        var entered = false;
        try
        {
            host = GpuRasterHost.TryGet(_gpu.Device, _gpu.AdapterName, out _);
            if (host is not null)
            {
                try
                {
                    host.Enter();
                    entered = true;
                }
                catch (Exception ex)
                {
                    Log.Warn("[PV/原版引擎] GPU 上下文绑定失败，回退 CPU：" + ex.Message);
                    host = null;
                }
            }

            engine = new LegacyPvEngine(_options.LrcText, _options.AudioPath!, _options.Style,
                _options.Seed, _designWidth, _designHeight, _fps, _options.Preset);
            if (engine.Error is not null)
            {
                _error = engine.Error;
                Log.Warn("[PV/原版引擎] 初始化失败：" + engine.Error);
                return;
            }

            _cutCount = engine.CutCount;
            _planInfo = engine.PlanInfo;
            _engine = engine;

            if (host is not null)
            {
                try
                {
                    var info = engine.PlanInfo;
                    var w = _renderWidth;
                    var h = Math.Max(1, (int)Math.Round(info.Height * (w / (double)Math.Max(1, info.Width))));
                    foreach (var part in _parts)
                    {
                        var gpu = new GpuLayer(host, part.GpuKey, w, h);
                        if (gpu.Frames[0].Surface.DxgiFormat != (int)Format.B8G8R8A8_UNorm)
                            throw new InvalidOperationException(
                                $"ANGLE 表面格式不是 BGRA8：{gpu.Frames[0].Surface.DxgiFormat}");
                        part.Gpu = gpu;
                    }
                    engine.UseGpu(host);
                    _gpuHost = host;
                    _gpuMode = true;
                }
                catch (Exception ex)
                {
                    Log.Warn("[PV/原版引擎] GPU 光栅初始化失败，回退 CPU：" + ex.Message);
                    _gpuMode = false;
                    foreach (var part in _parts)
                    {
                        part.Gpu?.DisposeBitmaps();
                        part.Gpu = null;
                    }
                    engine.UseCpu();
                }
            }

            if (_cameraTrack is { } track)
            {
                engine.ApplyCameraTrack(track);
                Interlocked.Exchange(ref _pendingCameraTrack, null);
            }

            ApplyPending(engine);
            PlanReady?.Invoke(_planInfo);
            Log.Info($"[PV/原版引擎] 就绪：{engine.CutCount} 个切分，{_renderWidth}px 渲染"
                     + $"（{(_gpuMode ? "GPU" : "CPU")} 光栅，背景/文字分层）");

            while (_running)
            {
                _wake.WaitOne(4);
                if (!_running) break;

                var dirty = ApplyPending(engine);
                var time = Volatile.Read(ref _requestedTime);
                if (!dirty && time == _lastRenderedTime) continue;

                var sw = Stopwatch.StartNew();
                var ok = _gpuMode ? RenderGpuFrame(engine, time) : RenderCpuFrame(engine, time);
                sw.Stop();
                LastRenderMs = sw.Elapsed.TotalMilliseconds;
                if (!ok)
                {
                    _error = engine.Error;
                    continue;
                }

                _lastRenderedTime = time;
            }
        }
        catch (Exception ex)
        {
            _error = ex.Message;
            Log.Error("[PV/原版引擎] 渲染线程异常：" + ex.Message, ex);
        }
        finally
        {
            _engine = null;
            _gpuMode = false;
            try
            {
                engine?.Dispose();
            }
            finally
            {
                if (entered) host!.Exit();
            }
        }
    }

    private bool RenderCpuFrame(LegacyPvEngine engine, double time)
    {
        foreach (var part in _parts)
        {
            var frame = engine.Render(time, _renderWidth, false, NoCutTrans,
                part.Layer, part.Transparent, NoCamera);
            if (frame is null) return false;

            lock (_frameGate)
            {
                part.Target.Frame = frame;
                part.Target.FrameVersion++;
            }
        }

        return true;
    }

    private bool RenderGpuFrame(LegacyPvEngine engine, double time)
    {
        foreach (var part in _parts)
        {
            var layer = part.Gpu;
            if (layer is null) continue;
            var frame = layer.Acquire(_frameGate);

            _gpuHost!.Activate(frame.Surface);
            engine.RenderTo(frame.Surface, time, NoCutTrans, part.Layer, part.Transparent, NoCamera);
            _gpuHost!.FlushAndSync();

            lock (_frameGate)
            {
                layer.Latest = frame.Index;
                part.Target.Version++;
            }
        }

        return true;
    }

    private bool ApplyPending(LegacyPvEngine engine)
    {
        var dirty = false;
        var track = Interlocked.Exchange(ref _pendingCameraTrack, null);
        if (track is not null)
        {
            engine.ApplyCameraTrack(track);
            dirty = true;
        }

        var cuts = Interlocked.Exchange(ref _pendingCutOverrides, null);
        if (cuts is not null)
        {
            engine.ApplyCutOverrides(cuts);
            dirty = true;
        }

        return dirty;
    }

    private void Upload(LayerTarget target, LegacyPvFrame frame)
    {
        if (target.Texture is null || target.Width != frame.Width || target.Height != frame.Height)
        {
            target.Dispose();
            target.Texture = _gpu.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)frame.Width,
                Height = (uint)frame.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.None,
            });
            target.Width = frame.Width;
            target.Height = frame.Height;
            using var surface = target.Texture.QueryInterface<IDXGISurface>();
            target.Bitmap = _ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
                new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
                96f, 96f, BitmapOptions.None));
        }

        _gpu.Context.UpdateSubresource(frame.Bgra, target.Texture, 0,
            (uint)(frame.Width * 4), (uint)(frame.Width * 4 * frame.Height), null);
        target.Version++;
    }

    public void Dispose()
    {
        if (_syncEngine is not null)
        {
            _syncEngine.Dispose();
            _syncEngine = null;
        }
        else
        {
            _running = false;
            _wake.Set();
            try { _thread.Join(3000); } catch { }
        }

        _engine = null;
        _gpuMode = false;
        _gpuHost = null;
        foreach (var part in _parts)
        {
            part.Gpu?.DisposeBitmaps();
            part.Gpu = null;
            part.Target.Dispose();
        }
        _wake.Dispose();
    }

    private sealed class Part(string id, string layer, bool transparent, string gpuKey)
    {
        public string Id { get; } = id;
        public string Layer { get; } = layer;
        public bool Transparent { get; } = transparent;
        public string GpuKey { get; } = gpuKey;
        public LayerTarget Target { get; } = new();
        public GpuLayer? Gpu;
    }

    private sealed class LayerProvider(LegacyPvProvider host, Part part) : IPvLayerProvider
    {
        public string Id => part.Id;
        public long Version => part.Target.Version;
        public PvLayerImage Pump(in PvFrameContext ctx) => host.PumpLayer(ctx, part);
        public void Dispose() { }
    }

    private sealed class GpuLayer
    {
        public readonly GpuFrame[] Frames;
        public int Latest = -1;
        public int Shown = -1;

        private int _next;

        public GpuLayer(GpuRasterHost host, string key, int width, int height)
        {
            Frames = new GpuFrame[3];
            for (var i = 0; i < Frames.Length; i++)
                Frames[i] = new GpuFrame(host.Rent($"{key}{i}", width, height), i);
        }

        public GpuFrame Acquire(object gate)
        {
            lock (gate)
            {
                for (var i = 0; i < Frames.Length; i++)
                {
                    var index = (_next + i) % Frames.Length;
                    if (index == Latest || index == Shown) continue;
                    _next = (index + 1) % Frames.Length;
                    return Frames[index];
                }
            }

            var fallback = Frames[_next];
            _next = (_next + 1) % Frames.Length;
            return fallback;
        }

        public void DisposeBitmaps()
        {
            foreach (var frame in Frames)
            {
                frame.Bitmap?.Dispose();
                frame.Bitmap = null;
            }
        }
    }

    private sealed class GpuFrame(GpuSurface surface, int index)
    {
        public GpuSurface Surface { get; } = surface;
        public int Index { get; } = index;
        public ID2D1Bitmap? Bitmap;
        public bool BitmapFailed;
    }

    private sealed class LayerTarget : IDisposable
    {
        public ID3D11Texture2D? Texture;
        public ID2D1Bitmap? Bitmap;
        public int Width;
        public int Height;
        public long Version;
        public long UploadedVersion;
        public LegacyPvFrame? Frame;
        public long FrameVersion;
        public double LastTime = double.NaN;

        public void Dispose()
        {
            Bitmap?.Dispose();
            Bitmap = null;
            Texture?.Dispose();
            Texture = null;
            Width = 0;
            Height = 0;
        }
    }
}
