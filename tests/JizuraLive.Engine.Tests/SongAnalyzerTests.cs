using System.IO;
using JizuraLive.Engine;
using JizuraLive.Engine.Music;
using NAudio.Wave;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// <see cref="SongAnalyzer"/> 的整曲端到端：合成音轨（PCM 与落盘 wav 两条入口）进，BPM / 段落 / 标签出。
/// </summary>
public class SongAnalyzerTests
{
    private const int Rate = 44100;

    /// <summary>A-B-A-B 的 60 秒合成音轨：B 段（15–30s / 45–60s）换和弦且更响。</summary>
    [Fact]
    public void RepeatedLoudBlocksAreLabelledChorus()
    {
        const double seconds = 60.0;
        const double bpm = 120.0;
        var pcm = RenderTrack(bpm, seconds,
        [
            (0.0, 15.0, 261.63, 0.05),
            (15.0, 30.0, 369.99, 0.15),
            (30.0, 45.0, 261.63, 0.05),
            (45.0, 60.0, 369.99, 0.15),
        ]);

        var map = SongAnalyzer.Analyze(pcm, 1, Rate);

        Assert.True(Math.Abs(map.Bpm - bpm) < 3.0, $"BPM {map.Bpm} 偏离 {bpm}");
        Assert.True(map.Sections.Count >= 3, $"段落数 {map.Sections.Count} 少于 3");

        var chorus = map.Sections.Where(s => s.Kind == "chorus").ToList();
        Assert.NotEmpty(chorus);

        // 同一标签的重复段才算副歌：chorus 段落必须共用一个 Label，且出现 ≥2 次
        Assert.Single(chorus.Select(s => s.Label).Distinct());
        Assert.True(map.Sections.Count(s => s.Label == chorus[0].Label) >= 2,
            $"副歌标签 {chorus[0].Label} 只出现 {map.Sections.Count(s => s.Label == chorus[0].Label)} 次");

        // 副歌是能量最高的重复段：两个响块都落在副歌上，安静块不是
        foreach (var s in chorus)
        {
            var center = 0.5 * (s.Start + s.End);
            var inLoudBlock = (center > 11.0 && center < 34.0) || (center > 41.0 && center < 60.0);
            Assert.True(inLoudBlock, $"副歌段 {s.Start:F2}-{s.End:F2}s 不在响块内");
        }

        var chorusEnergy = chorus.Average(s => s.Energy);
        var otherEnergy = map.Sections.Where(s => s.Kind != "chorus").Select(s => s.Energy).DefaultIfEmpty(0).Average();
        Assert.True(chorusEnergy > otherEnergy, $"副歌平均能量 {chorusEnergy:F3} 未高于其余段 {otherEnergy:F3}");

        Assert.Equal(0.0, map.Sections[0].Start);
        Assert.Equal(seconds, map.Sections[^1].End, 6);
    }

    /// <summary>文件入口：解码后的 wav 与"内存里的同一份 PCM"给出同一份结果。</summary>
    [Fact]
    public void AnalyzeFileMatchesInMemoryAnalysis()
    {
        const double seconds = 12.0;
        const double bpm = 128.0;
        var pcm = RenderTrack(bpm, seconds, [(0.0, seconds, 261.63, 0.15)]);
        var path = Path.Combine(Path.GetTempPath(), $"jizura-song-{Guid.NewGuid():N}.wav");
        try
        {
            WriteWav(path, pcm);

            var fromFile = SongAnalyzer.AnalyzeFile(path);
            var inMemory = SongAnalyzer.Analyze(pcm, 1, Rate);

            Assert.Equal(inMemory.Bpm, fromFile.Bpm, 1);
            Assert.Equal(inMemory.Beats.Count, fromFile.Beats.Count);
            Assert.Equal(inMemory.Downbeats.Count, fromFile.Downbeats.Count);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void AnalyzeFileRejectsEmptyPathAndHonoursCancellation()
    {
        Assert.Throws<ArgumentException>(() => SongAnalyzer.AnalyzeFile(""));

        var pcm = RenderTrack(120.0, 12.0, [(0.0, 12.0, 261.63, 0.15)]);
        var path = Path.Combine(Path.GetTempPath(), $"jizura-song-{Guid.NewGuid():N}.wav");
        try
        {
            WriteWav(path, pcm);
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => SongAnalyzer.AnalyzeFile(path, cts.Token));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static void WriteWav(string path, float[] mono)
    {
        var format = new WaveFormat(Rate, 16, 1);
        using var writer = new WaveFileWriter(path, format);
        foreach (var sample in mono) writer.WriteSample(sample);   // 16bit 格式下 WriteSample 自带换算
    }

    /// <summary>合成音轨（峰值归一到 0.8，16bit 落盘不削顶）：和弦块给 chroma / 段落能量，每拍 kick、每 4 拍加重给出 onset 与下拍。</summary>
    private static float[] RenderTrack(double bpm, double seconds, IReadOnlyList<(double From, double To, double RootHz, double Gain)> blocks)
    {
        var frames = (int)Math.Round(seconds * Rate);
        var pcm = new float[frames];

        for (var n = 0; n < frames; n++)
        {
            var t = (double)n / Rate;
            double v = 0;
            foreach (var (from, to, root, gain) in blocks)
            {
                if (t < from || t >= to) continue;
                v += gain * (Math.Sin(2 * Math.PI * root * t)
                             + 0.6 * Math.Sin(2 * Math.PI * root * 1.5 * t)
                             + 0.4 * Math.Sin(2 * Math.PI * root * 2.0 * t));
            }
            pcm[n] = (float)v;
        }

        var rng = new Random(7);
        var beat = 60.0 / bpm;
        for (var k = 0; ; k++)
        {
            var start = k * beat;
            if (start >= seconds) break;

            var amp = k % 4 == 0 ? 0.9 : 0.5;
            var offset = (int)Math.Round(start * Rate);
            var len = (int)Math.Round(0.12 * Rate);
            for (var i = 0; i < len && offset + i < frames; i++)
            {
                var tt = (double)i / Rate;
                var body = Math.Exp(-tt / 0.025) * Math.Sin(2 * Math.PI * 55 * tt);
                var click = (rng.NextDouble() * 2 - 1) * Math.Exp(-tt / 0.006) * 0.6;
                pcm[offset + i] += (float)(amp * (body + click));
            }
        }

        var peak = 0f;
        foreach (var v in pcm) peak = Math.Max(peak, Math.Abs(v));
        if (peak > 0)
            for (var n = 0; n < pcm.Length; n++) pcm[n] *= 0.8f / peak;

        return pcm;
    }
}
