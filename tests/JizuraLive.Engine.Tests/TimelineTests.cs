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
                            Keys = { new TKey(1, 0, "SmoothStep"), new TKey(4, 0.8, "Linear"), new TKey(30, 1, "Step") },
                        },
                    },
                    Lyrics =
                    {
                        new TClip { Id = "c1", Start = 10, End = 14, Text = "第一句\n第二句" },
                        new TClip { Id = "c2", Start = 20, End = 24, Text = "下一句" },
                    },
                    Waveform = WaveformPeaks.Build(SineSamples(44100), 1),
                });

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
