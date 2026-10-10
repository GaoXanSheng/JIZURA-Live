using System.IO;
using System.Runtime.InteropServices;
using JizuraLive.Logging;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SkiaSharp;
using Device = SharpDX.Direct3D11.Device;

namespace JizuraLive.Engine;

/// <summary>
/// GPU 光栅宿主：ANGLE(GL on D3D11) → EGL → Skia GRContext → 共享纹理面池。
///
/// 为什么是 ANGLE：SkiaSharp 只带 libSkiaSharp.dll，GPU 后端只有两条路——GL（要外部 ANGLE
/// 二进制）或 Vulkan（要手工 P/Invoke 建 VkDevice）。ANGLE 这条已被四轮 POC 实测打通，且
/// <c>GRGlInterface.CreateAngle</c> 是 SkiaSharp 现成入口；Vulkan/ANGLE 官方 CI 产物作为备选。
///
/// 纹理零拷贝（POC 四轮定死的 M1 路径）：
/// <code>
///   eglCreatePbufferSurface                        ← ANGLE 自建渲染表面
///   eglQuerySurfacePointerANGLE(0x3200)            ← 取表面背后 D3D11 纹理的共享句柄
///   ID3D11Device::OpenSharedResource(句柄)          ← 在自己的设备上打开（实测 BGRA8）
///   SKSurface.Create(gr, GRBackendRenderTarget FBO0) ← Skia 直接画进这块纹理
///   SpoutDX.SendTexture(该纹理)                     ← 同设备发送，全程无 CPU 拷贝
/// </code>
/// 每帧同步 = Skia Flush + glFinish：ANGLE 的 GPU 工作落盘后才允许 Spout 的设备读；实测同步
/// 反而比「只 Flush」更快（不等会让命令堆积、Skia 资源被在途 GPU 占用而反复 stall：
/// 1920×1080 工程 15.5ms/帧 → 6.9ms/帧）。
///
/// 线程约束：EGL 上下文同一时刻只能 current 在一个线程。宿主是进程单例，使用方（Spout 泵线程）
/// 进 <see cref="Enter"/> / 出 <see cref="Exit"/>，其间用 <see cref="Activate"/> 切换目标面。
/// </summary>
public sealed class GpuRasterHost : IDisposable
{
    private static readonly object Gate = new();
    private static GpuRasterHost? _instance;
    private static bool _failed;

    /// <summary>强制关 GPU 光栅（排障/对比用）：JIZURA_GPU=0。</summary>
    public static bool DisabledByEnv =>
        Environment.GetEnvironmentVariable("JIZURA_GPU") is "0" or "off" or "false";

    /// <summary>
    /// 取宿主单例；不可用（缺 ANGLE/初始化失败/环境变量关掉）返回 null 并给出原因。
    /// 初始化只尝试一次——设备/驱动级失败不会因为重试而变好，失败后统一走 CPU 回退。
    /// </summary>
    public static GpuRasterHost? TryGet(out string error)
    {
        lock (Gate)
        {
            if (_instance != null)
            {
                error = "";
                return _instance;
            }

            if (_failed)
            {
                error = _lastError;
                return null;
            }

            if (DisabledByEnv)
            {
                _failed = true;
                _lastError = "已由 JIZURA_GPU=0 关闭";
                error = _lastError;
                return null;
            }

            try
            {
                _instance = new GpuRasterHost();
                Log.Info($"[GpuRaster] 就绪：{_instance.Describe()}");
                error = "";
                return _instance;
            }
            catch (Exception ex)
            {
                _failed = true;
                _lastError = ex.Message;
                Log.Info("[GpuRaster] 不可用，回退 CPU 光栅：" + ex.Message);
                error = ex.Message;
                return null;
            }
        }
    }

    private static string _lastError = "";

    private readonly IntPtr _dpy;
    private readonly IntPtr _cfg;
    private readonly IntPtr _ctx;
    private readonly Device _device;
    private readonly GRContext _gr;
    private readonly string _provider;
    private readonly Dictionary<(string Key, int W, int H), GpuSurface> _surfaces = new();

    private int _ownerThread;
    private GpuSurface? _active;

    public string AngleVersion { get; }
    public string AngleVendor { get; }
    public string AdapterName { get; }

    /// <summary>交给 SpoutDXWrapper.OpenDirectX 的设备指针（共享纹理都开在这块设备上）。</summary>
    public IntPtr DevicePointer => _device.NativePointer;

    /// <summary>宿主设备（Spout 发送端与共享纹理同设备；CPU 回退上传纹理也建在这上面）。</summary>
    public Device D3DDevice => _device;

    private GpuRasterHost()
    {
        _provider = ResolveProvider();
        foreach (var dll in new[] { "libGLESv2.dll", "libEGL.dll" })
        {
            var path = Path.Combine(_provider, dll);
            if (LoadLibraryExW(path, IntPtr.Zero, AngleEgl.LoadWithAlteredSearchPath) == IntPtr.Zero)
                throw new InvalidOperationException($"ANGLE 载入失败：{path}（err={Marshal.GetLastWin32Error()}）");
        }

        _dpy = CreateDisplay();
        if (AngleEgl.eglInitialize(_dpy, out var mj, out var mn) == 0)
            throw new InvalidOperationException($"eglInitialize 失败 err=0x{AngleEgl.eglGetError():x}");
        AngleEgl.eglBindAPI(AngleEgl.EGL_OPENGL_ES_API);
        AngleVendor = Str(AngleEgl.eglQueryString(_dpy, AngleEgl.EGL_VENDOR));
        AngleVersion = $"{mj}.{mn} ({Str(AngleEgl.eglQueryString(_dpy, AngleEgl.EGL_VERSION))})";

        int[] cfgAttribs =
        [
            AngleEgl.EGL_SURFACE_TYPE, AngleEgl.EGL_PBUFFER_BIT,
            AngleEgl.EGL_RENDERABLE_TYPE, AngleEgl.EGL_OPENGL_ES3_BIT | AngleEgl.EGL_OPENGL_ES2_BIT,
            AngleEgl.EGL_RED_SIZE, 8, AngleEgl.EGL_GREEN_SIZE, 8, AngleEgl.EGL_BLUE_SIZE, 8,
            AngleEgl.EGL_ALPHA_SIZE, 8, AngleEgl.EGL_STENCIL_SIZE, 8,
            AngleEgl.EGL_NONE,
        ];
        var cfgs = new IntPtr[64];
        if (AngleEgl.eglChooseConfig(_dpy, cfgAttribs, cfgs, cfgs.Length, out var n) == 0 || n == 0)
            throw new InvalidOperationException("eglChooseConfig 无匹配配置");
        _cfg = cfgs[0];

        _ctx = AngleEgl.eglCreateContext(_dpy, _cfg, IntPtr.Zero,
            [AngleEgl.EGL_CONTEXT_CLIENT_VERSION, 3, AngleEgl.EGL_NONE]);
        if (_ctx == IntPtr.Zero)
            throw new InvalidOperationException($"eglCreateContext 失败 err=0x{AngleEgl.eglGetError():x}");

        // 建上下文 + 建 GRContext 都要上下文 current 在调用线程上
        if (AngleEgl.eglMakeCurrent(_dpy, IntPtr.Zero, IntPtr.Zero, _ctx) == 0)
            throw new InvalidOperationException($"eglMakeCurrent(surfaceless) 失败 err=0x{AngleEgl.eglGetError():x}");

        _device = new Device(DriverType.Hardware, DeviceCreationFlags.BgraSupport);
        AdapterName = QueryAdapterName(_device);

        var iface = GRGlInterface.CreateAngle()
                    ?? throw new InvalidOperationException("GRGlInterface.CreateAngle 返回空（libEGL/libGLESv2 不在预期目录？）");
        if (!iface.Validate())
            throw new InvalidOperationException("GRGlInterface.Validate 未通过（ANGLE GLES 入口不全）");
        _gr = GRContext.CreateGl(iface)
              ?? throw new InvalidOperationException("GRContext.CreateGl 失败");

        AngleEgl.eglMakeCurrent(_dpy, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); // 建完先解绑，等使用线程来 Enter
    }

    public string Describe() =>
        $"ANGLE {AngleVersion}，vendor={AngleVendor}，GPU={AdapterName}，provider={_provider}";

    /// <summary>使用线程进入（绑定宿主；EGL 上下文 surfless current）。</summary>
    public void Enter()
    {
        var tid = Environment.CurrentManagedThreadId;
        if (Interlocked.CompareExchange(ref _ownerThread, tid, 0) != 0 && _ownerThread != tid)
            throw new InvalidOperationException("GpuRasterHost 同一时刻只允许一个使用线程");
        if (AngleEgl.eglMakeCurrent(_dpy, IntPtr.Zero, IntPtr.Zero, _ctx) == 0)
            throw new InvalidOperationException($"GpuRasterHost.Enter 失败 err=0x{AngleEgl.eglGetError():x}");
        _active = null;
    }

    /// <summary>使用线程退出（解绑上下文，宿主保留给下一个线程复用）。</summary>
    public void Exit()
    {
        if (AngleEgl.eglMakeCurrent(_dpy, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == 0)
            Log.Debug($"[GpuRaster] Exit 解绑失败 err=0x{AngleEgl.eglGetError():x}");
        _active = null;
        Interlocked.Exchange(ref _ownerThread, 0);
    }

    /// <summary>把一个 GPU 面设为当前绘制目标（pbuffer 的 FBO0 随 eglMakeCurrent 切换）。</summary>
    public void Activate(GpuSurface s)
    {
        if (ReferenceEquals(_active, s)) return;
        if (AngleEgl.eglMakeCurrent(_dpy, s.SurfaceHandle, s.SurfaceHandle, _ctx) == 0)
            throw new InvalidOperationException($"eglMakeCurrent(面 {s.Width}x{s.Height}) 失败 err=0x{AngleEgl.eglGetError():x}");
        _active = s;
    }

    /// <summary>按通道标识与尺寸取面（同标识同尺寸复用；调用方须先 Enter + Activate）。</summary>
    public GpuSurface Rent(string key, int w, int h)
    {
        w = Math.Max(1, w);
        h = Math.Max(1, h);
        if (_surfaces.TryGetValue((key, w, h), out var s)) return s;
        var surf = AngleEgl.eglCreatePbufferSurface(_dpy, _cfg,
            [AngleEgl.EGL_WIDTH, w, AngleEgl.EGL_HEIGHT, h, AngleEgl.EGL_NONE]);
        if (surf == IntPtr.Zero)
            throw new InvalidOperationException($"{w}x{h} eglCreatePbufferSurface 失败 err=0x{AngleEgl.eglGetError():x}");
        s = new GpuSurface(this, w, h, surf);
        _surfaces[(key, w, h)] = s;
        return s;
    }

    /// <summary>按尺寸取面（向后兼容默认键；调用方须先 Enter + Activate）。</summary>
    public GpuSurface Rent(int w, int h) => Rent("default", w, h);

    /// <summary>提交本帧：Skia 刷命令 + 等 GPU 落盘（落盘后共享纹理才可被 Spout 的设备读）。</summary>
    public void FlushAndSync()
    {
        if (_gr.IsAbandoned)
            throw new InvalidOperationException("GRContext 已失效 (IsAbandoned = true)");
        _gr.Flush();
        AngleEgl.glFinish();
    }

    /// <summary>
    /// Skia 托管的 GPU 面（自带非零 FBO）：给受控的 GPU 面使用。
    /// 注意：在 SkiaSharp 中，若中间 GPU 面或其快照被 GC 回收，终结器线程在无 EGL 上下文下
    /// 调用 sk_surface_unref / sk_image_unref 会与 Flush 产生并发冲突引发 0xC000001D 崩溃。
    /// 渲染引擎内部中间面（scratch/trans/morph 等）统一使用 CPU 面以确保 GC 安全。
    /// </summary>
    public SKSurface CreateGpuSurface(int w, int h) =>
        SKSurface.Create(_gr, budgeted: true, new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul))
        ?? throw new InvalidOperationException($"Skia GPU 面创建失败 {w}x{h}");

    internal IntPtr Dpy => _dpy;
    internal Device D3D => _device;
    internal GRContext Gr => _gr;

    private static IntPtr CreateDisplay()
    {
        // D3D11 后端 + 硬件设备优先；本机实测该组合属性会被拒（返回 0），退回默认显示
        // ——ANGLE 在 Windows 的默认平台就是 D3D11，vendor 串会写明是哪块 GPU
        var dpy = AngleEgl.eglGetPlatformDisplayEXT(AngleEgl.EGL_PLATFORM_ANGLE_ANGLE, IntPtr.Zero,
            [AngleEgl.EGL_PLATFORM_ANGLE_TYPE_ANGLE, AngleEgl.EGL_PLATFORM_ANGLE_TYPE_D3D11_ANGLE,
             AngleEgl.EGL_PLATFORM_ANGLE_DEVICE_TYPE_ANGLE, AngleEgl.EGL_PLATFORM_ANGLE_DEVICE_TYPE_HARDWARE_ANGLE,
             AngleEgl.EGL_NONE]);
        if (dpy != IntPtr.Zero) return dpy;
        dpy = AngleEgl.eglGetDisplay(IntPtr.Zero);
        if (dpy == IntPtr.Zero)
            throw new InvalidOperationException("eglGetDisplay 失败（libEGL.dll 未正确载入？）");
        return dpy;
    }

    private static string QueryAdapterName(Device dev)
    {
        try
        {
            using var dxgiDev = dev.QueryInterface<SharpDX.DXGI.Device>();
            using var adapter = dxgiDev.Adapter;
            return $"{adapter.Description.Description.Trim()} (Luid={adapter.Description.Luid})";
        }
        catch (Exception ex)
        {
            return "(适配器名不可得：" + ex.Message + ")";
        }
    }

    /// <summary>
    /// 找 ANGLE 二进制目录。优先随包分发的 <c>angle/</c> 子目录（tools/fetch-angle.ps1 铺），
    /// 其次环境变量 JIZURA_ANGLE_DIR，再次开发机上的 NuGet 缓存（CEF 官方构建，BSD 许可）。
    /// </summary>
    private static string ResolveProvider()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("JIZURA_ANGLE_DIR") is { Length: > 0 } env)
            candidates.Add(env);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "angle"));
        candidates.Add(AppContext.BaseDirectory);
        // 开发机兜底：chromiumembeddedframework.runtime.* 包内的官方 ANGLE（多版本取最新）
        try
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages");
            if (Directory.Exists(root))
            {
                var cef = Directory.GetDirectories(Path.Combine(root, "chromiumembeddedframework.runtime.win-x64"))
                    .OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase)
                    .Select(d => Path.Combine(d, "runtimes", "win-x64", "native"));
                candidates.AddRange(cef);
            }
        }
        catch (Exception ex)
        {
            Log.Debug("[GpuRaster] 扫 NuGet 缓存失败：" + ex.Message);
        }

        foreach (var dir in candidates)
        {
            try
            {
                if (File.Exists(Path.Combine(dir, "libEGL.dll")) && File.Exists(Path.Combine(dir, "libGLESv2.dll")))
                    return dir;
            }
            catch { }
        }

        throw new InvalidOperationException(
            "未找到 ANGLE（libEGL.dll + libGLESv2.dll）。跑 tools/fetch-angle.ps1 铺到 native/angle/，"
            + "或设 JIZURA_ANGLE_DIR 指向已有 ANGLE 目录；也可 JIZURA_GPU=0 显式走 CPU 光栅");
    }

    private static string Str(IntPtr p) => p == IntPtr.Zero ? "(null)" : Marshal.PtrToStringAnsi(p) ?? "?";

    public void Dispose()
    {
        lock (Gate)
        {
            if (ReferenceEquals(_instance, this)) _instance = null;
        }

        foreach (var s in _surfaces.Values) s.Dispose();
        _surfaces.Clear();
        _gr.Dispose();
        _device.Dispose();
        AngleEgl.eglMakeCurrent(_dpy, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_ctx != IntPtr.Zero) AngleEgl.eglDestroyContext(_dpy, _ctx);
        if (_dpy != IntPtr.Zero) AngleEgl.eglTerminate(_dpy);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
}

/// <summary>
/// 一块 GPU 渲染面：ANGLE pbuffer + 它背后的 D3D11 共享纹理（已在宿主设备上打开）+ Skia 面。
/// 生命周期随宿主（按尺寸池化复用）；使用前必须 <see cref="GpuRasterHost.Activate"/>。
/// </summary>
public sealed class GpuSurface : IDisposable
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>ANGLE 表面句柄（eglMakeCurrent 的 draw/read）。</summary>
    internal IntPtr SurfaceHandle { get; }

    /// <summary>Skia 面：画它就是画进 <see cref="TexturePointer"/> 那块纹理。</summary>
    public SKSurface Skia { get; }

    /// <summary>纹理指针（交给 SpoutDX SendTexture）。</summary>
    public IntPtr TexturePointer { get; }

    /// <summary>共享纹理本体（已在宿主设备上打开）。预览镜像读回用它做 CopyResource 源。</summary>
    public Texture2D Texture => _texture;

    /// <summary>DXGI 格式（Spout 发送端格式须与之一致）。</summary>
    public int DxgiFormat { get; }

    private readonly GpuRasterHost _host;
    private readonly Texture2D _texture;
    private readonly GRBackendRenderTarget _rt;

    internal GpuSurface(GpuRasterHost host, int w, int h, IntPtr surfaceHandle)
    {
        _host = host;
        Width = w;
        Height = h;
        SurfaceHandle = surfaceHandle;
        host.Activate(this);

        if (AngleEgl.eglQuerySurfacePointerANGLE(host.Dpy, surfaceHandle,
                AngleEgl.EGL_D3D_TEXTURE_2D_SHARE_HANDLE_ANGLE, out var shareHandle) == 0 || shareHandle == IntPtr.Zero)
            throw new InvalidOperationException(
                $"{w}x{h} eglQuerySurfacePointerANGLE 取共享句柄失败 err=0x{AngleEgl.eglGetError():x}");

        _texture = host.D3D.OpenSharedResource<Texture2D>(shareHandle);
        var desc = _texture.Description;
        DxgiFormat = (int)desc.Format;
        TexturePointer = _texture.NativePointer;

        _rt = new GRBackendRenderTarget(w, h, 0, 0,
            new GRGlFramebufferInfo(0, unchecked((uint)AngleEgl.GL_RGBA8)));
        Skia = SKSurface.Create(host.Gr, _rt, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888)
               ?? throw new InvalidOperationException($"{w}x{h} SKSurface.Create(GPU) 失败");
    }

    /// <summary>
    /// 本帧结果的 GPU 快照（不读回内存）。调用方持有返回对象（using）。
    /// 注意：快照后继续画这块面会让 Skia 走一次写时复制（GPU 侧拷一份），
    /// 只在「画幅比与输出不一致要 letterbox」这类旁路里用。
    /// </summary>
    public SKImage Snapshot() => Skia.Snapshot();

    public void Dispose()
    {
        Skia.Dispose();
        _rt.Dispose();
        _texture.Dispose();
        if (SurfaceHandle != IntPtr.Zero)
            AngleEgl.eglDestroySurface(_host.Dpy, SurfaceHandle);
    }
}
