using System.Globalization;
using System.IO;
using JizuraLive.App;
using JizuraLive.App.Cloud;
using JizuraLive.App.I18n;
using JizuraLive.Engine;
using JizuraLive.Logging;
using SkiaSharp;

// EngineRenderGate（渲染串行闸，与预览/Spout 共享）

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

    /// <summary>云字幕（KRC 逐字）：Spec.BakeSubs 时逐帧烤入。</summary>
    public List<KrcLine>? SubtitleLines { get; init; }
    public SubtitleConfig? SubtitleCfg { get; init; }
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
                throw new NotSupportedException(L.F("Export.Err.UnknownKindFmt", req.Spec.Kind));
        }
    }

    /// <summary>导出分辨率（对位 J.outputSize）：渲染按 scale 缩放到目标尺寸。</summary>
    private static (int W, int H, float Scale) OutputDims(Project project, Plan plan)
    {
        var (ow, oh) = Planner.OutputSize(project);
        return (ow, oh, (float)(ow / (double)Math.Max(1, plan.W)));
    }

    /// <summary>
    /// 导出区间（对位 11_export.js 的 J.exportSpan + fxSpeed）：选段（可能 null）→ 起止秒。
    /// 输出时长 = 歌曲跨度 ÷speed（变速缩短成片）；帧 i 的歌曲时刻 = T0 + i/fps×speed；
    /// 音频取 [T0, T0+歌曲跨度] 后按 speed 变速 → 输出时长。
    /// </summary>
    private static (double T0, double SongDur, double OutDur, double Speed) SpanOf(ExportRequest req)
    {
        var plan = req.Plan;
        var span = Ranges.ExportSpanSeconds(plan, req.Project.ExportRange);
        var t0 = span?.T0 ?? 0;
        var t1 = span != null ? Math.Min(plan.Duration, span.Value.T1) : plan.Duration;
        var speed = AudioFx.FxSpeed(req.Project);
        var songDur = Math.Max(1.0 / Math.Max(1, plan.Fps), Math.Max(0, t1 - t0));
        return (t0, songDur, songDur / speed, speed);
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
        var (t0, _, outDur, speed) = SpanOf(req);
        var frames = Math.Max(1, (int)Math.Round(outDur * fps)); // 对位 total = round(span.dur/spd×fps)
        var transparent = req.Spec.Transparent;

        var (ow, oh, scale) = OutputDims(req.Project, plan);
        var bmp = new SKBitmap(StageInfo(ow, oh, transparent));
        var surface = SKSurface.Create(bmp.Info, bmp.GetPixels(), bmp.RowBytes);
        var isCustomDrawOnly = req.Spec.CustomDrawOnly;
        var (subLines, subNotes) = ResolveSubtitleSource(req, plan);
        if (isCustomDrawOnly && subLines is not { Count: > 0 })
            throw new InvalidOperationException(L.T("Export.Err.SubsOnlyNoData"));

        var renderer = isCustomDrawOnly ? null : new Renderer();
        var opts = isCustomDrawOnly ? null : new RenderOpts { Scale = scale, Transparent = transparent, Layer = req.Spec.Layer, NoHud = req.Spec.NoHud };

        // 音轨：解码 → 从选段起点取 → 特效链 + 变速 → 临时 WAV（与输出同长；ffmpeg 作第二输入混流）
        string? tmpWav = null;
        if (req.Spec.EmbedAudio && req.AudioPath != null)
        {
            progress?.Report((0, L.T("Export.Stage.PrepareAudio")));
            var ap = AudioPrep.Prepare(req.AudioPath, req.Project.AudioFx, outDur, t0, speed);
            if (ap != null)
            {
                tmpWav = Path.Combine(Path.GetTempPath(), $"jizura-mix-{Guid.NewGuid():N}.wav");
                WavWriter.Write(ap, tmpWav, outDur);
            }
        }

        // 字幕：CustomDrawOnly 时画全套（mainLine: true）；BakeSubs 时只画注音/翻译行（mainLine: false，主歌词由引擎画）
        var subLayer = (isCustomDrawOnly || req.Spec.BakeSubs) && subLines is { Count: > 0 }
            ? new SubtitleLayer(subLines, req.SubtitleCfg ?? new SubtitleConfig { Content = SubContent.Both },
                subNotes, mainLine: isCustomDrawOnly)
            : null;
        try
        {
            using var enc = new FFmpegEncoder(BuildVideoArgs(req, fps, transparent, tmpWav), frames);
            var stride = bmp.RowBytes;
            var pixels = bmp.GetPixels();
            for (var i = 0; i < frames; i++)
            {
                ct.ThrowIfCancellationRequested();
                var t = t0 + i / (double)fps * speed; // 对位 span.t0 + spd×i/fps（变速时歌曲时间走得快）
                if (isCustomDrawOnly)
                {
                    surface.Canvas.SetMatrix(SKMatrix.Identity);
                    surface.Canvas.Clear(transparent ? new SKColor(0, 0, 0, 0) : SKColors.Black);
                    subLayer!.Render(surface.Canvas, t, ow, oh);
                }
                else
                {
                    if (subLayer != null)
                    {
                        // 先清底层：引擎透明模式不铺满全帧，防上一帧字幕透出（引擎缓存 miss 路径自带全帧覆盖）
                        surface.Canvas.SetMatrix(SKMatrix.Identity);
                        surface.Canvas.Clear(transparent ? new SKColor(0, 0, 0, 0) : SKColors.Black);
                    }
                    lock (EngineRenderGate.Lock)
                        renderer!.Frame(surface.Canvas, plan, t, opts!);
                    if (subLayer != null)
                    {
                        surface.Canvas.SetMatrix(SKMatrix.Identity); // 字幕按设备像素绘制
                        subLayer.Render(surface.Canvas, t, ow, oh);
                    }
                }
                unsafe
                {
                    // 帧字节数必须按【输出尺寸】算（oh）：用设计高度 plan.H 会在缩小导出时
                    // 每帧多塞 (plan.H/oh) 倍数据（管道被拆成多帧），且越界读位图缓冲区
                    enc.WriteFrame(new ReadOnlySpan<byte>((void*)pixels, stride * oh));
                }
                if ((i & 3) == 0) progress?.Report((enc.Progress * 0.98, L.F("Export.Stage.EncodeFmt", i + 1, frames)));
            }
            enc.Finish();
        }
        finally
        {
            // 释放顺序：renderer（缓存快照）→ surface → bitmap
            renderer?.Dispose();
            surface.Dispose();
            bmp.Dispose();
            if (tmpWav != null)
                try { File.Delete(tmpWav); } catch { /* 清理失败不阻塞 */ }
        }

        progress?.Report((1.0, L.T("Export.Stage.Done")));
    }

    private static List<string> BuildVideoArgs(ExportRequest req, int fps, bool transparent, string? wav)
    {
        var (dimsW, dimsH, _) = OutputDims(req.Project, req.Plan);
        var withAudio = wav != null;
        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "rawvideo", "-pix_fmt", transparent ? "rgba" : "bgra",
            "-s", $"{dimsW}x{dimsH}", "-r", fps.ToString(CultureInfo.InvariantCulture),
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
        var (t0, _, outDur, speed) = SpanOf(req);
        var frames = Math.Max(1, (int)Math.Round(outDur * fps)); // 与视频同口径（选段 + 变速）
        var (subLines, subNotes) = ResolveSubtitleSource(req, plan);
        var isCustomDrawOnly = req.Spec.CustomDrawOnly;
        if (isCustomDrawOnly && subLines is not { Count: > 0 })
            throw new InvalidOperationException(L.T("Export.Err.SubsOnlyNoData"));

        var layered = !isCustomDrawOnly && req.Spec.Layers;

        using var zip = new PngSequenceWriter(req.OutputPath, layered);
        // 字幕：CustomDrawOnly 时画全套（mainLine: true）；BakeSubs 时只画注音/翻译行（mainLine: false，主歌词由引擎画）
        var subLayer = (isCustomDrawOnly || (!layered && req.Spec.BakeSubs)) && subLines is { Count: > 0 }
            ? new SubtitleLayer(subLines, req.SubtitleCfg ?? new SubtitleConfig { Content = SubContent.Both },
                subNotes, mainLine: isCustomDrawOnly)
            : null;

        // 两股流各自常驻一面（back/front 渲染互不污染；单流内保持引擎跨帧语义）
        var (ow, oh, scale) = OutputDims(req.Project, plan);
        var backTransparent = isCustomDrawOnly ? req.Spec.Transparent : (!layered && req.Spec.Transparent);
        var backBmp = new SKBitmap(StageInfo(ow, oh, transparent: backTransparent));
        var backSurf = SKSurface.Create(backBmp.Info, backBmp.GetPixels(), backBmp.RowBytes);
        var backRend = isCustomDrawOnly ? null : new Renderer();

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
                var t = t0 + i / (double)fps * speed; // 与视频同口径（选段起点 + 变速）
                if (isCustomDrawOnly)
                {
                    backSurf.Canvas.SetMatrix(SKMatrix.Identity);
                    backSurf.Canvas.Clear(req.Spec.Transparent ? SKColors.Transparent : SKColors.Black);
                    subLayer!.Render(backSurf.Canvas, t, ow, oh);
                    zip.AddFrame(null, i + 1, backBmp);
                }
                else if (layered)
                {
                    lock (EngineRenderGate.Lock)
                        backRend!.Frame(backSurf.Canvas, plan, t, new RenderOpts { Scale = scale, Layer = "back", NoHud = true });
                    zip.AddFrame("back", i + 1, backBmp);
                    lock (EngineRenderGate.Lock)
                        frontRend!.Frame(frontSurf!.Canvas, plan, t,
                            new RenderOpts { Scale = scale, Layer = "front", NoHud = true, Transparent = true });
                    zip.AddFrame("front", i + 1, frontBmp!);
                }
                else
                {
                    lock (EngineRenderGate.Lock)
                        backRend!.Frame(backSurf.Canvas, plan, t,
                            new RenderOpts { Scale = scale, Transparent = req.Spec.Transparent, NoHud = req.Spec.NoHud });
                    if (subLayer != null)
                    {
                        backSurf.Canvas.SetMatrix(SKMatrix.Identity);
                        subLayer.Render(backSurf.Canvas, t, ow, oh);
                    }
                    zip.AddFrame(null, i + 1, backBmp);
                }
                if ((i & 3) == 0) progress?.Report((i / (double)frames * 0.98, L.F("Export.Stage.WriteFrameFmt", i + 1, frames)));
            }
        }
        finally
        {
            // 释放顺序：renderer → surface → bitmap
            backRend?.Dispose();
            backSurf.Dispose();
            backBmp.Dispose();
            if (frontRend != null) frontRend.Dispose();
            if (frontSurf != null) frontSurf.Dispose();
            if (frontBmp != null) frontBmp.Dispose();
        }

        progress?.Report((1.0, L.T("Export.Stage.Done")));
    }

    // ---------------- 音频（WAV） ----------------

    private static void ExportWav(ExportRequest req, IProgress<(double Pct, string Stage)>? progress)
    {
        if (req.AudioPath == null || !File.Exists(req.AudioPath))
            throw new InvalidOperationException(L.T("Export.Err.NoAudio"));

        progress?.Report((0, L.T("Export.Stage.DecodeAudio")));
        var (t0, _, outDur, speed) = SpanOf(req);
        var ap = AudioPrep.Prepare(req.AudioPath, req.Project.AudioFx, outDur, t0, speed)
                 ?? throw new InvalidOperationException(L.T("Export.Err.AudioDecodeFailed"));
        WavWriter.Write(ap, req.OutputPath, outDur);
        progress?.Report((1.0, L.T("Export.Stage.Done")));
    }

    /// <summary>WavToo：非 MP4 导出勾了歌曲时，另存同名 _audio.wav（与内嵌音轨同一份采样）。</summary>
    public static void ExportSideWav(ExportRequest req)
    {
        if (req.AudioPath == null || !File.Exists(req.AudioPath)) return;
        // 选段后缀插在 _audio 之前（对位 export.ts 的 baseName() + rangeSuffix() + '_audio.wav'）
        var suffix = Ranges.RangeSuffix(req.Project.ExportRange, req.Plan.Cuts.Count);
        var wavPath = Path.ChangeExtension(req.OutputPath, null) + suffix + "_audio.wav";
        var (t0, _, outDur, speed) = SpanOf(req);
        var ap = AudioPrep.Prepare(req.AudioPath, req.Project.AudioFx, outDur, t0, speed);
        if (ap != null)
        {
            WavWriter.Write(ap, wavPath, outDur);
            Log.Info("[Export] 另存 WAV：" + wavPath);
        }
    }

    /// <summary>
    /// 解析导出字幕源：优先使用请求自带的 KRC 逐字数据；若未附带 KRC（如网易云 LRC、手动粘贴或重启恢复工程），
    /// 则自动从 plan.Lines 提炼基础行与注音（保证只要工程歌词带 |翻译 导出也能正确烤入字幕）。
    /// 合成走 SubtitleLayer.FromPlan——与预览叠加、Spout 字幕通道共用同一份实现。
    /// </summary>
    private static (List<KrcLine>? Lines, IReadOnlyList<string>? Notes) ResolveSubtitleSource(ExportRequest req, Plan plan)
    {
        var lines = req.SubtitleLines;
        if (lines is { Count: > 0 })
        {
            return (lines, SubtitleLayer.NotesFromPlan(lines, plan));
        }

        var synth = SubtitleLayer.FromPlan(plan);
        return synth == null ? (null, null) : (synth.Value.Lines, synth.Value.Notes);
    }
}
