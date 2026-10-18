using JizuraLive.Logging;
using JizuraLive.Render.Gpu;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using DCommonPixelFormat = Vortice.DCommon.PixelFormat;

namespace JizuraLive.Pv;

/// <summary>
/// 原版 PV 渲染宿主：同一份 Plan 出两张图——不透明背景层 + 透明文字层，
/// 舞台把角色插在中间，人物就不会再盖住歌词。
/// </summary>
public sealed class LegacyPvProvider : IDisposable
{
    public const string BackId = "legacy_pv_back";
    public const string FrontId = "legacy_pv_front";

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

    private readonly LayerTarget _back = new();
    private readonly LayerTarget _front = new();

    private volatile bool _running = true;
    private volatile int _cutCount;
    private volatile string? _error;
    private volatile LegacyPvPlanInfo? _planInfo;
    private LegacyPvEngine? _syncEngine;
    private volatile LegacyPvEngine? _engine;
    private volatile CameraTrack? _cameraTrack;

    private double _requestedTime;
    private double _lastRenderedTime = double.NaN;

    public string Id { get; }
    public IPvLayerProvider Back { get; }
    public IPvLayerProvider Front { get; }
    public int CutCount => _cutCount;
    public LegacyPvPlanInfo? PlanInfo => _planInfo;
    public event Action<LegacyPvPlanInfo>? PlanReady;

    /// <summary>舞台层接管转场时置真，原版引擎不再自己画 cut 间转场（morph 保留）。</summary>
    public volatile bool NoCutTrans;

    /// <summary>舞台层统一控制机位时置真，原版 cut 相机不再叠加。</summary>
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

        Back = new LayerProvider(this, front: false);
        Front = new LayerProvider(this, front: true);

        if (options.Synchronous)
        {
            _syncEngine = new LegacyPvEngine(options.LrcText, options.AudioPath!, options.Style,
                options.Seed, designWidth, designHeight, fps);
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

    /// <summary>舞台层编辑过的运镜轨道：写回原版 Plan，原版画面与角色一起变。</summary>
    public void ApplyCameraTrack(CameraTrack? track)
    {
        _cameraTrack = track;
        (_engine ?? _syncEngine)?.ApplyCameraTrack(track);
    }

    public void ApplyCutOverrides(LegacyPvPlanInfo info) =>
        (_engine ?? _syncEngine)?.ApplyCutOverrides(info);

    private PvLayerImage PumpLayer(in PvFrameContext ctx, bool front)
    {
        var target = front ? _front : _back;

        if (_syncEngine is not null)
        {
            if (ctx.Time != target.LastTime)
            {
                var rendered = _syncEngine.Render(ctx.Time, _renderWidth, false, NoCutTrans,
                    front ? "front" : "back", front, NoCamera);
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

    private void RenderLoop()
    {
        LegacyPvEngine? engine = null;
        try
        {
            engine = new LegacyPvEngine(_options.LrcText, _options.AudioPath!, _options.Style,
                _options.Seed, _designWidth, _designHeight, _fps);
            if (engine.Error is not null)
            {
                _error = engine.Error;
                Log.Warn("[PV/原版引擎] 初始化失败：" + engine.Error);
                return;
            }

            _cutCount = engine.CutCount;
            _planInfo = engine.PlanInfo;
            _engine = engine;
            if (_cameraTrack is { } track) engine.ApplyCameraTrack(track);
            PlanReady?.Invoke(_planInfo);
            Log.Info($"[PV/原版引擎] 就绪：{engine.CutCount} 个切分，{_renderWidth}px 渲染（背景/文字分层）");

            while (_running)
            {
                _wake.WaitOne(4);
                if (!_running) break;

                var time = Volatile.Read(ref _requestedTime);
                if (time == _lastRenderedTime) continue;

                var back = engine.Render(time, _renderWidth, false, NoCutTrans, "back", false, NoCamera);
                var front = engine.Render(time, _renderWidth, false, NoCutTrans, "front", true, NoCamera);
                if (back is null || front is null)
                {
                    _error = engine.Error;
                    continue;
                }

                _lastRenderedTime = time;
                lock (_frameGate)
                {
                    _back.Frame = back;
                    _back.FrameVersion++;
                    _front.Frame = front;
                    _front.FrameVersion++;
                }
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
            engine?.Dispose();
        }
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
        _wake.Dispose();
        _back.Dispose();
        _front.Dispose();
    }

    private sealed class LayerProvider(LegacyPvProvider host, bool front) : IPvLayerProvider
    {
        public string Id => front ? FrontId : BackId;
        public long Version => (front ? host._front : host._back).Version;
        public PvLayerImage Pump(in PvFrameContext ctx) => host.PumpLayer(ctx, front);
        public void Dispose() { }
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
