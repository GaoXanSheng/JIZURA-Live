using System.IO;
using JizuraLive.Engine.Music;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 真歌探针：仓库根 tools/real-song.txt 里写一行音频绝对路径，本机整曲分析一遍并打印结果。
/// 文件或音频不存在的机器上直接 return 通过——探针的结论是"在这台机器上这条链跑得通且数字合理"。
/// </summary>
public class RealSongProbeTests
{
    private readonly ITestOutputHelper _out;

    public RealSongProbeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void RealSong_AnalysisIsSane()
    {
        var root = RepoRoot();
        var probeFile = root is null ? null : Path.Combine(root, "tools", "real-song.txt");
        if (probeFile is null || !File.Exists(probeFile)) return;

        var path = File.ReadAllText(probeFile).Trim();
        if (path.Length == 0 || !File.Exists(path)) return;

        var map = SongAnalyzer.AnalyzeFile(path);

        _out.WriteLine($"文件：{path}");
        _out.WriteLine($"BPM {map.Bpm:F2} · 拍 {map.Beats.Count} · 下拍 {map.Downbeats.Count} · 显著 onset {map.Peaks.Count} · 段落 {map.Sections.Count}");
        foreach (var s in map.Sections)
            _out.WriteLine($"{s.Start,8:F2} - {s.End,8:F2}  {s.Kind,-7} {s.Label}  E={s.Energy:F3}");

        double duration = map.Sections.Count > 0 ? map.Sections[^1].End : 0;
        Assert.True(duration > 1.0, $"分析出的时长 {duration:F2}s 不合理");

        Assert.InRange(map.Bpm, 60.0, 200.0);

        Assert.NotEmpty(map.Beats);
        double expectedBeats = duration * map.Bpm / 60.0;
        double beatError = Math.Abs(map.Beats.Count - expectedBeats) / expectedBeats;
        Assert.True(beatError < 0.15, $"拍数 {map.Beats.Count} 与 时长×BPM/60={expectedBeats:F1} 相差 {beatError:P2}");

        Assert.InRange(map.Sections.Count, 3, 15);

        var chorus = map.Sections.Where(s => s.Kind == "chorus").ToList();
        Assert.NotEmpty(chorus);
        Assert.True(map.Sections.Count(s => s.Label == chorus[0].Label) >= 2,
            $"副歌标签 {chorus[0].Label} 只出现 {map.Sections.Count(s => s.Label == chorus[0].Label)} 次");

        Assert.Equal(0.0, map.Sections[0].Start);
        Assert.Equal(duration, map.Sections[^1].End, 6);
        for (var i = 0; i < map.Sections.Count - 1; i++)
            Assert.Equal(map.Sections[i].End, map.Sections[i + 1].Start, 6);
    }

    /// <summary>仓库根：从测试输出目录往上找 JizuraLive.csproj（探针文件放在仓库根的 tools/ 下）。</summary>
    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "JizuraLive.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
