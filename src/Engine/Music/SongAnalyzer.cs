using System.IO;
using JizuraLive.App.PvStage;
using NAudio.Wave;

namespace JizuraLive.Engine.Music;

/// <summary>
/// 整曲音乐分析入口：PCM → 特征 → 拍点 → 段落，产出 <see cref="BeatMap"/>。
/// 文件版走 <see cref="AudioDecoding.Open"/> 解码（分块读、可取消），PCM 进出的一次性版本不碰 IO。
/// </summary>
public static class SongAnalyzer
{
    private const int ChunkFloats = 65536;
    private const float PeakMinStrength = 0.5f;

    public static BeatMap Analyze(ReadOnlySpan<float> interleaved, int channels, int sampleRate)
    {
        var features = MusicFeatureExtractor.FromInterleaved(interleaved, channels, sampleRate);
        return Build(features);
    }

    /// <summary>
    /// 解码整个音频文件后分析。<paramref name="ct"/> 在每个解码块前检查，取消即刻生效。
    /// 失败（IO/解码/格式）抛异常由调用方兜住；空路径抛 <see cref="ArgumentException"/>。
    /// </summary>
    public static BeatMap AnalyzeFile(string path, CancellationToken ct = default)
    {
        using var reader = AudioDecoding.Open(path);
        var format = reader.WaveFormat;
        if (format.Channels <= 0 || format.SampleRate <= 0)
            throw new InvalidDataException(
                $"音频格式不可分析：{format.SampleRate}Hz/{format.Channels}ch（{path}）");

        var interleaved = DecodeAll(reader, ct);
        return Analyze(interleaved, format.Channels, format.SampleRate);
    }

    private static BeatMap Build(MusicFeatures features)
    {
        var beat = BeatTracker.Track(features);
        var peaks = new List<double>();
        for (var i = 0; i < beat.Onsets.Count && i < beat.OnsetStrengths.Count; i++)
            if (beat.OnsetStrengths[i] >= PeakMinStrength) peaks.Add(Math.Round(beat.Onsets[i], 6));

        return new BeatMap
        {
            Bpm = Math.Round(beat.Bpm, 2),
            Beats = beat.Beats,
            Downbeats = beat.Downbeats,
            Sections = StructureAnalyzer.Analyze(features, beat.Beats, beat.Downbeats),
            Peaks = peaks,
        };
    }

    /// <summary>整曲交错样本驻留内存：特征提取需要全曲 chroma 才能算自相似矩阵，不能像频带分析那样流式出窗。</summary>
    private static float[] DecodeAll(WaveStream reader, CancellationToken ct)
    {
        var samples = (ISampleProvider)reader;   // float 读取只挂在 ISampleProvider 上（WaveStream.Read 是 byte[]）
        var chunk = new float[ChunkFloats];
        var buffer = new float[ChunkFloats];
        var length = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = samples.Read(chunk.AsSpan());
            if (read <= 0) break;

            if (length + read > buffer.Length)
                Array.Resize(ref buffer, Math.Max(buffer.Length * 2, length + read));
            chunk.AsSpan(0, read).CopyTo(buffer.AsSpan(length));
            length += read;
        }

        if (length != buffer.Length) Array.Resize(ref buffer, length);
        return buffer;
    }
}
