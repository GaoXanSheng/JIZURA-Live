using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using JizuraLive.App.PvStage;
using JizuraLive.App.PvStage.Timeline;
using JizuraLive.Engine;
using JizuraLive.Pv;
using NAudio.Wave;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 时间轴纯逻辑（W1-TL）：网格吸附（含 Alt 关吸附）、时间/值 ↔ 像素往返、Ctrl+滚轮锚点缩放、
/// key 命中与同刻替换；外加 TimelineView 的 UI 冒烟（构造 + SetSnapshot）。
/// 全是数值断言，不需要 GPU / 窗口。
/// W2-TL 补：五档插值（Bezier 句柄归一化/clamp、采样单调性；Spring 端点与超调）。
/// </summary>
public class TimelineTests
{
    private static readonly List<TKey> TwoKeys =
    [
        new TKey(1, 0.2, "Linear"),
        new TKey(2, 0.8, "Linear"),
    ];

    [Theory]
    [InlineData(5, 1.0)]     // 整曲一屏：1s 才 5px，用最粗的 1s
    [InlineData(10, 1.0)]
    [InlineData(20, 0.5)]    // 0.5s 刚好占 10px
    [InlineData(99, 0.5)]
    [InlineData(100, 0.1)]   // 0.1s 刚好占 10px
    [InlineData(299, 0.1)]
    [InlineData(300, 1.0 / 30)] // 一帧刚好占 10px
    [InlineData(900, 1.0 / 30)]
    public void GridStepFollowsZoom(double pixelsPerSecond, double expected) =>
        Assert.Equal(expected, TimelineMath.GridStep(pixelsPerSecond, 30), 9);

    [Fact]
    public void GridStepUsesFrameRateForFinestStep()
    {
        Assert.Equal(1.0 / 60, TimelineMath.GridStep(600, 60), 9);
        Assert.Equal(1.0, TimelineMath.GridStep(600, 0), 9); // fps 非法时退回 1s，不做除零
    }

    [Fact]
    public void SnapRoundsToGridAndAltDisablesIt()
    {
        Assert.Equal(1.5, TimelineMath.Snap(1.3, 0.5, true), 9);
        Assert.Equal(1.0, TimelineMath.Snap(1.24, 0.5, true), 9);
        Assert.Equal(0.1, TimelineMath.Snap(0.0999, 0.1, true), 9);
        Assert.Equal(1.3, TimelineMath.Snap(1.3, 0.5, false), 9);      // Alt 按住 = 关吸附
        Assert.Equal(1.3, TimelineMath.Snap(1.3, 0, true), 9);         // 步长非法时原样返回
    }

    [Fact]
    public void TimeAndValuePixelMappingRoundTrips()
    {
        Assert.Equal(200, TimelineMath.TimeToX(3, 1, 100), 9);
        Assert.Equal(3, TimelineMath.XToTime(200, 1, 100), 9);
        Assert.Equal(1, TimelineMath.XToTime(0, 1, 100), 9);

        const double top = 10, height = 20;
        Assert.Equal(30, TimelineMath.ValueToY(0, 0, 1, top, height), 9);   // Min 在底
        Assert.Equal(10, TimelineMath.ValueToY(1, 0, 1, top, height), 9);   // Max 在顶
        Assert.Equal(20, TimelineMath.ValueToY(0.5, 0, 1, top, height), 9);
        Assert.Equal(0.25, TimelineMath.YToValue(TimelineMath.ValueToY(0.25, 0, 1, top, height), 0, 1, top, height), 9);
        Assert.Equal(0.5, TimelineMath.YToValue(0, 0.5, 0.5, top, height), 9); // 值域退化：居中
    }

    [Fact]
    public void ZoomKeepsAnchorTimeUnderPointer()
    {
        const double pps = 100, viewStart = 4, anchorX = 260;
        double anchorTime = TimelineMath.XToTime(anchorX, viewStart, pps);   // 6.6s

        double zoomed = TimelineMath.ClampPixelsPerSecond(pps * 1.25, 1000, 180);
        double newStart = TimelineMath.ZoomViewStart(anchorTime, anchorX, zoomed);
        Assert.Equal(anchorX, TimelineMath.TimeToX(anchorTime, newStart, zoomed), 6);

        double outStart = TimelineMath.ZoomViewStart(anchorTime, anchorX, pps * 0.8);
        Assert.Equal(anchorX, TimelineMath.TimeToX(anchorTime, outStart, pps * 0.8), 6);
    }

    [Fact]
    public void ZoomRangeIsWholeSongToOneScreenToTwoSeconds()
    {
        Assert.Equal(500, TimelineMath.ClampPixelsPerSecond(1e6, 1000, 180), 9);        // 上界：2 秒一屏
        Assert.Equal(1000.0 / 180, TimelineMath.ClampPixelsPerSecond(0.001, 1000, 180), 9); // 下界：整曲一屏
        Assert.Equal(250, TimelineMath.ClampPixelsPerSecond(250, 1000, 180), 9);
    }

    [Fact]
    public void ViewStartIsClampedInsideTheSong()
    {
        Assert.Equal(0, TimelineMath.ClampViewStart(-5, 100, 500, 60), 9);
        Assert.Equal(55, TimelineMath.ClampViewStart(999, 100, 500, 60), 9);  // 60 − 5s 视宽
        Assert.Equal(12, TimelineMath.ClampViewStart(12, 100, 500, 60), 9);
    }

    [Fact]
    public void HitKeyUsesFivePixelRadius()
    {
        Assert.Equal(0, TimelineMath.HitKey(TwoKeys, 100, 0, 100));   // t=1
        Assert.Equal(0, TimelineMath.HitKey(TwoKeys, 104.9, 0, 100)); // 半径内
        Assert.Equal(-1, TimelineMath.HitKey(TwoKeys, 106, 0, 100));  // 半径外
        Assert.Equal(1, TimelineMath.HitKey(TwoKeys, 200, 0, 100));   // t=2
        Assert.Equal(-1, TimelineMath.HitKey([], 100, 0, 100));       // 空轨
        Assert.Equal(-1, TimelineMath.HitKey(TwoKeys, 100, 0, 0));    // 比例非法
    }

    [Fact]
    public void SameTickLookupDrivesReplacement()
    {
        var keys = new List<TKey>
        {
            new(1, 0.2, "Linear"),
            new(1 + 5e-7, 0.9, "Linear"),  // 同一 tick（1µs 窗口内）
            new(3, 0.5, "Linear"),
        };

        Assert.Equal(0, TimelineMath.IndexAtTime(keys, 1));            // 同刻：已有 key，打帧 = 替换
        Assert.Equal(0, TimelineMath.IndexAtTime(keys, 1 + 5e-7));     // 同一 tick 上的时刻都命中它
        Assert.Equal(-1, TimelineMath.IndexAtTime(keys, 1.5));         // 新时刻才新增
        Assert.InRange(TimelineMath.HitKey(keys, 100, 0, 100), 0, 1);  // 同刻命中落在该 tick 的 key 上
    }

    [Fact]
    public void EvaluateFollowsLeftKeyInterp()
    {
        Assert.Equal(0.25, TimelineMath.Evaluate([new(0, 0, "Linear"), new(1, 1, "Linear")], 0.25, -1), 9);
        Assert.Equal(0, TimelineMath.Evaluate([new(0, 0, "Step"), new(1, 1, "Step")], 0.999, -1), 9);
        Assert.Equal(0.5, TimelineMath.Evaluate([new(0, 0, "SmoothStep"), new(1, 1, "SmoothStep")], 0.5, -1), 9);
        Assert.Equal(0, TimelineMath.Evaluate([new(0, 0, "Linear"), new(1, 1, "Linear")], -3, -1), 9);  // 端点钳制
        Assert.Equal(1, TimelineMath.Evaluate([new(0, 0, "Linear"), new(1, 1, "Linear")], 9, -1), 9);
        Assert.Equal(0.5, TimelineMath.Evaluate([], 2, 0.5), 9);                                        // 空轨取 fallback
    }

    [Fact]
    public void SortKeepsKeysByTime()
    {
        var keys = new List<TKey> { new(3, 0, "Linear"), new(1, 0, "Linear"), new(2, 0, "Linear") };
        TimelineMath.Sort(keys);
        Assert.Equal(new[] { 1.0, 2.0, 3.0 }, keys.Select(k => k.TimeSeconds));
    }

    [Fact]
    public void InterpMenuHasFiveGears()
    {
        Assert.Equal(new[] { "Step", "Linear", "SmoothStep", "Bezier", "Spring" }, TimelineMath.Interps);
    }

    /// <summary>句柄 (1/3, 2/3) 时 x 参数化退化为恒等，值 = 3u²−2u³；默认句柄下逐点非降。</summary>
    [Fact]
    public void BezierSamplingFollowsHandlesAndStaysMonotonic()
    {
        var exact = new List<TKey>
        {
            new(0, 0, "Bezier", 1.0 / 3, 0, 1.0 / 3, 0),
            new(1, 1, "Bezier", 1.0 / 3, 0, 1.0 / 3, 0),
        };
        Assert.Equal(0, TimelineMath.Evaluate(exact, 0, -1), 9);
        Assert.Equal(1, TimelineMath.Evaluate(exact, 1, -1), 9);
        Assert.Equal(0.15625, TimelineMath.Evaluate(exact, 0.25, -1), 5);   // 3u²−2u³ 在 u=1/4（二分容差 1e-6）

        var defaults = new List<TKey> { new(0, 0, "Bezier"), new(1, 1, "Bezier") };
        double prev = double.NegativeInfinity;
        for (int i = 0; i <= 200; i++)
        {
            double v = TimelineMath.Evaluate(defaults, i / 200.0, -1);
            Assert.True(v >= prev, $"Bezier 在 t={i / 200.0} 处回退：{prev} → {v}");
            prev = v;
        }
        Assert.True(TimelineMath.Evaluate(defaults, 0.25, -1) < 0.25, "Bezier 默认句柄应比 Linear 缓起步");

        // 出柄 Y 拉满 = 起步被抬起来（同一 u 处值更大）
        var pulled = new List<TKey> { new(0, 0, "Bezier", 0.333, 1, 0.333, 0), new(1, 1, "Bezier") };
        Assert.True(TimelineMath.Evaluate(pulled, 0.5, -1) > TimelineMath.Evaluate(defaults, 0.5, -1));
    }

    /// <summary>Spring 端点精确取 V0/V1；中间按 q(u)=e^(−5u)cos(12u) 归一化，超调不钳回。</summary>
    [Fact]
    public void SpringEndpointsAreExactAndOvershootIsNotClamped()
    {
        var keys = new List<TKey> { new(0, 0, "Spring"), new(1, 1, "Spring") };
        Assert.Equal(0, TimelineMath.Evaluate(keys, 0, -1), 12);
        Assert.Equal(1, TimelineMath.Evaluate(keys, 1, -1), 12);
        Assert.Equal(0, TimelineMath.Evaluate(keys, -3, -1), 12);   // 端点钳制
        Assert.Equal(1, TimelineMath.Evaluate(keys, 9, -1), 12);

        Assert.Equal(0.9265, TimelineMath.Evaluate(keys, 0.5, -1), 3);  // 与 SmoothStep 的 0.5 明显不同

        double peak = double.NegativeInfinity;
        for (int i = 0; i <= 1000; i++) peak = Math.Max(peak, TimelineMath.Evaluate(keys, i / 1000.0, -1));
        Assert.True(peak > 1.25, $"Spring 超调不足：{peak}");   // 约 1.30，Evaluate 不把超调钳回 [V0,V1]
    }

    [Fact]
    public void BezierHandlesNormalizeAndClampX()
    {
        var a = new TKey(1, 0, "Bezier", 0.25, 0.5, 1, 0);
        var b = new TKey(3, 4, "Bezier", 0.333, 0, 0.5, 0);

        var p1 = TimelineMath.OutHandlePoint(a, b);
        Assert.Equal(1.5, p1.Time, 9);    // 1 + 0.25·2
        Assert.Equal(2.0, p1.Value, 9);   // 0 + 0.5·4
        var p2 = TimelineMath.InHandlePoint(a, b);
        Assert.Equal(2.0, p2.Time, 9);    // 3 − 0.5·2
        Assert.Equal(4.0, p2.Value, 9);

        // 像素点 → 归一化往返
        Assert.Equal((0.25, 0.5), TimelineMath.OutHandleAt(p1.Time, p1.Value, a, b, a.OutY));
        Assert.Equal((0.5, 0.0), TimelineMath.InHandleAt(p2.Time, p2.Value, a, b, b.InY));

        // X 恒 clamp 0..1：拖到段外也落在端点上
        Assert.Equal(1, TimelineMath.OutHandleAt(9, 2, a, b, a.OutY).X, 9);
        Assert.Equal(0, TimelineMath.OutHandleAt(-9, 2, a, b, a.OutY).X, 9);
        Assert.Equal(0, TimelineMath.InHandleAt(9, 2, a, b, b.InY).X, 9);
        Assert.Equal(1, TimelineMath.InHandleAt(-9, 2, a, b, b.InY).X, 9);
        Assert.Equal(3, TimelineMath.InHandleAt(9, -8, a, b, b.InY).Y, 9);  // 1 − (−8−0)/4

        // Δv = 0：Y 不影响形状，保持原值而不是算出 NaN/0
        var flat = new TKey(0, 5, "Bezier", 0.5, 0.7, 0.5, 0);
        var flatB = new TKey(1, 5, "Bezier");
        Assert.Equal((0.5, 0.7), TimelineMath.OutHandleAt(0.5, 9, flat, flatB, flat.OutY));
        Assert.Equal((0.5, flatB.InY), TimelineMath.InHandleAt(0.5, 9, flat, flatB, flatB.InY));
    }

    [Fact]
    public void InterpEditCarriesHandlesThroughTEdit()
    {
        Assert.True(ParamAddress.TryParse("camera.scale", out var address));
        var before = new List<TKey> { new(1, 0.2, "Linear") };
        var after = new List<TKey> { before[0] with { Interp = "Bezier" } };
        var edit = new TEdit { Address = address, Before = before, After = after, Reason = "interp" };

        Assert.Equal("Linear", edit.Before[0].Interp);
        Assert.Equal("Bezier", edit.After[0].Interp);
        Assert.Equal(0.333, edit.After[0].OutX, 9);   // 默认句柄 (1/3, 0)
        Assert.Equal(0.333, edit.After[0].InX, 9);
        Assert.NotEqual(edit.Before[0], edit.After[0]);  // record 相等性把句柄算在内
    }

    // ---- 歌词轨 Clip 编辑（W4-TL：半开区间 [start,end)、不重叠、允许空档）----

    private static List<TClip> Lyrics() =>
    [
        new TClip { Start = 1, End = 3, Text = "第一句" },
        new TClip { Start = 5, End = 8, Text = "第二句" },
    ];

    [Fact]
    public void ClipHitPrefersEdgesAndLeavesTheGapEmpty()
    {
        var clips = Lyrics();

        Assert.Equal(new ClipHit(0, ClipEdge.Start), TimelineMath.HitClip(clips, 1.05, 0.1));
        Assert.Equal(new ClipHit(0, ClipEdge.End), TimelineMath.HitClip(clips, 2.95, 0.1));
        Assert.Equal(new ClipHit(0, ClipEdge.None), TimelineMath.HitClip(clips, 2, 0.2));   // 条体
        Assert.Equal(new ClipHit(1, ClipEdge.None), TimelineMath.HitClip(clips, 7.5, 0.1)); // 条体：半开右端点不含在内
        Assert.Equal(new ClipHit(1, ClipEdge.End), TimelineMath.HitClip(clips, 8, 0));       // 8 是它的右端点（开），命中尾巴
        Assert.Null(TimelineMath.HitClip(clips, 4, 0.1));                                    // 空档
        Assert.Null(TimelineMath.HitClip(clips, 9, 0));                                      // [8,∞) 不是任何一句
        Assert.Null(TimelineMath.HitClip([], 1, 0.1));
    }

    [Fact]
    public void DragClipMovesWholeClipKeepingDuration()
    {
        var origin = new ClipRange(1, 3);

        var moved = TimelineMath.DragClip(origin, ClipEdge.None, 0.5, 0.1, false);
        Assert.Equal(1.5, moved.Start, 9);
        Assert.Equal(3.5, moved.End, 9);

        var snapped = TimelineMath.DragClip(origin, ClipEdge.None, 0.8, 0.5, true);   // 吸附 0.5 网格
        Assert.Equal(2, snapped.Start, 9);
        Assert.Equal(4, snapped.End, 9);
        Assert.Equal(2, snapped.End - snapped.Start, 9);                              // 时长不变

        var unsnapped = TimelineMath.DragClip(origin, ClipEdge.None, 0.8, 0, true);   // 步长非法 = 不吸附
        Assert.Equal(1.8, unsnapped.Start, 9);

        var clamped = TimelineMath.DragClip(origin, ClipEdge.None, -9, 0.5, false);
        Assert.Equal(0, clamped.Start, 9);                                            // 夹到 0
        Assert.Equal(2, clamped.End, 9);
    }

    [Fact]
    public void DragClipEdgeMovesOnlyThatEdge()
    {
        var origin = new ClipRange(1, 3);

        var head = TimelineMath.DragClip(origin, ClipEdge.Start, -0.5, 0.1, false);
        Assert.Equal(0.5, head.Start, 9);
        Assert.Equal(3, head.End, 9);

        var tail = TimelineMath.DragClip(origin, ClipEdge.End, -0.6, 0.1, true);
        Assert.Equal(1, tail.Start, 9);
        Assert.Equal(2.4, tail.End, 9);

        // 拖尾越过起点：这里不夹时长，交给 ClipFault 拒（拖不动比悄悄改掉时长好）
        var crossed = TimelineMath.DragClip(origin, ClipEdge.End, -9, 0.1, false);
        Assert.Equal(1, crossed.Start, 9);
        Assert.Equal(0, crossed.End, 9);
    }

    [Fact]
    public void ClipFaultRejectsOverlapAndNonPositiveRange()
    {
        var clips = Lyrics();

        Assert.Contains("重叠", TimelineMath.ClipFault(clips, 1, new ClipRange(1, 3)));       // 盖住第一句
        Assert.Contains("重叠", TimelineMath.ClipFault(clips, 0, new ClipRange(2, 5.001)));   // 盖住第二句的起点
        Assert.Contains("重叠", TimelineMath.ClipFault(clips, 0, new ClipRange(7.999, 9)));   // 咬住第二句的尾巴
        Assert.Contains("太短", TimelineMath.ClipFault(clips, 1, new ClipRange(6, 6)));       // end == start
        Assert.Contains("太短", TimelineMath.ClipFault(clips, 1, new ClipRange(7, 6)));       // 反向
        Assert.Contains("太短", TimelineMath.ClipFault(clips, 0,
            TimelineMath.DragClip(new ClipRange(1, 3), ClipEdge.End, -5, 0.1, false)));       // 拖尾越到起点之前
    }

    [Fact]
    public void ClipFaultAllowsTouchingEndpointsAndEmptyGaps()
    {
        var clips = Lyrics();

        Assert.Null(TimelineMath.ClipFault(clips, 0, new ClipRange(3, 5)));       // 端点相接：半开区间不算重叠
        Assert.Null(TimelineMath.ClipFault(clips, 0, new ClipRange(3.5, 4.9)));   // 空档里
        Assert.Null(TimelineMath.ClipFault(clips, 0, new ClipRange(8, 9)));       // 末句之后
        Assert.Null(TimelineMath.ClipFault(clips, 0, new ClipRange(0.5, 1)));     // 首句之前
        Assert.Null(TimelineMath.ClipFault([new TClip { Start = 1, End = 3 }], 0, new ClipRange(1, 3))); // 只有这一句
    }

    /// <summary>拖动落点整条走一遍：进空档合法，咬住邻句被拒。</summary>
    [Fact]
    public void ClipDragIntoTheGapCommitsAndOverlapIsRefused()
    {
        var clips = Lyrics();
        var origin = new ClipRange(clips[0].Start, clips[0].End);

        var intoGap = TimelineMath.DragClip(origin, ClipEdge.None, 1.0, 0.1, false);   // [2,4)
        Assert.Null(TimelineMath.ClipFault(clips, 0, intoGap));
        Assert.Equal(4, intoGap.End, 9);

        var overlap = TimelineMath.DragClip(origin, ClipEdge.None, 3.0, 0.1, false);   // [4,6)
        Assert.Contains("重叠", TimelineMath.ClipFault(clips, 0, overlap));
    }

    // ---- 歌词条顺序契约（审查阻塞项：跨位拖动后非升序 → 查找侧整段失联）----

    /// <summary>歌词条的 Clip 序列（查找侧真正吃的那份，不是时间轴快照的 TClip）。</summary>
    private static List<LyricClip> ClipSeq() =>
    [
        new LyricClip("c0", 1000, 3000, "第一句", false),
        new LyricClip("c1", 5000, 8000, "第二句", false),
    ];

    /// <summary>
    /// 缺陷机制对照（不依赖宿主）：把第一句整条挪到第二句之后，ClipFault 放行（它只挡重叠、不挡跨位），
    /// 而查找侧是二分「最后一个 StartMs ≤ t」——序列一旦非升序，挪过去的那句与被跨过的那句
    /// 在各自区间内都命中不了（屏幕上表现为歌词整段消失）。
    /// 这条钉的是查找侧对升序的硬要求：宿主提交时必须归一化，否则这里的两条命中就是绿的。
    /// </summary>
    [Fact]
    public void OutOfOrderClipsMakeBothLinesUnfindable()
    {
        var clips = ClipSeq();

        // 跨位不被拒：区间 [9,11) 与第二句 [5,8) 不重叠
        var snapshot = new List<TClip>
        {
            new() { Id = "c0", Start = 1, End = 3, Text = "第一句" },
            new() { Id = "c1", Start = 5, End = 8, Text = "第二句" },
        };
        Assert.Null(TimelineMath.ClipFault(snapshot, 0, new ClipRange(9, 11)));

        // 未归一化的提交结果：第一句挪到 9s，序列 [9s, 5s] 非升序
        var unnormalized = new List<LyricClip>
        {
            clips[0] with { StartMs = 9000, EndMs = 11000 },
            clips[1],
        };

        Assert.Null(LrcParser.FindSample(unnormalized, 6));    // 第二句在 [5,8) 内，却被非升序挡掉
        Assert.Null(LrcParser.FindSample(unnormalized, 10));   // 挪过去的第一句同样查不到
        Assert.Null(LrcParser.FindLastBefore(unnormalized, 6)); // 「保留上一句」也一起失联
    }

    /// <summary>
    /// 回归（审查阻塞项）：提交一次跨位拖动后，交给查找侧的序列必须仍是升序。
    /// 归一化后两句各自的新位置都能命中，且新下标即新快照的下标（控件按新快照继续报编辑，不会错位）。
    /// </summary>
    [Fact]
    public void CommittingACrossOrderMoveKeepsTheSequenceAscending()
    {
        var clips = ClipSeq();

        var next = TimelineMath.WithClipRange(clips, 0, 9500, 11000);   // 第一句整条挪到第二句之后

        Assert.Equal(new[] { "第二句", "第一句" }, next.Select(c => c.Text));
        Assert.Equal(new long[] { 5000, 9500 }, next.Select(c => c.StartMs));

        Assert.Equal("第二句", LrcParser.FindSample(next, 6)!.Value.Text);    // 被跨过的那句照旧命中
        Assert.Equal("第一句", LrcParser.FindSample(next, 10)!.Value.Text);   // 挪过去的按新位置命中
        Assert.Null(LrcParser.FindSample(next, 4));                          // 空档仍然清屏

        // 身份跟着句子走，只换时刻；被跨过的那句一字未动
        Assert.Equal(new[] { "c1", "c0" }, next.Select(c => c.Id));
        Assert.Equal(clips[1], next[0]);
        Assert.Equal("第一句", next[1].Text);
        Assert.False(next[1].OutOfRange);

        // 发布出去的那份不许被就地改写（克隆 → 改 → 换引用，与信号图同一条约定）
        Assert.Equal(new long[] { 1000, 5000 }, clips.Select(c => c.StartMs));
    }

    /// <summary>
    /// 归一化是稳定排序：同 StartMs 的两句保持原相对次序。
    /// 并列区间本来就会被 <see cref="TimelineMath.ClipFault"/> 挡在提交之外，这里钉的是排序本身不许
    /// 顺手换位——非稳定排序会让"只是把别处挪了一下"的编辑悄悄改掉并列两句的先后。
    /// </summary>
    [Fact]
    public void NormalizationIsStableForEqualStarts()
    {
        var tied = new List<LyricClip>
        {
            new("t0", 5000, 6000, "甲", false),
            new("t1", 1000, 2000, "乙", false),
            new("t2", 5000, 7000, "丙", false),
        };

        var next = TimelineMath.WithClipRange(tied, 1, 1000, 2000);   // 乙原地不动

        Assert.Equal(new[] { "t1", "t0", "t2" }, next.Select(c => c.Id));
    }

    /// <summary>歌词轨事务走同一个 TEdit（Reason = "clip"）：宿主凭 Clip 分支，Before/After 不带 key。</summary>
    [Fact]
    public void ClipEditCarriesTheNewRangeThroughTEdit()
    {
        var edit = new TEdit { Reason = "clip", Clip = new TClipEdit(2, 1.5, 3.5) };

        Assert.NotNull(edit.Clip);
        Assert.Equal(2, edit.Clip!.Value.Index);
        Assert.Equal(1.5, edit.Clip!.Value.Start, 9);
        Assert.Equal(3.5, edit.Clip!.Value.End, 9);
        Assert.Empty(edit.After);
        Assert.Empty(edit.Before);
    }

    /// <summary>五档切换：每次一次 EditCommitted（Reason 仍是 "interp"），After 带新模式且句柄不丢。</summary>
    [Fact]
    public void InterpSwitchCommitsOncePerChange()
    {
        Assert.True(ParamAddress.TryParse("camera.scale", out var address));
        var edits = new List<TEdit>();
        Exception? failure = null;

        var thread = new Thread(() =>
        {
            try
            {
                var view = new TimelineView { Fps = 30 };
                view.SetSnapshot(new TSnapshot
                {
                    DurationSeconds = 10,
                    Curves =
                    {
                        new TCurve
                        {
                            Address = address,
                            Label = "镜头 · 缩放",
                            Min = 0,
                            Max = 1,
                            Keys = { new TKey(1, 0, "Linear", 0.5, 0.25, 0.2, -0.1), new TKey(2, 1, "Linear") },
                        },
                    },
                });
                view.EditCommitted += edits.Add;

                view.SelectKey(address, 1);
                view.SetInterp("Bezier");
                view.SelectKey(address, 1);
                view.SetInterp("Spring");
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA 线程未在 30 秒内结束");
        Assert.Null(failure);

        Assert.Equal(2, edits.Count);
        Assert.All(edits, edit => Assert.Equal("interp", edit.Reason));
        Assert.Equal("Bezier", edits[0].After[0].Interp);
        Assert.Equal("Linear", edits[0].Before[0].Interp);
        Assert.Equal(0.5, edits[0].After[0].OutX, 9);      // 句柄随事务往返
        Assert.Equal(0.25, edits[0].After[0].OutY, 9);
        Assert.Equal(-0.1, edits[0].After[0].InY, 9);
        Assert.Equal("Spring", edits[1].After[0].Interp);
        Assert.Equal("Bezier", edits[1].Before[0].Interp);
        Assert.Equal(0.5, edits[1].After[0].OutX, 9);
    }

    /// <summary>UI 冒烟：Control 构造需要 STA 线程，测试线程是 MTA，所以另起一根。</summary>
    [Fact]
    public void TimelineViewAcceptsSnapshot()
    {
        Assert.True(ParamAddress.TryParse("camera.scale", out var address));

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var view = new TimelineView { Fps = 30 };
                view.SetSnapshot(new TSnapshot { DurationSeconds = 180, PlayheadSeconds = 12 });

                view.SetSnapshot(new TSnapshot
                {
                    DurationSeconds = 180,
                    PlayheadSeconds = 12,
                    Curves =
                    {
                        new TCurve
                        {
                            Address = address,
                            Label = "镜头 · 缩放",
                            Min = 0.5,
                            Max = 2,
                            Keys = { new TKey(1, 1, "SmoothStep"), new TKey(4, 1.8, "Linear") },
                        },
                    },
                    Lyrics = { new TClip { Id = "c1", Start = 1, End = 3, Text = "第一句\n第二句" } },
                    Waveform = WaveformPeaks.Build(new float[8192], 1),
                });
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA 线程未在 30 秒内结束");
        Assert.Null(failure);
    }

    /// <summary>OnRender 也要跑一遍：实例化不经过渲染路径，画错只会在宿主里才炸。</summary>
    [Fact]
    public void TimelineViewRendersWithoutThrowing()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Assert.True(ParamAddress.TryParse("layer:lyrics_pv.opacity", out var address));
                var view = new TimelineView { Fps = 30 };
                view.SetSnapshot(new TSnapshot
                {
                    DurationSeconds = 90,
                    PlayheadSeconds = 12.5,
                    Curves =
                    {
                        new TCurve
                        {
                            Address = address,
                            Label = "歌词 · 不透明度",
                            Min = 0,
                            Max = 1,
                            Keys =
                            {
                                new TKey(1, 0, "SmoothStep"),
                                new TKey(4, 0.8, "Bezier", 0.5, 0.6, 0.4, -0.3),
                                new TKey(10, 0.3, "Spring"),
                                new TKey(30, 1, "Step"),
                            },
                        },
                    },
                    Lyrics =
                    {
                        new TClip { Id = "c1", Start = 10, End = 14, Text = "第一句\n第二句" },
                        new TClip { Id = "c2", Start = 20, End = 24, Text = "下一句" },
                    },
                    Waveform = WaveformPeaks.Build(SineSamples(44100), 1),
                });
                view.SelectKey(address, 4);   // 选中 Bezier 段端点：渲染要走控制柄那条分支

                view.Measure(new Size(900, 260));
                view.Arrange(new Rect(0, 0, 900, 260));
                view.UpdateLayout();
                new RenderTargetBitmap(900, 260, 96, 96, PixelFormats.Pbgra32).Render(view);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA 线程未在 30 秒内结束");
        Assert.Null(failure);
    }

    private static float[] SineSamples(int count)
    {
        var data = new float[count];
        for (int i = 0; i < count; i++) data[i] = (float)Math.Sin(i * 0.05);
        return data;
    }

    [Fact]
    public void WaveformLoaderDecodesWholeFileAndHonoursCancellation()
    {
        string path = Path.Combine(Path.GetTempPath(), "jz-timeline-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            using (var writer = new WaveFileWriter(path, new WaveFormat(44100, 16, 2)))
            {
                var data = new float[44100 * 2];
                for (int i = 0; i < 44100; i++)
                {
                    data[i * 2] = (float)Math.Sin(i * 0.05);
                    data[i * 2 + 1] = (float)Math.Sin(i * 0.05);
                }
                writer.WriteSamples(data, 0, data.Length);
            }

            var peaks = WaveformLoader.Load(path);
            Assert.Equal(2, peaks.Channels);
            Assert.Equal(44100, peaks.SampleCount);
            Assert.True(peaks.TryRead(0, 0, 0, 44100, out float min, out float max));
            Assert.True(max > 0.9f && min < -0.9f);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.Throws<OperationCanceledException>(() => WaveformLoader.Load(path, cancelled.Token));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

/// <summary>
/// 检查器段落可达性回归（审查阻塞项）：工程段承载歌词样式下拉，而那是改歌词样式的唯一入口，
/// 所以它不能挂在"有没有选中图层"上——旧实现按选中与否二选一，点过任意图层卡之后本次会话
/// 就再也回不到工程段，歌词样式下拉随之不可达。
///
/// 边界说明：与 EffectPresetPickerTests 同一条约定——不实例化 PvStageWindow（构造函数会读写真实
/// 用户配置目录，并起一个 5 秒一次的自动存盘）。所以这里钉的是 PvStageWindow.BuildInspector
/// 实际消费的那份段落计划：InspectorLayout.Sections。
/// </summary>
public class InspectorLayoutTests
{
    /// <summary>核心回归：选中图层后工程段仍必须在，否则歌词样式下拉不可达。</summary>
    [Fact]
    public void ProjectSectionSurvivesLayerSelection()
    {
        Assert.Contains(InspectorSectionKind.Project, InspectorLayout.Sections(hasLayerSelection: false));
        Assert.Contains(InspectorSectionKind.Project, InspectorLayout.Sections(hasLayerSelection: true));
    }

    /// <summary>图层段只在有选中时出现：没选中就没有"图层 · …"这一段的空壳。</summary>
    [Fact]
    public void LayerSectionOnlyAppearsWithASelection()
    {
        Assert.DoesNotContain(InspectorSectionKind.Layer, InspectorLayout.Sections(false));
        Assert.Contains(InspectorSectionKind.Layer, InspectorLayout.Sections(true));
    }

    /// <summary>顺序钉死：相机在最上，工程段在图层段之前（重建检查器时按这份顺序逐个渲染）。</summary>
    [Fact]
    public void SectionsKeepRenderOrder()
    {
        Assert.Equal(
            new[] { InspectorSectionKind.Camera, InspectorSectionKind.Project },
            InspectorLayout.Sections(false));
        Assert.Equal(
            new[] { InspectorSectionKind.Camera, InspectorSectionKind.Project, InspectorSectionKind.Layer },
            InspectorLayout.Sections(true));
    }
}
