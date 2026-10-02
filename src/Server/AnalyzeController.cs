using System.IO;
using System.Text;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using Newtonsoft.Json;

namespace JizuraLive.Server;

/// <summary>
///     POST /api/analyze —— 音频节拍分析。
///     请求体：[int32 sampleRate][int32 sampleCount][float32×count 单声道 PCM]。
///     前端 decode 后把单声道 22kHz 采样发过来，数值计算在 C# 完成，
///     浏览器主线程零重活（不再有载入歌曲时的 UI 卡顿）。
/// </summary>
internal sealed class AnalyzeController : WebApiController
{
    /// <summary>
    ///     POST /api/analyze/file —— 整文件分析（原始 mp3/flac/m4a/wav 字节直传）。
    ///     C# 端 NAudio 解码 + 重采样 + 分析，前端不再做 OfflineAudioContext 重采样
    ///     和 40MB 单声道 PCM 的拷贝/上传（载入歌曲时的主线程冻结源）。
    /// </summary>
    [Route(HttpVerbs.Post, "/api/analyze/file")]
    public async Task AnalyzeFile()
    {
        var len = Request.ContentLength64;
        if (len < 128 || len > 512 * 1024 * 1024)
        {
            Response.StatusCode = 400;
            await HttpContext.SendStringAsync("{\"ok\":false,\"error\":\"bad file payload\"}",
                "application/json; charset=utf-8", Encoding.UTF8);
            return;
        }

        // MF 按扩展名辅助识别格式：用原始文件名扩展写临时文件
        var ext = ".mp3";
        try
        {
            var name = Uri.UnescapeDataString(Request.Headers["X-Filename"] ?? "");
            var e2 = Path.GetExtension(name);
            if (e2.Length is >= 2 and <= 6 && e2.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) ext = e2;
        }
        catch
        {
            /* 保底 .mp3 */
        }

        var tmp = Path.Combine(Path.GetTempPath(), "jizura-analyze-" + Guid.NewGuid().ToString("N") + ext);
        try
        {
            // 请求体流式直写临时文件：一次 new byte[512MB] 的 LOH 峰值
            // 加上落盘拷贝，最坏 ≈1GB/请求；流式后内存恒定为缓冲区大小
            await using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, useAsync: true))
            {
                await Request.InputStream.CopyToAsync(fs, 81920);
            }

            var mono = AudioAnalyzer.DecodeMonoAnalyzeRate(tmp, out var duration);
            var result = AudioAnalyzer.Analyze(mono, AudioAnalyzer.AnalyzeRate, duration);

            await HttpContext.SendStringAsync(
                JsonConvert.SerializeObject(new
                {
                    ok = true,
                    bpm = result.Bpm,
                    beats = result.Beats,
                    energy = result.Energy,
                    energyRate = result.EnergyRate,
                    peaks = result.Peaks,
                    pitch = result.Pitch,
                    pitchRate = result.PitchRate
                }),
                "application/json; charset=utf-8", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Response.StatusCode = 502;
            var msg = JsonConvert.SerializeObject(new { ok = false, error = "解码失败：" + ex.Message });
            await HttpContext.SendStringAsync(msg, "application/json; charset=utf-8", Encoding.UTF8);
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch
            {
            }
        }
    }

    [Route(HttpVerbs.Post, "/api/analyze")]
    public async Task Analyze()
    {
        // 按声明长度一次读满，免去 MemoryStream+ToArray 的双重复制（长曲 payload 20MB+）
        var len = Request.ContentLength64;
        if (len < 12 || len > 512 * 1024 * 1024)
        {
            Response.StatusCode = 400;
            await HttpContext.SendStringAsync("{\"ok\":false,\"error\":\"bad analyze payload\"}",
                "application/json; charset=utf-8", Encoding.UTF8);
            return;
        }

        var body = new byte[(int)len];
        await Request.InputStream.ReadExactlyAsync(body);

        if (!AudioAnalyzer.TryParseRequest(body, out var mono, out var sampleRate))
        {
            Response.StatusCode = 400;
            await HttpContext.SendStringAsync("{\"ok\":false,\"error\":\"bad analyze payload\"}",
                "application/json; charset=utf-8", Encoding.UTF8);
            return;
        }

        var duration = mono.Length / (double)sampleRate;
        var result = AudioAnalyzer.Analyze(mono, sampleRate, duration);

        await HttpContext.SendStringAsync(
            JsonConvert.SerializeObject(new
            {
                ok = true,
                bpm = result.Bpm,
                beats = result.Beats,
                energy = result.Energy,
                energyRate = result.EnergyRate,
                peaks = result.Peaks,
                pitch = result.Pitch,
                pitchRate = result.PitchRate
            }),
            "application/json; charset=utf-8", Encoding.UTF8);
    }
}