using JizuraLive.Engine;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using JizuraLive.Render.Text;

namespace JizuraLive.Pv;

/// <summary>
/// 离线导出：离屏 D3D 设备 + <see cref="PvPipeline"/> 逐帧 Prepare/Composite 到离屏面 → 读回 → BGRA 转 NV12 → <see cref="MfSink"/> 编码。
/// 画面内容就是场景文档里的图层栈；歌词、字幕与镜头动画由 <see cref="PvExportInputs"/> 按与舞台同一条帧状态链喂进来（不传 = 该项不参与）。
/// 失败一律返回可读原因（不抛），与 <c>ProjectStore.Save</c> 同约定。
/// </summary>
public static class PvExporter
{
    /// <summary>
    /// 把场景按 <paramref name="durationSec"/> 的时长逐帧渲染并编码成 MP4。进度按帧汇报，最后一帧报 1.0。
    /// </summary>
    public static string? Export(PvScene scene, EffectGraph effects, double durationSec, int fps, string outPath,
        IProgress<double>? progress, PvExportInputs? inputs = null)
    {
        if (string.IsNullOrWhiteSpace(outPath)) return "导出路径为空";
        if (Validate(scene, effects, durationSec, fps, out _) is { } reason) return reason;

        try
        {
            using var sink = MfSink.Open(outPath, scene.Width, scene.Height, fps);
            var fault = RenderFrames(scene, effects, durationSec, fps, progress, sink.WriteFrame, inputs);
            // 中途失败也照样收尾：留一个能播的半成品，好过留一个没有索引的坏文件
            sink.Close();
            return fault;
        }
        catch (Exception ex)
        {
            return "导出失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 逐帧渲染，把每帧 NV12 交给 <paramref name="writeFrame"/>。编码与渲染在这里解耦：
    /// 真导出传 <see cref="MfSink.WriteFrame"/>，测试传一个只记数的假写入器（真编码不进单测）。
    /// 同一个数组每帧复用，<paramref name="writeFrame"/> 必须在返回前把数据消化掉。
    /// </summary>
    public static string? RenderFrames(PvScene scene, EffectGraph effects, double durationSec, int fps,
        IProgress<double>? progress, Action<byte[]> writeFrame, PvExportInputs? inputs = null)
    {
        if (Validate(scene, effects, durationSec, fps, out var frameCount) is { } reason) return reason;

        var width = scene.Width;
        var height = scene.Height;
        var nv12 = new byte[MfSink.Nv12Size(width, height)];

        try
        {
            var gpu = D3DDevice.TryCreate(out var gpuError);
            if (gpu is null) return $"建立离屏 D3D 设备失败：{gpuError}";

            using (gpu)
            using (var pipe = new PvPipeline(gpu, scene, effects, SubtitleSourceOf(inputs)))
            using (var target = OffscreenTarget.Create(gpu, pipe.D2DContext, width, height))
            {
                // 与舞台 BuildPipeline 同一条链：曲线 / 频带 / 歌词各发布一次，之后逐帧 Prepare 求值。
                // 不发布的话这一帧链上就只剩文档常量，导出的画面与预览对不上。
                // 镜头/图层曲线与效果参数曲线都由 Prepare 直连求值（后者见 PvPipeline.ApplyEffectCurves）。
                pipe.SetDirector(inputs?.Curves);
                pipe.SetAudioAnalysis(inputs?.Bands);
                // 歌词给冻结的 Clip 序列（SetLyrics 那条路只认导入时的 KrcLine，会丢掉歌词轨上拖过的区间）
                pipe.Renderer.Lyric?.SetClips(inputs?.Clips);
                // 歌词样式是场景文档的一部分（与舞台 ProjectLyricStyle 同一规则）：不设就是 Classic，与预览不一致
                pipe.Renderer.Lyric?.SetStyle(scene.LyricStyle ?? LyricStyle.FromName(scene.LyricStyleName));

                for (var i = 0; i < frameCount; i++)
                {
                    pipe.Prepare(i / (double)fps);
                    pipe.Composite(target.Bitmap, width, height);
                    gpu.Context.Flush();

                    using var frame = target.Readback();
                    BgraToNv12(frame.BgraPixels, width, height, nv12);
                    writeFrame(nv12);
                    progress?.Report((i + 1) / (double)frameCount);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            return "导出失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 字幕源：配置来自设置、歌词行来自工程的冻结 Clip 序列。与舞台同一个 <see cref="IPvSubtitleSource"/> 契约——
    /// 导出这一路只是把它接到离屏管线上。没有配置就整层不建（与"没接字幕源"等价）。
    /// </summary>
    private static IPvSubtitleSource? SubtitleSourceOf(PvExportInputs? inputs) =>
        inputs?.Subtitles is { } config ? new ExportSubtitleSource(inputs.Clips, config) : null;

    /// <summary>一次导出内歌词与配置都不变，所以版本号固定（提供者只在版本或配置变化时重建字幕面）。</summary>
    private sealed class ExportSubtitleSource(IReadOnlyList<LyricClip>? clips, SubtitleConfig config)
        : IPvSubtitleSource
    {
        /// <summary>契约的 Lines 收 KrcLine：按区间原样换算，不做任何时长修正（字幕只认文本与区间）。</summary>
        public IReadOnlyList<KrcLine>? Lines { get; } = LinesOf(clips);
        public SubtitleConfig? Config { get; } = config;
        public long Version => 1;

        private static IReadOnlyList<KrcLine>? LinesOf(IReadOnlyList<LyricClip>? clips)
        {
            if (clips is null) return null;

            var lines = new List<KrcLine>(clips.Count);
            foreach (var clip in clips)
                lines.Add(new KrcLine(clip.StartMs, clip.EndMs - clip.StartMs, clip.Text));
            return lines;
        }
    }

    /// <summary>
    /// BGRA（GPU 回读，逐行自上而下）→ NV12：Y 平面 + 交错 UV，BT.709 有限范围。
    /// <paramref name="nv12"/> 必须正好有 <see cref="MfSink.Nv12Size"/> 那么大。
    /// </summary>
    public static void BgraToNv12(byte[] bgra, int width, int height, byte[] nv12)
    {
        for (var y = 0; y < height; y++)
        {
            var src = y * width * 4;
            var dst = y * width;
            for (var x = 0; x < width; x++, src += 4, dst++)
            {
                int b = bgra[src], g = bgra[src + 1], r = bgra[src + 2];
                nv12[dst] = (byte)(16 + ((47 * r + 157 * g + 16 * b) >> 8));
            }
        }

        // 色度取 2×2 平均：直接取左上像素会让锐利色边（字幕/歌词描边）起锯齿
        var chromaBase = width * height;
        for (var y = 0; y < height; y += 2)
        {
            var top = y * width * 4;
            var bottom = top + width * 4;
            var dst = chromaBase + y / 2 * width;
            for (var x = 0; x < width; x += 2, dst += 2)
            {
                var i = top + x * 4;
                var j = bottom + x * 4;
                var b = (bgra[i] + bgra[i + 4] + bgra[j] + bgra[j + 4] + 2) >> 2;
                var g = (bgra[i + 1] + bgra[i + 5] + bgra[j + 1] + bgra[j + 5] + 2) >> 2;
                var r = (bgra[i + 2] + bgra[i + 6] + bgra[j + 2] + bgra[j + 6] + 2) >> 2;
                nv12[dst] = (byte)(128 + ((-26 * r - 87 * g + 112 * b) >> 8));
                nv12[dst + 1] = (byte)(128 + ((112 * r - 102 * g - 10 * b) >> 8));
            }
        }
    }

    /// <summary>校验导出参数并算出帧数。返回非空即可读原因；帧数 = round(时长 × 帧率)，不足一帧也算一帧。</summary>
    private static string? Validate(PvScene? scene, EffectGraph? effects, double durationSec, int fps, out int frameCount)
    {
        frameCount = 0;
        if (scene is null) return "导出缺少场景文档";
        if (effects is null) return "导出缺少效果图";
        if (!double.IsFinite(durationSec) || durationSec <= 0) return $"导出时长必须是正数（当前 {durationSec}）";
        if (fps is < 1 or > 240) return $"帧率必须在 1..240 之间（当前 {fps}）";

        // NV12 的色度按 2×2 采样，奇数宽高凑不出这个格式
        if (scene.Width <= 0 || scene.Height <= 0 || (scene.Width & 1) != 0 || (scene.Height & 1) != 0)
            return $"画布宽高必须是正偶数（当前 {scene.Width}×{scene.Height}）：NV12 色度按 2×2 采样";

        var frames = Math.Round(durationSec * fps);
        if (frames > int.MaxValue) return $"导出帧数超出上限（{durationSec} 秒 × {fps} fps）";
        frameCount = Math.Max(1, (int)frames);
        return null;
    }
}

/// <summary>
/// 导出要消费的帧状态链（与舞台同一批来源）。每个字段为 null = 该项不参与本次导出。
///
/// <see cref="Curves"/> 交给管线的必须是**此后不再改**的对象（调用方给快照副本）：
/// 导出线程逐帧读它，与舞台的"发布不可变快照"是同一条约定。
/// </summary>
public sealed record PvExportInputs(
    DirectorEvaluator? Curves = null,
    AudioBandSeries? Bands = null,
    IReadOnlyList<LyricClip>? Clips = null,
    SubtitleConfig? Subtitles = null);
