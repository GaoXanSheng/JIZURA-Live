using System.Diagnostics;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using JizuraLive.Vts;
using Vortice.Direct2D1;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using DCommonAlphaMode = Vortice.DCommon.AlphaMode;
using DCommonPixelFormat = Vortice.DCommon.PixelFormat;

namespace RenderProbe;

/// <summary>
/// 纯 GPU 渲染核心的实时路径验证：
/// **DirectWrite 矢量字形 + D3D11 效果图 + D2D 合成 + flip 交换链垂直同步呈现**。
/// </summary>
internal sealed class ProbeRenderer : IDisposable
{
    private const int EngineW = 1920;
    private const int EngineH = 1080;

    private readonly PresentHost _host;
    private readonly Stopwatch _sw = Stopwatch.StartNew();
    private readonly string[] _fxArgs;

    private D3DDevice? _gpu;
    private D2DHost? _d2d;
    private ID2D1Bitmap1? _target;
    private int _tw, _th;

    private OffscreenTarget? _offscreen;
    private ID3D11ShaderResourceView? _offscreenSrv;
    private GpuEffectRunner? _runner;
    private EffectGraph? _graph;
    private double _engineT;
    private string _mode = "(未初始化)";

    private readonly Dictionary<IntPtr, ID2D1Bitmap1> _texBitmaps = new();

    private readonly bool _vtsSim;
    private VtsApi? _vts;
    private VtsTrackingDriver? _vtsDriver;
    private readonly DriveSink _drive = new();
    private EffectNode? _flashNode;
    private EffectNode? _raysNode;

    private sealed class DriveSink : IVtsEffectTarget
    {
        private VtsEffectDrive _d = VtsEffectDrive.Idle;
        public void Apply(in VtsEffectDrive drive) => _d = drive;
        public VtsEffectDrive Read() => _d;
    }

    private long _lastReportMs;
    private long _reportBaseFrames;
    private double _sumFxMs, _sumCompositeMs;

    public string AdapterName { get; private set; } = "(未建)";
    public string Mode => _mode;

    public ProbeRenderer(PresentHost host, string[] fxArgs, bool vtsSim)
    {
        _host = host;
        _fxArgs = fxArgs;
        _vtsSim = vtsSim;
        host.Ready += OnReady;
        host.Prepare += OnPrepare;
        host.Frame += OnFrame;
        host.Resized += OnResized;
    }

    private void OnReady(PresentHost host)
    {
        _gpu = host.Gpu!;
        _d2d = D2DHost.Create(_gpu);
        AdapterName = _gpu.AdapterName;

        BindTarget();
        _runner = new GpuEffectRunner(_gpu);
        _graph = BuildGraph(_fxArgs);
        _mode = $"纯 GPU 渲染链路｜效果图：{_graph.Nodes.Count} 段";

        StartTracking();
        Console.WriteLine($"[Probe] 就绪：{AdapterName}｜{_mode}");
    }

    private void StartTracking()
    {
        if (_vtsSim)
        {
            Console.WriteLine("[Probe] 追踪源：合成（--vts-sim）");
            _sim = new VtsTrackingProbe();
            _vtsDriver = new VtsTrackingDriver(_sim, _drive);
            _vtsDriver.Start();
            return;
        }

        _vts = new VtsApi();
        _vts.Start();
        _vtsDriver = new VtsTrackingDriver(_vts.Tracking, _drive);
        _vtsDriver.Start();
        Console.WriteLine("[Probe] 追踪源：VTube Studio 插件 API");
    }

    private VtsTrackingProbe? _sim;

    private void TickSimTracking()
    {
        if (_sim is null) return;
        var t = _sw.Elapsed.TotalSeconds;
        var blink = (t % 3.0) < 0.15;
        _sim.Set(new VtsTrackingState(
            FaceFound: true,
            HeadX: Math.Sin(t * 0.8) * 0.7,
            HeadY: Math.Cos(t * 0.6) * 0.5,
            HeadZ: Math.Sin(t * 0.4) * 0.3,
            HeadAngleX: 0, HeadAngleY: 0, HeadAngleZ: Math.Sin(t * 0.7) * 12,
            BodyAngleX: 0, BodyAngleZ: 0,
            EyeOpenLeft: blink ? 0.05 : 1, EyeOpenRight: blink ? 0.05 : 1,
            MouthOpen: Math.Max(0, Math.Sin(t * 5.2)) * 0.85,
            MouthSmile: 0.4,
            HandLeftFound: true, HandRightFound: true,
            HandLeftX: Math.Cos(t * 0.5) * 0.6, HandLeftY: Math.Sin(t * 0.5) * 0.4,
            HandRightX: Math.Cos(t * 0.5) * 0.6, HandRightY: Math.Sin(t * 0.5) * 0.4,
            AtMs: Environment.TickCount64));
    }

    private EffectGraph BuildGraph(string[] fxArgs)
    {
        var g = new EffectGraph();
        if (fxArgs.Length == 0)
        {
            g.Add("colorGrade", n =>
            {
                var ps = EffectCatalog.Get("colorGrade")!.Params;
                n.Set(ps[0], 1.12f);
                n.Set(ps[1], 1.05f);
            });
            g.Add("glow", n => n.Set(EffectCatalog.Get("glow")!.Params[1], 0.7f));
            g.Add("grain", n => n.Set(EffectCatalog.Get("grain")!.Params[0], 0.02f));
            g.Add("vignette", n => n.Set(EffectCatalog.Get("vignette")!.Params[0], 0.35f));
            AddTrackingNodes(g);
            return g;
        }

        foreach (var raw in fxArgs)
        foreach (var id in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (EffectCatalog.Get(id) is null)
                Console.WriteLine($"[Probe] 未知效果「{id}」");
            else
                g.Add(id);
        }

        AddTrackingNodes(g);
        return g;
    }

    private void AddTrackingNodes(EffectGraph g)
    {
        g.Add("flash", n =>
        {
            n.Enabled = false;
            n.Set(EffectCatalog.Get("flash")!.Params[0], 0f);
        });
        _flashNode = g.Nodes[^1];

        g.Add("lightRays", n =>
        {
            n.Enabled = false;
            n.Set(EffectCatalog.Get("lightRays")!.Params[1], 1.2f);
        });
        _raysNode = g.Nodes[^1];
    }

    private void OnResized(PresentHost host)
    {
        BindTarget();
    }

    private void EnsureOffscreen(int w, int h)
    {
        if (_offscreen is not null && _offscreen.Width == w && _offscreen.Height == h) return;
        _offscreenSrv?.Dispose();
        _offscreen?.Dispose();
        _offscreen = OffscreenTarget.Create(_gpu!, _d2d!.Ctx, w, h, sampleable: true);
        _offscreenSrv = _gpu!.Device.CreateShaderResourceView(_offscreen.Texture);
    }

    private void OnPrepare(PresentHost host)
    {
        TickSimTracking();
        _engineT = _sw.Elapsed.TotalSeconds;
    }

    private void OnFrame(PresentHost host, double dt)
    {
        if (_d2d is null || _target is null || _gpu is null || _runner is null || _graph is null) return;

        int w = host.Target?.Width ?? 1280;
        int h = host.Target?.Height ?? 720;
        EnsureOffscreen(w, h);

        // 1) 绘制矢量文本与几何底图
        var d = _d2d.Draw;
        d.SetTarget(_offscreen!.Bitmap, w, h);
        d.Begin();
        d.Clear(new Color4(0.04f, 0.04f, 0.06f, 1f));

        var font = d.Fonts.Get("Microsoft YaHei UI", 64f, weight: Vortice.DirectWrite.FontWeight.Bold);
        d.DrawText("JIZURA PURE GPU PV ENGINE", w / 2f, h / 2f, TextAlign2D.Center, font, new Paint2D
        {
            Brush = new SolidBrush2D(new Color4(1f, 1f, 1f, 1f)),
        });
        d.End();

        // 2) 跑效果图
        var drive = _drive.Read();
        if (_flashNode is not null)
        {
            _flashNode.Enabled = drive.Flash > 0.01;
            _flashNode.Set(EffectCatalog.Get("flash")!.Params[0], (float)drive.Flash);
        }

        var t0 = Stopwatch.GetTimestamp();
        var graph = _graph.BuildRenderGraph(_runner.Shaders, (float)_engineT, w, h);
        var output = _runner.RunGraph(graph, _offscreenSrv!, w, h);
        var t1 = Stopwatch.GetTimestamp();

        // 3) 呈现至交换链
        var bitmap = BitmapFor(output);
        d.SetTarget(_target, w, h);
        d.Begin();
        d.Clear(new Color4(0f, 0f, 0f, 1f));
        d.DrawBitmap(bitmap, new Rect(0, 0, w, h));
        d.DrawText($"{_graph.Nodes.Count} 段效果 · {host.Frames} 帧 · {host.Fps:F1} fps",
            12f, h - 16f, TextAlign2D.Left, d.Fonts.Get("Consolas", 16),
            new Paint2D { Brush = new SolidBrush2D(new Color4(1f, 1f, 1f, 0.75f)) });
        d.End();

        _sumFxMs += (t1 - t0) * 1000.0 / Stopwatch.Frequency;
        _sumCompositeMs += (Stopwatch.GetTimestamp() - t1) * 1000.0 / Stopwatch.Frequency;
        _runner.Pool.Return(output);

        Report(host);
    }

    private ID2D1Bitmap1 BitmapFor(GpuTexturePool.Entry entry)
    {
        var key = entry.Texture.NativePointer;
        if (_texBitmaps.TryGetValue(key, out var bmp)) return bmp;

        using var surface = entry.Texture.QueryInterface<IDXGISurface>();
        bmp = _d2d!.Ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.None));
        _texBitmaps[key] = bmp;
        return bmp;
    }

    private void BindTarget()
    {
        _target?.Dispose();
        _target = null;
        if (_d2d is null || _host.Target is null) return;

        using var back = _host.Target.GetBackBuffer();
        using var surface = back.QueryInterface<IDXGISurface>();
        _tw = _host.Target.Width;
        _th = _host.Target.Height;
        _target = _d2d.Ctx.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new DCommonPixelFormat(Format.B8G8R8A8_UNorm, DCommonAlphaMode.Premultiplied),
            96f, 96f, BitmapOptions.Target | BitmapOptions.CannotDraw));
    }

    private void Report(PresentHost host)
    {
        var ms = _sw.ElapsedMilliseconds;
        if (ms - _lastReportMs < 1000) return;

        var frames = host.Frames - _reportBaseFrames;
        var elapsedSec = (ms - _lastReportMs) / 1000.0;
        var fps = elapsedSec > 0 ? frames / elapsedSec : 0;
        var avgFx = frames > 0 ? _sumFxMs / frames : 0;
        var avgComp = frames > 0 ? _sumCompositeMs / frames : 0;

        Console.WriteLine($"[Probe] {_tw}x{_th} 帧率: {fps:F1} fps｜特效: {avgFx:F2}ms｜呈现: {avgComp:F2}ms");

        _lastReportMs = ms;
        _reportBaseFrames = host.Frames;
        _sumFxMs = 0;
        _sumCompositeMs = 0;
    }

    public void Dispose()
    {
        _vtsDriver?.Dispose();
        _vts?.Dispose();
        _offscreenSrv?.Dispose();
        _offscreen?.Dispose();
        foreach (var b in _texBitmaps.Values) b.Dispose();
        _texBitmaps.Clear();
        _runner?.Dispose();
        _target?.Dispose();
        _d2d?.Dispose();
    }
}
