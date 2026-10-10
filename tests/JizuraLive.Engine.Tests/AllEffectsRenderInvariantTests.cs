// 全量效果渲染不变量（普查固化版）：
//   对 Reg 里每一个 enter/exit 效果，用同一 item 渲帧，并与「同一 cut、效果换成 cut」的静止基线比对：
//     1) 不许抛异常；
//     2) 进场结束后（InDur 之后、退场之前）画面必须与静止基线一致——不留残影/位移；
//     3) 退场起点（outStart 那一刻）画面必须与静止基线一致——起手不位移；
//     4) 退场终点（cut 末尾）画面必须**与静止基线不同**——这一条抓的是「特效写进 it.alpha 却没生效」
//        这类成批失效（曾因 JText 把 `it.alpha ?? 1` 写成 ZeroTo，约 160 个退场特效字幕永不消失）。
//   另对每个 hold 效果做一次不抛异常冒烟。只查通用不变量（不锁具体观感），任何机器/字体下都成立。
using JizuraLive.Engine;
using Newtonsoft.Json.Linq;
using SkiaSharp;
using Xunit;

namespace JizuraLive.Engine.Tests;

[Collection("EngineRegistry")]
public class AllEffectsRenderInvariantTests
{
    private const int W = 320; // 小面即可：只比「同机同字体下的相对差异」

    [Fact]
    public void 全部进退场效果_满足四条通用不变量()
    {
        EngineBoot.Install();
        EngineBoot.InstallPacks();
        var proj = Project.NewDefault();
        proj.Lyrics = "夜明けの色を覚えてる";
        var design = Planner.DesignSize(proj.Aspect);
        var scale = W / (double)design.W;
        var h = (int)Math.Round(design.H * scale);
        using var surface = SKSurface.Create(new SKImageInfo(W, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        var failures = new List<string>();

        JObject Item(bool vertical) => new()
        {
            ["text"] = "夜明けの色を覚えてる",
            ["font"] = "gothic_black",
            ["size"] = 42.0,
            ["x"] = design.W / 2,
            ["y"] = design.H / 2,
            ["mi"] = 0,
            ["vertical"] = vertical,
        };

        // 渲一帧：返回 (墨量占比, 与 baseline 的差异占比, 异常消息)；lt 由调用方给定（秒）
        (double Ratio, double Diff, string? Err) Shot(Plan plan, Cut cut, Scheme? sc, SKColor bg, bool vertical,
            double lt, SKBitmap? baseline)
        {
            canvas.Clear(bg);
            canvas.Save();
            canvas.Scale((float)scale);
            string? err = null;
            try
            {
                var env = Env.Make(canvas, plan, cut, sc, lt, 1.0 / 24, 0.5, 0, null, "main", null, scale,
                    cut.Start + lt, lt);
                JLayouts.MainDraw(env, Item(vertical));
            }
            catch (Exception e)
            {
                err = e.GetType().Name + ": " + e.Message;
            }
            canvas.Restore();
            canvas.Flush();
            using var img = surface.Snapshot();
            using var px = img.PeekPixels();
            double ink = 0, diff = 0, total = 0;
            for (var y = 0; y < px.Height; y++)
                for (var x = 0; x < px.Width; x++)
                {
                    total++;
                    var c = px.GetPixelColor(x, y);
                    var d = Math.Max(Math.Max(Math.Abs(c.Red - bg.Red), Math.Abs(c.Green - bg.Green)), Math.Abs(c.Blue - bg.Blue));
                    if (d > 40) ink++;
                    if (baseline != null)
                    {
                        var b = baseline.GetPixel(x, y);
                        var bd = Math.Max(Math.Max(Math.Abs(c.Red - b.Red), Math.Abs(c.Green - b.Green)), Math.Abs(c.Blue - b.Blue));
                        if (bd > 40) diff++;
                    }
                }
            return (ink / total, baseline == null ? 0 : diff / total, err);
        }

        SKBitmap Grab()
        {
            using var img = surface.Snapshot();
            var bm = new SKBitmap(new SKImageInfo(img.Width, img.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            img.ReadPixels(bm.Info, bm.GetPixels(), bm.RowBytes, 0, 0);
            return bm;
        }

        foreach (var group in new[] { "enter", "exit" })
            foreach (var vertical in new[] { false, true })
            {
                var vTag = vertical ? "V" : "H";
                foreach (var key in Reg.OrderOf(group))
                {
                    if (key == "cut") continue; // 空效果（无特效本身），不变量对它无意义
                    Plan plan;
                    try
                    {
                        plan = Planner.PreviewPlan(proj, group, key);
                    }
                    catch (Exception e)
                    {
                        failures.Add($"{group}.{key}/{vTag}: PreviewPlan 抛 {e.GetType().Name}: {e.Message}");
                        continue;
                    }
                    var cut = plan.Cuts[0];
                    // 固定时长：进度轴可精确取点（进场结束 / 退场起点 / 退场终点）
                    cut.InDur = 0.5;
                    cut.OutDur = 0.5;
                    cut.Dur = 2.0;
                    var sc = plan.Style.Schemes.Count > 0 ? plan.Style.Schemes[0] : null;
                    var bg = sc != null ? Env.SkColor(sc.Str("bg") ?? "#101014") : Env.SkColor("#101014");

                    // 静止基线：同一 cut、同一时刻，只把该组效果换成 cut
                    var saved = group == "enter" ? cut.Enter : cut.Exit;
                    if (group == "enter") cut.Enter = "cut";
                    else cut.Exit = "cut";
                    var (baseRatio, _, baseErr) = Shot(plan, cut, sc, bg, vertical, cut.Dur * 0.5, null);
                    using var baseline = Grab();
                    if (group == "enter") cut.Enter = saved;
                    else cut.Exit = saved;
                    Assert.Null(baseErr);
                    if (baseRatio <= 0.0005) continue; // 该条目静止时本就无墨（字体缺失等）：不变量失去意义

                    if (group == "enter")
                    {
                        var (_, diff, err) = Shot(plan, cut, sc, bg, vertical, cut.InDur + 0.1, baseline);
                        var tag = $"enter.{key}/{vTag}";
                        if (err != null) failures.Add($"{tag}: 进场结束抛异常 {err}");
                        else if (diff > 0.004) failures.Add($"{tag}: 进场结束仍与静止基线不同（残影/位移未归零）diff={diff:F4}");
                    }
                    else
                    {
                        var tag = $"exit.{key}/{vTag}";
                        var (_, diff0, err0) = Shot(plan, cut, sc, bg, vertical, cut.Dur - cut.OutDur, baseline);
                        if (err0 != null) failures.Add($"{tag}: 退场起点抛异常 {err0}");
                        else if (diff0 > 0.004) failures.Add($"{tag}: 退场起点就与静止基线不同（起手位移）diff={diff0:F4}");

                        var (_, diff1, err1) = Shot(plan, cut, sc, bg, vertical, cut.Dur, baseline);
                        if (err1 != null) failures.Add($"{tag}: 退场终点抛异常 {err1}");
                        else if (diff1 <= 0.0002) failures.Add($"{tag}: 退场终点画面与静止基线逐像素相同——退场没生效（如 it.alpha=0 未被执行）");
                    }
                }
            }

        // hold：只做不抛异常冒烟（hold 是持续状态，没有可比的静止基线）
        foreach (var key in Reg.OrderOf("hold"))
            foreach (var vertical in new[] { false, true })
            {
                Plan plan;
                try
                {
                    plan = Planner.PreviewPlan(proj, "hold", key);
                }
                catch (Exception e)
                {
                    failures.Add($"hold.{key}: PreviewPlan 抛 {e.GetType().Name}: {e.Message}");
                    continue;
                }
                var cut = plan.Cuts[0];
                cut.InDur = 0.5;
                cut.OutDur = 0.5;
                cut.Dur = 2.0;
                var sc = plan.Style.Schemes.Count > 0 ? plan.Style.Schemes[0] : null;
                var bg = sc != null ? Env.SkColor(sc.Str("bg") ?? "#101014") : Env.SkColor("#101014");
                var (_, _, err) = Shot(plan, cut, sc, bg, vertical, cut.Dur * 0.5, null);
                if (err != null) failures.Add($"hold.{key}/{(vertical ? "V" : "H")}: 抛异常 {err}");
            }

        Assert.True(failures.Count == 0,
            $"全部效果不变量失败 {failures.Count} 条：\n" + string.Join("\n", failures.Take(30)));
    }
}
