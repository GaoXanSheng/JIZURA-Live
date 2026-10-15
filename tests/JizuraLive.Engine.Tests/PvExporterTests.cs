using System.IO;
using System.Reflection;
using JizuraLive.Pv;
using JizuraLive.Render.Effects;

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
