using System.IO;
using JizuraLive.Engine;
using NAudio.Wave;
using JizuraLive.App.I18n;

namespace JizuraLive.App.PvStage.Timeline;

public static class WaveformLoader
{
    private const int ChunkFloats = 65536;

    public static WaveformPeaks Load(string path, CancellationToken ct = default)
    {
        using var reader = AudioDecoding.Open(path);
        var format = reader.WaveFormat;
        if (format.Channels <= 0 || format.SampleRate <= 0)
            throw new InvalidDataException(L.F("StageUi.TimelineUi.AudioFormatUnloadable", format.SampleRate, format.Channels, path));

        var builder = WaveformPeaks.CreateBuilder(format.Channels);
        var samples = (ISampleProvider)reader;
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
        if (total <= 0) throw new InvalidDataException(L.T("StageUi.TimelineUi.AudioNoDecodableSamples") + path);

        return builder.Finish();
    }
}
