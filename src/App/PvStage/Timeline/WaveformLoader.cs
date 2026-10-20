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
            throw new InvalidDataException(L.F("音频格式不可装载：{0}Hz/{1}ch（{2}）", format.SampleRate, format.Channels, path));

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
        if (total <= 0) throw new InvalidDataException(L.T("音频没有可解码的采样：") + path);

        return builder.Finish();
    }
}
