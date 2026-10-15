using System.IO;
using JizuraLive.Pv;
using JizuraLive.Render.Effects;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 信号图的工程往返与加载治理：嵌套 JSON（不是转义字符串）、未知节点/坏地址/未知键的就地丢弃、
/// 超容量拒绝打开、null 按空图、效果常量非有限值的兜底。
/// 每个用例独立临时目录（Guid 隔离，finally 删除），并且在断言里核对磁盘文件是否被改动。
/// </summary>
public class SignalGraphPersistenceTests
{
    [Fact]
    public void SignalGraphRoundTripsThroughProjectAndReproducesTheSameModulation()
    {
        var path = NewProjectPath();
        try
        {
            var effects = new EffectGraph();
            effects.Add("bloom");
            var address = $"effect:{effects.Nodes[0].InstanceId}.intensity";

            var graph = BuildAllKindsGraph(address);

            // 曲线也一起往返：算例的 base 来自曲线（0s/10s → 1.0/1.2）
            var curve = new FloatCurve();
            Assert.True(curve.TryAdd(0.0, 1.0));
            Assert.True(curve.TryAdd(10.0, 1.2));
            var curves = new DirectorEvaluator();
            curves.SetCurve(Addr(address), curve);

            var before = SignalCompiler.Compile(graph, effects);
            Assert.NotNull(before.Plan);
            var beforePlan = before.Plan!;

            var inputs = Frame(5.0, 0.8, curves);
            beforePlan.Evaluate(in inputs);
            var beforeResolved = Assert.Single(beforePlan.Resolved);
            Assert.Equal(ParamValueSource.Signal, beforeResolved.Source);

            var project = new DirectorProject
            {
                Name = "信号图往返",
                Effects = effects,
                SignalGraph = graph,
                Curves = { new CurveSave { Address = address, Curve = curve } },
            };

            Assert.Null(ProjectStore.Save(project, path));

            // 可读 JSON：嵌套对象 + 数组，而不是一层转义字符串
            var root = JObject.Parse(File.ReadAllText(path));
            Assert.IsType<JObject>(root["SignalGraph"]);
            Assert.IsType<JArray>(root["SignalGraph"]!["Nodes"]);
            Assert.IsType<JArray>(root["SignalGraph"]!["Edges"]);
            Assert.IsType<JArray>(root["SignalGraph"]!["Bindings"]);

            var result = ProjectStore.Load(path);
            Assert.Null(result.Fault);
            var loaded = Assert.IsType<DirectorProject>(result.Project);

            AssertGraphEquals(graph, loaded.SignalGraph);

            // 同一输入求值：逐位相同的 Resolved 值
            var loadedCurves = new DirectorEvaluator();
            foreach (var save in loaded.Curves) loadedCurves.SetCurve(Addr(save.Address), save.Curve);

            var after = SignalCompiler.Compile(loaded.SignalGraph, loaded.Effects);
            Assert.True(after.Plan is not null,
                "载入后编译失败：" + string.Join("；", after.Issues.Select(i => $"{i.Code}: {i.Message}")));

            var afterPlan = after.Plan!;
            afterPlan.Evaluate(Frame(5.0, 0.8, loadedCurves));
            var afterResolved = Assert.Single(afterPlan.Resolved);

            Assert.Equal(beforeResolved.Address, afterResolved.Address);
            Assert.Equal(beforeResolved.Source, afterResolved.Source);
            Assert.Equal(beforeResolved.Clamped, afterResolved.Clamped);
            Assert.Equal(beforeResolved.NodeId, afterResolved.NodeId);
            Assert.Equal(
                BitConverter.DoubleToInt64Bits(beforeResolved.Value),
                BitConverter.DoubleToInt64Bits(afterResolved.Value));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void UnknownNodeKindIsDroppedWithIncidentEdgesAndBindings()
    {
        var path = NewProjectPath();
        try
        {
            // Kind = 99：来自更晚版本的节点种类。整份拒绝打开太粗暴，但也不能假装它会求值
            const string json = """
            {
              "SchemaVersion": 1,
              "Name": "含未知节点",
              "SignalGraph": {
                "Nodes": [
                  { "Id": "n_known", "Kind": 0, "Enabled": true, "Params": { "value": 1.0, "unit": 1 }, "Texts": {} },
                  { "Id": "n_future", "Kind": 99, "Enabled": true, "Params": { "value": 0.0 }, "Texts": {} },
                  { "Id": "n_add", "Kind": 4, "Enabled": true, "Params": {}, "Texts": {} },
                  { "Id": "n_out", "Kind": 9, "Enabled": true, "Params": {}, "Texts": {} }
                ],
                "Edges": [
                  { "Id": "e_into_future", "FromNode": "n_known", "FromPort": "out", "ToNode": "n_future", "ToPort": "in" },
                  { "Id": "e_out_of_future", "FromNode": "n_future", "FromPort": "out", "ToNode": "n_add", "ToPort": "a" },
                  { "Id": "e_keep", "FromNode": "n_add", "FromPort": "out", "ToNode": "n_out", "ToPort": "in" }
                ],
                "Bindings": [
                  { "Id": "b_future", "NodeId": "n_future", "Address": "effect:fx_future.intensity" },
                  { "Id": "b_keep", "NodeId": "n_out", "Address": "effect:fx_1.intensity" }
                ]
              }
            }
            """;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);

            var result = ProjectStore.Load(path);

            Assert.Null(result.Fault);
            var graph = Assert.IsType<DirectorProject>(result.Project).SignalGraph;

            Assert.Equal(new[] { "n_known", "n_add", "n_out" }, graph.Nodes.Select(n => n.Id).ToArray());
            Assert.Equal(new[] { "e_keep" }, graph.Edges.Select(e => e.Id).ToArray());
            Assert.Equal(new[] { "b_keep" }, graph.Bindings.Select(b => b.Id).ToArray());

            // 治理只发生在内存：磁盘文件一字未动
            Assert.Equal(json, File.ReadAllText(path));

            // 用户主动保存一次，落盘的就是治理后的内存图
            var project = Assert.IsType<DirectorProject>(result.Project);
            Assert.Null(ProjectStore.Save(project, path));

            var saved = JObject.Parse(File.ReadAllText(path));
            var savedNodeIds = ((JArray)saved["SignalGraph"]!["Nodes"]!)
                .Select(n => n["Id"]!.ToString())
                .ToArray();
            Assert.Equal(new[] { "n_known", "n_add", "n_out" }, savedNodeIds);
            Assert.DoesNotContain("n_future", File.ReadAllText(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void UnparseableBindingAddressAndUnknownParamKeyAreDropped()
    {
        var path = NewProjectPath();
        try
        {
            const string json = """
            {
              "SchemaVersion": 1,
              "Name": "坏地址与未知键",
              "SignalGraph": {
                "Nodes": [
                  { "Id": "n_c", "Kind": 0, "Enabled": true,
                    "Params": { "value": 1.0, "unit": 1, "bogusKey": 7.0 },
                    "Texts": { "bogusText": "x" } }
                ],
                "Bindings": [
                  { "Id": "b_bad", "NodeId": "n_c", "Address": "bogus" },
                  { "Id": "b_ok", "NodeId": "n_c", "Address": "effect:fx_1.intensity" }
                ]
              }
            }
            """;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);

            var result = ProjectStore.Load(path);

            Assert.Null(result.Fault);
            var graph = Assert.IsType<DirectorProject>(result.Project).SignalGraph;

            var node = Assert.Single(graph.Nodes);
            Assert.Equal(2, node.Params.Count);
            Assert.True(node.Params.ContainsKey("value"));
            Assert.True(node.Params.ContainsKey("unit"));
            Assert.False(node.Params.ContainsKey("bogusKey"));
            Assert.Empty(node.Texts);

            var binding = Assert.Single(graph.Bindings);
            Assert.Equal("b_ok", binding.Id);

            // 治理只发生在内存：磁盘文件一字未动
            Assert.Equal(json, File.ReadAllText(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void SignalGraphOverCapsRejectsOpenWithCounts()
    {
        var path = NewProjectPath();
        try
        {
            var project = new DirectorProject { Name = "超容量信号图" };
            for (var i = 0; i < SignalGraph.MaxNodes + 1; i++)
            {
                project.SignalGraph.Nodes.Add(new SignalNode
                {
                    Id = $"n_constant_{i:x6}",
                    Kind = SignalNodeKind.Constant,
                    Params = new Dictionary<string, double> { ["value"] = 0, ["unit"] = 1 },
                });
            }

            Assert.Null(ProjectStore.Save(project, path));
            var onDisk = File.ReadAllBytes(path);

            var result = ProjectStore.Load(path);

            // 超容量 = 拒绝打开整份工程，并在消息里说明数量（不截断保存、不静默丢节点）
            Assert.Null(result.Project);
            Assert.Contains((SignalGraph.MaxNodes + 1).ToString(), result.Fault);
            Assert.Contains(SignalGraph.MaxNodes.ToString(), result.Fault);
            Assert.Equal(onDisk, File.ReadAllBytes(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void NullSignalGraphLoadsAsEmptyGraph()
    {
        var path = NewProjectPath();
        try
        {
            const string json = "{ \"SchemaVersion\": 1, \"Name\": \"空信号图\", \"SignalGraph\": null }";
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);

            var result = ProjectStore.Load(path);

            Assert.Null(result.Fault);
            var loaded = Assert.IsType<DirectorProject>(result.Project);
            Assert.NotNull(loaded.SignalGraph);
            Assert.Empty(loaded.SignalGraph.Nodes);
            Assert.Empty(loaded.SignalGraph.Edges);
            Assert.Empty(loaded.SignalGraph.Bindings);

            // null 只是"还没有图"，不是"文件坏了"，更不该顺手改写磁盘
            Assert.Equal(json, File.ReadAllText(path));
        }
        finally
        {
            Cleanup(path);
        }
    }

    [Fact]
    public void NonFiniteEffectValueIsDroppedOnLoadAndFallsBackToSchemaDefault()
    {
        var path = NewProjectPath();
        try
        {
            var param = EffectCatalog.Get("bloom")!.Params[1];   // intensity

            var effects = new EffectGraph();
            // 反例闸门：EffectNode.Set 走 Math.Clamp，而 NaN 与上下界比较恒为 false、夹不住
            effects.Add("bloom", n => n.Set(param, float.NaN));

            Assert.Null(ProjectStore.Save(new DirectorProject { Effects = effects }, path));

            var result = ProjectStore.Load(path);

            Assert.Null(result.Fault);
            var loaded = Assert.IsType<DirectorProject>(result.Project);
            var node = Assert.Single(loaded.Effects.Nodes);

            var value = node.Value(param);
            Assert.True(float.IsFinite(value), $"非有限值不该读回来：{value}");
            Assert.Equal(param.Default, value);
            Assert.False(node.Values.ContainsKey(param.Key));
        }
        finally
        {
            Cleanup(path);
        }
    }

    // ---------------- 小工具 ----------------

    /// <summary>首版全部 10 种节点各来一个，连成一条能编译、能求值的链（TimelineBaseValue + 音频调制 → Add → Clamp → Multiply → ParameterOutput）。</summary>
    private static SignalGraph BuildAllKindsGraph(string address)
    {
        var graph = new SignalGraph();

        var constantA = Constant(graph, "n_c_a", 1.0);
        var constantB = Constant(graph, "n_c_b", 0.5);

        var baseNode = Node(graph, "n_base", SignalNodeKind.TimelineBaseValue);
        Assert.True(graph.SetText(baseNode, "address", address));

        var time = Node(graph, "n_time", SignalNodeKind.Time);
        var audio = Node(graph, "n_audio", SignalNodeKind.AudioBands);
        var remap = Node(graph, "n_remap", SignalNodeKind.Remap);
        Set(graph, remap, "outUnit", (double)ParamUnit.Dimensionless);
        Set(graph, remap, "outMax", 0.04);

        var add = Node(graph, "n_add", SignalNodeKind.Add);
        var clamp = Node(graph, "n_clamp", SignalNodeKind.Clamp);
        Set(graph, clamp, "max", 3);   // 默认 max=1 会把算例的结果压掉，这里放宽到目标范围
        var multiply = Node(graph, "n_mul", SignalNodeKind.Multiply);
        var lfo = Node(graph, "n_lfo", SignalNodeKind.Lfo);
        var output = Node(graph, "n_out", SignalNodeKind.ParameterOutput);

        Connect(graph, audio, remap, "in");
        Connect(graph, baseNode, add, "a");
        Connect(graph, remap, add, "b");
        Connect(graph, add, clamp, "in");
        Connect(graph, clamp, multiply, "a");
        Connect(graph, constantA, multiply, "b");
        Connect(graph, time, lfo, "time");
        Connect(graph, constantB, lfo, "amplitude");
        Connect(graph, multiply, output, "in");

        Assert.True(graph.TryAddBinding(new SignalBinding { Id = "b_out", NodeId = output.Id, Address = address }, out var error), error);
        return graph;
    }

    private static void AssertGraphEquals(SignalGraph expected, SignalGraph actual)
    {
        Assert.Equal(expected.Nodes.Count, actual.Nodes.Count);
        for (var i = 0; i < expected.Nodes.Count; i++)
        {
            var e = expected.Nodes[i];
            var a = actual.Nodes[i];
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.Kind, a.Kind);
            Assert.Equal(e.Enabled, a.Enabled);
            Assert.Equal(e.Params.Count, a.Params.Count);
            foreach (var (key, value) in e.Params)
            {
                Assert.True(a.Params.TryGetValue(key, out var loaded), $"{e.Id} 缺参数 {key}");
                Assert.Equal(value, loaded);
            }

            Assert.Equal(e.Texts.Count, a.Texts.Count);
            foreach (var (key, value) in e.Texts) Assert.Equal(value, a.Texts[key]);
        }

        Assert.Equal(expected.Edges.Count, actual.Edges.Count);
        for (var i = 0; i < expected.Edges.Count; i++)
        {
            var e = expected.Edges[i];
            var a = actual.Edges[i];
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.FromNode, a.FromNode);
            Assert.Equal(e.FromPort, a.FromPort);
            Assert.Equal(e.ToNode, a.ToNode);
            Assert.Equal(e.ToPort, a.ToPort);
        }

        Assert.Equal(expected.Bindings.Count, actual.Bindings.Count);
        for (var i = 0; i < expected.Bindings.Count; i++)
        {
            var e = expected.Bindings[i];
            var a = actual.Bindings[i];
            Assert.Equal(e.Id, a.Id);
            Assert.Equal(e.NodeId, a.NodeId);
            Assert.Equal(e.Address, a.Address);
        }
    }

    private static SignalNode Node(SignalGraph graph, string id, SignalNodeKind kind)
    {
        var node = graph.CreateNode(kind);
        node.Id = id;
        Assert.True(graph.TryAddNode(node, out var error), error);
        return node;
    }

    /// <summary>Constant 节点：显式单位常量（这里统一用无量纲，链路里逐段保持同单位）。</summary>
    private static SignalNode Constant(SignalGraph graph, string id, double value)
    {
        var node = Node(graph, id, SignalNodeKind.Constant);
        Set(graph, node, "value", value);
        Set(graph, node, "unit", (double)ParamUnit.Dimensionless);
        return node;
    }

    private static void Set(SignalGraph graph, SignalNode node, string name, double value) =>
        Assert.True(graph.SetParam(node, name, value), $"{node.Id}.{name} = {value} 应当被接受");

    private static void Connect(SignalGraph graph, SignalNode from, SignalNode to, string toPort)
    {
        var edge = new SignalEdge
        {
            Id = $"e_{from.Id}_{to.Id}_{toPort}",
            FromNode = from.Id,
            FromPort = "out",
            ToNode = to.Id,
            ToPort = toPort,
        };
        Assert.True(graph.TryAddEdge(edge, out var error), error);
    }

    private static ParamAddress Addr(string text)
    {
        Assert.True(ParamAddress.TryParse(text, out var addr), $"地址不可解析：{text}");
        return addr;
    }

    private static SignalFrameInputs Frame(double transportTime, double bass, DirectorEvaluator? curves) =>
        new(transportTime, transportTime, new AudioBandSnapshot(true, bass, bass, bass, bass), curves);

    private static string NewProjectPath() =>
        Path.Combine(Path.GetTempPath(), "jizura-signalgraph-" + Guid.NewGuid().ToString("N"), "project.jzpv");

    private static void Cleanup(string projectPath)
    {
        var dir = Path.GetDirectoryName(projectPath);
        if (dir is not null && Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
