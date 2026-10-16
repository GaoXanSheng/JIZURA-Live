using System.IO;
using System.Text;
using JizuraLive.Engine;
using JizuraLive.Engine.Music;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using JizuraLive.Render.Text;
using Xunit;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 一键成片联系表探针：本机真歌（tools/real-song.txt 里的音频路径，JZ_DIRECTOR_IMAGE 给一张底图）整曲分析 → 给工程合成歌词 →
/// 三种风格各生成一次 → 在转场、蓄力、副歌下拍、前奏 / 尾奏、色块转场与漫画分格等关键时刻离屏抽帧，拼成一张 4 列联系表写到临时目录。
/// 只在 JZ_DIRECTOR_SHEET=1 时运行：要真歌、要 GPU、要几十秒，不进常规测试。
/// </summary>
public class DirectorSheetProbeTests(ITestOutputHelper output)
{
    private const int W = 480, H = 270, Cols = 4;

    [Fact]
    public void RenderContactSheets()
    {
        if (Environment.GetEnvironmentVariable("JZ_DIRECTOR_SHEET") != "1") return;

        var probe = FindProbeFile();
        if (probe is null) return;
        var audio = File.ReadAllLines(probe).Select(l => l.Trim()).First(l => l.Length > 0);
        var image = Environment.GetEnvironmentVariable("JZ_DIRECTOR_IMAGE");
        if (!File.Exists(audio)) return;

        var map = SongAnalyzer.AnalyzeFile(audio);
        output.WriteLine($"BPM {map.Bpm:F1}  beats {map.Beats.Count}  downbeats {map.Downbeats.Count}");
        foreach (var s in map.Sections) output.WriteLine($"  {s.Start,7:F2}–{s.End,7:F2}  {s.Kind,-7} {s.Label} e={s.Energy:F2}");

        var lrc = SynthLyrics(map);
        var clips = LrcParser.ParseClips(lrc).Clips;
        output.WriteLine($"合成歌词 {clips.Count} 句" + (clips.Count > 0 ? $"，首句 {clips[0].StartMs / 1000.0:F2}s" : ""));

        foreach (var style in new[] { "none", "soft", "dynamic", "epic" })
        {
            var project = new DirectorProject { LrcText = lrc };
            project.Scene.Width = W;
            project.Scene.Height = H;
            if (image is not null && File.Exists(image))
                project.Scene.Layers.Add(PvPipeline.NewImageLayer("src_1", image));

            // none = 不生成：同一组时刻的原图，作对照
            if (style != "none")
            {
                var report = AutoDirector.Generate(project, map, new AutoOptions { Style = style, Intensity = 0.8 });
                output.WriteLine($"[{style}] 相机 {report.CameraKeys} 键 · 特效 {report.EffectInstances} 个 · 出场 {report.EffectWindows} 段");
            }

            var times = KeyTimes(map);
            var path = Path.Combine(Path.GetTempPath(), $"jz_director_sheet_{style}.png");
            Render(project, times, output).SavePng(path);
            output.WriteLine($"  → {path}");
            output.WriteLine("  帧时刻：" + string.Join(" ", times.Select(t => $"{t.Label}@{t.Time:F2}")));
        }
    }

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

    /// <summary>歌曲时长：段落尾、末拍 + 一拍、末句尾三者取大（与 AutoDirector.Song.Build 同一口径）。</summary>
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

        bool High(Section s) => s.Kind is not ("intro" or "outro")
            && (s.Kind == "chorus" || Energy(s) >= (hasChorus ? 0.8 : 0.66));

        double Energy(Section s) =>
            double.IsFinite(s.Energy) && hi - lo > 1e-9 ? (s.Energy - lo) / (hi - lo) : 0.5;

        var drops = new List<double>();
        for (int i = 0; i < secs.Count; i++)
            if (High(secs[i]) && (i == 0 || !High(secs[i - 1]))) drops.Add(secs[i].Start);
        return drops;
    }

    private static List<(string Label, double Time)> KeyTimes(BeatMap map)
    {
        var list = new List<(string, double)>();
        var secs = map.Sections.OrderBy(s => s.Start).ToList();
        var chorus = secs.FirstOrDefault(s => s.Kind == "chorus") ?? secs.OrderByDescending(s => s.Energy).FirstOrDefault();
        var verse = secs.FirstOrDefault(s => s.Kind == "verse");
        double bar = Bar(map);
        var drops = Drops(map);

        if (verse is not null)
        {
            list.Add(("主歌起", verse.Start + 0.5));
            list.Add(("主歌末", verse.End - 0.3));
        }

        if (chorus is not null)
        {
            list.Add(("蓄力中", chorus.Start - bar * 0.5));
            list.Add(("蓄力末", chorus.Start - 0.05));
            list.Add(("落点", chorus.Start + 0.02));
            list.Add(("落点+.15", chorus.Start + 0.15));
            list.Add(("落点+.4", chorus.Start + 0.4));
            var db = map.Downbeats.FirstOrDefault(d => d > chorus.Start + bar * 0.9);
            if (db > 0)
            {
                list.Add(("副歌下拍", db + 0.02));
                list.Add(("下拍+.3", db + 0.3));
            }
            list.Add(("副歌中", (chorus.Start + chorus.End) / 2));
        }

        foreach (var s in secs.Where(s => s.Kind is "bridge" or "outro"))
            list.Add((s.Kind, s.Start + Math.Min(2.0, (s.End - s.Start) / 2)));

        // 前奏中 / 尾奏中：黑边区间的中段（淡入淡出各一小节，中间才是满幅）
        foreach (var (kind, label) in new[] { ("intro", "前奏中"), ("outro", "尾奏中") })
        {
            var sec = secs.FirstOrDefault(s => s.Kind == kind);
            if (sec is not null && sec.End - sec.Start > 1.0) list.Add((label, (sec.Start + sec.End) / 2));
        }

        // 第一个非落点段落边界：色块转场（wipe）区间的中点正落在这条边界上
        var wipe = secs.Select(s => s.Start)
            .FirstOrDefault(t => t > 0 && !drops.Any(d => Math.Abs(d - t) < 1e-6));
        if (wipe > 0) list.Add(("色块转场中", wipe));

        // 落点 +1.5 小节：漫画分格区间 [落点+1小节, 落点+3小节] 的中段
        if (drops.Count > 0) list.Add(("分格中", drops[0] + bar * 1.5));

        return list.Take(24).ToList();
    }

    private static ReadbackImage Render(DirectorProject project, List<(string Label, double Time)> times,
        ITestOutputHelper output)
    {
        var rows = (times.Count + Cols - 1) / Cols;
        var sheet = new byte[W * Cols * H * rows * 4];
        var gpu = D3DDevice.TryCreate(out var err) ?? throw new InvalidOperationException(err);
        using (gpu)
        using (var pipe = new PvPipeline(gpu, project.Scene, project.Effects))
        using (var target = OffscreenTarget.Create(gpu, pipe.D2DContext, W, H))
        {
            pipe.SetDirector(DirectorEvaluator.FromSaves(project.Curves));
            // 歌词层：管线的歌词提供者（照 PvStageWindow.Lyrics.cs 的做法），逐段样式区间先套到 clip 上
            pipe.Renderer.Lyric?.SetClips(LyricStyleSpans.Apply(
                LrcParser.ParseClips(project.LrcText).Clips, project.LyricStyles));
            pipe.Renderer.Lyric?.SetStyle(project.Scene.LyricStyle ?? LyricStyle.FromName(project.Scene.LyricStyleName));

            // 粒子层只在管线构造时注册（RegisterProviders → RegisterLayerProvider）：建完确认一遍，
            // 注册漏了这里就是一张没有粒子的联系表，而画面本身看不出"少了什么"
            foreach (var layer in project.Scene.Layers.Where(l => l.Particles is not null))
                if (pipe.Renderer.Stage.Get(layer.Id) is not ParticleLayerProvider)
                    throw new InvalidOperationException($"粒子层 {layer.Id} 没有注册进舞台");
            output.WriteLine($"  粒子层 {project.Scene.Layers.Count(l => l.Particles is not null)} 个已注册");

            // 图片图层在渲染线程首帧异步建立：先空跑几帧
            for (int i = 0; i < 3; i++)
            {
                pipe.Prepare(0);
                pipe.Composite(target.Bitmap, W, H);
                gpu.Context.Flush();
            }

            for (int i = 0; i < times.Count; i++)
            {
                pipe.Prepare(times[i].Time);
                pipe.Composite(target.Bitmap, W, H);
                gpu.Context.Flush();
                using var frame = target.Readback();
                int ox = (i % Cols) * W, oy = (i / Cols) * H;
                for (int y = 0; y < H; y++)
                    Buffer.BlockCopy(frame.BgraPixels, y * W * 4, sheet, ((oy + y) * W * Cols + ox) * 4, W * 4);
            }
        }

        return new ReadbackImage(W * Cols, H * rows, sheet);
    }

    private static string? FindProbeFile()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var f = Path.Combine(dir.FullName, "tools", "real-song.txt");
            if (File.Exists(f)) return f;
            dir = dir.Parent;
        }

        return null;
    }
}
