using Newtonsoft.Json.Linq;
using SkiaSharp;

namespace JizuraLive.Engine.Packs;

/// <summary>
/// K线·音高风格（移植自 frontend/src/engine/11p_kline.js）：音高驱动 OHLC 蜡烛图，
/// 高音涨/低音跌（A股红涨绿跌），能量当成交量，全曲视口一次锁定。
/// 独立渲染分支：由 Renderer 在 st.kline 时直调 Frame，不画歌词（歌词归前景层/字幕通道）。
/// </summary>
public static class P11Kline
{
    private const double CandleDuration = 0.3; // 一根蜡烛多少秒
    private const string UpColor = "#ff3b47";     // 涨（音高上行）
    private const string DownColor = "#00e07a";   // 跌（音高下行）
    private const string FlatColor = "#c9d3e3";   // 平盘（窗口内无有效音高）
    private const string BgColor = "#0a0e17";

    // 蜡烛数据缓存：JS WeakMap 语义（按 plan 实例弱引用缓存，预览/导出不同实例互不踩）
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Plan, CandleData> Cache = new();
    private static SKTypeface? _cursorFace; // 游标时间文本的 Consolas（渲染单线程，静态缓存即可）

    private sealed class Candle
    {
        public double Time, Open, Close, High, Low, Volume;
    }

    private static SKColor WithAlpha(SKColor c, byte a) => new SKColor(c.Red, c.Green, c.Blue, a);

    private static double MidiOf(double hz) => 69 + 12 * Math.Log2(hz / 440);

    // 蜡烛数据：音高驱动 OHLC（高音涨/低音跌），能量当成交量。
    // 稳健聚合：开=前1/3中位、收=后1/3中位、高低=90/10分位——直接取 min/max 会被
    // 八度误检/滑音毛刺撑成满格柱子。窗口内全无声帧 = 横走的"停牌"蜡烛。
    // （仅音高路径如此聚合；energy 分支无音高可聚，直接取 min/max。）
    private static CandleData? PrepareCandles(Plan plan)
    {
        var pitch = plan.Pitch is { Length: > 0 } ? plan.Pitch : null;
        var energy = plan.Energy is { Length: > 0 } ? plan.Energy : null;
        var rate = pitch != null ? plan.PitchRate : plan.EnergyRate;
        if (rate <= 0) return null;
        var nFrames = pitch != null ? pitch.Length : energy!.Length;
        var win = Math.Max(1, (int)Math.Round(CandleDuration * rate));
        var numCandles = Math.Max(1, (int)Math.Ceiling(nFrames / (double)win));

        float EAt(int f) => energy != null && f < energy.Length ? energy[f] : 0;

        // 全曲第一个有效音高：开头停牌蜡烛的锚定价（避免假价格台阶）
        var firstMidi = 60.0;
        if (pitch != null)
            for (var f = 0; f < pitch.Length; f++)
                if (pitch[f] > 0)
                {
                    firstMidi = MidiOf(pitch[f]);
                    break;
                }

        var candles = new List<Candle>();
        double? lastClose = null;
        for (var i = 0; i < numCandles; i++)
        {
            var s = i * win;
            var e = Math.Min(nFrames, s + win);
            var volume = 0.0;
            for (var f = s; f < e; f++) volume += EAt(f);
            volume /= e - s;

            double? open = null, close = null;
            var high = double.NegativeInfinity;
            var low = double.PositiveInfinity;
            if (pitch != null)
            {
                var vals = new List<double>();
                for (var f = s; f < e; f++)
                    if (pitch[f] > 0) vals.Add(MidiOf(pitch[f]));
                if (vals.Count > 0)
                {
                    var sorted = vals.OrderBy(x => x).ToList();
                    double Q(double p) => sorted[Math.Min(sorted.Count - 1, (int)Math.Floor(sorted.Count * p))];
                    var k = Math.Max(1, (int)Math.Ceiling(vals.Count / 3.0));
                    var head = vals.Take(k).OrderBy(x => x).ToList();
                    var tail = vals.TakeLast(k).OrderBy(x => x).ToList();
                    open = head[head.Count / 2];
                    close = tail[tail.Count / 2];
                    high = Q(0.9);
                    low = Q(0.1);
                }
            }
            else
            {
                open = EAt(s) * 100;
                close = EAt(e - 1) * 100;
                for (var f = s; f < e; f++)
                {
                    var m = EAt(f) * 100;
                    if (m > high) high = m;
                    if (m < low) low = m;
                }
            }

            if (open == null)
            {
                // 停牌：横盘在最后成交价
                var px = lastClose ?? firstMidi;
                candles.Add(new Candle { Time = i * CandleDuration, Open = px, Close = px, High = px, Low = px, Volume = volume });
                continue;
            }
            lastClose = close;
            candles.Add(new Candle { Time = i * CandleDuration, Open = open.Value, Close = close.Value, High = high, Low = low, Volume = volume });
        }

        var maxVol = 0.001;
        foreach (var c in candles)
            if (c.Volume > maxVol) maxVol = c.Volume;

        // 全曲固定视口：一次算好锁定范围（8% 余量，最小范围兜底），全程不缩放不跟踪
        var glo = double.PositiveInfinity;
        var ghi = double.NegativeInfinity;
        foreach (var c in candles)
        {
            if (c.Low < glo) glo = c.Low;
            if (c.High > ghi) ghi = c.High;
        }
        if (!double.IsFinite(glo) || !double.IsFinite(ghi))
        {
            glo = 0;
            ghi = 100;
        }
        var minRange = pitch != null ? 16 : 30;
        var pad = Math.Max((ghi - glo) * 0.08, minRange * 0.15);
        glo -= pad;
        ghi += pad;
        if (ghi - glo < minRange)
        {
            var mid = (ghi + glo) / 2;
            glo = mid - minRange / 2;
            ghi = mid + minRange / 2;
        }

        double?[] Ma(int n) => candles.Select((c, i) =>
        {
            if (i < n - 1) return (double?)null;
            var sum = 0.0;
            for (var j = 0; j < n; j++) sum += candles[i - j].Close;
            return (double?)sum / n;
        }).ToArray();

        return new CandleData { Candles = candles.ToArray(), Ma5 = Ma(5), Ma10 = Ma(10), MaxVol = maxVol, Lo = glo, Hi = ghi };
    }

    private sealed class CandleData
    {
        public Candle[] Candles = System.Array.Empty<Candle>();
        public double?[] Ma5 = System.Array.Empty<double?>();
        public double?[] Ma10 = System.Array.Empty<double?>();
        public double MaxVol = 0.001;
        public double Lo, Hi;
    }

    /// <summary>K线渲染主入口（对位 J.KLINE_RENDERER）。</summary>
    public static void Frame(SKCanvas ctx, Plan plan, double t, RenderOpts opt, int cw, int ch)
    {
        var W = plan.W;
        var H = plan.H;
        var scale = opt.Scale != 0 ? opt.Scale : 1;

        using var _ = new SKAutoCanvasRestore(ctx);
        // 画布清底 + scale 变换：导出/预览画布 ≠ plan 设计尺寸时同样铺满 100% 画面
        ctx.SetMatrix(SKMatrix.Identity);
        ctx.Clear(new SKColor(0, 0, 0, 0));
        ctx.Scale((float)scale, (float)scale);
        if (!opt.Transparent)
            using (var bg = new SKPaint { Color = SKColor.Parse(BgColor) })
                ctx.DrawRect(0, 0, W, H, bg);

        if (plan.Energy == null && plan.Pitch == null)
        {
            using var fp = new SKPaint { Color = SKColor.Parse("#00e676"), IsAntialias = true };
            using var font = new SKFont(SKTypeface.FromFamilyName("Consolas"), Math.Max(8, (int)Math.Round(H * 0.037)));
            ctx.DrawText("NO AUDIO DATA · 载入歌曲后生成K线", W / 2f, H / 2f, SKTextAlign.Center, font, fp);
            return;
        }

        var data = Cache.GetValue(plan, _ => PrepareCandles(plan)!);
        if (data.Candles.Length == 0) return;
        var candles = data.Candles;
        var ma5 = data.Ma5;
        var ma10 = data.Ma10;
        var maxVol = data.MaxVol;

        var beatPulse = 0.0;
        if (plan.Beats.Length > 0)
            for (var i = plan.Beats.Length - 1; i >= 0; i--)
            {
                var b = plan.Beats[i];
                if (b <= t && t - b < 0.2)
                {
                    beatPulse = 1.0 - (t - b) / 0.2;
                    break;
                }
                if (b < t - 0.2) break;
            }

        var timeH = (int)Math.Round(H * 0.034);  // 底部时间轴
        var volH = (int)Math.Round(H * 0.1);     // 成交量带
        var gap = (int)Math.Round(H * 0.008);
        var chartX = (int)Math.Round(H * 0.014);
        var chartY = (int)Math.Round(H * 0.016);
        var chartW = W - chartX * 2;
        var chartH = H - chartY - volH - gap - timeH - gap;
        var volY = chartY + chartH + gap;

        const int visibleCandles = 60;
        var candleWidth = chartW / (double)visibleCandles;
        var candleSpacing = candleWidth * 0.18;
        var candleBodyWidth = Math.Max(1, candleWidth - candleSpacing);

        var currentIdxFloat = t / CandleDuration;
        // 数据尾停靠：蜡烛唱完后视角钉在最后几根上，不再继续左移扫过空白
        var endCap = Math.Max(0, candles.Length - visibleCandles * 0.92);
        var targetScrollIdx = Math.Min(currentIdxFloat - visibleCandles * 0.7, endCap);
        var startIdx = Math.Max(0, targetScrollIdx);
        var startIdxInt = (int)Math.Floor(startIdx); // 索引整数化（JS 数组可吞浮点索引，C# 不行）
        var offsetX = (startIdx - startIdxInt) * candleWidth;

        // 视口固定：全曲范围一次算好锁死，蜡烛只横向滚动、纵向永不移动
        var viewLo = data.Lo;
        var viewHi = data.Hi;
        var padY = chartH * 0.05;
        double GetY(double val) => chartY + padY + (1 - (val - viewLo) / (viewHi - viewLo)) * (chartH - padY * 2);

        using var grid = new SKPaint { StrokeWidth = 1 };
        grid.Color = new SKColor(255, 255, 255, (byte)Math.Clamp(0.09 * 255 + beatPulse * 0.16 * 255, 0, 255));
        for (var i = 0; i <= 10; i++)
        {
            var y = chartY + i / 10.0 * chartH;
            ctx.DrawLine(chartX, (float)y, chartX + chartW, (float)y, grid);
        }
        for (var i = 0; i <= visibleCandles + 1; i++)
        {
            var x = chartX + i * candleWidth - offsetX;
            if (x >= chartX && x <= chartX + chartW)
            {
                ctx.DrawLine((float)x, chartY, (float)x, chartY + chartH, grid);
                ctx.DrawLine((float)x, volY, (float)x, volY + volH, grid);
            }
        }

        ctx.Save();
        ctx.ClipRect(SKRect.Create(chartX, chartY, chartW, chartH));

        var ma5Points = new List<SKPoint>();
        var ma10Points = new List<SKPoint>();
        SKColor CandleColor(Candle c) =>
            c.Close > c.Open ? SKColor.Parse(UpColor)
            : c.Close < c.Open ? SKColor.Parse(DownColor)
            : SKColor.Parse(FlatColor);

        for (var i = 0; i <= visibleCandles + 1; i++)
        {
            var idx = startIdxInt + i;
            if (idx >= candles.Length) break;
            var c = candles[idx];
            var cx = (float)(chartX + i * candleWidth - offsetX);
            var centerX = cx + (float)candleWidth / 2;
            var color = CandleColor(c);
            var isBull = c.Close > c.Open;
            var isFlat = c.Close == c.Open;

            // 影线
            using (var wick = new SKPaint
            {
                Color = color,
                StrokeWidth = (float)Math.Max(1, candleWidth * 0.08),
                IsAntialias = true,
            })
                ctx.DrawLine(centerX, (float)GetY(c.High), centerX, (float)GetY(c.Low), wick);

            // 实体：阳线空心、阴线实心（A股样式）
            var bodyTop = (float)GetY(Math.Max(c.Open, c.Close));
            var bodyH = (float)Math.Max(1, Math.Abs(GetY(c.Open) - GetY(c.Close)));
            var bodyRect = SKRect.Create(cx + (float)candleSpacing / 2, bodyTop, (float)candleBodyWidth, bodyH);
            if (isFlat)
            {
                using var fp = new SKPaint { Color = WithAlpha(color, 140) };
                ctx.DrawRect(bodyRect, fp);
            }
            else if (isBull)
            {
                using (var fp = new SKPaint { Color = SKColor.Parse(BgColor) }) ctx.DrawRect(bodyRect, fp);
                using var sp = new SKPaint
                {
                    Color = color,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = (float)Math.Max(1, candleWidth * 0.09),
                };
                ctx.DrawRect(bodyRect, sp);
            }
            else
            {
                using var fp = new SKPaint { Color = color };
                ctx.DrawRect(bodyRect, fp);
            }

            if (ma5[idx] != null) ma5Points.Add(new SKPoint(centerX, (float)GetY(ma5[idx]!.Value)));
            if (ma10[idx] != null) ma10Points.Add(new SKPoint(centerX, (float)GetY(ma10[idx]!.Value)));
        }

        void DrawMA(List<SKPoint> points, string color)
        {
            if (points.Count < 2) return;
            using var p = new SKPaint
            {
                Color = SKColor.Parse(color),
                StrokeWidth = (float)Math.Max(1.5, H * 0.0016),
                IsAntialias = true,
            };
            for (var i = 1; i < points.Count; i++) ctx.DrawLine(points[i - 1], points[i], p);
        }
        DrawMA(ma5Points, "#ffd740");
        DrawMA(ma10Points, "#00e5ff");

        // 现价中心线：K线围绕它流动
        var curIdx = Math.Min(candles.Length - 1, Math.Max(0, (int)Math.Floor(currentIdxFloat)));
        var priceY = (float)GetY(candles[curIdx].Close);
        using (var dash = new SKPaint
        {
            Color = new SKColor(0, 229, 255, 128),
            StrokeWidth = (float)Math.Max(1, H * 0.0014),
            PathEffect = SKPathEffect.CreateDash(new float[] { 10, 8 }, 0),
        })
            ctx.DrawLine(chartX, priceY, chartX + chartW, priceY, dash);
        ctx.Restore();

        ctx.Save();
        ctx.ClipRect(SKRect.Create(chartX, volY, chartW, volH));
        for (var i = startIdxInt; i <= Math.Min(candles.Length - 1, startIdxInt + visibleCandles + 1); i++)
        {
            var cx = (float)(chartX + (i - startIdxInt) * candleWidth - offsetX);
            var color = CandleColor(candles[i]);
            var vH = (float)Math.Max(1, candles[i].Volume / maxVol * volH);
            using var fp = new SKPaint { Color = WithAlpha(color, 140) };
            ctx.DrawRect(cx + (float)candleSpacing / 2, volY + volH - vH, (float)candleBodyWidth, vH, fp);
        }
        ctx.Restore();

        // 时间游标：尾段视角停靠后游标继续向右滑到数据尽头，贴着最后一根蜡烛停住
        var cursorX = (float)Math.Min(chartX + chartW - candleWidth * 0.5,
            chartX + (currentIdxFloat - startIdx) * candleWidth);
        if (cursorX >= chartX && cursorX <= chartX + chartW)
        {
            using var glow = new SKPaint { Color = SKColor.Parse("#00e5ff"), IsAntialias = true };
            for (var i = 1; i <= 3; i++)
            {
                glow.StrokeWidth = i * 2 + (float)(beatPulse * 4);
                glow.Color = WithAlpha(glow.Color, (byte)Math.Min(255, (0.2 / i + beatPulse * 0.2) * 255));
                ctx.DrawLine(cursorX, chartY, cursorX, volY + volH, glow);
            }
            glow.Color = WithAlpha(glow.Color, 255);
            glow.StrokeWidth = 2;
            ctx.DrawLine(cursorX, chartY, cursorX, volY + volH, glow);

            // Consolas 游标字体每帧新建会把 SKTypeface 灌进 finalizer 队列：提为缓存
            _cursorFace ??= SKTypeface.FromFamilyName("Consolas");
            using var font = new SKFont(_cursorFace, Math.Max(8, (int)Math.Round(timeH * 0.52)));
            using var fp = new SKPaint { Color = SKColor.Parse("#00e5ff"), IsAntialias = true };
            ctx.DrawText(J.FmtTime(t),
                (float)Math.Min(chartX + chartW - 40, Math.Max(chartX + 40, cursorX)), volY + volH + gap,
                SKTextAlign.Center, font, fp);
        }
    }

    /// <summary>注册 kline 风格（对位源文件尾部的 STYLES.kline 段）。</summary>
    public static void Install()
    {
        var scheme = new Scheme();
        scheme.Extra!["bg"] = "#0a0e17";
        scheme.Extra!["fg"] = "#e0e6f0";
        scheme.Extra!["sub"] = "#4a5568";
        scheme.Extra!["accent"] = "#ff3b47";
        scheme.Extra!["accent2"] = "#00e07a";
        scheme.Extra!["ink"] = "#e0e6f0";
        scheme.Extra!["dim"] = "#151c2a";
        scheme.Extra!["ghostA"] = "#ff3b47";
        scheme.Extra!["ghostB"] = "#00e07a";

        var fonts = new JObject
        {
            ["display"] = new JArray("mono"),
            ["serif"] = new JArray("mono"),
            ["body"] = new JArray("mono"),
            ["mono"] = new JArray("mono"),
        };
        var texture = new JObject { ["grain"] = 0.15, ["paper"] = 0, ["scan"] = 0.3 };

        Reg.Styles["kline"] = new StyleDef
        {
            Key = "kline",
            Name = "K线·音高",
            Hud = false,
            Schemes = new List<Scheme> { scheme },
            Extra =
            {
                ["fonts"] = fonts,
                ["texture"] = texture,
                ["ghost"] = 0,
                ["bias"] = new JObject(),
                ["decor"] = new JObject(),
                ["kline"] = true, // Renderer 走独立渲染分支：只画K线图表，不画歌词
            },
        };
        if (!JStyles.StyleOrder.Contains("kline")) JStyles.StyleOrder.Add("kline");
    }
}
