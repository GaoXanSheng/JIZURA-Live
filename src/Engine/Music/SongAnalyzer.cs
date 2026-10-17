using System.IO;
using JizuraLive.App.PvStage;
using NAudio.Wave;

namespace JizuraLive.Engine.Music;

public static class SongAnalyzer
{
    private const int ChunkFloats = 65536;
    private const float PeakMinStrength = 0.5f;

    public static BeatMap Analyze(ReadOnlySpan<float> interleaved, int channels, int sampleRate)
    {
        var features = MusicFeatureExtractor.FromInterleaved(interleaved, channels, sampleRate);
        return Build(features);
    }

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

    private static float[] DecodeAll(WaveStream reader, CancellationToken ct)
    {
        var samples = (ISampleProvider)reader;
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
