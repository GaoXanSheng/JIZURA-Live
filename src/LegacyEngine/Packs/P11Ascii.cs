using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.LegacyEngine.Packs;

public static class P11Ascii
{
    private const string Ramp = " .'`^\",:;Il!i><~+_-?][}{1)(|\\/tfjrxnuvczXYUJCLQ0OZmwqpdbkhao*#MW&8%B@$";
    private const double Boost = 1.4;        private const double LumSkip = 8;    
    private static SKSurface? _buf;
    private static int _bw = -1, _bh = -1;
    private static SKBitmap? _small;
    private static SKSurface? _smallSurf;
    private static int _sw = -1, _sh = -1;

    private static void Ensure(int w, int h, int cols, int rows)
    {
        if (_buf == null || _bw != w || _bh != h)
        {
            _buf = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
            _bw = w;
            _bh = h;
        }
        if (_small == null || _sw != cols || _sh != rows)
        {
            _small?.Dispose();
            _smallSurf?.Dispose();
            _small = new SKBitmap(cols, rows, SKColorType.Bgra8888, SKAlphaType.Premul);
            _smallSurf = SKSurface.Create(_small.Info, _small.GetPixels(), _small.RowBytes);
            _sw = cols;
            _sh = rows;
        }
    }

        public static void Frame(SKCanvas ctx, Plan plan, double t, RenderOpts opt,
        Action<SKCanvas, RenderOpts> renderInner)
    {
        var W = plan.W;
        var H = plan.H;
        var scale = opt.Scale != 0 ? opt.Scale : 1;
        var dm = ctx.DeviceClipBounds;
        var cw = Math.Max(1, (int)dm.Width);
        var ch = Math.Max(1, (int)dm.Height);

        var fontPx = Math.Max(8, (int)Math.Round(H * 0.023));
        var cellH = (int)Math.Round(fontPx * 1.08);
        using var _ = new SKAutoCanvasRestore(ctx);
        ctx.SetMatrix(SKMatrix.Identity);
        ctx.Clear(new SKColor(0, 0, 0, 0));
        ctx.Scale((float)scale, (float)scale);
        using var typeface = SKTypeface.FromFamilyName("Consolas", SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        using var font = new SKFont(typeface, fontPx);
        using var meas = new SKPaint();
        var cellW = Math.Max(4f, font.MeasureText("M", meas));
        var cols = Math.Max(8, (int)Math.Floor(W / cellW));
        var rows = Math.Max(6, (int)Math.Floor(H / (double)cellH));

        Ensure(W, H, cols, rows);

        var bctx = _buf!.Canvas;
        bctx.SetMatrix(SKMatrix.Identity);
        bctx.Clear(new SKColor(0, 0, 0, 0));
        var io = opt.Clone();
        io.AsciiInner = true;
        io.Scale = 1;
        io.Transparent = false;
        io.Layer = null;
        renderInner(bctx, io);

        using var smooth = new SKPaint { IsAntialias = true };
        _smallSurf!.Canvas.Clear();
        using (var full = SKImage.FromPixels(_buf.PeekPixels()!.Info, _buf.PeekPixels()!.GetPixels(), _buf.PeekPixels()!.RowBytes))
            _smallSurf.Canvas.DrawImage(full, new SKRect(0, 0, cols, rows),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), smooth);
        var px = _small.PeekPixels();
        var data = px.GetPixelSpan();

        var n = Ramp.Length;
        var lastFill = "";
        using var fp = new SKPaint { IsAntialias = true };
        for (var r = 0; r < rows; r++)
        {
            var y = (r + 0.5f) * cellH;
            for (var c = 0; c < cols; c++)
            {
                var i = (r * cols + c) * 4;
                var rr = data[i + 2];                 var gg = data[i + 1];
                var bb = data[i];
                var lum = 0.2126 * rr + 0.7152 * gg + 0.0722 * bb;
                var idx = (int)Math.Round(lum / 255 * (n - 1));
                if (idx <= 0 || lum < LumSkip) continue;                 var fill = "rgb(" + Math.Min(255, (int)Math.Round(rr * Boost)) + "," +
                           Math.Min(255, (int)Math.Round(gg * Boost)) + "," +
                           Math.Min(255, (int)Math.Round(bb * Boost)) + ")";
                if (fill != lastFill)
                {
                    fp.Color = ParseRgb(fill);
                    lastFill = fill;
                }
                ctx.DrawText(Ramp[idx].ToString(), (c + 0.5f) * cellW, y, SKTextAlign.Center, font, fp);
            }
        }
    }

        private static SKColor ParseRgb(string s)
    {
        var parts = s[4..^1].Split(',');
        return new SKColor(byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]));
    }

    }
