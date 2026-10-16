using System.Diagnostics;
using JizuraLive.Pv;
using JizuraLive.Render.Effects;
using Xunit.Abstractions;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 跨线程安全：效果图「克隆 → 发布 → 渲染侧取快照遍历」与图层栈「写时复制」两条通道。
///
/// 全程不需要 GPU：管线本体构造不出来（要 D3D 设备），所以这里直接驱动那两个**真实的**机制——
/// <see cref="EffectGraph.Clone"/>、<see cref="SnapshotSlot{T}"/>、<see cref="PvPipeline.ApplyEffectCurves"/>
/// 与 <see cref="PvPipeline.WithLayerAdded"/> 一族（管线内部用的就是它们）。
/// 两条压力用例各跑 2 秒：一个线程按 UI 线程的形状改，另一个线程按渲染线程的形状读。
/// </summary>
public class RenderThreadSafetyTests
{
    private const int StressMs = 2000;

    private readonly ITestOutputHelper _out;

    public RenderThreadSafetyTests(ITestOutputHelper output) => _out = output;

    private static long Deadline() => Stopwatch.GetTimestamp() + StressMs * Stopwatch.Frequency / 1000;

    private static PvLayerConfig Layer(string id) => new()
    {
        Id = id,
        Name = id,
        Kind = PvLayerKind.Background,
    };

    // ---------------- ① 克隆是深拷贝 ----------------

    /// <summary>
    /// 改克隆不影响原图：参数值、颜色、顺序、启用状态、增删都不串。
    /// 这条是整套方案的地基——发布出去的就是克隆，串了就等于 UI 线程还在改渲染线程正在读的对象。
    /// </summary>
    [Fact]
    public void CloneIsDeepCopy()
    {
        var grain = EffectCatalog.Get("grain")!.Params[0];
        var glow = EffectCatalog.Get("glow")!.Params[1];
        var tint = EffectCatalog.Get("colorGrade")!.Params[4];

        var graph = new EffectGraph();
        graph.Add("grain", n => n.Set(grain, 0.21f));
        graph.Add("glow", n => n.Set(glow, 0.9f));
        graph.Add("colorGrade", n => n.SetColor(tint, "#8ad4ff")).Nodes[2].Enabled = false;

        var clone = graph.Clone();

        Assert.Equal(graph.Nodes.Select(n => n.DefId), clone.Nodes.Select(n => n.DefId));
        Assert.Equal(graph.Nodes.Select(n => n.InstanceId), clone.Nodes.Select(n => n.InstanceId));
        Assert.Equal(graph.Nodes.Select(n => n.Enabled), clone.Nodes.Select(n => n.Enabled));
        Assert.Equal(0.21f, clone.Nodes[0].Value(grain), 4);
        Assert.Equal("#8ad4ff", clone.Nodes[2].ColorValue(tint));

        for (var i = 0; i < graph.Nodes.Count; i++)
        {
            Assert.NotSame(graph.Nodes[i], clone.Nodes[i]);
            Assert.NotSame(graph.Nodes[i].Values, clone.Nodes[i].Values);
            Assert.NotSame(graph.Nodes[i].Colors, clone.Nodes[i].Colors);
        }

        // 改克隆：值 / 颜色 / 启用 / 顺序 / 增删一起上，原图必须一字不动
        clone.Nodes[0].Set(grain, grain.Max);
        clone.Nodes[1].Set(glow, glow.Min);
        clone.Nodes[2].SetColor(tint, "#ff2080");
        clone.Nodes[2].Enabled = true;
        Assert.True(clone.Move(clone.Nodes[0], 2));
        clone.Add("vignette");
        clone.Nodes.RemoveAt(0);

        Assert.Equal(new[] { "grain", "glow", "colorGrade" }, graph.Nodes.Select(n => n.DefId));
        Assert.Equal(0.21f, graph.Nodes[0].Value(grain), 4);
        Assert.Equal(0.9f, graph.Nodes[1].Value(glow), 4);
        Assert.Equal("#8ad4ff", graph.Nodes[2].ColorValue(tint));
        Assert.False(graph.Nodes[2].Enabled);

        // 反过来也一样：改原图，已经发出去的那份克隆不受影响
        graph.Nodes[0].Set(grain, 0.02f);
        Assert.Equal(grain.Max, clone.Nodes.First(n => n.DefId == "grain").Value(grain), 4);
    }

    // ---------------- ② 效果图：发布 + 取快照遍历 ----------------

    /// <summary>
    /// 一个线程循环改文档图（增 / 删 / 排序 / 改值）并发布克隆，另一个线程循环「取快照 →
    /// 渲染侧等价遍历」（曲线求值 + 逐节点读参数，即打包常量缓冲那条路）。2 秒内两边都不许抛。
    /// </summary>
    [Fact]
    public void PublishedEffectSnapshotsSurviveConcurrentTraversal()
    {
        var pool = BuildNodePool(8, out var curves, out var wideParams);
        var doc = new EffectGraph();                              // UI 线程独占的文档图
        var slot = new SnapshotSlot<EffectGraph>(doc.Clone());    // 发布槽（管线用的就是它）

        Exception? writerFault = null;
        Exception? readerFault = null;
        long writes = 0, reads = 0, curveValues = 0;
        var deadline = Deadline();

        var writer = new Thread(() =>
        {
            try
            {
                while (Stopwatch.GetTimestamp() < deadline)
                {
                    MutateDocument(doc, pool);
                    slot.Publish(doc.Clone());
                    writes++;
                }
            }
            catch (Exception ex)
            {
                writerFault = ex;
            }
        })
        { IsBackground = true, Name = "ui-thread" };

        writer.Start();
        try
        {
            while (Stopwatch.GetTimestamp() < deadline)
            {
                var snapshot = slot.Snapshot;
                PvPipeline.ApplyEffectCurves(snapshot, curves, reads / 60.0);

                foreach (var node in snapshot.Nodes)
                {
                    _ = node.Enabled;
                    var def = EffectCatalog.Get(node.DefId);
                    if (def is null) continue;
                    foreach (var p in def.Params)
                    {
                        var v = node.Value(p);
                        if (p.Kind != EffectParamKind.Color && wideParams.Contains((def.Id, p.Key))
                            && Math.Abs(v - p.Max) < 1e-6) curveValues++;
                    }
                }

                reads++;
            }
        }
        catch (Exception ex)
        {
            readerFault = ex;
        }

        writer.Join();

        Assert.Null(writerFault);
        Assert.Null(readerFault);
        Assert.True(writes > 100, $"写侧几乎没跑起来（{writes} 次）");
        Assert.True(reads > 100, $"读侧几乎没跑起来（{reads} 次）");
        Assert.True(curveValues > 0, "曲线值一次都没写进快照——遍历是空转，没验到写入路径");
        _out.WriteLine($"[线程安全] 效果图 {writes} 次发布 / {reads} 次取快照遍历（2 秒）");
    }

    /// <summary>预先建好节点池（实例 Id 与曲线地址对得上），池里的宽范围参数一律取 Min 当文档常量。</summary>
    private static List<EffectNode> BuildNodePool(int count, out DirectorEvaluator curves,
        out HashSet<(string DefId, string Key)> wideParams)
    {
        string[] defIds = ["grain", "glow", "vignette", "chromatic", "scanline"];
        curves = new DirectorEvaluator();
        wideParams = new HashSet<(string, string)>();

        var seed = new EffectGraph();
        for (var i = 0; i < count; i++) seed.Add(defIds[i % defIds.Length]);

        foreach (var node in seed.Nodes)
        {
            var def = EffectCatalog.Get(node.DefId)!;
            foreach (var p in def.Params)
            {
                if (p.Kind == EffectParamKind.Color || p.Max - p.Min <= 0.1) continue;
                node.Set(p, p.Min);          // 文档常量取 Min
                wideParams.Add((def.Id, p.Key));

                var curve = new FloatCurve();
                Assert.True(curve.TryAdd(0.0, p.Max));   // 曲线一律推到 Max：读到 Max 就证明曲线写进去了
                curves.SetCurve(new ParamAddress(ParamAddressKind.Effect, node.InstanceId, p.Key), curve);
            }
        }

        return seed.Nodes.ToList();
    }

    private static void MutateDocument(EffectGraph doc, List<EffectNode> pool)
    {
        switch (Random.Shared.Next(4))
        {
            case 0:
                if (doc.Nodes.Count < 8) doc.Nodes.Add(pool[Random.Shared.Next(pool.Count)]);
                break;
            case 1:
                if (doc.Nodes.Count > 0) doc.Nodes.RemoveAt(Random.Shared.Next(doc.Nodes.Count));
                break;
            case 2:
                if (doc.Nodes.Count > 1) doc.Move(doc.Nodes[0], Random.Shared.Next(doc.Nodes.Count));
                break;
            default:
                var node = pool[Random.Shared.Next(pool.Count)];
                var def = EffectCatalog.Get(node.DefId)!;
                foreach (var p in def.Params)
                {
                    if (p.Kind != EffectParamKind.Color) node.Set(p, p.Min);
                }

                break;
        }
    }

    // ---------------- ③ 图层栈：写时复制 ----------------

    /// <summary>
    /// 图层栈的三个写入口都不许动它收到的那份列表——「换引用」才是渲染线程改结构的唯一方式，
    /// 读者（图层卡、状态条、存盘）手里的那一份因此永远是冻结的。
    /// </summary>
    [Fact]
    public void LayerStackWritesNeverTouchTheListTheyAreGiven()
    {
        var original = new List<PvLayerConfig> { Layer("a"), Layer("b"), Layer("c") };

        var added = PvPipeline.WithLayerAdded(original, Layer("d"));
        Assert.Equal(new[] { "a", "b", "c" }, original.Select(l => l.Id));
        Assert.Equal(new[] { "a", "b", "c", "d" }, added.Select(l => l.Id));

        var removed = PvPipeline.WithLayerRemovedAt(added, 0);
        Assert.Equal(new[] { "b", "c", "d" }, removed.Select(l => l.Id));
        Assert.Equal(4, added.Count);

        // 传入的是「从上到下」：列表顺序 = 绘制顺序（画在最上面的层排在末尾）
        var reordered = PvPipeline.WithLayerOrder(original, new[] { "a", "b", "c" });
        Assert.Equal(new[] { "c", "b", "a" }, reordered.Select(l => l.Id));
        Assert.Equal(new[] { "a", "b", "c" }, original.Select(l => l.Id));

        // 顺序里没提到的层留在末尾，不丢
        var partial = PvPipeline.WithLayerOrder(original, new[] { "b" });
        Assert.Equal(new[] { "b", "a", "c" }, partial.Select(l => l.Id));
    }

    /// <summary>
    /// 一个线程循环改图层栈结构（增 / 删 / 重排，每次都换新列表），另一个线程循环读
    /// （取一次列表就只认它，再枚举 + 按下标取——界面线程与存盘都是这个形状）。2 秒内不许抛。
    /// </summary>
    [Fact]
    public void LayerStackRestructuringSurvivesConcurrentReads()
    {
        var scene = new PvScene();
        scene.Layers = [Layer("a"), Layer("b"), Layer("c")];

        Exception? writerFault = null;
        Exception? readerFault = null;
        long writes = 0, reads = 0, spare = 0;
        var deadline = Deadline();

        var writer = new Thread(() =>
        {
            try
            {
                while (Stopwatch.GetTimestamp() < deadline)
                {
                    var layers = scene.Layers;
                    switch (Random.Shared.Next(3))
                    {
                        case 0:
                            if (layers.Count < 12)
                                scene.Layers = PvPipeline.WithLayerAdded(layers, Layer("src_" + ++spare));
                            break;
                        case 1:
                            if (layers.Count > 1)
                                scene.Layers = PvPipeline.WithLayerRemovedAt(layers,
                                    Random.Shared.Next(layers.Count));
                            break;
                        default:
                            scene.Layers = PvPipeline.WithLayerOrder(layers,
                                layers.Select(l => l.Id).Reverse().ToList());
                            break;
                    }

                    writes++;
                }
            }
            catch (Exception ex)
            {
                writerFault = ex;
            }
        })
        { IsBackground = true, Name = "render-thread" };

        writer.Start();
        try
        {
            while (Stopwatch.GetTimestamp() < deadline)
            {
                var layers = scene.Layers;
                var count = layers.Count;
                for (var i = 0; i < count; i++) _ = layers[i].Id;
                foreach (var layer in layers) _ = layer.Name;
                reads++;
            }
        }
        catch (Exception ex)
        {
            readerFault = ex;
        }

        writer.Join();

        Assert.Null(writerFault);
        Assert.Null(readerFault);
        Assert.True(writes > 100, $"写侧几乎没跑起来（{writes} 次）");
        Assert.True(reads > 100, $"读侧几乎没跑起来（{reads} 次）");
        Assert.True(scene.Layers.Count > 0, "图层栈被清空了");
        _out.WriteLine($"[线程安全] 图层栈 {writes} 次改结构 / {reads} 次枚举（2 秒）");
    }
}
