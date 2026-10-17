using System.IO;
using JizuraLive.App.PvStage;
using NAudio.Wave;

namespace JizuraLive.Engine.Music;

public static class SongAnalyzer
{
    private const int ChunkFloats = 65536;
    private const float PeakMinStrength = 0.5f;
    private const int PitchHop = 512;

    public static BeatMap Analyze(ReadOnlySpan<float> interleaved, int channels, int sampleRate,
        double? manualBpm = null, double manualOffsetSeconds = 0)
    {
        var features = MusicFeatureExtractor.FromInterleaved(interleaved, channels, sampleRate);
        return Build(features, manualBpm, manualOffsetSeconds);
    }

    public static BeatMap AnalyzeFile(string path, CancellationToken ct = default,
        double? manualBpm = null, double manualOffsetSeconds = 0)
    {
        using var reader = AudioDecoding.Open(path);
        var format = reader.WaveFormat;
        if (format.Channels <= 0 || format.SampleRate <= 0)
            throw new InvalidDataException(
                $"音频格式不可分析：{format.SampleRate}Hz/{format.Channels}ch（{path}）");

        var interleaved = DecodeAll(reader, ct);
        return Analyze(interleaved, format.Channels, format.SampleRate, manualBpm, manualOffsetSeconds);
    }

    public static BeatMap BuildManualGrid(double bpm, double offsetSeconds, double duration)
    {
        var map = new BeatMap();
        if (!(bpm > 0)) return map;

        var period = 60.0 / bpm;
        var beats = new List<double>();
        for (var t = offsetSeconds; t < duration + 0.01; t += period)
            if (t >= 0) beats.Add(Math.Round(t, 4, MidpointRounding.AwayFromZero));

        var downbeats = new List<double>();
        for (var i = 0; i < beats.Count; i += 4) downbeats.Add(beats[i]);

        map.Bpm = Math.Round(bpm, 2);
        map.Beats = beats;
        map.Downbeats = downbeats;
        return map;
    }

    private static BeatMap Build(MusicFeatures features, double? manualBpm, double manualOffset)
    {
        BeatMap map;
        if (manualBpm is { } bpm && bpm > 0)
        {
            map = BuildManualGrid(bpm, manualOffset, features.Duration);
        }
        else
        {
            var beat = BeatTracker.Track(features);
            var peaks = new List<double>();
            for (var i = 0; i < beat.Onsets.Count && i < beat.OnsetStrengths.Count; i++)
                if (beat.OnsetStrengths[i] >= PeakMinStrength) peaks.Add(Math.Round(beat.Onsets[i], 6));

            map = new BeatMap
            {
                Bpm = Math.Round(beat.Bpm, 2),
                Beats = beat.Beats,
                Downbeats = beat.Downbeats,
                Peaks = peaks,
            };
        }

        map.Sections = StructureAnalyzer.Analyze(features, map.Beats, map.Downbeats);
        var (pitch, _) = PitchTracker.Extract(features.Mono, features.SampleRate, features.Energy, PitchHop);
        map.Pitch = pitch;
        map.PitchRate = (float)(PitchHop / (double)features.SampleRate);
        return map;
    }

    private static float[] DecodeAll(WaveStream reader, CancellationToken ct)
    {
        var format = reader.WaveFormat;
        var totalSamples = format.Channels > 0 ? (long)(reader.Length / 4) : 0;
        var buffer = totalSamples > 0 ? new float[Math.Min(totalSamples, int.MaxValue)] : new float[ChunkFloats];
        var samples = (ISampleProvider)reader;
        var chunk = new float[ChunkFloats];
        var length = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var read = samples.Read(chunk.AsSpan());
            if (read <= 0) break;

            if (length + read > buffer.Length)
                Array.Resize(ref buffer,
                    (int)Math.Min(int.MaxValue, Math.Max(buffer.Length * 2L, (long)length + read)));
            chunk.AsSpan(0, read).CopyTo(buffer.AsSpan(length));
            length += read;
        }

        if (length != buffer.Length) Array.Resize(ref buffer, length);
        return buffer;
    }
}
