using System.IO;
using JizuraLive.Engine;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using JizuraLive.Vts;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// PV 舞台（P3）：视差运镜、防露底、图层栈合成、环境光融合、场景文档持久化。
/// 全链路纯 GPU，零旧引擎依赖。
/// </summary>
public class PvStageTests
{
    private const int W = 320;
    private const int H = 180;

    private readonly ITestOutputHelper _out;

    public PvStageTests(ITestOutputHelper output) => _out = output;

    // ---------------- 视差数学（纯函数） ----------------

    private static PvLayerConfig Layer(double depth, double antiBleed = 0.06, double scale = 1) => new()
    {
        Id = "L",
        Scale = scale,
        Parallax = new PvParallaxConfig { Depth = depth },
        VisualBlend = new PvVisualBlendConfig { AntiBleedMargin = antiBleed },
    };

    private static double TranslationX(in PvLayerTransform t) => Math.Abs(t.Matrix.M31);

    [Fact]
    public void DepthScalesCameraMovement()
    {
        var cam = new PvCamState(X: 100);
        var near = PvParallax.Compute(W, H, Layer(1.35), cam);
        var far = PvParallax.Compute(W, H, Layer(0.35), cam);

        var nearDx = TranslationX(near);
        var farDx = TranslationX(far);
        Assert.True(nearDx > farDx * 3, $"近景位移 {nearDx:F1} 应远大于远景 {farDx:F1}");
    }

    [Fact]
    public void ZeroDepthStaysFixed()
    {
        var cam = new PvCamState(X: 50, Y: -30, Scale: 1.5, Rotation: 12);
        var t = PvParallax.Compute(W, H, Layer(0.0), cam);

        Assert.Equal(0, t.Matrix.M31, 3);
        Assert.Equal(0, t.Matrix.M32, 3);
        Assert.Equal(1, t.EffectiveScale, 3);
        Assert.Equal(1, t.Overscan, 3);
    }

    [Fact]
    public void AntiBleedScalesWithCameraMovement()
    {
        var still = PvParallax.Compute(W, H, Layer(1.0, antiBleed: 0.10), PvCamState.Identity);
        Assert.Equal(1.0, still.Overscan, 4);

        var moving = PvParallax.Compute(W, H, Layer(1.0, antiBleed: 0.10), new PvCamState(X: 80, Y: 40));
        Assert.True(moving.Overscan > 1.01, $"移动时应触发防露底放大，实际 {moving.Overscan:F4}");
        Assert.True(moving.Overscan <= 1.1001, $"放大系数不应超上限 1.10，实际 {moving.Overscan:F4}");
    }

    [Fact]
    public void LayerFiltersOnlyWantsSpoutOnVisualAsset()
    {
        var path = Path.Combine(Path.GetTempPath(), "jizura-filter-" + Guid.NewGuid() + ".json");
        try
        {
            var scene = PvScene.CreateDefault();
            scene.Layers.Add(PvPipeline.NewSourceLayer("src0", "MyCam"));
            scene.SaveToFile(path);

            var readBack = PvScene.LoadFromFile(path);
            var spoutLayers = readBack.Layers
                .Where(PvPipeline.WantsSpout)
                .Select(l => l.Id)
                .ToArray();
            Assert.Equal(new[] { "character", "src0" }, spoutLayers);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// 重开工程后新增图层不许复用量标：序号必须从场景里已有的 src_&lt;n&gt; 接着往下编。
    /// 撞名后两层共用一个提供者，删一层会把另一层也变成空层。
    /// </summary>
    [Fact]
    public void NewLayerIdsContinueAfterReloadedScene()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) return;

        var dir = Path.Combine(Path.GetTempPath(), "jizura-seq-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        try
        {
            using (gpu)
            {
                var scene = PvScene.CreateDefault();
                scene.Layers.Add(PvPipeline.NewSourceLayer("src_7", "OldCam"));

                using var pipe = new PvPipeline(gpu, Path.Combine(dir, "scene.json"), scene, new EffectGraph());
                pipe.RequestAdd("NewCam");
                pipe.Prepare(0.0);

                var ids = pipe.Scene.Layers.Select(l => l.Id).ToArray();
                Assert.Equal(ids.Length, ids.Distinct().Count());
                Assert.Contains("src_8", ids);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    /// <summary>
    /// 舞台能把合成直接写进外部分享纹理（纯 D3D11 共享纹理）。
    /// </summary>
    [Fact]
    public void StageCompositesIntoExternalSharedTexture()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) return;

        using (gpu)
        using (var host = D2DHost.Create(gpu))
        {
            var desc = new Texture2DDescription
            {
                Width = (uint)W,
                Height = (uint)H,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.Shared,
            };

            using var sharedTex = gpu.Device.CreateTexture2D(desc);
            using var dxgiRes = sharedTex.QueryInterface<IDXGIResource>();
            var shareHandle = dxgiRes.SharedHandle;

            var scene = new PvScene
            {
                Name = "bridge",
                Width = W,
                Height = H,
                AmbientBlendEnabled = false,
            };
            scene.Layers.Add(new PvLayerConfig
            {
                Id = "bg",
                Name = "Bridge Fill",
                Kind = PvLayerKind.Background,
                Parallax = new PvParallaxConfig { Depth = 0, RotationFactor = 0, ScaleFactor = 0 },
                VisualBlend = new PvVisualBlendConfig
                {
                    AmbientTintIntensity = 0, RimGlow = 0, DropShadow = 0, AntiBleedMargin = 0,
                },
            });

            var expect = new Color4(0.25f, 0.50f, 0.75f, 1f);
            using var renderer = new PvStageRenderer(gpu, host, scene, new EffectGraph(), W, H);
            renderer.Stage.Register(new ColorProvider("bg", expect));

            using var target = renderer.OpenSharedTarget(shareHandle, W, H);
            renderer.Composite(target.Bitmap, W, H);
            gpu.Context.Flush();

            using var px = GpuReadback.ToImage(gpu, target.Texture, W, H);
            var got = px.GetPixel(W / 2, H / 2);
            _out.WriteLine($"[桥接] 共享纹理 {target.Width}×{target.Height} 中心像素 R{got.Red} G{got.Green} B{got.Blue}");

            Assert.InRange(got.Red, 64 - 6, 64 + 6);
            Assert.InRange(got.Green, 128 - 6, 128 + 6);
            Assert.InRange(got.Blue, 191 - 6, 191 + 6);
        }
    }

    /// <summary>
    /// 1080p 纯 GPU 合成耗时基线验证。
    /// </summary>
    [Fact]
    public void StageCompositeIntoSharedTextureFitsFrameBudgetAt1080p()
    {
        const int w = 1920, h = 1080;
        const int frames = 30;

        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) return;

        using (gpu)
        using (var host = D2DHost.Create(gpu))
        {
            var desc = new Texture2DDescription
            {
                Width = (uint)w,
                Height = (uint)h,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CPUAccessFlags = CpuAccessFlags.None,
                MiscFlags = ResourceOptionFlags.Shared,
            };

            using var sharedTex = gpu.Device.CreateTexture2D(desc);
            using var dxgiRes = sharedTex.QueryInterface<IDXGIResource>();
            var shareHandle = dxgiRes.SharedHandle;

            var scene = new PvScene { Name = "budget", Width = w, Height = h };
            for (var i = 0; i < 5; i++)
                scene.Layers.Add(new PvLayerConfig
                {
                    Id = "L" + i,
                    Name = "L" + i,
                    Kind = PvLayerKind.Background,
                    Parallax = new PvParallaxConfig { Depth = 0.3 * i, RotationFactor = 0.4 },
                });

            var effects = new EffectGraph();
            foreach (var id in new[] { "bloom", "chromatic", "vignette" })
                effects.Add(id);

            using var renderer = new PvStageRenderer(gpu, host, scene, effects, w, h);
            var tint = new Color4(0.2f, 0.35f, 0.6f, 1f);
            for (var i = 0; i < 5; i++) renderer.Stage.Register(new ColorProvider("L" + i, tint));

            using var target = renderer.OpenSharedTarget(shareHandle, w, h);
            for (var i = 0; i < 5; i++) renderer.Composite(target.Bitmap, w, h);
            gpu.Context.Flush();

            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (var i = 0; i < frames; i++) renderer.Composite(target.Bitmap, w, h);
            gpu.Context.Flush();
            sw.Stop();
            var submitMs = sw.Elapsed.TotalMilliseconds / frames;

            _out.WriteLine($"[预算] 1080p 纯 GPU 合成耗时：{submitMs:F2} ms/帧");
            Assert.True(submitMs < 16.7, $"1080p 合成 {submitMs:F2} ms/帧 超过 60fps 预算");
        }
    }

    /// <summary>
    /// VTS 追踪驱动虚拟摄像机。
    /// </summary>
    [Fact]
    public void VtsTrackingDrivesCameraInPipeline()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) return;

        using (gpu)
        {
            var probe = new VtsTrackingProbe();
            using var pipe = new PvPipeline(gpu, "unused.json", PvScene.CreateDefault(),
                new EffectGraph(), null, false, probe);

            probe.Set(VtsTrackingState.Empty with
            {
                FaceFound = true,
                HeadX = 0.8,
                HeadY = -0.5,
                MouthOpen = 0.9,
                EyeOpenLeft = 1,
                EyeOpenRight = 1,
                AtMs = Environment.TickCount64,
            });

            pipe.Prepare(0.0);
            var cam = pipe.Renderer.Stage.Camera;

            Assert.True(Math.Abs(cam.X) > 1, $"头右移镜位移应显著，实际 {cam.X}");
            Assert.True(cam.Scale > 0.5, "缩放应合理");
        }
    }

    /// <summary>
    /// 效果图文档存储与往返。
    /// </summary>
    [Fact]
    public void EffectsDocumentRoundTrips()
    {
        var fxPath = Path.Combine(Path.GetTempPath(), "jizura-fx-" + Guid.NewGuid() + ".json");
        try
        {
            var original = new EffectGraph();
            original.Add("bloom");
            original.Add("glitch");
            var json = EffectGraphJson.Serialize(original);
            File.WriteAllText(fxPath, json);

            var loaded = EffectGraphJson.Deserialize(File.ReadAllText(fxPath));
            Assert.Equal(2, loaded.Nodes.Count);
            Assert.Equal("bloom", loaded.Nodes[0].DefId);
            Assert.Equal("glitch", loaded.Nodes[1].DefId);
        }
        finally
        {
            if (File.Exists(fxPath)) File.Delete(fxPath);
        }
    }

    private static Image2D Checkerboard(Vortice.Direct2D1.ID2D1DeviceContext ctx, int w, int h, int cell)
    {
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var isLight = ((x / cell) + (y / cell)) % 2 == 0;
            var idx = (y * w + x) * 4;
            if (isLight)
            {
                pixels[idx] = 160;
                pixels[idx + 1] = 210;
                pixels[idx + 2] = 235;
                pixels[idx + 3] = 255;
            }
            else
            {
                pixels[idx] = 40;
                pixels[idx + 1] = 30;
                pixels[idx + 2] = 30;
                pixels[idx + 3] = 255;
            }
        }
        return Image2D.FromBgraPixels(ctx, pixels, w, h);
    }

    // ---------------- 图层栈合成 ----------------

    private static string Hex(Color4 c) =>
        $"#{(int)Math.Round(c.R * 255):x2}{(int)Math.Round(c.G * 255):x2}{(int)Math.Round(c.B * 255):x2}";

    private static Image2D SolidBitmap(D2DHost host, int w, int h, Color4 color)
    {
        var b = (byte)Math.Clamp((int)Math.Round(color.B * 255), 0, 255);
        var g = (byte)Math.Clamp((int)Math.Round(color.G * 255), 0, 255);
        var r = (byte)Math.Clamp((int)Math.Round(color.R * 255), 0, 255);
        var a = (byte)Math.Clamp((int)Math.Round(color.A * 255), 0, 255);

        var pixels = new byte[w * h * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = a;
        }

        return Image2D.FromBgraPixels(host.Ctx, pixels, w, h);
    }

    private static PvScene TwoLayerScene(Color4 tint, double tintStrength, double depthBottom, double depthTop)
    {
        var scene = new PvScene { Width = W, Height = H, AmbientTint = Hex(tint) };
        scene.Layers.Add(new PvLayerConfig
        {
            Id = "bottom",
            Kind = PvLayerKind.Background,
            Cover = true,
            Parallax = new PvParallaxConfig { Depth = depthBottom },
            VisualBlend = new PvVisualBlendConfig { AmbientTintIntensity = 0, RimGlow = 0, DropShadow = 0 },
        });
        scene.Layers.Add(new PvLayerConfig
        {
            Id = "top",
            Kind = PvLayerKind.VisualAsset,
            Cover = true,
            Parallax = new PvParallaxConfig { Depth = depthTop },
            VisualBlend = new PvVisualBlendConfig
            {
                AmbientTintIntensity = tintStrength,
                RimGlow = 0,
                DropShadow = 0,
                AntiBleedMargin = 0.06,
            },
        });
        return scene;
    }

    private static ReadbackImage RenderStage(PvScene scene, PvCamState cam,
        Action<D2DHost, PvStage, List<IDisposable>> bind)
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) throw new InvalidOperationException("no gpu");
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var target = OffscreenTarget.Create(gpu, host.Ctx, W, H))
        {
            host.Draw.SetTarget(target.Bitmap, W, H);
            var stage = new PvStage(scene) { Camera = cam };
            var keep = new List<IDisposable>();
            try
            {
                bind(host, stage, keep);
                host.Draw.Begin();
                host.Draw.Clear(new Color4(0, 0, 0, 1));
                stage.RenderFrame(host.Draw, new PvFrameContext(0, 0, 0, W, H));
                host.Draw.End();
                return target.Readback();
            }
            finally
            {
                foreach (var d in keep) d.Dispose();
                stage.Dispose();
            }
        }
    }

    [Fact]
    public void TopLayerCoversBottomLayer()
    {
        var scene = TwoLayerScene(new Color4(1, 1, 1, 1), 0, 1.0, 1.0);
        using var bmp = RenderStage(scene, PvCamState.Identity, (host, stage, keep) =>
        {
            var bottom = SolidBitmap(host, W, H, new Color4(1f, 0f, 0f, 1f));
            var top = SolidBitmap(host, W, H, new Color4(0f, 0f, 1f, 1f));
            keep.Add(bottom);
            keep.Add(top);
            stage.Register(new TextureProvider("bottom", bottom.Bitmap, W, H));
            stage.Register(new TextureProvider("top", top.Bitmap, W, H));
        });

        var c = bmp.GetPixel(W / 2, H / 2);
        Assert.True(c.Blue > 200 && c.Red < 60, $"上层（蓝）应盖住下层（红），实际 {c}");
    }

    [Fact]
    public void AmbientTintShiftsLayerColor()
    {
        var scene = TwoLayerScene(new Color4(1f, 0.1f, 0.1f, 1f), tintStrength: 0.6, 1.0, 1.0);
        using var bmp = RenderStage(scene, PvCamState.Identity, (host, stage, keep) =>
        {
            var bottom = SolidBitmap(host, W, H, new Color4(0f, 0f, 0f, 1f));
            var top = SolidBitmap(host, W, H, new Color4(1f, 1f, 1f, 1f));
            keep.Add(bottom);
            keep.Add(top);
            stage.Register(new TextureProvider("bottom", bottom.Bitmap, W, H));
            stage.Register(new TextureProvider("top", top.Bitmap, W, H));
        });

        var c = bmp.GetPixel(W / 2, H / 2);
        Assert.True(c.Red > c.Blue + 40, $"环境光染色后应偏红，实际 {c}");
    }

    [Fact]
    public void CameraMotionMovesLayersByDepth()
    {
        var scene = TwoLayerScene(new Color4(1, 1, 1, 1), 0, 0.35, 1.35);

        static Image2D Blocked(D2DHost host)
        {
            var pixels = new byte[W * H * 4];
            var left = W / 2 - 20;
            var right = W / 2 + 20;
            var top = H / 2 - 20;
            var bottom = H / 2 + 20;
            for (var y = top; y < bottom; y++)
            for (var x = left; x < right; x++)
            {
                var idx = (y * W + x) * 4;
                pixels[idx] = 0;
                pixels[idx + 1] = 0;
                pixels[idx + 2] = 255;
                pixels[idx + 3] = 255;
            }
            return Image2D.FromBgraPixels(host.Ctx, pixels, W, H);
        }

        static int CenterOfMassX(ReadbackImage bmp)
        {
            long sum = 0, n = 0;
            for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
            {
                if (bmp.GetPixel(x, y).Red > 150)
                {
                    sum += x;
                    n++;
                }
            }

            return n == 0 ? -1 : (int)(sum / n);
        }

        var cam = new PvCamState(X: 20);
        int farX, nearX;
        using (var bmp = RenderStage(scene, cam, (host, stage, keep) =>
               {
                   var blocked = Blocked(host);
                   var empty = SolidBitmap(host, W, H, new Color4(0, 0, 0, 0));
                   keep.Add(blocked);
                   keep.Add(empty);
                   stage.Register(new TextureProvider("bottom", blocked.Bitmap, W, H));
                   stage.Register(new TextureProvider("top", empty.Bitmap, W, H));
               }))
        {
            farX = CenterOfMassX(bmp);
        }

        using (var bmp = RenderStage(scene, cam, (host, stage, keep) =>
               {
                   var blocked = Blocked(host);
                   var empty = SolidBitmap(host, W, H, new Color4(0, 0, 0, 0));
                   keep.Add(blocked);
                   keep.Add(empty);
                   stage.Register(new TextureProvider("bottom", empty.Bitmap, W, H));
                   stage.Register(new TextureProvider("top", blocked.Bitmap, W, H));
               }))
        {
            nearX = CenterOfMassX(bmp);
        }

        Assert.True(Math.Abs(nearX - W / 2) > Math.Abs(farX - W / 2),
            $"近景位移（{nearX - W / 2}px）应大于远景位移（{farX - W / 2}px）");
    }

    [Fact]
    public void RimGlowEnhancesEdges()
    {
        static PvScene SceneWith(double rimGlow) => new()
        {
            Width = W,
            Height = H,
            AmbientBlendEnabled = true,
            Layers =
            {
                new PvLayerConfig
                {
                    Id = "actor",
                    Cover = true,
                    VisualBlend = new PvVisualBlendConfig
                    {
                        RimGlow = rimGlow,
                        AmbientTintIntensity = 0,
                        DropShadow = 0,
                    },
                },
            },
        };

        static Image2D Blocked(D2DHost host)
        {
            var pixels = new byte[W * H * 4];
            var cx = W / 2;
            var cy = H / 2;
            var r2 = 34 * 34;
            for (var y = 0; y < H; y++)
            for (var x = 0; x < W; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                if (dx * dx + dy * dy <= r2)
                {
                    var idx = (y * W + x) * 4;
                    pixels[idx] = 120;
                    pixels[idx + 1] = 220;
                    pixels[idx + 2] = 255;
                    pixels[idx + 3] = 255;
                }
            }
            return Image2D.FromBgraPixels(host.Ctx, pixels, W, H);
        }

        using var plain = RenderStage(SceneWith(0), PvCamState.Identity, (host, stage, keep) =>
        {
            var img = Blocked(host);
            keep.Add(img);
            stage.Register(new TextureProvider("actor", img.Bitmap, W, H));
        });
        using var glowing = RenderStage(SceneWith(0.8), PvCamState.Identity, (host, stage, keep) =>
        {
            var img = Blocked(host);
            keep.Add(img);
            stage.Register(new TextureProvider("actor", img.Bitmap, W, H));
        });

        var diff = GoldenImage.Compare(plain, glowing);
        _out.WriteLine($"[pv] 边缘光差异 meanAbs={diff.MeanAbs:F2}");
        Assert.True(diff.MeanAbs > 1.0, "开启边缘光应当改变画面");
    }
}
