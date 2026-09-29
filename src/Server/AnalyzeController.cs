using System.IO;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using JizuraLive.Server;

namespace JizuraLive.Server;

/// <summary>
/// POST /api/analyze —— 音频节拍分析。
/// 请求体：[int32 sampleRate][int32 sampleCount][float32×count 单声道 PCM]。
/// 前端 decode 后把单声道 22kHz 采样发过来，数值计算在 C# 完成，
/// 浏览器主线程零重活（不再有载入歌曲时的 UI 卡顿）。
/// </summary>
internal sealed class AnalyzeController : WebApiController
{
    [Route(HttpVerbs.Post, "/api/analyze")]
    public async Task Analyze()
    {
        using var ms = new MemoryStream();
        await Request.InputStream.CopyToAsync(ms);
        var body = ms.ToArray();

        if (!AudioAnalyzer.TryParseRequest(body, out var mono, out var sampleRate))
        {
            Response.StatusCode = 400;
            await HttpContext.SendStringAsync("{\"ok\":false,\"error\":\"bad analyze payload\"}", "application/json; charset=utf-8", System.Text.Encoding.UTF8);
            return;
        }

        double duration = mono.Length / (double)sampleRate;
        var result = AudioAnalyzer.Analyze(mono, sampleRate, duration);

        await HttpContext.SendStringAsync(
            Newtonsoft.Json.JsonConvert.SerializeObject(new
            {
                ok = true,
                bpm = result.Bpm,
                beats = result.Beats,
                energy = result.Energy,
                energyRate = result.EnergyRate,
                peaks = result.Peaks,
            }),
            "application/json; charset=utf-8", System.Text.Encoding.UTF8);
    }
}
