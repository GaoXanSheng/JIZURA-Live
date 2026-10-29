using System.Windows.Media;
using System.Windows.Media.Imaging;
using JizuraLive.Logging;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace JizuraLive.App.PvStage;

internal static class PvTransitionPreview
{
    private const int Width = 320;
    private const int Height = 180;

    private static readonly object Gate = new();
    private static Host? _host;

    public static BitmapSource? Render(TransitionTechnique technique)
    {
        lock (Gate)
        {
            try
            {
                _host ??= Host.Create();
                return _host.Render(technique);
            }
            catch (Exception ex)
            {
                Log.Warn("[PV 转场预览] 渲染失败：" + ex.Message);
                return null;
            }
        }
    }

    private sealed class Host : IDisposable
    {
        private readonly D3DDevice _gpu;
        private readonly D2DHost _d2d;
        private readonly GpuTexturePool _pool;
        private readonly TransitionRunner _runner;
        private readonly OffscreenTarget _target;

        private Host(D3DDevice gpu, D2DHost d2d, GpuTexturePool pool,
            TransitionRunner runner, OffscreenTarget target)
        {
            _gpu = gpu;
            _d2d = d2d;
            _pool = pool;
            _runner = runner;
            _target = target;
        }

        public static Host Create()
        {
            var gpu = D3DDevice.TryCreate(out var error)
                      ?? throw new InvalidOperationException(error ?? "无法建立离屏 D3D 设备");
            var d2d = D2DHost.Create(gpu);
            var pool = new GpuTexturePool(gpu.Device);
            var runner = new TransitionRunner(d2d.Draw, pool, Width, Height);
            var target = OffscreenTarget.Create(gpu, d2d.Ctx, Width, Height, sampleable: true);
            var host = new Host(gpu, d2d, pool, runner, target);
            host.Prepare();
            return host;
        }

        private void Prepare()
        {
            _runner.Acquire();
            var a = _runner.SurfaceA ?? throw new InvalidOperationException("转场预览缺少 A 面");
            var b = _runner.SurfaceB ?? throw new InvalidOperationException("转场预览缺少 B 面");
            DrawPattern(a, new Color4(0.04f, 0.09f, 0.22f, 1f), new Color4(0.20f, 0.88f, 1f, 1f), false);
            DrawPattern(b, new Color4(0.24f, 0.05f, 0.08f, 1f), new Color4(1f, 0.48f, 0.18f, 1f), true);
        }

        private void DrawPattern(ID2D1Bitmap1 bitmap, Color4 background, Color4 accent, bool second)
        {
            var draw = _d2d.Draw;
            draw.SetTarget(bitmap, Width, Height);
            draw.Begin();
            try
            {
                draw.Clear(background);
                var paint = new Paint2D { Brush = new SolidBrush2D(accent) };
                if (second)
                {
                    draw.FillCircle(Width * 0.68f, Height * 0.42f, Height * 0.28f, paint);
                    draw.FillRect(new Rect(Width * 0.08f, Height * 0.62f, Width * 0.34f, Height * 0.12f), paint);
                }
                else
                {
                    draw.FillCircle(Width * 0.32f, Height * 0.42f, Height * 0.28f, paint);
                    draw.FillRect(new Rect(Width * 0.58f, Height * 0.26f, Width * 0.34f, Height * 0.12f), paint);
                }

                var line = new Paint2D
                {
                    Brush = new SolidBrush2D(new Color4(1f, 1f, 1f, 1f)),
                    StrokeWidth = 3f,
                };
                draw.DrawLine(Width * 0.1f, Height * 0.82f, Width * 0.9f, Height * 0.82f, line);
            }
            finally
            {
                draw.End();
            }
        }

        public BitmapSource Render(TransitionTechnique technique)
        {
            var pars = new TransitionParams { Seed = 123 };
            _runner.Composite(_target.Bitmap, new Rect(0, 0, Width, Height), technique, 0.5, pars);
            using var frame = _target.Readback();
            var bitmap = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32,
                null, frame.BgraPixels, Width * 4);
            bitmap.Freeze();
            return bitmap;
        }

        public void Dispose()
        {
            _target.Dispose();
            _runner.Dispose();
            _pool.Dispose();
            _d2d.Dispose();
            _gpu.Dispose();
        }
    }
}
