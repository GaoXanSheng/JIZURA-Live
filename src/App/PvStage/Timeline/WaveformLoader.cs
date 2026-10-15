using System.IO;
using JizuraLive.Engine;
using NAudio.Wave;

namespace JizuraLive.App.PvStage.Timeline;

/// <summary>后台线程用的音频 → 多级 min/max 波形装载。</summary>
public static class WaveformLoader
{
    private const int ChunkFloats = 65536;

    /// <summary>
    /// 解码整段音频为 float interleaved 后 <see cref="WaveformPeaks.Build"/>。
    /// 只开解码器（AudioFileReader），不碰播放设备；失败抛异常。
    /// </summary>
    public static WaveformPeaks Load(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path)) throw new ArgumentException("音频路径为空", nameof(path));

        using var reader = new AudioFileReader(path);
        var format = reader.WaveFormat;
        if (format.Channels <= 0) throw new InvalidDataException("音频声道数为 0");

        long frames = (long)Math.Ceiling(reader.TotalTime.TotalSeconds * format.SampleRate);
        if (frames > int.MaxValue / format.Channels)
            throw new InvalidDataException($"音频过长：{frames} 帧 × {format.Channels} 声道超出单次解码上限");

        var data = new float[(int)(frames * format.Channels)];
        var samples = (ISampleProvider)reader; // float 读取只在 ISampleProvider 上（WaveStream.Read 是 byte[]）
        int total = 0;
        while (total < data.Length)
        {
            ct.ThrowIfCancellationRequested();
            int read = samples.Read(data.AsSpan(total, Math.Min(ChunkFloats, data.Length - total)));
            if (read <= 0) break;
            total += read;
        }
        if (total <= 0) throw new InvalidDataException("音频没有可解码的采样：" + path);

        return WaveformPeaks.Build(data.AsSpan(0, total), format.Channels);
    }
}
