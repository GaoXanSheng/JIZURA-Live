using System.IO;
using JizuraLive.App; // EngineRenderGate（渲染串行闸，与预览/Spout 共享）
using JizuraLive.Engine;
using JizuraLive.Logging;
using SkiaSharp;

namespace JizuraLive.Export;

/// <summary>导出请求（UI 从 ExportSpec.BuildSpec 组装）。</summary>
public sealed class ExportRequest
{
    public required Project Project { get; init; }
    public required Plan Plan { get; init; }
    public required ExportSpecDef Spec { get; init; }
    public required string OutputPath { get; init; }

    /// <summary>音频源文件（null = 无音轨）。</summary>
    public string? AudioPath { get; init; }

    /// <summary>另存同名 _audio.wav（非 MP4 格式勾了歌曲时为 true）。</summary>
    public bool WavToo { get; init; }
}

/// <summary>
/// 导出编排（对位 11_export.js）：离线逐帧渲染 → 编码/写盘。
/// 独立 Renderer 实例（与预览流缓存隔离）+ 单面常驻（引擎跨帧语义：缓存快照引用着面，
/// 释放顺序必须 renderer → surface → bitmap）。进度帧级回调，取消帧间协作式。
/// </summary>
public static class ExportJob
{
    public static void Run(ExportRequest req, IProgress<(double Pct, string Stage)>? progress, CancellationToken ct)
    {
        if (req.Spec.BakeSubs || req.Spec.CustomDrawOnly)
            throw new NotSupportedException("字幕烤入/纯字幕层导出在 M6 接入字幕系统后可用");

        switch (req.Spec.Kind)
        {
            case "png":
                ExportPng(req, progress, ct);
                break;
            case "wav":
                ExportWav(req, progress);
                break;
            case "mp4":
            case "mp4file":
            case "webm":
                ExportVideo(req, progress, ct);
                break;
            default:
                throw new NotSupportedException("未知导出类型：" + req.Spec.Kind);
        }
    }

    /// <summary>导出分辨率（对位 J.outputSize）：渲染按 scale 缩放到目标尺寸。</summary>
    private static (int W, int H, float Scale) OutputDims(Project project, Plan plan)
    {
        var (ow, oh) = Planner.OutputSize(project);
        return (ow, oh, (float)(ow / (double)Math.Max(1, plan.W)));
    }

    private static SKImageInfo StageInfo(int w, int h, bool transparent) => new(
        w, h,
        transparent ? SKColorType.Rgba8888 : SKColorType.Bgra8888,
        transparent ? SKAlphaType.Unpremul : SKAlphaType.Premul);

    // ---------------- 视频（MP4 / WebM） ----------------

    private static void ExportVideo(ExportRequest req, IProgress<(double Pct, string Stage)>? progress, CancellationToken ct)
    {
        var plan = req.Plan;
        var fps = Math.Max(1, plan.Fps);
        var frames = Math.Max(1, (int)Math.Round(plan.Duration * fps));
        var transparent = req.Spec.Transparent;

        var (ow, oh, scale) = OutputDims(req.Project, plan);
        var bmp = new SKBitmap(StageInfo(ow, oh, transparent));
        var surface = SKSurface.Create(bmp.Info, bmp.GetPixels(), bmp.RowBytes);
        var renderer = new Renderer();
        var opts = new RenderOpts { Scale = scale, Transparent = transparent, Layer = req.Spec.Layer, NoHud = req.Spec.NoHud };

        // 音轨：解码 → 特效链 → 临时 WAV（与视频同长；ffmpeg 作第二输入混流）
        string? tmpWav = null;
        if (req.Spec.EmbedAudio && req.AudioPath != null)
        {
            progress?.Report((0, "准备音频"));
            var ap = AudioPrep.Prepare(req.AudioPath, req.Project.AudioFx, plan.Duration);
            if (ap != null)
            {
                tmpWav = Path.Combine(Path.GetTempPath(), $"jizura-mix-{Guid.NewGuid():N}.wav");
                WavWriter.Write(ap, tmpWav, plan.Duration);
            }
        }

        try
        {
            using var enc = new FFmpegEncoder(BuildVideoArgs(req, fps, transparent, tmpWav), frames);
            var stride = bmp.RowBytes;
            var pixels = bmp.GetPixels();
            for (var i = 0; i < frames; i++)
            {
                ct.ThrowIfCancellationRequested();
                var t = i / (double)fps;
                lock (EngineRenderGate.Lock)
                    renderer.Frame(surface.Canvas, plan, t, opts);
                unsafe
                {
                    // 帧字节数必须按【输出尺寸】算（oh）：用设计高度 plan.H 会在缩小导出时
                    // 每帧多塞 (plan.H/oh) 倍数据（管道被拆成多帧），且越界读位图缓冲区
                    enc.WriteFrame(new ReadOnlySpan<byte>((void*)pixels, stride * oh));
                }
                if ((i & 3) == 0) progress?.Report((enc.Progress * 0.98, $"编码 {i + 1}/{frames}"));
            }
            enc.Finish();
        }
        finally
        {
            // 释放顺序：renderer（缓存快照）→ surface → bitmap
            renderer.Dispose();
            surface.Dispose();
            bmp.Dispose();
            if (tmpWav != null)
                try { File.Delete(tmpWav); } catch { /* 清理失败不阻塞 */ }
        }

        progress?.Report((1.0, "完成"));
    }

    private static List<string> BuildVideoArgs(ExportRequest req, int fps, bool transparent, string? wav)
    {
        var (dimsW, dimsH, _) = OutputDims(req.Project, req.Plan);
        var withAudio = wav != null;
        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "rawvideo", "-pix_fmt", transparent ? "rgba" : "bgra",
            "-s", $"{dimsW}x{dimsH}", "-r", fps.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i", "pipe:0",
        };
        if (withAudio)
        {
            args.Add("-i");
            args.Add(wav!);
        }

        var bps = Math.Clamp(dimsW * (double)dimsH * fps * 0.42, 8e6, 90e6);
        if (req.Spec.Kind == "webm")
        {
            args.AddRange(new[] { "-c:v", "libvpx-vp9", "-b:v", ((long)(bps * 0.6)).ToString(), "-row-mt", "1" });
            args.AddRange(transparent
                ? new[] { "-pix_fmt", "yuva420p", "-auto-alt-ref", "0" }
                : new[] { "-pix_fmt", "yuv420p" });
            if (withAudio) args.AddRange(new[] { "-c:a", "libopus", "-b:a", "160k" });
        }
        else
        {
            args.AddRange(new[] { "-c:v", "libx264", "-preset", "medium", "-b:v", ((long)bps).ToString(), "-pix_fmt", "yuv420p" });
            if (withAudio) args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
            args.Add("-movflags");
            args.Add("+faststart");
        }
        if (withAudio) args.Add("-shortest");
        args.Add(req.OutputPath);
        return args;
    }

    // ---------------- PNG 序列 ----------------

    private static void ExportPng(ExportRequest req, IProgress<(double Pct, string Stage)>? progress, CancellationToken ct)
    {
        var plan = req.Plan;
        var fps = Math.Max(1, plan.Fps);
        var frames = Math.Max(1, (int)Math.Round(plan.Duration * fps));
        var layered = req.Spec.Layers;

        using var zip = new PngSequenceWriter(req.OutputPath, layered);

        // 两股流各自常驻一面（back/front 渲染互不污染；单流内保持引擎跨帧语义）
        var (ow, oh, scale) = OutputDims(req.Project, plan);
        var backT = layered || !req.Spec.Transparent;
        var backBmp = new SKBitmap(StageInfo(ow, oh, transparent: !backT));
        var backSurf = SKSurface.Create(backBmp.Info, backBmp.GetPixels(), backBmp.RowBytes);
        var backRend = new Renderer();

        SKBitmap? frontBmp = null;
        SKSurface? frontSurf = null;
        Renderer? frontRend = null;
        if (layered)
        {
            frontBmp = new SKBitmap(StageInfo(ow, oh, transparent: true));
            frontSurf = SKSurface.Create(frontBmp.Info, frontBmp.GetPixels(), frontBmp.RowBytes);
            frontRend = new Renderer();
        }

        try
        {
            for (var i = 0; i < frames; i++)
            {
                ct.ThrowIfCancellationRequested();
                var t = i / (double)fps;
                if (layered)
                {
                    lock (EngineRenderGate.Lock)
                        backRend.Frame(backSurf.Canvas, plan, t, new RenderOpts { Scale = scale, Layer = "back", NoHud = true });
                    zip.AddFrame("back", i + 1, backBmp);
                    lock (EngineRenderGate.Lock)
                        frontRend!.Frame(frontSurf!.Canvas, plan, t,
                            new RenderOpts { Scale = scale, Layer = "front", NoHud = true, Transparent = true });
                    zip.AddFrame("front", i + 1, frontBmp!);
                }
                else
                {
                    lock (EngineRenderGate.Lock)
                        backRend.Frame(backSurf.Canvas, plan, t,
                            new RenderOpts { Scale = scale, Transparent = req.Spec.Transparent, NoHud = req.Spec.NoHud });
                    zip.AddFrame(null, i + 1, backBmp);
                }
                if ((i & 3) == 0) progress?.Report((i / (double)frames * 0.98, $"写帧 {i + 1}/{frames}"));
            }
        }
        finally
        {
            // 释放顺序：renderer → surface → bitmap
            backRend.Dispose();
            backSurf.Dispose();
            backBmp.Dispose();
            if (frontRend != null) frontRend.Dispose();
            if (frontSurf != null) frontSurf.Dispose();
            if (frontBmp != null) frontBmp.Dispose();
        }

        progress?.Report((1.0, "完成"));
    }

    // ---------------- 音频（WAV） ----------------

    private static void ExportWav(ExportRequest req, IProgress<(double Pct, string Stage)>? progress)
    {
        if (req.AudioPath == null || !File.Exists(req.AudioPath))
            throw new InvalidOperationException("没有已投入的歌曲，无法导出 WAV");

        progress?.Report((0, "解码音频"));
        var ap = AudioPrep.Prepare(req.AudioPath, req.Project.AudioFx, req.Plan.Duration)
                 ?? throw new InvalidOperationException("音频解码失败");
        WavWriter.Write(ap, req.OutputPath, req.Plan.Duration);
        progress?.Report((1.0, "完成"));
    }

    /// <summary>WavToo：非 MP4 导出勾了歌曲时，另存同名 _audio.wav（与内嵌音轨同一份采样）。</summary>
    public static void ExportSideWav(ExportRequest req)
    {
        if (req.AudioPath == null || !File.Exists(req.AudioPath)) return;
        var wavPath = Path.ChangeExtension(req.OutputPath, null) + "_audio.wav";
        var ap = AudioPrep.Prepare(req.AudioPath, req.Project.AudioFx, req.Plan.Duration);
        if (ap != null)
        {
            WavWriter.Write(ap, wavPath, req.Plan.Duration);
            Log.Info("[Export] 另存 WAV：" + wavPath);
        }
    }
}
