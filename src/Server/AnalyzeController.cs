using System.IO;
using System.Text;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using Newtonsoft.Json;

namespace JizuraLive.Server;

// POST /api/analyze，音频节拍分析。请求体 [int32 sampleRate][int32 sampleCount][float32×count 单声道 PCM]，
// 数值计算在 C# 完成，浏览器主线程零重活
internal sealed class AnalyzeController : WebApiController
{
    // POST /api/analyze/file：整文件原始字节直传，C# 端 NAudio 解码+重采样+分析。
    // 免去前端 OfflineAudioContext 重采样和 40MB 单声道 PCM 的拷贝上传（载歌时主线程冻结的源头）
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

        // MediaFoundation 按扩展名认格式，用原始文件名的扩展名建临时文件
        var ext = ".mp3";
        try
        {
            var name = Uri.UnescapeDataString(Request.Headers["X-Filename"] ?? "");
            var e2 = Path.GetExtension(name);
            if (e2.Length is >= 2 and <= 6 && e2.IndexOfAny(Path.GetInvalidFileNameChars()) < 0) ext = e2;
        }
        catch
        {
        }

        var tmp = Path.Combine(Path.GetTempPath(), "jizura-analyze-" + Guid.NewGuid().ToString("N") + ext);
        try
        {
            // 请求体流式直写临时文件：一次 new byte[512MB] 的 LOH 峰值
            // 加落盘拷贝最坏 ≈1GB/请求，流式后内存恒定为缓冲区大小
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
        // 按声明长度一次读满，免掉 MemoryStream+ToArray 的双重复制（长曲 payload 20MB+）
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