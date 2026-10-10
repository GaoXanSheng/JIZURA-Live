using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using JizuraLive.Engine;
using JizuraLive.Logging;
using Newtonsoft.Json;
using SkiaSharp;

namespace JizuraLive.App.Views;

/// <summary>
/// 卡片预览的渲染卡（手法栏的 10 组卡片、切分替换面板的候选卡共用）。
/// 位图由服务写入（<see cref="Bmp"/> 复用，逐帧不再新建 BitmapSource），宿主只拿 Image 承载。
/// </summary>
internal sealed class ThumbCard
{
    public string G = "";
    public string K = "";

    /// <summary>回贴目标（Image.Source 由服务写）。</summary>
    public Image Host = null!;

    /// <summary>像素尺寸（高固定 90，宽按画幅）。</summary>
    public int W = 90, H = 90;

    public WriteableBitmap? Bmp;
    public bool Pending;     // 已排队（避免同一帧重复入队）
    public bool FirstFrame = true; // 首帧取静态代表相位（卡片不留空白），之后进动画循环
}

/// <summary>
/// 手法预览缩略图服务（对位 frontend/src/jizura/preview.ts）：
/// 一台专用 Renderer（与主预览/Spout 的整帧缓存隔离）+ PreviewPlan 缓存 + 离屏面复用 +
/// 后台单线程队列 + 动画时钟。可见卡由宿主经 <see cref="SetLive"/> 交出（展开组 + 在视口内），
/// 服务每 90ms 把缺帧的可见卡排进队列；相位取 <see cref="PreviewTime"/>（逐行对位 preview.ts:38-50），
/// 同一时刻全体卡片共享一个 now，循环动画彼此同步。
///
/// 让路规矩（性能专项，同旧 TechTab）：每张图全程持 EngineRenderGate——输出泵（Spout 镜像）与
/// 主预览都靠这把闸，播放中把本线程占闸时间压到 ~1/10 壁钟（睡眠按实测成本自适），暂停时全速补图。
/// </summary>
internal sealed class TechThumbPreview
{
    /// <summary>进程内共享（TechTab 与 CutInfo 面板共用一台渲染器与一份缓存，对位 Vue 的全局 previewR）。</summary>
    public static TechThumbPreview Shared { get; } = new();

    private const int TickMs = 90;      // 排帧节拍（对位 FRAME_MS = 90）
    private const int ThumbH = 90;      // 缩略图高（对位 canvas th = 90）

    private sealed class Job
    {
        public ThumbCard Card = null!;
        public string Sig = "";
        public double NowMs;
        public bool First;
    }

    private readonly Renderer _renderer = new();
    private readonly ConcurrentDictionary<string, Plan> _plans = new();              // sig|g|k → plan
    private readonly Dictionary<(int W, int H), SKSurface> _surfaces = new();       // 尺寸 → 离屏面（保留复用）
    private readonly ConcurrentQueue<Job> _queue = new();
    private readonly AutoResetEvent _kick = new(false);
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly Dictionary<object, ThumbCard[]> _liveByOwner = new(); // 多宿主（手法栏 / 切分替换面板）
    private ThumbCard[] _live = Array.Empty<ThumbCard>();
    private volatile string _sig = "";
    private Project _snap = new();
    private int _started;

    private TechThumbPreview()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(TickMs) };
        _timer.Tick += (_, _) => Schedule();
    }

    /// <summary>渲染签名（对齐旧 TechTab.CurrentSig）：风格/画幅/seed/抠像/语言/整列/统一感/配色/字体变则全量重渲。</summary>
    private static string CurrentSig()
    {
        var p = App.MainInstance!.State.Project;
        return string.Join("|",
            p.Style, p.Aspect, p.Seed, p.KeyBg, p.Lang, p.Typeset, p.Unify,
            p.Colors.ToString(Formatting.None),
            JsonConvert.SerializeObject(p.Fonts),
            JsonConvert.SerializeObject(p.UserFonts));
    }

    /// <summary>
    /// 交出可见卡集合（宿主在展开/筛选/滚动/切页/尺寸变化时调用）。
    /// 签名变化时在这里换快照并清空 plan/位图缓存（UI 线程 JSON 克隆，worker 只经 Job 拿引用）。
    /// </summary>
    public void SetLive(object owner, IReadOnlyList<ThumbCard> cards)
    {
        if (App.MainInstance == null) return;
        EnsureStarted();
        var sig = CurrentSig();
        _liveByOwner[owner] = cards.ToArray();
        _live = _liveByOwner.Values.SelectMany(x => x).ToArray();
        if (sig != _sig)
        {
            _sig = sig;
            _snap = JsonConvert.DeserializeObject<Project>(
                        JsonConvert.SerializeObject(App.MainInstance!.State.Project)) ?? new Project();
            _plans.Clear();
            foreach (var c in _live) c.FirstFrame = true; // 签名变了：全部重渲
        }
        foreach (var c in _live)
            if (c.Bmp == null) c.FirstFrame = true; // 新卡（或尺寸变了）先出静态代表帧
        Schedule();
    }

    /// <summary>强制全量重渲（外部改动了目标尺寸等，卡片重挂后调用）。</summary>
    public void Invalidate()
    {
        _plans.Clear();
        foreach (var c in _live) c.FirstFrame = true;
        Schedule();
    }

    private void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        var ct = _cts.Token;
        _ = Task.Run(() =>
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_queue.TryDequeue(out var job))
                {
                    try { _kick.WaitOne(500); }
                    catch (ObjectDisposedException) { return; }
                    continue;
                }
                try
                {
                    if (job.Sig != _sig) { job.Card.Pending = false; continue; } // 旧签名的任务作废（不复位会让卡片永久卡黑）
                    var t0 = Stopwatch.GetTimestamp();
                    var bmp = Render(job);
                    if (bmp == null) { job.Card.Pending = false; continue; }
                    var card = job.Card;
                    var pixels = bmp.Value.Pixels;
                    var stride = bmp.Value.Stride;
                    App.MainInstance?.Dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            card.Bmp ??= new WriteableBitmap(card.W, card.H, 96, 96, PixelFormats.Bgra32, null);
                            card.Bmp.WritePixels(new Int32Rect(0, 0, card.W, card.H), pixels, stride, 0);
                            card.Host.Source = card.Bmp;
                        }
                        finally
                        {
                            ArrayPool<byte>.Shared.Return(pixels);
                            card.Pending = false;
                        }
                    });
                    // 让路（对位旧 TechTab 的预算制）：播放中占闸 ~1/10 壁钟（睡 9×实测成本），暂停时全速补图。
                    // 下限只防自旋：旧下限 20ms 在单张 ~1ms 时把占闸压到 4%，几十张可见卡的刷新周期被拖成秒级。
                    var costMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    var playing = App.MainInstance?.Preview.IsPlaying == true;
                    Thread.Sleep(playing ? (int)Math.Clamp(costMs * 9, 2, 400) : 2);
                }
                catch (Exception ex)
                {
                    job.Card.Pending = false;
                    Log.Error($"[Thumb] 缩略图渲染失败：{job.Card.G}.{job.Card.K}", ex);
                }
            }
        }, ct);
        _timer.Start();
    }

    /// <summary>排帧：可见卡里缺图的按动画相位入队（已排队的不重复入）。</summary>
    private void Schedule()
    {
        if (_started == 0) return;
        var live = _live;
        if (live.Length == 0) return;
        if (App.MainInstance == null) return;
        var now = _clock.Elapsed.TotalMilliseconds;
        foreach (var card in live)
        {
            if (card.Pending) continue;
            card.Pending = true;
            _queue.Enqueue(new Job { Card = card, Sig = _sig, NowMs = now, First = card.FirstFrame });
        }
        _kick.Set();
    }

    /// <summary>
    /// 单张出图（全程持 EngineRenderGate）：PreviewPlan（缓存）→ 离屏面 Frame → 拷进池化缓冲。
    /// 失败出暗底占位（对位 Vue 的 catch 底色 #131316）。
    /// </summary>
    private (byte[] Pixels, int Stride)? Render(Job job)
    {
        var card = job.Card;
        const int th = ThumbH;
        lock (EngineRenderGate.Lock)
        {
            var key = $"{job.Sig}|{card.G}|{card.K}";
            if (!_plans.TryGetValue(key, out var plan))
            {
                try
                {
                    plan = Planner.PreviewPlan(_snap, card.G, card.K);
                }
                catch (Exception ex)
                {
                    Log.Error($"[Thumb] PreviewPlan 失败：{card.G}.{card.K}", ex);
                    return null;
                }
                _plans[key] = plan;
                if (_plans.Count > 900) _plans.Clear(); // 八百卡保险丝：容量兜底清空，重建只是时间
            }
            var tw = Math.Max(80, (int)Math.Round(th * (double)plan.W / plan.H));
            if (card.W != tw || card.H != th)
            {
                card.W = tw;
                card.H = th;
                card.Bmp = null; // 尺寸变了：位图重建（回贴线程里建）
            }
            if (!_surfaces.TryGetValue((tw, th), out var surf))
            {
                surf = SKSurface.Create(new SKImageInfo(tw, th, SKColorType.Bgra8888, SKAlphaType.Premul))
                       ?? throw new OutOfMemoryException("SKSurface.Create");
                _surfaces[(tw, th)] = surf;
            }
            try
            {
                _renderer.Frame(surf.Canvas, plan, job.First ? StaticTime(card.G, plan) : PreviewTime(card.G, plan, job.NowMs),
                    new RenderOpts
                    {
                        Scale = (double)tw / plan.W,
                        Fast = true,
                        NoHud = true,
                        NoGhost = card.G != "fx", // Vue preview 的 opts 原样
                    });
                if (job.First) card.FirstFrame = false;
            }
            catch (Exception ex)
            {
                Log.Warn($"[Thumb] Frame 失败（暗底占位）：{card.G}.{card.K} · {ex.Message}");
                surf.Canvas.Clear(new SKColor(19, 19, 22)); // #131316
            }

            var px = surf.PeekPixels();
            if (px == null) return null;
            var len = px.RowBytes * px.Height;
            var buf = ArrayPool<byte>.Shared.Rent(len);
            Marshal.Copy(px.GetPixels(), buf, 0, len);
            return (buf, px.RowBytes);
        }
    }

    /// <summary>静态代表帧（旧 TechTab.PreviewTime 的单相取法）：enter 进场中段、exit 出场中段、
    /// trans 混合中点、fx 事件峰值后一刻，其余取切分中段。</summary>
    private static double StaticTime(string g, Plan plan)
    {
        if (plan.Cuts.Count == 0) return 0;
        var c = plan.Cuts[^1];
        switch (g)
        {
            case "enter": return c.Start + Math.Max(0.3, c.InDur) * 0.5;
            case "exit":
            {
                var od = Math.Max(0.3, c.OutDur > 0 ? c.OutDur : 0.5);
                return c.End - od * 0.5;
            }
            case "trans": return c.Start + Math.Max(0.05, c.TransDur > 0 ? c.TransDur : 0.35) * 0.5;
            case "fx": return 0.1; // PreviewPlan 把事件打在 t=0.04（衰减窗口内）
            default: return c.Start + Math.Max(1.6, c.Dur * 0.96) * 0.5;
        }
    }

    /// <summary>动画相位（对位 preview.ts:38-50）：按组取循环窗口，now 毫秒全体共享。</summary>
    public static double PreviewTime(string g, Plan plan, double nowMs)
    {
        if (plan.Cuts.Count == 0) return 0;
        var c = plan.Cuts[^1];
        var u = nowMs / 1000.0;
        switch (g)
        {
            case "enter": return c.Start + (u % 1.5) / 1.5 * Math.Max(0.3, c.InDur);
            case "exit":
            {
                var od = Math.Max(0.3, c.OutDur > 0 ? c.OutDur : 0.5);
                return c.End - od + (u % 1.5) / 1.5 * od;
            }
            case "trans": return c.Start + (u % 1.7) / 1.7 * (c.TransDur > 0 ? c.TransDur : 0.35);
            case "fx": return u % 1.05;
            default:
            {
                var span = Math.Max(1.6, c.Dur * 0.96);
                return c.Start + (u % span) / span * (c.Dur * 0.96);
            }
        }
    }

    /// <summary>卡片的像素尺寸（高 90，宽按 plan 画幅；拿不到 plan 时按 16:9 兜底）。</summary>
    public static (int W, int H) SizeOf(Plan? plan)
    {
        var th = ThumbH;
        if (plan == null) return (th * 16 / 9, th);
        return (Math.Max(80, (int)Math.Round(th * (double)plan.W / plan.H)), th);
    }
}
