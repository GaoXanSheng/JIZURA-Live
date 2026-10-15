using System.IO;
using JizuraLive.Engine;
using NAudio.Wave;

namespace JizuraLive.App.PvStage.Timeline;

/// <summary>后台线程用的音频 → 多级 min/max 波形装载。</summary>
public static class WaveformLoader
{
    private const int ChunkFloats = 65536;

    /// <summary>
    /// 分块解码音频并建多级 min/max 波形。内存 O(一块 + 输出)：整曲 PCM 不驻留，时长不再有上限。
    /// 只开解码器（AudioFileReader），不碰播放设备；失败抛带格式/路径原因的异常，由调用方显示。
    /// </summary>
    public static WaveformPeaks Load(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("音频路径为空", nameof(path));

        using var reader = new AudioFileReader(path);
        var format = reader.WaveFormat;
        if (format.Channels <= 0 || format.SampleRate <= 0)
            throw new InvalidDataException($"音频格式不可装载：{format.SampleRate}Hz/{format.Channels}ch（{path}）");

        var builder = WaveformPeaks.CreateBuilder(format.Channels);
        var samples = (ISampleProvider)reader; // float 读取只在 ISampleProvider 上（WaveStream.Read 是 byte[]）
        var chunk = new float[ChunkFloats];
        long total = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            int read = samples.Read(chunk);
            if (read <= 0) break;
            builder.Feed(chunk.AsSpan(0, read));
            total += read;
        }
        if (total <= 0) throw new InvalidDataException("音频没有可解码的采样：" + path);

        return builder.Finish();
    }
}
