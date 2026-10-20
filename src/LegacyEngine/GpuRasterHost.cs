using System.IO;
using System.Runtime.InteropServices;
using JizuraLive.Logging;
using SkiaSharp;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace JizuraLive.LegacyEngine;

public sealed class GpuRasterHost : IDisposable
{
    private static readonly object Gate = new();
    private static GpuRasterHost? _instance;
    private static bool _failed;

        public static bool DisabledByEnv =>
        Environment.GetEnvironmentVariable("JIZURA_GPU") is "0" or "off" or "false";

        public static GpuRasterHost? TryGet(ID3D11Device device, string adapterName, out string error)
    {
        lock (Gate)
        {
            if (_instance != null)
            {
                if (_instance._device.NativePointer != device.NativePointer)
                {
                    error = "GpuRasterHost 已绑定到另一块 D3D11 设备";
                    return null;
                }

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
                _instance = new GpuRasterHost(device, adapterName);
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
    private readonly ID3D11Device _device;
    private readonly GRContext _gr;
    private readonly string _provider;
    private readonly Dictionary<(string Key, int W, int H), GpuSurface> _surfaces = new();

    private int _ownerThread;
    private GpuSurface? _active;

    public string AngleVersion { get; }
    public string AngleVendor { get; }
    public string AdapterName { get; }

    private GpuRasterHost(ID3D11Device device, string adapterName)
    {
        _device = device;
        AdapterName = adapterName;
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

        if (AngleEgl.eglMakeCurrent(_dpy, IntPtr.Zero, IntPtr.Zero, _ctx) == 0)
            throw new InvalidOperationException($"eglMakeCurrent(surfaceless) 失败 err=0x{AngleEgl.eglGetError():x}");

        var iface = GRGlInterface.CreateAngle()
                    ?? throw new InvalidOperationException("GRGlInterface.CreateAngle 返回空（libEGL/libGLESv2 不在预期目录？）");
        if (!iface.Validate())
            throw new InvalidOperationException("GRGlInterface.Validate 未通过（ANGLE GLES 入口不全）");
        _gr = GRContext.CreateGl(iface)
              ?? throw new InvalidOperationException("GRContext.CreateGl 失败");

        AngleEgl.eglMakeCurrent(_dpy, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);     }

    public string Describe() =>
        $"ANGLE {AngleVersion}，vendor={AngleVendor}，GPU={AdapterName}，provider={_provider}";

        public void Enter()
    {
        var tid = Environment.CurrentManagedThreadId;
        if (Interlocked.CompareExchange(ref _ownerThread, tid, 0) != 0 && _ownerThread != tid)
            throw new InvalidOperationException("GpuRasterHost 同一时刻只允许一个使用线程");
        if (AngleEgl.eglMakeCurrent(_dpy, IntPtr.Zero, IntPtr.Zero, _ctx) == 0)
        {
            Interlocked.Exchange(ref _ownerThread, 0);
            throw new InvalidOperationException($"GpuRasterHost.Enter 失败 err=0x{AngleEgl.eglGetError():x}");
        }
        _active = null;
    }

        public void Exit()
    {
        if (AngleEgl.eglMakeCurrent(_dpy, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == 0)
            Log.Debug($"[GpuRaster] Exit 解绑失败 err=0x{AngleEgl.eglGetError():x}");
        _active = null;
        Interlocked.Exchange(ref _ownerThread, 0);
    }

        public void Activate(GpuSurface s)
    {
        if (ReferenceEquals(_active, s)) return;
        if (AngleEgl.eglMakeCurrent(_dpy, s.SurfaceHandle, s.SurfaceHandle, _ctx) == 0)
            throw new InvalidOperationException($"eglMakeCurrent(面 {s.Width}x{s.Height}) 失败 err=0x{AngleEgl.eglGetError():x}");
        _active = s;
    }

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

        public void Flush()
    {
        if (_gr.IsAbandoned)
            throw new InvalidOperationException("GRContext 已失效 (IsAbandoned = true)");
        _gr.Flush();
        AngleEgl.glFlush();
    }

        public void FlushAndSync()
    {
        if (_gr.IsAbandoned)
            throw new InvalidOperationException("GRContext 已失效 (IsAbandoned = true)");
        _gr.Flush();
        AngleEgl.glFinish();
    }

        public SKSurface CreateGpuSurface(int w, int h) =>
        SKSurface.Create(_gr, budgeted: true, new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul))
        ?? throw new InvalidOperationException($"Skia GPU 面创建失败 {w}x{h}");

    internal IntPtr Dpy => _dpy;
    internal ID3D11Device D3D => _device;
    internal GRContext Gr => _gr;

    private static IntPtr CreateDisplay()
    {
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

        private static string ResolveProvider()
    {
        var candidates = new List<string>();
        if (Environment.GetEnvironmentVariable("JIZURA_ANGLE_DIR") is { Length: > 0 } env)
            candidates.Add(env);
        candidates.Add(Path.Combine(AppContext.BaseDirectory, "angle"));
        candidates.Add(AppContext.BaseDirectory);
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
            "未找到 ANGLE（libEGL.dll + libGLESv2.dll）。把 ANGLE 放到 native/angle/，"
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
        AngleEgl.eglMakeCurrent(_dpy, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_ctx != IntPtr.Zero) AngleEgl.eglDestroyContext(_dpy, _ctx);
        if (_dpy != IntPtr.Zero) AngleEgl.eglTerminate(_dpy);
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryExW(string path, IntPtr file, uint flags);
}

public sealed class GpuSurface : IDisposable
{
    public int Width { get; }
    public int Height { get; }

        internal IntPtr SurfaceHandle { get; }

        public SKSurface Skia { get; }

        public IntPtr TexturePointer { get; }

        public ID3D11Texture2D Texture => _texture;

        public int DxgiFormat { get; }

    private readonly GpuRasterHost _host;
    private readonly ID3D11Texture2D _texture;
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

        _texture = host.D3D.OpenSharedResource<ID3D11Texture2D>(shareHandle);
        var desc = _texture.Description;
        DxgiFormat = (int)desc.Format;
        TexturePointer = _texture.NativePointer;

        _rt = new GRBackendRenderTarget(w, h, 0, 0,
            new GRGlFramebufferInfo(0, unchecked((uint)AngleEgl.GL_RGBA8)));
        Skia = SKSurface.Create(host.Gr, _rt, GRSurfaceOrigin.TopLeft, SKColorType.Rgba8888)
               ?? throw new InvalidOperationException($"{w}x{h} SKSurface.Create(GPU) 失败");
    }

    public void Dispose()
    {
        Skia.Dispose();
        _rt.Dispose();
        _texture.Dispose();
        if (SurfaceHandle != IntPtr.Zero)
            AngleEgl.eglDestroySurface(_host.Dpy, SurfaceHandle);
    }
}
