using System.IO;
using System.Reflection;
using JizuraLive.Engine;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using JizuraLive.Render.Text;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// L4 导出引擎：参数校验、逐帧口径、BGRA→NV12 转换。
/// 真编码（Media Foundation）不进单测：渲染循环用注入的假写入器跑，编码器只走产品路径。
/// </summary>
public class PvExporterTests
{
    private static string TempPath =>
        Path.Combine(Path.GetTempPath(), "jizura-export-" + Guid.NewGuid().ToString("N") + ".mp4");

    private static PvScene SmallScene()
    {
        var scene = PvScene.CreateBlank();
        scene.Width = 320;
        scene.Height = 180;
        return scene;
    }

    /// <summary>同步进度收集器：<see cref="Progress{T}"/> 会把回调 Post 到同步上下文，回调可能晚于导出返回。</summary>
    private sealed class SyncProgress : IProgress<double>
    {
        private readonly List<double> _reports = new();
        public IReadOnlyList<double> Reports => _reports;
        public void Report(double value) => _reports.Add(value);
    }

    private static byte[] Bgra(int width, int height, byte r, byte g, byte b)
    {
        var pixels = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            pixels[i * 4] = b;
            pixels[i * 4 + 1] = g;
            pixels[i * 4 + 2] = r;
            pixels[i * 4 + 3] = 255;
        }

        return pixels;
    }

    // ---------------- 参数校验：返回可读原因，不抛、不碰输出文件 ----------------

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.5)]
    [InlineData(double.NaN)]
    public void RejectsNonPositiveDuration(double duration)
    {
        var path = TempPath;

        var reason = PvExporter.Export(PvScene.CreateBlank(), new EffectGraph(), duration, 60, path, null);

        Assert.NotNull(reason);
        Assert.Contains("时长", reason);
        Assert.False(File.Exists(path), "参数校验没过就不该动输出文件");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    [InlineData(1000)]
    public void RejectsInvalidFps(int fps)
    {
        var reason = PvExporter.Export(PvScene.CreateBlank(), new EffectGraph(), 1.0, fps, TempPath, null);

        Assert.NotNull(reason);
        Assert.Contains("帧率", reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void RejectsEmptyPath(string? path)
    {
        var reason = PvExporter.Export(PvScene.CreateBlank(), new EffectGraph(), 1.0, 60, path!, null);

        Assert.NotNull(reason);
        Assert.Contains("路径", reason);
    }

    /// <summary>NV12 色度是 2×2 采样，奇数画布凑不出这个格式：当场报出来，不要等编码器报 HRESULT。</summary>
    [Fact]
    public void RejectsOddCanvas()
    {
        var scene = SmallScene();
        scene.Width = 321;

        var reason = PvExporter.Export(scene, new EffectGraph(), 1.0, 60, TempPath, null);

        Assert.NotNull(reason);
        Assert.Contains("偶数", reason);
    }

    // ---------------- 逐帧口径：真渲染 + 假写入，编码不进单测 ----------------

    [GpuFact]
    public void OneFrameExportReportsOneFrame()
    {
        var scene = SmallScene();
        var progress = new SyncProgress();
        var written = new List<int>();

        var reason = PvExporter.RenderFrames(scene, new EffectGraph(), 1.0 / 60, 60, progress,
            nv12 => written.Add(nv12.Length));

        Assert.Null(reason);
        Assert.Equal(new[] { 1.0 }, progress.Reports);
        Assert.Equal(new[] { MfSink.Nv12Size(scene.Width, scene.Height) }, written);
    }

    [GpuFact]
    public void FrameCountFollowsDurationTimesFps()
    {
        var scene = SmallScene();
        var progress = new SyncProgress();
        var written = 0;

        var reason = PvExporter.RenderFrames(scene, new EffectGraph(), 0.1, 60, progress, _ => written++);

        Assert.Null(reason);
        Assert.Equal(6, written);                            // round(0.1 × 60)
        Assert.Equal(6, progress.Reports.Count);
        Assert.Equal(1.0, progress.Reports[^1], 6);
    }

    // ---------------- 帧状态链：曲线 / 歌词 / 字幕 ----------------

    /// <summary>
    /// 导出要画动效歌词，场景里就得有 <c>lyrics_pv</c> 这一层：舞台按层 Id 找提供者，
    /// 没有这一层，歌词提供者建了也不进合成。
    /// </summary>
    private static PvScene LyricScene()
    {
        var scene = SmallScene();
        scene.Layers.Add(new PvLayerConfig
        {
            Id = "lyrics_pv",
            Name = "Lyrics",
            Kind = PvLayerKind.LyricPv,
            // 必须显式清掉：PvLayerConfig 的默认值会让这层被当成 Spout 源图层
            SpoutSenderName = null,
        });
        return scene;
    }

    /// <summary>逐帧收 NV12：导出复用同一个数组，每帧必须自己拷一份。</summary>
    private static List<byte[]> CaptureFrames(PvScene scene, PvExportInputs? inputs, double durationSec, int fps)
    {
        var frames = new List<byte[]>();
        var reason = PvExporter.RenderFrames(scene, new EffectGraph(), durationSec, fps, null,
            nv12 => frames.Add((byte[])nv12.Clone()), inputs);
        Assert.Null(reason);
        return frames;
    }

    /// <summary>一句 1 秒的歌词：t=0.25 / 0.75 时进度落在 0.15–0.85 之间，正文完全显现（不受进出场渐变影响）。</summary>
    private static List<LyricClip> OneLyricClip() => [new LyricClip("c0", 0, 1000, "动效歌词", false)];

    /// <summary>相机曲线进导出帧：同一句歌词、两个时刻的镜头缩放分别是 1.25 与 1.75，两帧像素必须不同。</summary>
    [GpuFact]
    public void FramesFollowCameraCurve()
    {
        var curves = new DirectorEvaluator();
        var curve = new FloatCurve();
        curve.Keys.Add(new CurveKey(0, 1.0, CurveInterp.Linear));
        curve.Keys.Add(new CurveKey(1, 2.0, CurveInterp.Linear));
        curves.SetCurve(new ParamAddress(ParamAddressKind.Camera, "", "scale"), curve);

        var frames = CaptureFrames(LyricScene(), new PvExportInputs(Curves: curves, Clips: OneLyricClip()), 1.0, 4);

        Assert.Equal(4, frames.Count);   // round(1.0 × 4)
        Assert.False(frames[1].SequenceEqual(frames[3]),
            "t=0.25 与 t=0.75 的镜头缩放不同（1.25 / 1.75），导出帧一样就说明曲线没进管线");
    }

    /// <summary>歌词样本进导出帧：同一时刻轴上「有歌词」与「没喂歌词」两帧必须不同。</summary>
    [GpuFact]
    public void FramesFollowLyricSamples()
    {
        var withLyrics = CaptureFrames(LyricScene(), new PvExportInputs(Clips: OneLyricClip()), 1.0, 4);
        var withoutLyrics = CaptureFrames(LyricScene(), null, 1.0, 4);

        Assert.False(withLyrics[1].SequenceEqual(withoutLyrics[1]),
            "t=0.25 该句进度 0.25、正文已显现，导出帧应当带上歌词");
    }

    /// <summary>字幕设置进导出帧：给了配置就多一层压顶字幕（挂在全局后期之后，不进图层栈）。</summary>
    [GpuFact]
    public void FramesBurnInSubtitles()
    {
        var withSubtitle = CaptureFrames(LyricScene(),
            new PvExportInputs(Clips: OneLyricClip(), Subtitles: new SubtitleConfig { Content = SubContent.Both }),
            1.0, 4);
        var withoutSubtitle = CaptureFrames(LyricScene(), new PvExportInputs(Clips: OneLyricClip()), 1.0, 4);

        Assert.False(withSubtitle[1].SequenceEqual(withoutSubtitle[1]),
            "字幕配置给了却没画上去，说明离屏管线没接字幕源");
    }

    /// <summary>
    /// 三输出一致（本线的选择：**导出默认不烧字幕**，要烧只能在导出弹窗里显式勾）。
    ///
    /// 预览那条路（<c>PvStageWindow.BuildPipeline</c>）从来不接字幕源，舞台图层栈与 Spout 广播里都没有这一层，
    /// 所以导出默认帧必须与预览逐字节相同；而勾上之后必须真的多出一层——否则这个开关是假的。
    /// 反过来做（预览侧补一个字幕源）得给渲染线程喂一份 UI 线程正在改的歌词条快照，
    /// 那是另一条线程契约的事，不塞进这次收口。
    /// </summary>
    [GpuFact]
    public void DefaultExportMatchesPreviewAndOnlyExplicitChoiceBurnsSubtitles()
    {
        var scene = LyricScene();
        var clips = OneLyricClip();

        var exportDefault = CaptureFrames(scene, new PvExportInputs(Clips: clips), 1.0, 4)[1];
        var exportBurned = CaptureFrames(scene,
            new PvExportInputs(Clips: clips, Subtitles: new SubtitleConfig { Content = SubContent.Trans }),
            1.0, 4)[1];
        var preview = PreviewFrame(scene, clips, 0.25); // 第 1 帧 = 0.25s，与导出取同一时刻

        Assert.True(preview.SequenceEqual(exportDefault), "导出默认（不烧字幕）应当与预览逐字节相同");
        Assert.False(preview.SequenceEqual(exportBurned), "勾上烧字幕必须真的多出一层，否则这个开关是假的");
    }

    /// <summary>预览那条路：与 <c>PvStageWindow.BuildPipeline</c> 同一套接线（不传字幕源，只喂歌词条与样式）。</summary>
    private static byte[] PreviewFrame(PvScene scene, IReadOnlyList<LyricClip> clips, double time)
    {
        var gpu = D3DDevice.TryCreate(out var error);
        if (gpu is null) GpuRequire.Fail(error);

        var nv12 = new byte[MfSink.Nv12Size(scene.Width, scene.Height)];
        using (gpu)
        using (var pipe = new PvPipeline(gpu, scene, new EffectGraph()))
        using (var target = OffscreenTarget.Create(gpu, pipe.D2DContext, scene.Width, scene.Height))
        {
            pipe.Renderer.Lyric?.SetClips(clips);
            pipe.Renderer.Lyric?.SetStyle(scene.LyricStyle ?? LyricStyle.FromName(scene.LyricStyleName));
            pipe.Prepare(time);
            pipe.Composite(target.Bitmap, scene.Width, scene.Height);
            gpu.Context.Flush();

            using var read = target.Readback();
            PvExporter.BgraToNv12(read.BgraPixels, scene.Width, scene.Height, nv12);
        }

        return nv12;
    }

    // ---------------- 互操作声明形状 ----------------

    /// <summary>
    /// IMFSample 必须把 IMFAttributes 的 30 个槽位铺在自己方法的前面：ComImport 接口按声明顺序对 vtable，
    /// 用「接口继承」写不会让派生接口的方法落到基类之后——实测 AddBuffer 会打到 IMFAttributes::GetString 上
    /// （返回 MF_E_ATTRIBUTENOTFOUND），样本送不进编码器，而这事只有真编码时才暴露：所以在这里用声明形状兜住。
    /// </summary>
    [Fact]
    public void SampleInterfaceKeepsAttributeSlotsInPlace()
    {
        var sample = typeof(MfSink).GetNestedType("IMFSample", BindingFlags.NonPublic);

        Assert.NotNull(sample);
        Assert.Empty(sample!.GetInterfaces());
        Assert.Equal(44, sample.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Length);
        Assert.Contains(sample.GetMethods(), m => m.Name == "AddBuffer");
        Assert.Contains(sample.GetMethods(), m => m.Name == "SetSampleTime");
    }

    // ---------------- BGRA → NV12 ----------------

    /// <summary>BT.709 有限范围的三根锚点：黑=16、白=235、灰 128=126；中性色色度停在 128 附近。</summary>
    [Fact]
    public void BgraToNv12HitsBt709Anchors()
    {
        var black = new byte[MfSink.Nv12Size(2, 2)];
        PvExporter.BgraToNv12(Bgra(2, 2, 0, 0, 0), 2, 2, black);
        Assert.Equal(16, black[0]);
        Assert.Equal(128, black[4]);
        Assert.Equal(128, black[5]);

        var white = new byte[MfSink.Nv12Size(2, 2)];
        PvExporter.BgraToNv12(Bgra(2, 2, 255, 255, 255), 2, 2, white);
        Assert.Equal(235, white[0]);
        Assert.Equal(127, white[4]);
        Assert.Equal(128, white[5]);

        var gray = new byte[MfSink.Nv12Size(2, 2)];
        PvExporter.BgraToNv12(Bgra(2, 2, 128, 128, 128), 2, 2, gray);
        Assert.Equal(126, gray[0]);
    }

    /// <summary>Y 平面按行优先、自上而下排列；UV 交错且不串面（纯红的 Cr 必须高于 Cb）。</summary>
    [Fact]
    public void BgraToNv12KeepsRowOrderAndChromaPlanes()
    {
        var red = new byte[MfSink.Nv12Size(2, 2)];
        PvExporter.BgraToNv12(Bgra(2, 2, 255, 0, 0), 2, 2, red);
        Assert.Equal(62, red[0]);
        Assert.True(red[5] > red[4] + 100, $"纯红应 Cr 高 Cb 低，实测 Cb={red[4]} Cr={red[5]}");

        var mixed = Bgra(2, 2, 255, 255, 255);
        for (var i = 0; i < 4; i++) mixed[i] = 0;           // 左上像素置黑，其余保持白
        var nv12 = new byte[MfSink.Nv12Size(2, 2)];
        PvExporter.BgraToNv12(mixed, 2, 2, nv12);

        Assert.Equal(new byte[] { 16, 235, 235, 235 }, nv12[..4]);
    }
}
