using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;

namespace JizuraLive.Pv;

/// <summary>
/// 离线导出：离屏 D3D 设备 + <see cref="PvPipeline"/> 逐帧 Prepare/Composite 到离屏面 → 读回 → BGRA 转 NV12 → <see cref="MfSink"/> 编码。
/// 画面内容就是场景文档里的图层栈（歌词文本与字幕要调用方自己接提供者，本方法没接）。
/// 失败一律返回可读原因（不抛），与 <c>ProjectStore.Save</c> 同约定。
/// </summary>
public static class PvExporter
{
    /// <summary>
    /// 把场景按 <paramref name="durationSec"/> 的时长逐帧渲染并编码成 MP4。进度按帧汇报，最后一帧报 1.0。
    /// </summary>
    public static string? Export(PvScene scene, EffectGraph effects, double durationSec, int fps, string outPath,
        IProgress<double>? progress)
    {
        if (string.IsNullOrWhiteSpace(outPath)) return "导出路径为空";
        if (Validate(scene, effects, durationSec, fps, out _) is { } reason) return reason;

        try
        {
            using var sink = MfSink.Open(outPath, scene.Width, scene.Height, fps);
            var fault = RenderFrames(scene, effects, durationSec, fps, progress, sink.WriteFrame);
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
        IProgress<double>? progress, Action<byte[]> writeFrame)
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
            using (var pipe = new PvPipeline(gpu, scene, effects))
            using (var target = OffscreenTarget.Create(gpu, pipe.D2DContext, width, height))
            {
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
