using JizuraLive.Render.Draw;
using JizuraLive.Render.Effects;
using JizuraLive.Render.Gpu;
using Vortice.Mathematics;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 效果图的持久化与预设库：存档要能原样往返、旧存档要能容忍、预设要全部合法且互相有区别。
/// </summary>
public class EffectPersistenceTests
{
    private const int W = 320;
    private const int H = 180;

    private readonly ITestOutputHelper _out;

    public EffectPersistenceTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void RoundTripPreservesEveryNode()
    {
        foreach (var preset in EffectPresets.All)
        {
            var original = preset.Build();
            Assert.NotEmpty(original.Nodes);

            var json = EffectGraphJson.Serialize(original);
            var restored = EffectGraphJson.Deserialize(json);

            Assert.Equal(original.Nodes.Count, restored.Nodes.Count);
            for (var i = 0; i < original.Nodes.Count; i++)
            {
                var a = original.Nodes[i];
                var b = restored.Nodes[i];
                Assert.Equal(a.DefId, b.DefId);
                Assert.Equal(a.Enabled, b.Enabled);
                Assert.Equal(a.Values.Count, b.Values.Count);
                foreach (var (k, v) in a.Values)
                {
                    Assert.True(b.Values.TryGetValue(k, out var bv), $"{a.DefId}.{k} 丢失");
                    Assert.Equal(v, bv, 3);
                }

                Assert.Equal(a.Colors.Count, b.Colors.Count);
                foreach (var (k, v) in a.Colors)
                {
                    Assert.True(b.Colors.TryGetValue(k, out var bv), $"{a.DefId}.{k} 颜色丢失");
                    Assert.Equal(v, bv);
                }
            }
        }
    }

    [Fact]
    public void ToleratesUnknownNodesAndExtraParams()
    {
        const string raw = """
            {
              "version": 1,
              "nodes": [
                {
                  "defId": "definitelyDoesNotExist",
                  "enabled": true,
                  "values": { "foo": 1.0 }
                },
                {
                  "defId": "bloom",
                  "enabled": true,
                  "values": {
                    "threshold": 0.72,
                    "unknownFutureParam": 42.0
                  }
                }
              ]
            }
            """;

        var g = EffectGraphJson.Deserialize(raw);
        Assert.Single(g.Nodes);
        Assert.Equal("bloom", g.Nodes[0].DefId);
        Assert.Equal(0.72f, g.Nodes[0].Value(EffectCatalog.Get("bloom")!.Params[0]), 3);
    }

    [Fact]
    public void ToleratesMissingValuesByFallingBackToDefaults()
    {
        const string raw = """
            {
              "version": 1,
              "nodes": [
                {
                  "defId": "bloom",
                  "enabled": true
                }
              ]
            }
            """;

        var g = EffectGraphJson.Deserialize(raw);
        Assert.Single(g.Nodes);
        var def = EffectCatalog.Get("bloom")!;
        foreach (var p in def.Params)
            Assert.Equal(p.Default, g.Nodes[0].Value(p), 3);
    }

    /// <summary>
    /// 同一种效果放两个（M4/M6 要靠实例 Id 寻址，参数必须各归各）：
    /// Id 非空且互不相同，落盘往返后每个 Id 仍带着自己那组参数值。
    /// </summary>
    [Fact]
    public void SameDefTwiceKeepsIndependentInstanceIdsAndValues()
    {
        var intensity = EffectCatalog.Get("glow")!.Params[1];

        var g = new EffectGraph()
            .Add("glow", n => n.Set(intensity, 0.3f));
        g.Add("glow", n => n.Set(intensity, 1.4f));

        var idA = g.Nodes[0].InstanceId;
        var idB = g.Nodes[1].InstanceId;
        Assert.False(string.IsNullOrEmpty(idA), "Add 必须给节点发实例 Id");
        Assert.False(string.IsNullOrEmpty(idB), "Add 必须给节点发实例 Id");
        Assert.NotEqual(idA, idB);

        var restored = EffectGraphJson.Deserialize(EffectGraphJson.Serialize(g));
        Assert.Equal(2, restored.Nodes.Count);
        Assert.Equal(0.3f, restored.Nodes.First(n => n.InstanceId == idA).Value(intensity), 3);
        Assert.Equal(1.4f, restored.Nodes.First(n => n.InstanceId == idB).Value(intensity), 3);
    }

    /// <summary>
    /// 旧存档没有 InstanceId：读入时全图补号且唯一，补出来的号被写回 JSON，再次读入保持一致
    /// （即补号结果本身可往返，不会每次加载都换一批号）。
    /// </summary>
    [Fact]
    public void LegacySaveWithoutInstanceIdsIsBackfilledAndStable()
    {
        const string legacy = """
            {
              "version": 1,
              "nodes": [
                { "defId": "glow", "enabled": true, "values": { "intensity": 0.4 } },
                { "defId": "glow", "enabled": true, "values": { "intensity": 1.7 } },
                { "defId": "vignette", "enabled": false }
              ]
            }
            """;

        var g = EffectGraphJson.Deserialize(legacy);
        Assert.Equal(3, g.Nodes.Count);
        Assert.All(g.Nodes, n => Assert.False(string.IsNullOrEmpty(n.InstanceId), "旧存档节点必须补号"));
        Assert.Equal(3, g.Nodes.Select(n => n.InstanceId).Distinct().Count());

        var json = EffectGraphJson.Serialize(g);
        Assert.Contains("InstanceId", json, StringComparison.Ordinal);

        var again = EffectGraphJson.Deserialize(json);
        Assert.Equal(g.Nodes.Select(n => n.InstanceId), again.Nodes.Select(n => n.InstanceId));
    }

    /// <summary>预设与默认后期链产出的图同样全带号，且图内唯一。</summary>
    [Fact]
    public void PresetsAndDefaultPostAssignUniqueInstanceIds()
    {
        var graphs = EffectPresets.All.Select(p => (p.Id, Graph: p.Build()))
            .Append(("CreateDefaultPost", EffectGraph.CreateDefaultPost()));

        foreach (var (id, graph) in graphs)
        {
            Assert.NotEmpty(graph.Nodes);
            foreach (var n in graph.Nodes)
            {
                Assert.False(string.IsNullOrEmpty(n.InstanceId), $"{id} 的 {n.DefId} 节点没有实例 Id");
                Assert.StartsWith($"fx_{n.DefId}_", n.InstanceId);
            }

            Assert.Equal(graph.Nodes.Count,
                graph.Nodes.Select(n => n.InstanceId).Distinct().Count());
        }
    }

    private static ReadbackImage BuildContactSheet(IReadOnlyList<(string Id, ReadbackImage Bmp)> frames)
    {
        const int cols = 4;
        var rows = (frames.Count + cols - 1) / cols;
        var w = W;
        var h = H;
        var totalW = cols * w;
        var totalH = rows * h;
        var totalPixels = new byte[totalW * totalH * 4];

        for (var i = 0; i < frames.Count; i++)
        {
            var col = i % cols;
            var row = i / cols;
            var src = frames[i].Bmp;

            for (var y = 0; y < h; y++)
            {
                var srcOffset = y * w * 4;
                var dstOffset = ((row * h + y) * totalW + col * w) * 4;
                Buffer.BlockCopy(src.BgraPixels, srcOffset, totalPixels, dstOffset, w * 4);
            }
        }

        return new ReadbackImage(totalW, totalH, totalPixels);
    }

    /// <summary>把联表图里第 (col,row) 格裁出来，用于把整图差异定位到具体预设。</summary>
    private static ReadbackImage CropTile(ReadbackImage sheet, int col, int row)
    {
        var px = new byte[W * H * 4];
        for (var y = 0; y < H; y++)
            Buffer.BlockCopy(sheet.BgraPixels, ((row * H + y) * sheet.Width + col * W) * 4,
                px, y * W * 4, W * 4);
        return new ReadbackImage(W, H, px);
    }

    /// <summary>
    /// 整图差异只给一个 mean，12 格里是哪一格变了完全看不出来（这次就是这样：新增预设让整图 mean=11.985，
    /// 排查得另写探针）。这里逐格点名，差异格按 mean 从大到小列出来。
    /// </summary>
    private static string DescribeSheetDiff(ReadbackImage golden, ReadbackImage sheet,
        IReadOnlyList<(string Id, ReadbackImage Bmp)> frames)
    {
        const int cols = 4;
        var rows = (frames.Count + cols - 1) / cols;
        var changed = new List<(string Id, double Mean, int Max, double Over8)>();

        for (var r = 0; r < rows; r++)
        for (var c = 0; c < cols; c++)
        {
            var idx = r * cols + c;
            using var tile = CropTile(sheet, c, r);
            using var goldTile = CropTile(golden, c, r);
            var d = GoldenImage.Compare(goldTile, tile);
            if (d.MeanAbs <= 3.0) continue;

            var id = idx < frames.Count ? frames[idx].Id : $"第 {idx + 1} 格空位";
            changed.Add((id, d.MeanAbs, d.MaxAbs, d.PctOver8));
        }

        if (changed.Count == 0) return "（逐格都在容差内——差异来自全图性的细微偏移）";

        return string.Join("；", changed.OrderByDescending(x => x.Mean)
            .Select(x => $"{x.Id} mean={x.Mean:F3} max={x.Max} over8={x.Over8 * 100:F2}%"));
    }

    [Fact]
    public void EveryPresetIsWellFormed()
    {
        foreach (var preset in EffectPresets.All)
        {
            var g = preset.Build();
            Assert.NotEmpty(g.Nodes);
            foreach (var n in g.Nodes)
                Assert.NotNull(EffectCatalog.Get(n.DefId));
        }

        _out.WriteLine($"[perset] {EffectPresets.All.Count} 个预设全部合法："
                       + string.Join(", ", EffectPresets.All.Select(p => p.Id)));
    }

    /// <summary>预设要真的互相不同——否则「预设库」只是几个换了名字的同一张图。</summary>
    [GpuFact]
    public void PresetsProduceVisiblyDifferentFrames()
    {
        var gpu = D3DDevice.TryCreate(out _);
        if (gpu is null) GpuRequire.Fail();
        using (gpu)
        using (var host = D2DHost.Create(gpu))
        using (var target = OffscreenTarget.Create(gpu, host.Ctx, W, H))
        using (var runner = new GpuEffectRunner(gpu))
        {
            host.Draw.SetTarget(target.Bitmap, W, H);
            host.Draw.Begin();
            host.Draw.Clear(new Color4(0.06f, 0.06f, 0.09f, 1f));
            host.Draw.FillRect(new Rect(0, 0, W, H), new Paint2D
            {
                Brush = new LinearGradient2D(new System.Numerics.Vector2(0, 0),
                    new System.Numerics.Vector2(W, H),
                [
                    new GradientStop2D(0f, new Color4(0.1f, 0.12f, 0.25f, 1f)),
                    new GradientStop2D(1f, new Color4(0.95f, 0.7f, 0.15f, 1f)),
                ]),
            });
            host.Draw.FillCircle(90, 80, 34, new Paint2D
            {
                Brush = new SolidBrush2D(new Color4(0.1f, 0.96f, 0.84f, 1f)),
            });
            host.Draw.DrawText("PRESET", W / 2f, H * 0.78f, TextAlign2D.Center,
                host.Draw.Fonts.Get("Microsoft YaHei UI", 26),
                new Paint2D { Brush = new SolidBrush2D(new Color4(1f, 1f, 1f, 1f)) });
            host.Draw.End();

            using var baseBmp = target.Readback();
            var frames = new List<(string Id, ReadbackImage Bmp)>();
            foreach (var preset in EffectPresets.All)
            {
                using var input = runner.Upload(baseBmp);
                var rg = preset.Build().BuildRenderGraph(runner.Shaders, 1.5f, W, H);
                var outp = runner.RunGraph(rg, input.Srv, W, H);
                frames.Add((preset.Id, GpuReadback.ToImage(gpu, outp.Texture, W, H)));
                runner.Pool.Return(outp);
            }

            try
            {
                for (var i = 0; i < frames.Count; i++)
                for (var j = i + 1; j < frames.Count; j++)
                {
                    var d = GoldenImage.Compare(frames[i].Bmp, frames[j].Bmp);
                    Assert.True(d.MeanAbs > 0.5,
                        $"预设 {frames[i].Id} 与 {frames[j].Id} 几乎一样（meanAbs={d.MeanAbs:F3}）");
                }

                _out.WriteLine($"[preset] {frames.Count} 个预设两两可区分");

                // 联表图：全部预设拼成 4 列一张，既是肉眼验收材料，也是视觉回归基准
                using var sheet = BuildContactSheet(frames);
                if (GoldenImage.UpdateRequested)
                {
                    GoldenImage.SaveOrUpdate("render-core-presets", sheet);
                }
                else
                {
                    // 缺基准 = 失败（LoadOrFail 会抛出并说明如何显式更新）；
                    // 之前这里是「TryLoad 到 null 就什么都不断言」，等于把这条回归闸拆了。
                    using var golden = GoldenImage.LoadOrFail("render-core-presets", "预设联表图 4 列");

                    var d = GoldenImage.Compare(golden, sheet);
                    Assert.True(d.MeanAbs <= 3.0,
                        $"预设联表图与基准差异过大：{d}。变化集中在——{DescribeSheetDiff(golden, sheet, frames)}");
                }
            }
            finally
            {
                foreach (var (_, b) in frames) b.Dispose();
            }
        }
    }
}
