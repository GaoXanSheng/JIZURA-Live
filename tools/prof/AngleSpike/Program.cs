using System.Diagnostics;
using System.IO;
using System.Reflection;
using JizuraLive.Engine;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SkiaSharp;

namespace AngleSpike;

/// <summary>
/// M-G1 POC：ANGLE(GL on D3D11) 零拷贝链路
/// Skia(CreateAngle) → EGL pbuffer(共享句柄纹理) → ID3D11Texture2D → SpoutDX.SendTexture。
/// 输出报告落 exe 目录 angle-report.txt（控制台通道不可靠），另存 GPU/CPU 帧 PNG 供目检。
/// </summary>
internal static class Program
{
    private const int Wid = 1920, Hgt = 1080, Frames = 24;
    private static readonly List<string> Lines = new();

    private static void Log(string s) => Lines.Add(s);

    private static void SysFonts()
    {
        var fam = new[] { "Yu Gothic", "Meiryo", "MS Gothic", "SimSun", "Arial" }
            .FirstOrDefault(f =>
            {
                try
                {
                    using var t = SKTypeface.FromFamilyName(f);
                    return t != null && string.Equals(t.FamilyName, f, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }) ?? "Arial";
        foreach (var kv in JFonts.Table.ToArray())
            JFonts.AddUserFont(kv.Key, kv.Value.Label, fam, kv.Value.Weight, kv.Value.Kind);
    }

    private static int Main(string[] args)
    {
        var swAll = Stopwatch.StartNew();
        try { Run(); }
        catch (Exception ex) { Log("致命：" + ex); }
        finally
        {
            Log($"总耗时 {swAll.ElapsedMilliseconds}ms");
            try { File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "angle-report.txt"), Lines); } catch { }
        }
        return 0;
    }

    private static void Run()
    {
        // ---------- 0. 引擎 + 工程 ----------
        EngineBoot.InstallPacks();
        SysFonts();
        var p = Project.NewDefault();
        p.Lyrics = "夜明けの色を/覚えてる\nほどけた声が遠くで鳴った";
        var plan = Planner.Plan(p, null);
        Log($"[0] plan: {plan.Cuts.Count} cuts, {plan.Duration:F2}s, {plan.W}x{plan.H}");
        var times = Enumerable.Range(0, Frames).Select(i => 0.8 + i * 0.35).ToArray();

        // ---------- 1. CPU 基线 ----------
        var cpuRenderer = new Renderer();
        var cpuBmp = new SKBitmap(new SKImageInfo(plan.W, plan.H, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var cpuSurf = SKSurface.Create(cpuBmp.Info, cpuBmp.GetPixels(), cpuBmp.RowBytes))
        {
            cpuRenderer.Frame(cpuSurf.Canvas, plan, times[0]); // 预热缓存
            var sw = Stopwatch.StartNew();
            foreach (var t in times) cpuRenderer.Frame(cpuSurf.Canvas, plan, t);
            sw.Stop();
            Log($"[1] CPU 光栅基线：{sw.Elapsed.TotalMilliseconds / Frames:F1} ms/帧（{plan.W}x{plan.H}）");
        }

        // ---------- 2. EGL 初始化 ----------
        // 强制 D3D11 后端（EGL_PLATFORM_ANGLE_TYPE_D3D11_ANGLE）：默认可能落到 Vulkan，
        // 那样 EGL_D3D11_DEVICE_ANGLE 必然拿不到设备
        var dpy = Egl.eglGetPlatformDisplayEXT(Egl.EGL_PLATFORM_ANGLE_ANGLE, IntPtr.Zero,
            new[] { Egl.EGL_PLATFORM_ANGLE_TYPE_ANGLE, Egl.EGL_PLATFORM_ANGLE_TYPE_D3D11_ANGLE,
                    Egl.EGL_PLATFORM_ANGLE_DEVICE_TYPE_ANGLE, Egl.EGL_PLATFORM_ANGLE_DEVICE_TYPE_HARDWARE_ANGLE,
                    Egl.EGL_NONE });
        Log($"[2a] EXT+完整属性: {(dpy == IntPtr.Zero ? "失败" : "成功")}");
        if (dpy == IntPtr.Zero)
        {
            dpy = Egl.eglGetPlatformDisplayEXT(Egl.EGL_PLATFORM_ANGLE_ANGLE, IntPtr.Zero,
                new[] { Egl.EGL_PLATFORM_ANGLE_TYPE_ANGLE, Egl.EGL_PLATFORM_ANGLE_TYPE_D3D11_ANGLE, Egl.EGL_NONE });
            Log($"[2b] EXT+仅类型: {(dpy == IntPtr.Zero ? "失败" : "成功")}");
        }
        if (dpy == IntPtr.Zero)
        {
            var attrs = new IntPtr[5];
            attrs[0] = Egl.EGL_PLATFORM_ANGLE_TYPE_ANGLE; attrs[1] = Egl.EGL_PLATFORM_ANGLE_TYPE_D3D11_ANGLE;
            attrs[2] = Egl.EGL_PLATFORM_ANGLE_DEVICE_TYPE_ANGLE; attrs[3] = Egl.EGL_PLATFORM_ANGLE_DEVICE_TYPE_HARDWARE_ANGLE;
            attrs[4] = Egl.EGL_NONE;
            dpy = Egl.eglGetPlatformDisplay(Egl.EGL_PLATFORM_ANGLE_ANGLE, IntPtr.Zero, attrs);
            Log($"[2c] core(EGLAttrib): {(dpy == IntPtr.Zero ? "失败" : "成功")}");
        }
        if (dpy == IntPtr.Zero) { Log("[2] 全部 platform display 失败，回退 eglGetDisplay"); dpy = Egl.eglGetDisplay(IntPtr.Zero); }
        if (dpy == IntPtr.Zero) { Log("[2] eglGetDisplay 失败（libEGL.dll 是否在 exe 目录？）"); return; }
        if (Egl.eglInitialize(dpy, out var mj, out var mn) == 0) { Log("[2] eglInitialize 失败"); return; }
        Egl.eglBindAPI(Egl.EGL_OPENGL_ES_API);
        Log($"[2] EGL {mj}.{mn}, vendor={Ptr(Egl.eglQueryString(dpy, 0x3053))}, version={Ptr(Egl.eglQueryString(dpy, 0x3054))}");
        var ext = Ptr(Egl.eglQueryString(dpy, Egl.EGL_EXTENSIONS));
        Log($"[2] 含 EGL_ANGLE_device_d3d: {ext.Contains("EGL_ANGLE_device_d3d")}（扩展共 {ext.Length} 字符）");
        Log("[2] 扩展全量：" + ext);

        // 配置：ES3 + alpha + 支持 D3D 共享句柄表面
        var cfgAttribs = new[]
        {
            Egl.EGL_SURFACE_TYPE, Egl.EGL_PBUFFER_BIT,
            Egl.EGL_RENDERABLE_TYPE, Egl.EGL_OPENGL_ES3_BIT | Egl.EGL_OPENGL_ES2_BIT,
            Egl.EGL_RED_SIZE, 8, Egl.EGL_GREEN_SIZE, 8, Egl.EGL_BLUE_SIZE, 8, Egl.EGL_ALPHA_SIZE, 8,
            Egl.EGL_DEPTH_SIZE, 24,
            Egl.EGL_D3D_TEXTURE_2D_SHARE_HANDLE_ANGLE, Egl.EGL_TRUE,
            Egl.EGL_NONE,
        };
        var cfgs = new IntPtr[8];
        if (Egl.eglChooseConfig(dpy, cfgAttribs, cfgs, cfgs.Length, out var nCfg) == 0 || nCfg == 0)
        {
            Log("[2] eglChooseConfig（带共享句柄）无匹配，回退不带该属性");
            cfgAttribs = cfgAttribs.Where((_, i) => i < cfgAttribs.Length - 3).Concat(new[] { Egl.EGL_NONE }).ToArray();
            if (Egl.eglChooseConfig(dpy, cfgAttribs, cfgs, cfgs.Length, out nCfg) == 0 || nCfg == 0)
            { Log("[2] eglChooseConfig 彻底失败"); return; }
        }
        var cfg = cfgs[0];
        Log($"[2] 配置 {nCfg} 个可用");

        var ctx = Egl.eglCreateContext(dpy, cfg, IntPtr.Zero,
            new[] { Egl.EGL_CONTEXT_CLIENT_VERSION, 3, Egl.EGL_NONE });
        if (ctx == IntPtr.Zero) { Log("[2] eglCreateContext 失败"); return; }

        // ---------- 3. 自建 D3D11 设备（A'' 变体：该 ANGLE 构建无 device_d3d，
        //            但带 EGL_ANGLE_d3d_share_handle_client_buffer —— 共享句柄直接建表面，
        //            完全不需要取 ANGLE 的设备） ----------
        using var dev11 = new SharpDX.Direct3D11.Device(SharpDX.Direct3D.DriverType.Hardware,
            DeviceCreationFlags.BgraSupport);
        var d3dTex = new Texture2D(dev11, new Texture2DDescription
        {
            Width = plan.W, Height = plan.H, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
            CpuAccessFlags = CpuAccessFlags.None,
            OptionFlags = ResourceOptionFlags.Shared,
        });
        var shareHandle = d3dTex.QueryInterface<SharpDX.DXGI.Resource>().SharedHandle;
        Log($"[4] 共享纹理 {plan.W}x{plan.H} BGRA, shareHandle=0x{shareHandle:x}");

        // ---------- 5. EGL 表面（包该纹理）→ 上下文 → Skia ----------
        // 5a：共享句柄 pbuffer —— 逐 config 探测支持 EGL_D3D_TEXTURE_2D_SHARE_HANDLE_ANGLE 的配置
        var surf = IntPtr.Zero;
        for (var ci = 0; ci < nCfg; ci++)
        {
            Egl.eglGetConfigAttrib(dpy, cfgs[ci], Egl.EGL_D3D_TEXTURE_2D_SHARE_HANDLE_ANGLE, out var okFlag);
            if (okFlag != Egl.EGL_TRUE) continue;
            surf = Egl.eglCreatePbufferSurface(dpy, cfgs[ci], new[]
            {
                Egl.EGL_WIDTH, plan.W,
                Egl.EGL_HEIGHT, plan.H,
                Egl.EGL_D3D_TEXTURE_2D_SHARE_HANDLE_ANGLE, shareHandle.ToInt32(),
                Egl.EGL_NONE,
            });
            if (surf != IntPtr.Zero) { Log($"[5] 共享句柄表面成功（config #{ci}，flag={okFlag}）"); break; }
        }
        var fboForSkia = 0; // 0 = 默认 FBO（共享句柄表面路径）
        if (surf == IntPtr.Zero)
        {
            Log($"[5] 共享句柄表面不可用（eglErr=0x{Egl.eglGetError():x}）；回退 A'：EGLImage → 自建 FBO");
            // A'：纹理直接用（同一 D3D 设备上创建，ANGLE 可访问）→ eglCreateImageKHR → GL 纹理 → FBO
            if (Egl.eglMakeCurrent(dpy, IntPtr.Zero, IntPtr.Zero, ctx) == 0 && Egl.eglGetCurrentContext() == IntPtr.Zero)
            { Log("[5] surfaceless makeCurrent 失败"); return; }
            // 四形态探测：buffer ∈ {纹理指针, 共享句柄} × attribs ∈ {无, SHARED_HANDLE=TRUE}
            const int EGL_D3D11_TEXTURE_SHARED_HANDLE_ANGLE = 0x3487;
            var shapes = new (string Name, IntPtr Buffer, int[]? Attrs)[]
            {
                ("纹理指针/无属性", d3dTex.NativePointer, null),
                ("共享句柄/无属性", shareHandle, null),
                ("纹理指针/SHARED=1", d3dTex.NativePointer, new[] { EGL_D3D11_TEXTURE_SHARED_HANDLE_ANGLE, Egl.EGL_TRUE, Egl.EGL_NONE }),
                ("共享句柄/SHARED=1", shareHandle, new[] { EGL_D3D11_TEXTURE_SHARED_HANDLE_ANGLE, Egl.EGL_TRUE, Egl.EGL_NONE }),
            };
            var img = IntPtr.Zero;
            foreach (var sh in shapes)
            {
                img = Egl.eglCreateImageKHR(dpy, IntPtr.Zero, Egl.EGL_D3D11_TEXTURE_ANGLE, sh.Buffer, sh.Attrs);
                Log($"[5] eglCreateImageKHR[{sh.Name}] => 0x{img:x} eglErr=0x{Egl.eglGetError():x}");
                if (img != IntPtr.Zero) break;
            }
            if (img == IntPtr.Zero) { Log("[5] 四形态全失败"); return; }
            var texIds = new int[1];
            Egl.glGenTextures(1, texIds);
            Egl.glBindTexture(Egl.GL_TEXTURE_2D, texIds[0]);
            Egl.glEGLImageTargetTexture2DOES(Egl.GL_TEXTURE_2D, img);
            var fboIds = new int[1];
            Egl.glGenFramebuffers(1, fboIds);
            Egl.glBindFramebuffer(Egl.GL_FRAMEBUFFER, fboIds[0]);
            Egl.glFramebufferTexture2D(Egl.GL_FRAMEBUFFER, Egl.GL_COLOR_ATTACHMENT0, Egl.GL_TEXTURE_2D, texIds[0], 0);
            fboForSkia = fboIds[0];
            Log($"[5] A' 就绪：EGLImage→GL 纹理 {texIds[0]}→FBO {fboForSkia}");
        }
        else
        {
            if (Egl.eglMakeCurrent(dpy, surf, surf, ctx) == 0) { Log("[5] eglMakeCurrent 失败"); return; }
            Log("[5] EGL 表面绑定共享纹理，上下文 current");
        }

        var glIface = GRGlInterface.CreateAngle();
        if (glIface == null || !glIface.Validate()) { Log("[5] GRGlInterface.CreateAngle 失败/未通过 Validate"); return; }
        var gr = GRContext.CreateGl(glIface);
        if (gr == null) { Log("[5] GRContext.CreateGl 失败"); return; }
        Log("[5] GRContext(ANGLE) 就绪");

        // 4.x：以 GRBackendRenderTarget 描述 FBO0（= ANGLE 表面 = 我们的共享纹理）
        var fbInfo = new GRGlFramebufferInfo((uint)fboForSkia, 0x8058); // FBO（0=共享句柄表面默认 FBO；非 0=A' 自建）
        var rendTarget = new GRBackendRenderTarget(plan.W, plan.H, 0, 0, fbInfo);
        var gpuSurf = SKSurface.Create(gr, rendTarget, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888);
        if (gpuSurf == null) { Log("[5] GPU 表面创建失败（FBO0 未挂钩？）"); return; }

        // ---------- 6. GPU 渲染 timing + 像素对拍 ----------
        var gpuRenderer = new Renderer();
        gpuRenderer.Frame(gpuSurf.Canvas, plan, times[0]);
        gr.Flush();
        var swG = Stopwatch.StartNew();
        foreach (var t in times) { gpuRenderer.Frame(gpuSurf.Canvas, plan, t); gr.Flush(); }
        swG.Stop();
        Log($"[6] GPU(ANGLE) 渲染：{swG.Elapsed.TotalMilliseconds / Frames:F1} ms/帧（含 Flush）");

        // 对拍第 0 帧
        var gInfo = new SKImageInfo(plan.W, plan.H, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var gBmp = new SKBitmap(gInfo);
        unsafe
        {
            if (!gpuSurf.ReadPixels(gInfo, gBmp.GetPixels(), gBmp.RowBytes, 0, 0))
                Log("[6] GPU ReadPixels 失败");
            else
            {
                // GPU 是 GL RGBA 视图、CPU 是 BGRA 内存：比较时交换 R/B
                long diff = 0, total = (long)plan.W * plan.H;
                var pa = (byte*)gBmp.GetPixels();
                var pb = (byte*)cpuBmp.GetPixels();
                for (long i = 0; i < total; i++)
                {
                    var o = i * 4;
                    if (Math.Abs(pa[o] - pb[o + 2]) > 3 || Math.Abs(pa[o + 1] - pb[o + 1]) > 3 || Math.Abs(pa[o + 2] - pb[o]) > 3) diff++;
                }
                Log($"[6] 像素对拍（R/B 交换）：差异 {diff}/{total} = {diff * 100.0 / total:F3}%（阈值 >3/通道）");
                using (var img = SKImage.FromBitmap(gBmp))
                using (var data = img.Encode(SKEncodedImageFormat.Png, 95))
                using (var fs = File.Create(Path.Combine(AppContext.BaseDirectory, "gpu-frame0.png")))
                    data.SaveTo(fs);
            }
        }
        using (var img = SKImage.FromBitmap(cpuBmp))
        using (var data = img.Encode(SKEncodedImageFormat.Png, 95))
        using (var fs = File.Create(Path.Combine(AppContext.BaseDirectory, "cpu-frame0.png")))
            data.SaveTo(fs);

        // ---------- 7. Spout 零拷贝发送 + 回环 ----------
        // 反射打印 SpoutReceiver 的 API（两遍法：本遍先拿签名）
        try
        {
            var wrap = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "SpoutDXWrapper.dll"));
            foreach (var tn in new[] { "SpoutSender", "SpoutReceiver" })
            {
                var t = wrap.GetTypes().FirstOrDefault(x => x.Name == tn);
                if (t == null) { Log($"[7] {tn} 类型未找到"); continue; }
                Log($"[7] {tn} API：");
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Take(24))
                    Log("    " + m);
            }
        }
        catch (Exception ex) { Log("[7] 反射 SpoutDXWrapper 失败：" + ex.Message); }

        try
        {
            var wrap = Assembly.LoadFrom(Path.Combine(AppContext.BaseDirectory, "SpoutDXWrapper.dll"));
            var senderT = wrap.GetTypes().First(x => x.Name == "SpoutSender");
            dynamic sender = Activator.CreateInstance(senderT)!;
            sender.SetSenderName("JIZURA-GPU-POC");
            var opened = sender.OpenDirectX(dev11.NativePointer);
            Log($"[7] Sender.OpenDirectX(ANGLE 设备) = {opened}");
            var swS = Stopwatch.StartNew();
            for (var i = 0; i < 60; i++)
            {
                sender.SendTexture(d3dTex.NativePointer);
                Thread.Sleep(16);
            }
            swS.Stop();
            Log($"[7] 已发送 60 帧（{(swS.Elapsed.TotalMilliseconds / 60):F1} ms/帧含 16ms 节拍）");
            try { sender.Release(); } catch { }
        }
        catch (Exception ex) { Log("[7] Spout 发送失败：" + ex); }

        // 清理（面/上下文按宿主纪律保留到进程尾，不主动 Dispose）
        Log("[done]");
    }

    private static string Ptr(IntPtr p) => p == IntPtr.Zero ? "(null)" : System.Runtime.InteropServices.Marshal.PtrToStringAnsi(p) ?? "?";
}
