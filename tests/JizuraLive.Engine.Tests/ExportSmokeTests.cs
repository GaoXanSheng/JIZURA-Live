using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using JizuraLive.Engine;
using JizuraLive.Export;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 导出冒烟：小分辨率（640×360@12fps）真跑整条管线（渲染→编码→ffprobe 校验/zip 校验）。
/// 依赖系统 ffmpeg/ffprobe（开发机 WinGet 版；无则测试失败并在消息里说明）。
/// </summary>
[Collection("EngineRegistry")]
public class ExportSmokeTests
{
    private static (Project Project, Plan Plan) MakeSmall()
    {
        EngineBoot.Install();
        var p = Project.NewDefault();
        p.Lyrics = "夜明けの色を覚えてる";
        p.Title = "T";
        p.Artist = "A";
        p.Res = 360; // 640×360，测试速度优先
        p.Fps = 12;
        p.Timing.Snap = false;
        var plan = Planner.Plan(p, null);
        return (p, plan);
    }

    // JZ_KEEP=1：保留产物到 bin 目录供 ffprobe 检查（帧数/码率类问题排查）
    private static string TempPath(string ext) =>
        Environment.GetEnvironmentVariable("JZ_KEEP") == "1"
            ? Path.Combine(AppContext.BaseDirectory, "export-keep" + ext)
            : Path.Combine(Path.GetTempPath(), $"jizura-test-{Guid.NewGuid():N}{ext}");

    private static void Cleanup(string p)
    {
        if (Environment.GetEnvironmentVariable("JZ_KEEP") != "1") File.Delete(p);
    }

    private static string Probe(string args)
    {
        var psi = new ProcessStartInfo("ffprobe")
        {
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in args.Split(' ', StringSplitOptions.RemoveEmptyEntries)) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffprobe 启动失败（需系统 PATH 有 ffprobe）");
        var outp = p.StandardOutput.ReadToEnd();
        p.WaitForExit(30000);
        return outp.Trim();
    }

    [Fact]
    public void 导出MP4_h264与时长校验()
    {
        var (proj, plan) = MakeSmall();
        var outp = TempPath(".mp4");
        try
        {
            ExportJob.Run(new ExportRequest
            {
                Project = proj, Plan = plan, OutputPath = outp,
                Spec = new ExportSpecDef("mp4", Transparent: false, Layer: null, NoHud: false,
                    Layers: false, CustomDrawOnly: false, BakeSubs: false, EmbedAudio: false, KeyBg: "off"),
            }, null, CancellationToken.None);

            Assert.True(new FileInfo(outp).Length > 2000, "mp4 太小，疑似空文件");
            var codec = Probe($"-v error -select_streams v:0 -show_entries stream=codec_name,width,height -of csv=p=0 {outp}");
            Assert.Contains("h264", codec);
            Assert.Contains("640,360", codec);
            var dur = double.Parse(Probe($"-v error -show_entries format=duration -of csv=p=0 {outp}"),
                System.Globalization.CultureInfo.InvariantCulture);
            var framesRead = int.Parse(Probe($"-v error -count_frames -select_streams v:0 -show_entries stream=nb_read_frames -of csv=p=0 {outp}"));
            Assert.True(Math.Abs(dur - plan.Duration) <= 0.35,
                $"dur={dur} plan.Duration={plan.Duration} plan.Fps={plan.Fps} frames={framesRead} res={proj.Res} ly='{proj.Lyrics}'");
        }
        finally { Cleanup(outp); }
    }

    [Fact]
    public void 导出WebM透明_yuva420p校验()
    {
        var (proj, plan) = MakeSmall();
        var outp = TempPath(".webm");
        try
        {
            ExportJob.Run(new ExportRequest
            {
                Project = proj, Plan = plan, OutputPath = outp,
                Spec = new ExportSpecDef("webm", Transparent: true, Layer: "front", NoHud: true,
                    Layers: false, CustomDrawOnly: false, BakeSubs: false, EmbedAudio: false, KeyBg: "off"),
            }, null, CancellationToken.None);

            Assert.True(new FileInfo(outp).Length > 1000, "webm 太小，疑似空文件");
            Assert.Contains("vp9", Probe($"-v error -select_streams v:0 -show_entries stream=codec_name -of csv=p=0 {outp}"));
            // WebM alpha 存在 BlockAdditional 里：容器带 alpha_mode=1 标签；
            // 且 ffmpeg 原生 vp9 解码器不合并 alpha 平面（业界坑），必须强制 libvpx 解码器验 pix_fmt
            Assert.Equal("1", Probe($"-v error -show_entries stream_tags=alpha_mode -of csv=p=0 {outp}"));
            Assert.Contains("yuva420p",
                Probe($"-v error -c:v libvpx-vp9 -select_streams v:0 -show_entries stream=pix_fmt -of csv=p=0 {outp}"));
        }
        finally { Cleanup(outp); }
    }

    [Fact]
    public void 导出PNG序列_zip条目数校验()
    {
        var (proj, plan) = MakeSmall();
        var outp = TempPath(".zip");
        try
        {
            ExportJob.Run(new ExportRequest
            {
                Project = proj, Plan = plan, OutputPath = outp,
                Spec = new ExportSpecDef("png", Transparent: false, Layer: null, NoHud: false,
                    Layers: false, CustomDrawOnly: false, BakeSubs: false, EmbedAudio: false, KeyBg: "off"),
            }, null, CancellationToken.None);

            using var zip = ZipFile.OpenRead(outp);
            var frames = (int)Math.Round(plan.Duration * plan.Fps);
            Assert.Equal(frames, zip.Entries.Count);
            Assert.All(zip.Entries, e => Assert.True(e.Length > 200, e.Name + " 过小"));
        }
        finally { Cleanup(outp); }
    }

    [Fact]
    public void 导出PNG分层_back与front双目录()
    {
        var (proj, plan) = MakeSmall();
        var outp = TempPath(".zip");
        try
        {
            ExportJob.Run(new ExportRequest
            {
                Project = proj, Plan = plan, OutputPath = outp,
                Spec = new ExportSpecDef("png", Transparent: true, Layer: null, NoHud: true,
                    Layers: true, CustomDrawOnly: false, BakeSubs: false, EmbedAudio: false, KeyBg: "off"),
            }, null, CancellationToken.None);

            using var zip = ZipFile.OpenRead(outp);
            var frames = (int)Math.Round(plan.Duration * plan.Fps);
            Assert.Equal(frames * 2, zip.Entries.Count);
            Assert.Contains(zip.Entries, e => e.FullName.StartsWith("back/"));
            Assert.Contains(zip.Entries, e => e.FullName.StartsWith("front/"));
        }
        finally { Cleanup(outp); }
    }
}

/// <summary>字幕烤入冒烟：KRC 逐字 → 导出帧底部出现字幕亮像素。</summary>
public class SubtitleBakeTests
{
    [Fact]
    public void 烤字幕导出_帧内出现字幕像素()
    {
        EngineBoot.InstallPacks();
        var p = Project.NewDefault();
        p.Lyrics = "夜明けの色を覚えてる";
        p.Res = 360;
        p.Fps = 12;
        p.Timing.Snap = false;
        var plan = Planner.Plan(p, null);

        var lines = new List<JizuraLive.App.Cloud.KrcLine>
        {
            new(0, 5000, "字幕测试ABC",
                new[] { new JizuraLive.App.Cloud.KrcWord(0, 2000, "字幕测试"), new JizuraLive.App.Cloud.KrcWord(2000, 3000, "ABC") }),
        };
        var outp = Path.Combine(Path.GetTempPath(), $"jizura-sub-{Guid.NewGuid():N}.zip");
        try
        {
            ExportJob.Run(new ExportRequest
            {
                Project = p, Plan = plan, OutputPath = outp,
                Spec = new ExportSpecDef("png", Transparent: false, Layer: null, NoHud: false,
                    Layers: false, CustomDrawOnly: false, BakeSubs: true, EmbedAudio: false, KeyBg: "off"),
                SubtitleLines = lines,
                SubtitleCfg = new JizuraLive.Engine.SubtitleConfig { Content = SubContent.Both },
            }, null, CancellationToken.None);

            using var zip = System.IO.Compression.ZipFile.OpenRead(outp);
            var first = zip.Entries.OrderBy(e => e.FullName).First();
            using var es = first.Open();
            using var img = SkiaSharp.SKImage.FromEncodedData(es);
            using var bmp = SkiaSharp.SKBitmap.FromImage(img);
            // 底部 25% 区域统计近白像素（字幕主色 #ffffff + 描边黑；暗底工程对比明显）
            var bright = 0;
            for (var y = (int)(bmp.Height * 0.72); y < bmp.Height; y++)
                for (var x = 0; x < bmp.Width; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    if (c.Red > 200 && c.Green > 200 && c.Blue > 200) bright++;
                }
            Assert.True(bright > 50, $"底部近白像素 {bright}，字幕未烤入");
        }
        finally { File.Delete(outp); }
    }
}
