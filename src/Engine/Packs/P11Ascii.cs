using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine.Packs;

/// <summary>
/// ASCII 字符画风格（移植自 frontend/src/engine/11p_ascii.js）：整帧先走正常渲染管线
/// 画进离屏画布，再降采样成字符网格——亮度映射字符密度阶梯，颜色取单元格均色并提亮补偿。
/// 由 Renderer 在 st.ascii 时回调 renderInner（正常管线一帧），本类负责转换与绘制。
/// </summary>
public static class P11Ascii
{
    private const string Ramp = " .'`^\",:;Il!i><~+_-?][}{1)(|\\/tfjrxnuvczXYUJCLQ0OZmwqpdbkhao*#MW&8%B@$";
    private const double Boost = 1.4;    // 字符只盖住单元格的一小部分面积，整体提亮补偿
    private const double LumSkip = 8;    // 亮度低于此值画空格（暗背景→留黑/留透明）

    // 复用：全分辨率离屏面（正常管线的渲染目标）。被递归 Frame 的整帧缓存持有快照——
    // 与其他引擎面同规矩：绝不 Dispose，换尺寸旧面孤儿化（进程生命周期内随 GC/退出释放）
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

    /// <summary>ASCII 帧主入口（对位 J.asciiFrame）。renderInner 由 Renderer 提供：把正常管线一帧画进指定画布。</summary>
    public static void Frame(SKCanvas ctx, Plan plan, double t, RenderOpts opt,
        Action<SKCanvas, RenderOpts> renderInner)
    {
        var W = plan.W;
        var H = plan.H;
        var scale = opt.Scale != 0 ? opt.Scale : 1;
        var dm = ctx.DeviceClipBounds;
        var cw = Math.Max(1, (int)dm.Width);
        var ch = Math.Max(1, (int)dm.Height);

        // 字符网格：等宽字体决定密度
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

        // 1) 正常管线渲整帧进离屏（强制不透明、无分层，保证转换吃到完整画面）
        var bctx = _buf!.Canvas;
        bctx.SetMatrix(SKMatrix.Identity);
        bctx.Clear(new SKColor(0, 0, 0, 0));
        var io = opt.Clone();
        io.AsciiInner = true;
        io.Scale = 1;
        io.Transparent = false;
        io.Layer = null;
        renderInner(bctx, io);

        // 2) 降采样到 cols×rows 再读像素（tiny 位图采样，逐像素读全帧会拖垮帧率）
        using var smooth = new SKPaint { IsAntialias = true };
        _smallSurf!.Canvas.Clear();
        // JS drawImage(buf, 0, 0, cols, rows)：整帧缩放进 cols×rows；点重载是原尺寸，只会取到画面左上角
        using (var full = SKImage.FromPixels(_buf.PeekPixels()!.Info, _buf.PeekPixels()!.GetPixels(), _buf.PeekPixels()!.RowBytes))
            _smallSurf.Canvas.DrawImage(full, new SKRect(0, 0, cols, rows),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear), smooth);
        var px = _small.PeekPixels();
        var data = px.GetPixelSpan();

        // 3) 逐格绘制：亮度→字符，颜色→单元格均色（提亮后取整）
        var n = Ramp.Length;
        var lastFill = "";
        using var fp = new SKPaint { IsAntialias = true };
        for (var r = 0; r < rows; r++)
        {
            var y = (r + 0.5f) * cellH;
            for (var c = 0; c < cols; c++)
            {
                var i = (r * cols + c) * 4;
                var rr = data[i + 2]; // BGRA8888 内存序 B,G,R,A：data[i]=B、data[i+2]=R
                var gg = data[i + 1];
                var bb = data[i];
                var lum = 0.2126 * rr + 0.7152 * gg + 0.0722 * bb;
                var idx = (int)Math.Round(lum / 255 * (n - 1));
                if (idx <= 0 || lum < LumSkip) continue; // 暗部留空：背景不糊字符
                var fill = "rgb(" + Math.Min(255, (int)Math.Round(rr * Boost)) + "," +
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

    /// <summary>解析 "rgb(r,g,b)"（本文件内私有；喂给 SKPaint.Color）。</summary>
    private static SKColor ParseRgb(string s)
    {
        var parts = s[4..^1].Split(',');
        return new SKColor(byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]));
    }

    /// <summary>注册 ascii 风格（对位源文件尾部的 STYLES.ascii 段）。</summary>
    public static void Install()
    {
        var scheme = new Scheme();
        scheme.Extra!["bg"] = "#040704";
        scheme.Extra!["fg"] = "#d8ffe0";
        scheme.Extra!["sub"] = "#2e8f4a";
        scheme.Extra!["accent"] = "#33ff66";
        scheme.Extra!["accent2"] = "#d8ffe0";
        scheme.Extra!["ink"] = "#d8ffe0";
        scheme.Extra!["dim"] = "#0a1a0e";
        scheme.Extra!["ghostA"] = "#33ff66";
        scheme.Extra!["ghostB"] = "#d8ffe0";

        var fonts = new JObject
        {
            ["display"] = new JArray("mono"),
            ["serif"] = new JArray("mono"),
            ["body"] = new JArray("mono"),
            ["mono"] = new JArray("mono"),
        };
        var texture = new JObject { ["grain"] = 0.15, ["paper"] = 0, ["scan"] = 0 };

        Reg.Styles["ascii"] = new StyleDef
        {
            Key = "ascii",
            Name = "ASCII字符画",
            Hud = false,
            Schemes = new List<Scheme> { scheme },
            Extra =
            {
                ["fonts"] = fonts,
                ["texture"] = texture,
                ["ghost"] = 0,
                ["bias"] = new JObject(),
                ["decor"] = new JObject(),
                ["ascii"] = true, // Renderer 整帧先走正常管线再经 asciiFrame 转成字符画
            },
        };
        if (!JStyles.StyleOrder.Contains("ascii")) JStyles.StyleOrder.Add("ascii");
    }
}
