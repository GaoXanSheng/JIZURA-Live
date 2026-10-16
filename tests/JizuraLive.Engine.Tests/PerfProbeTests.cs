using System.Diagnostics;
using JizuraLive.Engine;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using JizuraLive.Render.Text;
using Vortice.Direct3D11;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 性能探针：1080p 离屏，图片层 + 动效歌词层 + 六段全局后期（bloom 是多 pass mip 链），
/// 按 60fps 时间步逐帧渲染并在每帧末等 GPU 落地，统计 p50/p95/max。
///
/// 默认不跑：并发构建会把机器压满，那种负载下量出来的帧时没有意义（会误报）。
/// 只在 JZ_PERF=1 的机器上执行——探针的结论是「这台机器上这条链能不能守住 60fps」。
/// </summary>
public class PerfProbeTests
{
    private const int W = 1920;
    private const int H = 1080;
    private const int Frames = 600;
    private const double Dt = 1.0 / 60.0;
    private const double BudgetMs = 16.6;
    private const string ImageLayerId = "perf_image";

    private readonly ITestOutputHelper _out;

    public PerfProbeTests(ITestOutputHelper output) => _out = output;

    [GpuFact]
    public void Offscreen1080pFrameTimeStaysWithin60FpsBudget()
    {
        if (Environment.GetEnvironmentVariable("JZ_PERF") != "1") return;

        var gpu = D3DDevice.TryCreate(out var error);
        if (gpu is null) GpuRequire.Fail(error);

        using (gpu)
        {
            var scene = ProbeScene();
            var effects = ProbeEffects();
            var clips = new List<LyricClip> { new("perf", 0, 10_000, "動效歌詞の性能探針テスト", false) };

            using var pipe = new PvPipeline(gpu, scene, effects);
            using var picture = Image2D.FromBgraPixels(pipe.D2DContext, ProbePixels(), W, H);
            using var target = OffscreenTarget.Create(gpu, pipe.D2DContext, W, H);
            using var query = gpu.Device.CreateQuery(new QueryDescription(QueryType.Event, QueryFlags.None));

            pipe.Renderer.Lyric?.SetClips(clips);
            pipe.Renderer.Lyric?.SetStyle(scene.LyricStyle ?? LyricStyle.FromName(scene.LyricStyleName));
            pipe.Renderer.Stage.Register(new TextureProvider(ImageLayerId, picture.Bitmap, W, H));
            pipe.SetDirector(ProbeCurves(effects));

            // 先出一帧并回读：既让着色器编译与纹理池分配落在计时之外（量的是稳态），
            // 也确认场景真的上屏了——全黑画面的帧时再漂亮也没有意义。
            pipe.Prepare(0.0);
            pipe.Composite(target.Bitmap, W, H);
            gpu.Context.Flush();
            using (var first = target.Readback())
            {
                var c = first.GetPixel(W / 2, H / 2);
                Assert.True(c.Red + c.Green + c.Blue > 30,
                    $"首帧中心像素 {c} 近乎全黑：图层没上屏，帧时数字无从谈起");
            }

            var samples = new double[Frames];
            var sw = new Stopwatch();
            for (var i = 0; i < Frames; i++)
            {
                var time = i * Dt;
                sw.Restart();
                pipe.Prepare(time);                        // 曲线求值 + 歌词排版
                pipe.Composite(target.Bitmap, W, H);       // 图层栈合成 + 效果链 + 贴到目标
                WaitGpu(gpu, query);                       // 等 GPU 真的画完
                sw.Stop();
                samples[i] = sw.Elapsed.TotalMilliseconds;
            }

            Array.Sort(samples);
            var p50 = Percentile(samples, 0.50);
            var p95 = Percentile(samples, 0.95);
            var max = samples[^1];

            _out.WriteLine($"[perf] {gpu.AdapterName}（软件光栅={gpu.IsSoftware}，feature level={gpu.FeatureLevel}）");
            _out.WriteLine($"[perf] {W}×{H} · {Frames} 帧 · 图片层+歌词层+bloom/chromatic/glitch/motionBlur/colorGrade/vignette");
            _out.WriteLine($"[perf] p50 {p50:F2} ms · p95 {p95:F2} ms · max {max:F2} ms（60fps 预算 {BudgetMs} ms）");

            Assert.True(p95 < BudgetMs,
                $"p95 {p95:F2} ms 超出 60fps 预算 {BudgetMs} ms（p50 {p50:F2} ms，max {max:F2} ms）");
        }
    }

    /// <summary>
    /// 导演整套生成物（史诗风格、强度拉满：调色/转场/集中线/分格/粒子层/逐字歌词全开）在第一个落点前后的 1080p 帧时。
    /// </summary>
    [GpuFact]
    public void DirectorEpicChorus1080pStaysWithin60FpsBudget()
    {
        if (Environment.GetEnvironmentVariable("JZ_PERF") != "1") return;

        var gpu = D3DDevice.TryCreate(out var error);
        if (gpu is null) GpuRequire.Fail(error);

        using (gpu)
        {
            var (map, lrc) = ChorusSong();
            var project = new DirectorProject { Scene = ProbeScene(), LrcText = lrc };
            var report = AutoDirector.Generate(project, map, new AutoOptions { Style = "epic", Intensity = 1.0, Seed = 1 });
            var drop = map.Sections.First(s => s.Kind == "chorus").Start;

            using var pipe = new PvPipeline(gpu, project.Scene, project.Effects);
            using var picture = Image2D.FromBgraPixels(pipe.D2DContext, ProbePixels(), W, H);
            using var target = OffscreenTarget.Create(gpu, pipe.D2DContext, W, H);
            using var query = gpu.Device.CreateQuery(new QueryDescription(QueryType.Event, QueryFlags.None));

            pipe.Renderer.Lyric?.SetClips(LyricStyleSpans.Apply(LrcParser.ParseClips(lrc).Clips, project.LyricStyles));
            pipe.Renderer.Stage.Register(new TextureProvider(ImageLayerId, picture.Bitmap, W, H));
            pipe.SetDirector(DirectorEvaluator.FromSaves(project.Curves));

            pipe.Prepare(drop - 2.0);
            pipe.Composite(target.Bitmap, W, H);
            WaitGpu(gpu, query);

            var samples = new double[Frames];
            var sw = new Stopwatch();
            for (var i = 0; i < Frames; i++)
            {
                var time = drop - 2.0 + i * Dt;
                sw.Restart();
                pipe.Prepare(time);
                pipe.Composite(target.Bitmap, W, H);
                WaitGpu(gpu, query);
                sw.Stop();
                samples[i] = sw.Elapsed.TotalMilliseconds;
            }

            Array.Sort(samples);
            var p50 = Percentile(samples, 0.50);
            var p95 = Percentile(samples, 0.95);
            var max = samples[^1];

            _out.WriteLine($"[perf] 导演 epic：效果实例 {report.EffectInstances}（出场 {report.EffectWindows} 段）· 图层 {project.Scene.Layers.Count} · 歌词样式段 {project.LyricStyles.Count}");
            _out.WriteLine($"[perf] {W}×{H} · {Frames} 帧（落点 {drop:F1}s 前 2s 起）");
            _out.WriteLine($"[perf] p50 {p50:F2} ms · p95 {p95:F2} ms · max {max:F2} ms（60fps 预算 {BudgetMs} ms）");

            Assert.True(p95 < BudgetMs,
                $"p95 {p95:F2} ms 超出 60fps 预算 {BudgetMs} ms（p50 {p50:F2} ms，max {max:F2} ms）");
        }
    }

    /// <summary>120 BPM 4/4：前奏 8 小节 / 主歌 16 / 副歌 16 / 主歌 16 / 副歌 16 / 尾奏 8，16 秒起每 2 小节一句歌词。</summary>
    private static (BeatMap Map, string Lrc) ChorusSong()
    {
        var parts = new (string Kind, int Bars, double Energy)[]
        {
            ("intro", 8, 0.3), ("verse", 16, 0.5), ("chorus", 16, 0.95),
            ("verse", 16, 0.55), ("chorus", 16, 1.0), ("outro", 8, 0.3),
        };
        var map = new BeatMap { Bpm = 120 };
        var t = 0.0;
        foreach (var (kind, bars, energy) in parts)
        {
            var start = t;
            for (var b = 0; b < bars * 4; b++)
            {
                map.Beats.Add(t);
                if (b % 4 == 0) map.Downbeats.Add(t);
                t += 0.5;
            }
            map.Sections.Add(new Section { Start = start, End = t, Kind = kind, Label = kind, Energy = energy });
        }

        var lrc = new System.Text.StringBuilder();
        for (var at = 16.0; at < t - 4; at += 4.0)
            lrc.AppendLine($"[{(int)(at / 60):00}:{at % 60:00.00}]测试歌词第{(int)((at - 16) / 4) + 1}句");
        return (map, lrc.ToString());
    }

    /// <summary>场景 = 1 个图片层（纹理在测试内生成）+ 动效歌词层（一句歌词覆盖全部 600 帧）。</summary>
    private static PvScene ProbeScene()
    {
        var scene = new PvScene
        {
            Name = "perf",
            Width = W,
            Height = H,
            Fps = 60,
            Layers =
            {
                new PvLayerConfig
                {
                    Id = ImageLayerId,
                    Name = "Perf Image",
                    Kind = PvLayerKind.VisualAsset,
                    // 三源都留空：管线不会为这层建提供者，纹理由用例自己注册（TextureProvider）
                    SpoutSenderName = null,
                    Cover = true,
                    Parallax = new PvParallaxConfig { Depth = 0.9 },
                    VisualBlend = new PvVisualBlendConfig { AmbientTintIntensity = 0.18, RimGlow = 0.12 },
                },
                new PvLayerConfig
                {
                    Id = "lyrics_pv",
                    Name = "Lyrics",
                    Kind = PvLayerKind.LyricPv,
                    SpoutSenderName = null,
                    Parallax = new PvParallaxConfig { Depth = 1.0, RotationFactor = 1.0 },
                },
            },
        };
        return scene;
    }

    /// <summary>六段全局后期，全用目录里的默认参数（探针不改画质参数来凑帧时）。</summary>
    private static EffectGraph ProbeEffects() => new EffectGraph()
        .Add("bloom")
        .Add("chromatic")
        .Add("glitch")
        .Add("motionBlur")
        .Add("colorGrade")
        .Add("vignette");

    /// <summary>驱动曲线：镜头（缩放/平移）、图层（透明度）、以及一个效果实例参数——每帧都要真求值。</summary>
    private static DirectorEvaluator ProbeCurves(EffectGraph effects)
    {
        var director = new DirectorEvaluator();
        director.SetCurve(new ParamAddress(ParamAddressKind.Camera, "", "scale"), Ramp(1.0, 1.15));
        director.SetCurve(new ParamAddress(ParamAddressKind.Camera, "", "offsetX"), Ramp(-40, 40));
        director.SetCurve(new ParamAddress(ParamAddressKind.Layer, ImageLayerId, "opacity"), Ramp(0.9, 1.0));

        var chromatic = effects.Find("chromatic")!.InstanceId;
        director.SetCurve(new ParamAddress(ParamAddressKind.Effect, chromatic, "amount"), Ramp(4, 30));
        return director;

        static FloatCurve Ramp(double from, double to)
        {
            var curve = new FloatCurve();
            curve.TryAdd(0, from, CurveInterp.Linear);
            curve.TryAdd(Frames * Dt, to, CurveInterp.Linear);
            return curve;
        }
    }

    /// <summary>测试内生成纹理：横向蓝渐变 + 纵向绿渐变 + 红棋盘，亮部够多，bloom/glitch 才有东西可吃。</summary>
    private static byte[] ProbePixels()
    {
        var pixels = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
        for (var x = 0; x < W; x++)
        {
            var i = (y * W + x) * 4;
            pixels[i] = (byte)(x * 255 / W);
            pixels[i + 1] = (byte)(y * 255 / H);
            pixels[i + 2] = (byte)(((x / 64) + (y / 64)) % 2 == 0 ? 220 : 40);
            pixels[i + 3] = 255;
        }

        return pixels;
    }

    /// <summary>
    /// 等 GPU 追到本帧末：D3D11 事件查询。Flush 只提交不等待，只量到提交耗时——
    /// 计时必须含 GPU，否则效果链的算力开销全被漏掉。
    /// </summary>
    private static void WaitGpu(D3DDevice gpu, ID3D11Query query)
    {
        gpu.Context.Flush();        // 先把 D2D 攒的命令推下去，End 标记才落在它们之后
        gpu.Context.End(query);
        while (gpu.Context.GetData(query, IntPtr.Zero, 0, AsyncGetDataFlags.None).Code != 0)
            Thread.Sleep(0);
    }

    /// <summary>升序样本的 p 分位（取不超过该分位的最大样本）。</summary>
    private static double Percentile(double[] sorted, double p)
    {
        var idx = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(idx, 0, sorted.Length - 1)];
    }
}
