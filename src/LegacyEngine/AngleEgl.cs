using System.Runtime.InteropServices;

namespace JizuraLive.LegacyEngine;

internal static class AngleEgl
{
    public const string EglDll = "libEGL.dll";
    public const string GlesDll = "libGLESv2.dll";

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
    public const int EGL_VENDOR = 0x3053;
    public const int EGL_VERSION = 0x3054;

    public const int EGL_PLATFORM_ANGLE_ANGLE = 0x3202;
    public const int EGL_PLATFORM_ANGLE_TYPE_ANGLE = 0x3203;
    public const int EGL_PLATFORM_ANGLE_TYPE_D3D11_ANGLE = 0x320B;
    public const int EGL_PLATFORM_ANGLE_DEVICE_TYPE_ANGLE = 0x3209;
    public const int EGL_PLATFORM_ANGLE_DEVICE_TYPE_HARDWARE_ANGLE = 0x320A;

        public const int EGL_D3D_TEXTURE_2D_SHARE_HANDLE_ANGLE = 0x3200;

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
    [DllImport(EglDll)] public static extern int eglDestroySurface(IntPtr dpy, IntPtr surface);
    [DllImport(EglDll)] public static extern int eglDestroyContext(IntPtr dpy, IntPtr ctx);
    [DllImport(EglDll)] public static extern int eglTerminate(IntPtr dpy);
    [DllImport(EglDll)] public static extern int eglGetError();

    [DllImport(GlesDll)] public static extern void glViewport(int x, int y, int w, int h);
    [DllImport(GlesDll)] public static extern void glClear(int mask);
    [DllImport(GlesDll)] public static extern void glFinish();
    [DllImport(GlesDll)] public static extern void glFlush();

        public const uint LoadWithAlteredSearchPath = 0x00000008;
}
