using System.Runtime.InteropServices;

namespace AngleSpike;

/// <summary>libEGL（ANGLE）P/Invoke：surfaceless GLES 上下文 + D3D11 共享句柄 pbuffer 表面。</summary>
internal static class Egl
{
    private const string Dll = "libEGL.dll";

    public const int EGL_NONE = 0x3038;
    public const int EGL_TRUE = 1;
    public const int EGL_WIDTH = 0x3057;
    public const int EGL_HEIGHT = 0x3056;
    public const int EGL_ALPHA_SIZE = 0x3021;
    public const int EGL_BLUE_SIZE = 0x3022;
    public const int EGL_GREEN_SIZE = 0x3023;
    public const int EGL_RED_SIZE = 0x3024;
    public const int EGL_DEPTH_SIZE = 0x3025;
    public const int EGL_SURFACE_TYPE = 0x3033;
    public const int EGL_PBUFFER_BIT = 0x0001;
    public const int EGL_RENDERABLE_TYPE = 0x3040;
    public const int EGL_OPENGL_ES2_BIT = 0x0004;
    public const int EGL_OPENGL_ES3_BIT = 0x0040;
    public const int EGL_CONTEXT_CLIENT_VERSION = 0x3098;
    public const int EGL_OPENGL_ES_API = 0x30A0;
    public const int EGL_D3D_TEXTURE_2D_SHARE_HANDLE_ANGLE = 0x3200;
    public const int EGL_DEVICE_EXT = 0x322C;
    public const int EGL_D3D11_DEVICE_ANGLE = 0x33A0;

    public const int EGL_EXTENSIONS = 0x3055;
    public const int EGL_PLATFORM_ANGLE_ANGLE = 0x3202;
    public const int EGL_PLATFORM_ANGLE_TYPE_ANGLE = 0x3203;
    public const int EGL_PLATFORM_ANGLE_TYPE_D3D11_ANGLE = 0x320B;
    public const int EGL_PLATFORM_ANGLE_DEVICE_TYPE_ANGLE = 0x3209;
    public const int EGL_PLATFORM_ANGLE_DEVICE_TYPE_HARDWARE_ANGLE = 0x320A;

    [DllImport(Dll)] public static extern IntPtr eglGetDisplay(IntPtr nativeDisplay);
    [DllImport(Dll)] public static extern IntPtr eglGetPlatformDisplayEXT(int platform, IntPtr nativeDisplay, int[]? attribs);
    [DllImport(Dll)] public static extern IntPtr eglGetPlatformDisplay(int platform, IntPtr nativeDisplay, IntPtr[]? attribs);
    [DllImport(Dll)] public static extern int eglInitialize(IntPtr dpy, out int major, out int minor);
    [DllImport(Dll)] public static extern int eglBindAPI(int api);
    [DllImport(Dll)] public static extern IntPtr eglQueryString(IntPtr dpy, int name);
    [DllImport(Dll)] public static extern int eglChooseConfig(IntPtr dpy, int[] attribs, IntPtr[] configs, int configSize, out int numConfig);
    [DllImport(Dll)] public static extern IntPtr eglCreateContext(IntPtr dpy, IntPtr cfg, IntPtr share, int[] attribs);
    [DllImport(Dll)] public static extern IntPtr eglCreatePbufferSurface(IntPtr dpy, IntPtr cfg, int[] attribs);
    [DllImport(Dll)] public static extern int eglMakeCurrent(IntPtr dpy, IntPtr draw, IntPtr read, IntPtr ctx);
    [DllImport(Dll)] public static extern int eglQueryDisplayAttribEXT(IntPtr dpy, int attr, out IntPtr value);
    [DllImport(Dll)] public static extern int eglQueryDeviceAttribEXT(IntPtr device, int attr, out IntPtr value);
    [DllImport(Dll)] public static extern IntPtr eglGetCurrentContext();
    [DllImport(Dll)] public static extern int eglGetError();
    [DllImport(Dll)] public static extern int eglGetConfigAttrib(IntPtr dpy, IntPtr cfg, int attr, out int value);
    public const int EGL_D3D11_TEXTURE_ANGLE = 0x3484;
    [DllImport(Dll)] public static extern IntPtr eglCreateImageKHR(IntPtr dpy, IntPtr ctx, int target, IntPtr buffer, int[]? attribs);
    [DllImport("libGLESv2.dll")] public static extern void glGenTextures(int n, int[] textures);
    [DllImport("libGLESv2.dll")] public static extern void glBindTexture(int target, int texture);
    [DllImport("libGLESv2.dll")] public static extern void glGenFramebuffers(int n, int[] framebuffers);
    [DllImport("libGLESv2.dll")] public static extern void glBindFramebuffer(int target, int framebuffer);
    [DllImport("libGLESv2.dll")] public static extern void glFramebufferTexture2D(int target, int attachment, int textarget, int texture, int level);
    [DllImport("libGLESv2.dll")] public static extern void glEGLImageTargetTexture2DOES(int target, IntPtr image);
    public const int GL_TEXTURE_2D = 0x0DE1;
    public const int GL_FRAMEBUFFER = 0x8D40;
    public const int GL_COLOR_ATTACHMENT0 = 0x8CE0;
}
