using System.IO;
using JizuraLive.Engine.Music;
using JizuraLive.Pv;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using Xunit;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 一键成片联系表探针：本机真歌（tools/real-song.txt 里的音频路径，JZ_DIRECTOR_IMAGE 给一张底图）整曲分析 → 三种风格各生成一次 →
/// 在转场、蓄力、副歌下拍等关键时刻离屏抽帧，拼成一张 4 列联系表写到临时目录。
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

        foreach (var style in new[] { "none", "soft", "dynamic", "epic" })
        {
            var project = new DirectorProject();
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
            Render(project, times).SavePng(path);
            output.WriteLine($"  → {path}");
            output.WriteLine("  帧时刻：" + string.Join(" ", times.Select(t => $"{t.Label}@{t.Time:F2}")));
        }
    }

    private static List<(string Label, double Time)> KeyTimes(BeatMap map)
    {
        var list = new List<(string, double)>();
        var secs = map.Sections.OrderBy(s => s.Start).ToList();
        var chorus = secs.FirstOrDefault(s => s.Kind == "chorus") ?? secs.OrderByDescending(s => s.Energy).FirstOrDefault();
        var verse = secs.FirstOrDefault(s => s.Kind == "verse");

        if (verse is not null)
        {
            list.Add(("主歌起", verse.Start + 0.5));
            list.Add(("主歌末", verse.End - 0.3));
        }

        if (chorus is not null)
        {
            var bar = map.Downbeats.Count > 1 ? map.Downbeats[1] - map.Downbeats[0] : 2.0;
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

        return list.Take(16).ToList();
    }

    private static ReadbackImage Render(DirectorProject project, List<(string Label, double Time)> times)
    {
        var rows = (times.Count + Cols - 1) / Cols;
        var sheet = new byte[W * Cols * H * rows * 4];
        var gpu = D3DDevice.TryCreate(out var err) ?? throw new InvalidOperationException(err);
        using (gpu)
        using (var pipe = new PvPipeline(gpu, project.Scene, project.Effects))
        using (var target = OffscreenTarget.Create(gpu, pipe.D2DContext, W, H))
        {
            pipe.SetDirector(DirectorEvaluator.FromSaves(project.Curves));
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
