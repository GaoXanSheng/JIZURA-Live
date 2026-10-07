// 渲染冒烟（临时）：plan → Renderer 逐帧出 PNG。
// 验证「EngineBoot.Install → Planner.Plan → Renderer.Frame」整条链在纯 C# 里跑通，
// 并用像素统计确认 PNG 有实际内容（与 scheme bg 差异 > 60 的像素占比 > 0.5%）。
// 输出落在仓库根 obj/render-smoke-*.png，供人工抽查。

using System.Globalization;
using System.IO;
using JizuraLive.Engine;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

[Collection("EngineRegistry")]
public class RenderSmoke
{
    private readonly ITestOutputHelper _out;

    public RenderSmoke(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Plan_Then_Frame_Renders_NonEmpty_Png()
    {
        EngineBoot.Install();
        SmokeFonts();

        var project = Project.NewDefault();
        project.Lyrics = "夜明けの色を/覚えてる\nほどけた声が遠くで鳴った";
        project.Title = "テスト曲";
        project.Artist = "歌手";
        var plan = Planner.Plan(project, null);
        Assert.True(plan.Cuts.Count > 0, "plan.cuts 为空");
        _out.WriteLine($"plan: {plan.Cuts.Count} cuts, duration={plan.Duration:F2}s, style={plan.StyleKey}, " +
                       $"events={plan.Events.Count}, hud={plan.Hud}, W={plan.W} H={plan.H}");

        var outDir = RepoObjDir();
        using var renderer = new Renderer();
        foreach (var t in new[] { 1.2, 2.5, 4.0 })
        {
            var cut = Planner.CutAt(plan, t);
            using var surface = SKSurface.Create(new SKImageInfo(1920, 1080));
            surface.Canvas.Clear(SKColors.White); // 白底兜底；Frame 内部按 scheme 填 bg
            renderer.Frame(surface.Canvas, plan, t);
            surface.Canvas.Flush();

            using var img = surface.Snapshot();
            var path = Path.Combine(outDir, $"render-smoke-{t.ToString(CultureInfo.InvariantCulture)}.png");
            using (var png = img.Encode(SKEncodedImageFormat.Png, 95))
            using (var fs = File.Create(path))
                png.SaveTo(fs);

            var sc = SchemeOf(plan, cut);
            var bg = Env.SkColor(sc.Str("bg") ?? "#000000");
            double content = 0, total = 0;
            using (var px = img.PeekPixels())
            {
                for (var y = 0; y < px.Height; y += 3)
                    for (var x = 0; x < px.Width; x += 3)
                    {
                        var c = px.GetPixelColor(x, y);
                        total++;
                        var d = Math.Max(Math.Max(Math.Abs(c.Red - bg.Red), Math.Abs(c.Green - bg.Green)),
                            Math.Abs(c.Blue - bg.Blue));
                        if (d > 60) content++;
                    }
            }
            var ratio = total > 0 ? content / total : 0;
            Assert.True(ratio > 0.005, $"t={t}: 内容像素占比 {ratio:P2} < 0.5%（PNG 全黑/全白？）");
            Assert.True(new FileInfo(path).Length > 10_000, $"t={t}: PNG 太小 {new FileInfo(path).Length}B");

            var bytes = new FileInfo(path).Length;
            _out.WriteLine($"t={t}s: cut#{cut?.Index ?? -1} layout={cut?.Layout} enter={cut?.Enter} exit={cut?.Exit} " +
                           $"cam={cut?.Cam} bg={cut?.Bg} schemeBg={sc.Str("bg")} fg={sc.Str("fg")} " +
                           $"text=\"{(cut?.Text ?? "").Replace("\n", "/")}\" " +
                           $"contentRatio={ratio:P2} png={bytes / 1024}KB → {path}");
        }
    }

    /// <summary>仓库根 obj/（从测试 bin 向上找 JizuraLive.csproj）。</summary>
    private static string RepoObjDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "JizuraLive.csproj")))
            dir = dir.Parent!;
        var root = dir?.FullName ?? AppContext.BaseDirectory;
        var obj = Path.Combine(root, "obj");
        Directory.CreateDirectory(obj);
        return obj;
    }

    private static Scheme SchemeOf(Plan plan, Cut? cut)
    {
        var schemes = plan.Style.Schemes;
        if (schemes.Count == 0) return new Scheme();
        var i = cut != null ? ((cut.Scheme % schemes.Count) + schemes.Count) % schemes.Count : 0;
        return schemes[i];
    }

    /// <summary>
    /// 冒烟字体兜底：仓库自托管字体是 woff2 分片（M2 字体管线未接，FaceJobs 解析不到会落到
    /// SKTypeface.Default，CJK 变 .notdef 方块）。把字体目录全部条目改指系统 CJK 字体，
    /// 让冒烟图能看到真字形。仅本测试进程内生效。
    /// </summary>
    private static void SmokeFonts()
    {
        var fam = new[] { "Yu Gothic", "Meiryo", "MS Gothic", "SimSun", "Arial" }
            .FirstOrDefault(f =>
            {
                try
                {
                    using var t = SKTypeface.FromFamilyName(f);
                    return t != null && string.Equals(t.FamilyName, f, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }) ?? "Arial";
        foreach (var kv in JFonts.Table.ToArray())
            JFonts.AddUserFont(kv.Key, kv.Value.Label, fam, kv.Value.Weight, kv.Value.Kind);
    }
}
