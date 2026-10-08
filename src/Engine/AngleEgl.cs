using System.Runtime.InteropServices;

namespace JizuraLive.Engine;

/// <summary>
/// libEGL/libGLESv2（ANGLE）P/Invoke 的极小集合：GPU 光栅宿主只要「建显示/配置/上下文、
/// 建 pbuffer 表面、问出表面背后的 D3D11 共享句柄、清屏等待」这五件事。
///
/// 常量来源：EGL 1.5 核心 + ANGLE 扩展。凡本机无头文件可核实的（0x3200 一族）都注明实测依据——
/// 四轮 POC 已用运行时探测定死：eglQuerySurfacePointerANGLE 只认 attribute=0x3200，
/// 返回的是 D3D11 纹理共享句柄（ID3D11Device::OpenSharedResource 可直接打开，BGRA8）。
/// </summary>
internal static class AngleEgl
{
    public const string EglDll = "libEGL.dll";
    public const string GlesDll = "libGLESv2.dll";

    // ---- EGL 核心 ----
    public const int EGL_NONE = 0x3038;
    public const int EGL_WIDTH = 0x3057;
    public const int EGL_HEIGHT = 0x3056;
    public const int EGL_RED_SIZE = 0x3024;
    public const int EGL_GREEN_SIZE = 0x3023;
    public const int EGL_BLUE_SIZE = 0x3022;
    public const int EGL_ALPHA_SIZE = 0x3021;
    public const int EGL_STENCIL_SIZE = 0x3026;
    public const int EGL_SURFACE_TYPE = 0x3033;
    public const int EGL_PBUFFER_BIT = 0x0001;
    public const int EGL_RENDERABLE_TYPE = 0x3040;
    public const int EGL_OPENGL_ES2_BIT = 0x0004;
    public const int EGL_OPENGL_ES3_BIT = 0x0040;
    public const int EGL_CONTEXT_CLIENT_VERSION = 0x3098;
    public const int EGL_OPENGL_ES_API = 0x30A0;
    public const int EGL_EXTENSIONS = 0x3055;
    public const int EGL_VENDOR = 0x3053;
    public const int EGL_VERSION = 0x3054;

    // ---- EGL_ANGLE_* ----
    public const int EGL_PLATFORM_ANGLE_ANGLE = 0x3202;
    public const int EGL_PLATFORM_ANGLE_TYPE_ANGLE = 0x3203;
    public const int EGL_PLATFORM_ANGLE_TYPE_D3D11_ANGLE = 0x320B;
    public const int EGL_PLATFORM_ANGLE_DEVICE_TYPE_ANGLE = 0x3209;
    public const int EGL_PLATFORM_ANGLE_DEVICE_TYPE_HARDWARE_ANGLE = 0x320A;

    /// <summary>EGL_D3D_TEXTURE_2D_SHARE_HANDLE_ANGLE（实测：eglQuerySurfacePointerANGLE 唯一认的属性）。</summary>
    public const int EGL_D3D_TEXTURE_2D_SHARE_HANDLE_ANGLE = 0x3200;

    // ---- GLES ----
    public const int GL_COLOR_BUFFER_BIT = 0x00004000;
    public const int GL_RGBA8 = 0x8058;

    [DllImport(EglDll)] public static extern IntPtr eglGetDisplay(IntPtr nativeDisplay);
    [DllImport(EglDll)] public static extern IntPtr eglGetPlatformDisplayEXT(int platform, IntPtr nativeDisplay, int[]? attribs);
    [DllImport(EglDll)] public static extern int eglInitialize(IntPtr dpy, out int major, out int minor);
    [DllImport(EglDll)] public static extern int eglBindAPI(int api);
    [DllImport(EglDll)] public static extern IntPtr eglQueryString(IntPtr dpy, int name);
    [DllImport(EglDll)] public static extern int eglChooseConfig(IntPtr dpy, int[] attribs, IntPtr[] configs, int configSize, out int numConfig);
    [DllImport(EglDll)] public static extern IntPtr eglCreateContext(IntPtr dpy, IntPtr cfg, IntPtr share, int[] attribs);
    [DllImport(EglDll)] public static extern IntPtr eglCreatePbufferSurface(IntPtr dpy, IntPtr cfg, int[] attribs);
    [DllImport(EglDll)] public static extern int eglQuerySurfacePointerANGLE(IntPtr dpy, IntPtr surface, int attribute, out IntPtr value);
    [DllImport(EglDll)] public static extern int eglMakeCurrent(IntPtr dpy, IntPtr draw, IntPtr read, IntPtr ctx);
    [DllImport(EglDll)] public static extern int eglGetError();

    [DllImport(GlesDll)] public static extern void glViewport(int x, int y, int w, int h);
    [DllImport(GlesDll)] public static extern void glClear(int mask);
    [DllImport(GlesDll)] public static extern void glFinish();

    /// <summary>加载同目录依赖（libGLESv2/d3dcompiler_47）用：GpuRasterHost 预载 ANGLE 时设。</summary>
    public const uint LoadWithAlteredSearchPath = 0x00000008;
}
