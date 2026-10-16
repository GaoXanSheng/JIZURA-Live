using System.IO;
using System.Text;
using JizuraLive.Engine;
using JizuraLive.Engine.Music;
using JizuraLive.Pv;
using JizuraLive.Render.Effects;
using JizuraLive.Vts;
using NAudio.Wave;
using Newtonsoft.Json;
using Xunit;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 真歌导演探针（M6b）：tools/real-song.txt 里的真歌整曲分析 → 合成歌词 → 一键成片（dynamic / 0.8 / Seed 1），
/// 断言生成物自洽——出场区间合法、相机与效果曲线全曲采样有限且参数在目录范围内、cue 升序且 role:chorus 开关成对、
/// 逐段歌词样式盖住每句歌词起点、同种子两次生成逐字节相同而换种子不同；JZ_DIRECTOR_EXPORT=1 时再真导出一段带音轨的 MP4 读回校验时长。
/// 文件 / 音频不存在的机器上直接 return 通过——探针的结论是"在这台机器上这条链跑得通且数字合理"。
/// </summary>
public class RealSongDirectorProbeTests(ITestOutputHelper output)
{
    private const int Seed = 1;

    [Fact]
    public void RealSong_GeneratedProjectIsSane()
    {
        if (Real() is not { } song) return;

        output.WriteLine($"文件：{song.Audio}");
        output.WriteLine($"BPM {song.Map.Bpm:F2} · 拍 {song.Map.Beats.Count} · 下拍 {song.Map.Downbeats.Count} · "
                         + $"段落 {song.Map.Sections.Count} · 时长 {song.Duration:F1}s");
        output.WriteLine($"合成歌词 {LrcParser.ParseClips(song.Lrc).Clips.Count} 句，落点 {string.Join(", ", Drops(song.Map).Select(d => $"{d:F1}s"))}");

        var project = Generate(song, Seed);
        output.WriteLine($"生成：曲线 {project.Curves.Count} 条 · 效果 {project.Effects.Nodes.Count} 个 · "
                         + $"出场 {project.Effects.Nodes.Sum(n => n.Windows.Count)} 段 · cue {project.VtsCues.Cues.Count} 条 · "
                         + $"歌词样式区间 {project.LyricStyles.Count} 段 · 粒子层 {project.Scene.Layers.Count(l => l.Particles is not null)} 个");

        AssertWindowsLegal(project, song.Duration);
        AssertCurvesAndParamsInRange(project, song.Duration);
        AssertCuesAscendingAndChorusPaired(project, song.Duration);
        AssertLyricStylesCoverLines(project, song.Lrc);

        // 确定性的判据是整份工程（含图层与 cue）的序列化：同种子逐字节相同，换种子至少粒子种子变了
        Assert.Equal(Json(project), Json(Generate(song, Seed)));
        Assert.NotEqual(Json(project), Json(Generate(song, Seed + 1)));
    }

    // ---------------------------------------------------------------- 断言

    private void AssertWindowsLegal(DirectorProject project, double duration)
    {
        int windows = 0, pastEnd = 0;
        foreach (var node in project.Effects.Nodes)
        foreach (var w in node.Windows)
        {
            Assert.True(w.IsValid, $"{node.InstanceId} 出场区间非法：{w.Start:F3}–{w.End:F3}"
                                   + $"（淡入 {w.FadeIn:F3} 淡出 {w.FadeOut:F3}）");
            Assert.True(w.Start <= duration, $"{node.InstanceId} 区间起点 {w.Start:F3}s 在曲尾 {duration:F3}s 之后，永远不可达");
            windows++;
            if (w.End > duration + 1e-6) pastEnd++;
        }

        Assert.True(windows > 0, "一个出场区间都没有：整套点缀都成了全程常驻");
        Assert.True(pastEnd == 0, $"{pastEnd} 段出场区间的尾端越过曲尾 {duration:F3}s（那段永远播不全）");
        output.WriteLine($"出场区间 {windows} 段，尾端越过曲尾 {pastEnd} 段");
    }

    /// <summary>
    /// 曲线与参数的范围：文档参数先按目录 schema 查（曲线求值那一步会夹到同一范围，夹取前的越界只有这里看得见），
    /// 再按渲染线程每帧同一条路径（<see cref="PvPipeline.ApplyEffectCurves"/>）在 0..时长上每 50ms 采一遍。
    /// </summary>
    private static void AssertCurvesAndParamsInRange(DirectorProject project, double duration)
    {
        foreach (var node in project.Effects.Nodes)
        {
            var def = EffectCatalog.Get(node.DefId);
            Assert.True(def is not null, $"效果目录里没有 {node.DefId}");
            foreach (var p in def!.Params)
            {
                if (p.Kind != EffectParamKind.Number) continue;
                var v = node.Value(p);
                Assert.True(double.IsFinite(v) && v >= p.Min - 1e-6 && v <= p.Max + 1e-6,
                    $"{node.InstanceId}.{p.Key} = {v} 超出目录范围 [{p.Min}, {p.Max}]");
            }
        }

        var eval = DirectorEvaluator.FromSaves(project.Curves);
        var graph = project.Effects.Clone();
        var camera = new (string Name, ParamAddress Addr)[]
        {
            ("camera.scale", new ParamAddress(ParamAddressKind.Camera, "", "scale")),
            ("camera.rotation", new ParamAddress(ParamAddressKind.Camera, "", "rotation")),
            ("camera.offsetX", new ParamAddress(ParamAddressKind.Camera, "", "offsetX")),
            ("camera.offsetY", new ParamAddress(ParamAddressKind.Camera, "", "offsetY")),
        };

        for (double t = 0; t <= duration; t += 0.05)
        {
            foreach (var c in project.Curves)
                Assert.True(double.IsFinite(c.Curve.Evaluate(t)),
                    $"{c.Address}（层 {c.Layer ?? "基础"}）在 {t:F2}s 采到非有限值");
            foreach (var (name, addr) in camera)
                Assert.True(double.IsFinite(eval.Resolve(addr, t, 1.0)), $"{name} 在 {t:F2}s 采到非有限值");

            PvPipeline.ApplyEffectCurves(graph, eval, t);
            foreach (var node in graph.Nodes)
            {
                var def = EffectCatalog.Get(node.DefId);
                if (def is null) continue;
                foreach (var p in def.Params)
                {
                    if (p.Kind != EffectParamKind.Number) continue;
                    var v = node.Value(p);
                    Assert.True(double.IsFinite(v) && v >= p.Min - 1e-6 && v <= p.Max + 1e-6,
                        $"{node.InstanceId}.{p.Key} 在 {t:F2}s 求值成 {v}，超出目录范围 [{p.Min}, {p.Max}]");
                }
            }
        }
    }

    private static void AssertCuesAscendingAndChorusPaired(DirectorProject project, double duration)
    {
        var cues = project.VtsCues.Cues;
        Assert.NotEmpty(cues);
        for (var i = 1; i < cues.Count; i++)
            Assert.True(cues[i].Time >= cues[i - 1].Time, $"cue {i}（{cues[i].Time:F3}s）未升序");
        foreach (var c in cues)
        {
            Assert.InRange(c.Time, 0.0, duration + 1e-6);
            Assert.True(double.IsFinite(c.Value) && double.IsFinite(c.Duration), $"cue @{c.Time:F3}s 的值非有限");
        }

        var chorus = cues.Where(c => c.Kind == VtsCueKind.Expression && c.Target == "role:" + VtsRoles.Chorus).ToList();
        var on = chorus.Where(c => c.Value == 1).ToList();
        var off = chorus.Where(c => c.Value == 0).ToList();
        Assert.True(on.Count > 0, "一个高潮段都没判出来，role:chorus 开关全空");
        Assert.Equal(on.Count, off.Count);
        foreach (var o in on)
            Assert.Contains(off, x => x.Time > o.Time);
    }

    private static void AssertLyricStylesCoverLines(DirectorProject project, string lrc)
    {
        var clips = LrcParser.ParseClips(lrc).Clips;
        Assert.NotEmpty(clips);

        var spans = project.LyricStyles;
        Assert.NotEmpty(spans);
        foreach (var clip in clips)
        {
            double t = clip.StartMs / 1000.0;
            Assert.True(spans.Any(s => t >= s.Start && t < s.End), $"{t:F2}s 的「{clip.Text}」不在任何样式区间里");
            Assert.False(string.IsNullOrEmpty(LyricStyleSpans.Apply([clip], spans)[0].Style),
                $"{t:F2}s 的「{clip.Text}」经 LyricStyleSpans.Apply 没套到样式");
        }
    }

    // ---------------------------------------------------------------- 导出

    [GpuFact]
    public void Export_ProbeClipCarriesAudio()
    {
        if (Environment.GetEnvironmentVariable("JZ_DIRECTOR_EXPORT") != "1") return;
        if (Real() is not { } song) return;

        var outPath = Path.Combine(RepoRoot()!, "tools", "probe-out.mp4");
        var project = Generate(song, Seed);
        // 导到 min(首个落点 + 8s, 75s)：够看到蓄力、落点与落点后的段，又不必整曲真编码（很慢）
        double duration = Math.Min(Drops(song.Map).DefaultIfEmpty(0).First() + 8.0, 75.0);
        var clips = LyricStyleSpans.Apply(LrcParser.ParseClips(song.Lrc).Clips, project.LyricStyles);

        var reason = PvExporter.Export(project.Scene, project.Effects, duration, 24, outPath, null,
            new PvExportInputs(Curves: DirectorEvaluator.FromSaves(project.Curves), Clips: clips, AudioPath: song.Audio));
        Assert.Null(reason);

        var file = new FileInfo(outPath);
        Assert.True(file.Exists, $"导出没落文件：{outPath}");
        output.WriteLine($"导出 {outPath} · {file.Length / 1024}KB · 时长 {duration:F2}s @24fps");
        Assert.True(file.Length > 100 * 1024, $"导出文件只有 {file.Length} 字节，不够 100KB");

        using var reader = new MediaFoundationReader(outPath);
        var readBack = reader.TotalTime.TotalSeconds;
        output.WriteLine($"读回音轨时长 {readBack:F3}s（导出 {duration:F3}s，差 {Math.Abs(readBack - duration):F3}s）");
        Assert.True(Math.Abs(readBack - duration) < 0.3,
            $"读回的 {readBack:F3}s 与导出的 {duration:F3}s 相差 {Math.Abs(readBack - duration):F3}s");
    }

    // ---------------------------------------------------------------- 夹具

    private sealed record SongProbe(string Audio, BeatMap Map, string Lrc, double Duration);

    private static SongProbe? Real()
    {
        var root = RepoRoot();
        if (root is null) return null;

        var probeFile = Path.Combine(root, "tools", "real-song.txt");
        if (!File.Exists(probeFile)) return null;
        var audio = File.ReadAllLines(probeFile).Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        if (audio is null || !File.Exists(audio)) return null;

        var map = SongAnalyzer.AnalyzeFile(audio);
        return new SongProbe(audio, map, SynthLyrics(map), Duration(map));
    }

    private static DirectorProject Generate(SongProbe song, int seed)
    {
        var project = new DirectorProject { LrcText = song.Lrc, AudioPath = song.Audio };
        AutoDirector.Generate(project, song.Map, new AutoOptions { Style = "dynamic", Intensity = 0.8, Seed = seed });
        return project;
    }

    private static string Json(DirectorProject project) => JsonConvert.SerializeObject(project);

    /// <summary>
    /// 合成歌词：真歌没有歌词文件，从 8 秒后第一个下拍起每 2 小节一句「测试歌词第N句」，
    /// 末尾 4 秒内不起新句（LRC 末句兜底 3s，压在曲尾会把 song.Duration 撑过分析时长）。
    /// </summary>
    private static string SynthLyrics(BeatMap map)
    {
        double bar = Bar(map), end = Duration(map);
        var first = map.Downbeats.Where(double.IsFinite).Where(d => d >= 8).OrderBy(d => d).FirstOrDefault();
        if (first <= 0 || bar <= 0 || end <= first) return "";

        var sb = new StringBuilder();
        int n = 1;
        for (double t = first; t <= end - 4.0; t += bar * 2)
            sb.Append('[').Append(TimeTag(t)).Append("]测试歌词第").Append(n++).Append("句\n");
        return sb.ToString();
    }

    private static string TimeTag(double seconds)
    {
        var ms = (long)Math.Round(seconds * 1000);
        return $"{ms / 60000:00}:{ms % 60000 / 1000:00}.{ms % 1000:000}";
    }

    /// <summary>小节长：下拍间距的中位数（与 AutoDirector 的 Song.BarLen 同一口径）。</summary>
    private static double Bar(BeatMap map)
    {
        var diffs = map.Downbeats.Where(double.IsFinite)
            .Zip(map.Downbeats.Skip(1), (a, b) => b - a).Where(d => d > 0).OrderBy(d => d).ToList();
        if (diffs.Count > 0) return diffs[diffs.Count / 2];
        return map.Downbeats.Count > 1 ? map.Downbeats[1] - map.Downbeats[0] : 2.0;
    }

    /// <summary>歌曲时长：段落尾、末拍 + 一拍两者取大（与 AutoDirector.Song.Build 同一口径）。</summary>
    private static double Duration(BeatMap map)
    {
        double end = map.Sections.Where(s => double.IsFinite(s.End)).Select(s => s.End).DefaultIfEmpty(0).Max();
        var beats = map.Beats.Where(double.IsFinite).Where(b => b >= 0).Distinct().OrderBy(b => b).ToList();
        if (beats.Count > 0)
        {
            var diffs = beats.Zip(beats.Skip(1), (a, b) => b - a).Where(d => d > 0).OrderBy(d => d).ToList();
            end = Math.Max(end, beats[^1] + (diffs.Count > 0 ? diffs[diffs.Count / 2] : 0.5));
        }
        return end;
    }

    /// <summary>
    /// 落点：高潮段入口（前一段不是高潮），高潮判定照 <c>AutoDirector.Song.IsHigh</c> 复刻
    /// （合唱标签，或 min-max 归一化后能量 ≥ 0.8 / 无合唱标签时 0.66；前奏尾奏不算）。
    /// </summary>
    private static List<double> Drops(BeatMap map)
    {
        var secs = map.Sections.Where(s => double.IsFinite(s.Start)).OrderBy(s => s.Start).ToList();
        if (secs.Count == 0) return [];
        double lo = secs.Min(s => s.Energy), hi = secs.Max(s => s.Energy);
        bool hasChorus = secs.Any(s => s.Kind == "chorus");

        double Energy(Section s) =>
            double.IsFinite(s.Energy) && hi - lo > 1e-9 ? (s.Energy - lo) / (hi - lo) : 0.5;

        bool High(Section s) => s.Kind is not ("intro" or "outro")
            && (s.Kind == "chorus" || Energy(s) >= (hasChorus ? 0.8 : 0.66));

        var drops = new List<double>();
        for (int i = 0; i < secs.Count; i++)
            if (High(secs[i]) && (i == 0 || !High(secs[i - 1]))) drops.Add(secs[i].Start);
        return drops;
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
