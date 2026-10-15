using JizuraLive.Pv;
using JizuraLive.Render.Effects;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 信号图（M6/S07 纯数值部分）：编译校验、单位规则、绑定仲裁、帧内求值的有效位与回落、拓扑复用、0 分配。
/// 全部是纯 CPU 用例：无 GPU、无文件、无网络。
/// </summary>
public class SignalGraphTests
{
    // 目标键的单位事实来自 EffectCatalog：只有 bloom/glow 六个 Number 参数标了单位（M6 闸门与解锁路径用到的）。
    private static readonly EffectParam BloomThreshold = EffectCatalog.Get("bloom")!.Params[0]; // Ratio
    private static readonly EffectParam BloomIntensity = EffectCatalog.Get("bloom")!.Params[1]; // Dimensionless
    private static readonly EffectParam BloomRadius = EffectCatalog.Get("bloom")!.Params[2];    // Pixels

    private const string AudioNodeId = "n_audio";

    [Fact]
    public void CatalogAnnotatesOnlyTheGateAndUnlockKeys()
    {
        // M6 只标注闸门与解锁路径用到的六个键；其余参数的 Unspecified 会在绑定时被 fail-closed 拒绝
        Assert.Equal(ParamUnit.Ratio, BloomThreshold.Unit);
        Assert.Equal(ParamUnit.Dimensionless, BloomIntensity.Unit);
        Assert.Equal(ParamUnit.Pixels, BloomRadius.Unit);

        var glow = EffectCatalog.Get("glow")!.Params;
        Assert.Equal(ParamUnit.Ratio, glow[0].Unit);
        Assert.Equal(ParamUnit.Dimensionless, glow[1].Unit);
        Assert.Equal(ParamUnit.Pixels, glow[2].Unit);

        // 未标注的键保持 Unspecified：这是"缺口可见"而不是"按错单位也放行"
        Assert.Equal(ParamUnit.Unspecified, EffectCatalog.Get("chromatic")!.Params[0].Unit);
        Assert.Equal(ParamUnit.Unspecified, EffectCatalog.Get("grain")!.Params[0].Unit);
    }

    [Fact]
    public void CompileRejectsCycleAndReportsResidualAsCycleOrBlocked()
    {
        var graph = new SignalGraph();
        var a = Node(graph, "n_a", SignalNodeKind.Add);
        var b = Node(graph, "n_b", SignalNodeKind.Add);
        var c = Node(graph, "n_c", SignalNodeKind.Add);
        var d = Node(graph, "n_d", SignalNodeKind.Add);

        Connect(graph, a, b, "a");
        Connect(graph, b, c, "a");
        Connect(graph, c, a, "a");   // 环：a → b → c → a
        Connect(graph, b, d, "a");   // d 不在环上，只是被环阻塞

        var result = SignalCompiler.Compile(graph, new EffectGraph());

        Assert.Null(result.Plan);

        var residuals = result.Issues.Where(i => i.Code == "cycle-or-blocked").ToList();
        Assert.Equal(4, residuals.Count);
        Assert.Equal(new[] { "n_a", "n_b", "n_c", "n_d" }, residuals.Select(i => i.NodeId!).ToArray());

        // 措辞必须是「环或受环阻塞」：残余里既有环上节点，也有被环阻塞的下游节点
        Assert.All(residuals, i => Assert.Contains("环或受环阻塞", i.Message));
        Assert.All(residuals, i => Assert.Contains("可能不在环上", i.Message));
        Assert.All(residuals, i => Assert.DoesNotContain("全在环内", i.Message));
    }

    [Fact]
    public void CompileRejectsDanglingPinReferences()
    {
        var effects = new EffectGraph();

        // (a) 边指向不存在的节点 Id
        var danglingNode = new SignalGraph();
        var a1 = Node(danglingNode, "n_a1", SignalNodeKind.Constant);
        danglingNode.Edges.Add(new SignalEdge
        {
            Id = "e1", FromNode = a1.Id, FromPort = "out", ToNode = "n_missing", ToPort = "in",
        });
        AssertIssue(danglingNode, effects, "edge-missing-node");

        // (b) 端口名未在 Kind 声明
        var badPort = new SignalGraph();
        var b1 = Node(badPort, "n_b1", SignalNodeKind.Constant);
        var b2 = Node(badPort, "n_b2", SignalNodeKind.ParameterOutput);
        badPort.Edges.Add(new SignalEdge
        {
            Id = "e1", FromNode = b1.Id, FromPort = "bogus", ToNode = b2.Id, ToPort = "in",
        });
        AssertIssue(badPort, effects, "edge-missing-port");

        // (c) 方向反了：把 Output 当目的地
        var reversed = new SignalGraph();
        var c1 = Node(reversed, "n_c1", SignalNodeKind.Constant);
        var c2 = Node(reversed, "n_c2", SignalNodeKind.Lfo);
        reversed.Edges.Add(new SignalEdge
        {
            Id = "e1", FromNode = c1.Id, FromPort = "out", ToNode = c2.Id, ToPort = "out",
        });
        AssertIssue(reversed, effects, "edge-direction");

        // (d) 自环
        var selfLoop = new SignalGraph();
        var d1 = Node(selfLoop, "n_d1", SignalNodeKind.Add);
        selfLoop.Edges.Add(new SignalEdge
        {
            Id = "e1", FromNode = d1.Id, FromPort = "out", ToNode = d1.Id, ToPort = "a",
        });
        AssertIssue(selfLoop, effects, "edge-self-loop");

        // (e) 同一输入两条边
        var doubleInput = new SignalGraph();
        var e1 = Node(doubleInput, "n_e1", SignalNodeKind.Constant);
        var e2 = Node(doubleInput, "n_e2", SignalNodeKind.Constant);
        var e3 = Node(doubleInput, "n_e3", SignalNodeKind.Add);
        doubleInput.Edges.Add(new SignalEdge { Id = "e1", FromNode = e1.Id, FromPort = "out", ToNode = e3.Id, ToPort = "a" });
        doubleInput.Edges.Add(new SignalEdge { Id = "e2", FromNode = e2.Id, FromPort = "out", ToNode = e3.Id, ToPort = "a" });
        AssertIssue(doubleInput, effects, "edge-multiple-into-input");
    }

    [Fact]
    public void TryAddEdgeRejectsSecondEdgeIntoSameInputAndSelfLoop()
    {
        var graph = new SignalGraph();
        var a = Node(graph, "n_a", SignalNodeKind.Constant);
        var b = Node(graph, "n_b", SignalNodeKind.Constant);
        var add = Node(graph, "n_add", SignalNodeKind.Add);

        Assert.True(graph.TryAddEdge(Edge(a, add, "a", "e1"), out _));
        var edgesBefore = graph.Edges.Count;

        // 第二个连进同一个输入：就地拒绝，图内容一字不变
        Assert.False(graph.TryAddEdge(Edge(b, add, "a", "e2"), out var dupError));
        Assert.Contains("已有一条连线", dupError ?? "");
        Assert.Equal(edgesBefore, graph.Edges.Count);

        // 自环：就地拒绝
        Assert.False(graph.TryAddEdge(Edge(add, add, "a", "e3"), out var loopError));
        Assert.Contains("自环", loopError ?? "");
        Assert.Equal(edgesBefore, graph.Edges.Count);
    }

    [Fact]
    public void CompileRejectsIllegalUnits()
    {
        var effects = new EffectGraph();
        effects.Add("bloom");
        var instance = effects.Nodes[0].InstanceId;
        var threshold = $"effect:{instance}.threshold";  // Ratio
        var radius = $"effect:{instance}.radius";        // Pixels

        // Add(Ratio, Pixels)：两个都有量纲且不同单位
        var badAdd = SignalCompiler.Compile(BinaryChain(threshold, SignalNodeKind.Add, ParamUnit.Ratio, ParamUnit.Pixels), effects);
        Assert.Null(badAdd.Plan);
        Assert.Contains(badAdd.Issues, i => i.Code == "unit-mismatch" && i.NodeId == "n_op");

        // Multiply(Pixels, Degrees)：Multiply 至多一个输入有量纲
        var badMultiply = SignalCompiler.Compile(
            BinaryChain(radius, SignalNodeKind.Multiply, ParamUnit.Pixels, ParamUnit.Degrees), effects);
        Assert.Null(badMultiply.Plan);
        Assert.Contains(badMultiply.Issues, i => i.Code == "unit-mismatch" && i.NodeId == "n_op");

        // Add(Ratio, Ratio) ⇒ 输出 Ratio，能绑到 Ratio 目标
        CompileOk(BinaryChain(threshold, SignalNodeKind.Add, ParamUnit.Ratio, ParamUnit.Ratio), effects);

        // Multiply(Ratio, Dimensionless) ⇒ 输出取那个有量纲的输入（Ratio）
        CompileOk(BinaryChain(threshold, SignalNodeKind.Multiply, ParamUnit.Ratio, ParamUnit.Dimensionless), effects);

        // 同一条 Multiply 输出若真是 Pixels，就绑不上 Ratio 目标 ⇒ 上一条的通过确实在断言"输出是 Ratio"
        var pixelsMultiply = SignalCompiler.Compile(
            BinaryChain(threshold, SignalNodeKind.Multiply, ParamUnit.Pixels, ParamUnit.Dimensionless), effects);
        Assert.Null(pixelsMultiply.Plan);
        Assert.Contains(pixelsMultiply.Issues, i => i.Code == "unit-mismatch" && i.NodeId == "n_out");
    }

    [Fact]
    public void UnspecifiedUnitCanOnlyEnterRemap()
    {
        var effects = new EffectGraph();
        effects.Add("chromatic");  // amount 未标注单位（Unspecified）
        effects.Add("bloom");
        var amount = $"effect:{effects.Nodes[0].InstanceId}.amount";
        var intensity = $"effect:{effects.Nodes[1].InstanceId}.intensity";

        // (1) Unspecified 进 Add ⇒ unit-unspecified
        var intoAdd = new SignalGraph();
        var src = Node(intoAdd, "n_src", SignalNodeKind.TimelineBaseValue);
        SetAddress(intoAdd, src, amount);
        var one = Constant(intoAdd, "n_one", 1, ParamUnit.Dimensionless);
        var add = Node(intoAdd, "n_op", SignalNodeKind.Add);
        Connect(intoAdd, src, add, "a");
        Connect(intoAdd, one, add, "b");
        var addResult = SignalCompiler.Compile(intoAdd, effects);
        Assert.Null(addResult.Plan);
        Assert.Contains(addResult.Issues, i => i.Code == "unit-unspecified" && i.NodeId == "n_op");

        // (2) Unspecified 直接绑定 ⇒ unit-unspecified
        var direct = new SignalGraph();
        var src2 = Node(direct, "n_src", SignalNodeKind.TimelineBaseValue);
        SetAddress(direct, src2, amount);
        var output2 = Node(direct, "n_out", SignalNodeKind.ParameterOutput);
        Connect(direct, src2, output2, "in");
        Bind(direct, output2, intensity);
        var directResult = SignalCompiler.Compile(direct, effects);
        Assert.Null(directResult.Plan);
        Assert.Contains(directResult.Issues, i => i.Code == "unit-unspecified" && i.NodeId == "n_out");

        // (3) Unspecified 进 Remap：合法，输出单位取 outUnit 声明值（Dimensionless ⇒ 可绑 intensity）
        var remapped = new SignalGraph();
        var src3 = Node(remapped, "n_src", SignalNodeKind.TimelineBaseValue);
        SetAddress(remapped, src3, amount);
        var remap = Node(remapped, "n_remap", SignalNodeKind.Remap);
        Set(remapped, remap, "outUnit", (double)ParamUnit.Dimensionless);
        var output3 = Node(remapped, "n_out", SignalNodeKind.ParameterOutput);
        Connect(remapped, src3, remap, "in");
        Connect(remapped, remap, output3, "in");
        Bind(remapped, output3, intensity);
        CompileOk(remapped, effects);
    }

    [Fact]
    public void CompileRejectsBindingToDifferentUnitTarget()
    {
        var effects = new EffectGraph();
        effects.Add("bloom");
        var instance = effects.Nodes[0].InstanceId;
        var intensity = $"effect:{instance}.intensity";  // Dimensionless
        var radius = $"effect:{instance}.radius";        // Pixels

        // Pixels 信号绑 Dimensionless 目标：同值不同单位不误接
        var mismatch = SignalCompiler.Compile(ConstantChain(intensity, 8, ParamUnit.Pixels), effects);
        Assert.Null(mismatch.Plan);
        Assert.Contains(mismatch.Issues, i => i.Code == "unit-mismatch" && i.NodeId == "n_out");

        // Pixels 绑 Pixels 目标：通过
        CompileOk(ConstantChain(radius, 8, ParamUnit.Pixels), effects);

        // 目标单位尚未登记（S06 未标注的键）：fail-closed 拒绝，消息点明"尚未注册单位"
        var unregistered = SignalCompiler.Compile(ConstantChain($"effect:{instance}.threshold", 0.5, ParamUnit.Ratio), effects);
        Assert.NotNull(unregistered.Plan);  // threshold 已标注 Ratio，这条只是对照组

        effects.Add("chromatic");
        var chromaticAmount = $"effect:{effects.Nodes[1].InstanceId}.amount";
        var noUnit = SignalCompiler.Compile(ConstantChain(chromaticAmount, 1, ParamUnit.Dimensionless), effects);
        Assert.Null(noUnit.Plan);
        var noUnitIssue = Assert.Single(noUnit.Issues, i => i.Code == "unit-unspecified");
        Assert.Contains("尚未注册单位", noUnitIssue.Message);
    }

    [Fact]
    public void CompileRejectsMissingKeyDefIdAddressAndNonNumberTarget()
    {
        var effects = new EffectGraph();
        effects.Add("glow");
        effects.Add("bloom");
        effects.Add("grain");
        var glow = effects.Nodes[0].InstanceId;
        var grain = effects.Nodes[2].InstanceId;

        // glow 没有 amount 这个键
        var missingKey = SignalCompiler.Compile(ConstantChain($"effect:{glow}.amount", 1, ParamUnit.Dimensionless), effects);
        Assert.Null(missingKey.Plan);
        Assert.Contains(missingKey.Issues, i => i.Code == "target-missing-key");

        // 用 DefId 当地址：必须点明"DefId 不能代替实例 Id"
        var defId = SignalCompiler.Compile(ConstantChain("effect:bloom.intensity", 1, ParamUnit.Dimensionless), effects);
        Assert.Null(defId.Plan);
        var defIdIssue = Assert.Single(defId.Issues, i => i.Code == "target-missing-instance");
        Assert.Contains("DefId", defIdIssue.Message);
        Assert.Contains("实例 Id", defIdIssue.Message);

        // grain.colored 是 Bool：首版不开放数值引脚
        var notNumber = SignalCompiler.Compile(ConstantChain($"effect:{grain}.colored", 1, ParamUnit.Dimensionless), effects);
        Assert.Null(notNumber.Plan);
        Assert.Contains(notNumber.Issues, i => i.Code == "target-not-number");
    }

    [Fact]
    public void CompileRejectsTwoBindingsToSameTarget()
    {
        var effects = new EffectGraph();
        effects.Add("bloom");
        var intensity = $"effect:{effects.Nodes[0].InstanceId}.intensity";

        var graph = new SignalGraph();
        var first = Constant(graph, "n_c1", 1, ParamUnit.Dimensionless);
        var out1 = Node(graph, "n_o1", SignalNodeKind.ParameterOutput);
        Connect(graph, first, out1, "in");
        Bind(graph, out1, intensity, "b_1");

        var second = Constant(graph, "n_c2", 2, ParamUnit.Dimensionless);
        var out2 = Node(graph, "n_o2", SignalNodeKind.ParameterOutput);
        Connect(graph, second, out2, "in");
        // 直接写 Bindings 列表、不走 TryAddBinding：同目标的第二条绑定在编辑期就被 TryAddBinding 拒了
        // （契约 PV-DIRECTOR-SYSTEM.md:72「一个参数至多一个输出绑定」，与 AssertIssue 里用 Edges.Add
        // 造非法边同一个手法）。编译器这条检查是给「从存档读回来 / 被列表改坏」的图兜底，
        // 只有绕过编辑期入口才测得到。
        graph.Bindings.Add(new SignalBinding { Id = "b_2", NodeId = out2.Id, Address = intensity });

        var result = SignalCompiler.Compile(graph, effects);

        Assert.Null(result.Plan);
        Assert.Contains(result.Issues, i => i.Code == "target-duplicate-writer" && i.BindingId == "b_2");
    }

    [Fact]
    public void CompileOrderIsStableAndOrdinalById()
    {
        var graph = new SignalGraph();
        // 刻意逆序加入：编译顺序必须是 Id 的 Ordinal 序，而不是"加入顺序"
        Node(graph, "n_z", SignalNodeKind.Constant);
        Node(graph, "n_b", SignalNodeKind.Constant);
        Node(graph, "n_a", SignalNodeKind.Constant);
        Node(graph, "n_c", SignalNodeKind.Constant);

        var effects = new EffectGraph();
        var first = CompileOk(graph, effects).Nodes.Select(n => n.Id).ToArray();
        var second = CompileOk(graph, effects).Nodes.Select(n => n.Id).ToArray();

        Assert.Equal(new[] { "n_a", "n_b", "n_c", "n_z" }, first);
        Assert.Equal(first, second);
    }

    [Fact]
    public void CompileRejectsNonFiniteAndIllegalNodeParams()
    {
        var effects = new EffectGraph();

        // Constant value = NaN（手改 JSON / 旧存档形态；编辑入口 SetParam 会当场拒绝非有限值）
        var nanValue = new SignalGraph();
        Constant(nanValue, "n_c", 1, ParamUnit.Dimensionless).Params["value"] = double.NaN;
        AssertIssue(nanValue, effects, "param-not-finite");

        // Remap 输入区间零长度：编辑入口允许（两个值各自合法），编译器报语义错误
        var zeroSpan = new SignalGraph();
        var remap = Node(zeroSpan, "n_remap", SignalNodeKind.Remap);
        Set(zeroSpan, remap, "inMin", 0);
        Set(zeroSpan, remap, "inMax", 0);
        AssertIssue(zeroSpan, effects, "remap-zero-span");

        // Clamp 下界大于上界
        var inverted = new SignalGraph();
        var clamp = Node(inverted, "n_clamp", SignalNodeKind.Clamp);
        Set(inverted, clamp, "min", 2);
        Set(inverted, clamp, "max", 1);
        AssertIssue(inverted, effects, "clamp-inverted");

        // AudioBands band = 7（越出 0..3 的枚举域）
        var badBand = new SignalGraph();
        Node(badBand, AudioNodeId, SignalNodeKind.AudioBands).Params["band"] = 7;
        AssertIssue(badBand, effects, "param-invalid-enum");

        // Constant unit = Unspecified：已定义但不是一种量纲
        var unspecified = new SignalGraph();
        Constant(unspecified, "n_c", 0, ParamUnit.Unspecified);
        AssertIssue(unspecified, effects, "param-invalid-enum");
    }

    [Fact]
    public void UnconnectedOptionalInputUsesDeclaredDefault()
    {
        var effects = new EffectGraph();
        effects.Add("bloom");
        var intensity = $"effect:{effects.Nodes[0].InstanceId}.intensity";

        var graph = new SignalGraph();
        var time = Node(graph, "n_time", SignalNodeKind.Time);
        var lfo = Node(graph, "n_lfo", SignalNodeKind.Lfo);
        var output = Node(graph, "n_out", SignalNodeKind.ParameterOutput);
        Connect(graph, time, lfo, "time");
        Connect(graph, lfo, output, "in");
        Bind(graph, output, intensity);

        var plan = CompileOk(graph, effects);
        var inputs = Frame(0.25, 0.8);
        plan.Evaluate(in inputs);

        // offset/amplitude 未连 ⇒ 取端口声明默认 0 / 1
        Assert.Equal(Math.Sin(2 * Math.PI * 1.0 * 0.25), Resolve(plan, intensity).Value, 12);

        // 接上 amplitude = 0.5 后同一时刻输出减半（连边属结构变化 ⇒ 重编）
        var half = Constant(graph, "n_half", 0.5, ParamUnit.Dimensionless);
        Connect(graph, half, lfo, "amplitude");
        var plan2 = CompileOk(graph, effects);
        plan2.Evaluate(in inputs);

        Assert.Equal(Math.Sin(2 * Math.PI * 1.0 * 0.25) * 0.5, Resolve(plan2, intensity).Value, 12);
    }

    [Fact]
    public void ConnectedButInvalidUpstreamDoesNotUseDefault()
    {
        var effects = new EffectGraph();
        effects.Add("bloom");
        var intensity = $"effect:{effects.Nodes[0].InstanceId}.intensity";

        var graph = new SignalGraph();
        var audio = Node(graph, AudioNodeId, SignalNodeKind.AudioBands);
        var time = Node(graph, "n_time", SignalNodeKind.Time);
        var lfo = Node(graph, "n_lfo", SignalNodeKind.Lfo);
        var output = Node(graph, "n_out", SignalNodeKind.ParameterOutput);
        Connect(graph, audio, lfo, "amplitude");   // 已连，但音频失效
        Connect(graph, time, lfo, "time");
        Connect(graph, lfo, output, "in");
        Bind(graph, output, intensity);

        var plan = CompileOk(graph, effects);
        var invalid = Frame(0.25, 0.8, audioValid: false);
        plan.Evaluate(in invalid);

        var resolved = Resolve(plan, intensity);

        // 上游失效 ⇒ 整个节点无效、回落 base，绝不偷用 amplitude 的默认值 1
        Assert.Equal(ParamValueSource.Base, resolved.Source);
        Assert.Equal((double)BloomIntensity.Default, resolved.Value, 12);
        Assert.Contains(plan.Issues, i => i.Code == "eval-source-invalid" && i.NodeId == AudioNodeId);
    }

    [Fact]
    public void UnlockPathMatchesDocumentedArithmetic()
    {
        var effects = new EffectGraph();
        effects.Add("bloom");
        var address = $"effect:{effects.Nodes[0].InstanceId}.intensity";

        // 契约 §3.3 算例：0s/10s 两个 Linear Key 为 1.0/1.2
        var curves = new DirectorEvaluator();
        var curve = new FloatCurve();
        Assert.True(curve.TryAdd(0.0, 1.0));
        Assert.True(curve.TryAdd(10.0, 1.2));
        curves.SetCurve(Addr(address), curve);

        var graph = UnlockPath(effects, address);
        var plan = CompileOk(graph, effects);

        var inputs = Frame(5.0, 0.8, curves: curves);
        plan.Evaluate(in inputs);
        var resolved = Resolve(plan, address);

        // 5s：base = 1.1，有效 bass 0.8 经 Remap(0..1 → 0..0.04) 得 0.032，最终 1.132
        Assert.Equal(ParamValueSource.Signal, resolved.Source);
        Assert.False(resolved.Clamped);
        Assert.Equal(1.132, resolved.Value, 12);

        // 同一张图改成输入失效：回到 1.1，而不是 0
        var invalid = Frame(5.0, 0.8, audioValid: false, curves: curves);
        plan.Evaluate(in invalid);
        var fallback = Resolve(plan, address);

        Assert.Equal(ParamValueSource.Base, fallback.Source);
        Assert.Equal(1.1, fallback.Value, 12);
        Assert.NotEqual(0.0, fallback.Value);

        // 两次求值之后：曲线两个 Key 与实例常量一字未改
        Assert.Equal(2, curve.Count);
        Assert.Equal(1.0, curve.Keys[0].Value);
        Assert.Equal(1.2, curve.Keys[1].Value);
        Assert.False(effects.Nodes[0].Values.ContainsKey(BloomIntensity.Key));
    }

    [Fact]
    public void ValidZeroPropagatesAsRealValue()
    {
        var effects = new EffectGraph();
        effects.Add("bloom");
        var address = $"effect:{effects.Nodes[0].InstanceId}.intensity";

        var plan = CompileOk(UnlockPath(effects, address), effects);
        var inputs = Frame(5.0, bass: 0.0);   // 有效位 true 且 Bass 恰好是 0
        plan.Evaluate(in inputs);

        var resolved = Resolve(plan, address);

        // 真 0 不是"无数据"：来源仍是信号，值等于 base（此处等于实例常量默认 1.1）
        Assert.Equal(ParamValueSource.Signal, resolved.Source);
        Assert.Equal((double)BloomIntensity.Default, resolved.Value, 12);
        Assert.DoesNotContain(plan.Issues, i => i.Code == "eval-source-invalid");
    }

    [Fact]
    public void InvalidSourceFallsBackToBaseAndReportsNodeId()
    {
        var effects = new EffectGraph();
        effects.Add("bloom");
        var address = $"effect:{effects.Nodes[0].InstanceId}.intensity";

        var plan = CompileOk(UnlockPath(effects, address), effects);
        var invalid = Frame(5.0, 0.8, audioValid: false);
        plan.Evaluate(in invalid);

        var resolved = Resolve(plan, address);
        Assert.Equal(ParamValueSource.Base, resolved.Source);
        Assert.Equal((double)BloomIntensity.Default, resolved.Value, 12);

        // 失效必须能定位到那个 AudioBands 节点
        var issue = Assert.Single(plan.Issues, i => i.Code == "eval-source-invalid");
        Assert.Equal(AudioNodeId, issue.NodeId);
    }

    [Fact]
    public void NonFiniteDynamicValueIsInvalidAndResolvedStaysFinite()
    {
        var effects = new EffectGraph();
        effects.Add("bloom");
        var address = $"effect:{effects.Nodes[0].InstanceId}.intensity";

        // (1) 音频快照是 NaN，但自称有效
        var plan = CompileOk(UnlockPath(effects, address), effects);
        plan.Evaluate(Frame(5.0, bass: double.NaN));
        var resolved = Resolve(plan, address);

        Assert.Equal(ParamValueSource.Base, resolved.Source);
        Assert.Contains(plan.Issues, i => i.Code == "eval-non-finite");

        // (2) 1e308 + 1e308 溢出成 +∞
        var overflow = new SignalGraph();
        var big1 = Constant(overflow, "n_big1", 1e308, ParamUnit.Dimensionless);
        var big2 = Constant(overflow, "n_big2", 1e308, ParamUnit.Dimensionless);
        var add = Node(overflow, "n_add", SignalNodeKind.Add);
        var output = Node(overflow, "n_out", SignalNodeKind.ParameterOutput);
        Connect(overflow, big1, add, "a");
        Connect(overflow, big2, add, "b");
        Connect(overflow, add, output, "in");
        Bind(overflow, output, address);

        var overflowPlan = CompileOk(overflow, effects);
        overflowPlan.Evaluate(Frame(0.0, 0.0));

        Assert.Contains(overflowPlan.Issues, i => i.Code == "eval-non-finite" && i.NodeId == "n_add");
        Assert.Equal(ParamValueSource.Base, Resolve(overflowPlan, address).Source);

        // 不变量：Resolved 里每个值恒为有限
        Assert.All(plan.Resolved, p => Assert.True(double.IsFinite(p.Value)));
        Assert.All(overflowPlan.Resolved, p => Assert.True(double.IsFinite(p.Value)));
    }

    [Fact]
    public void NonFiniteBaseFallsBackToTargetDefault()
    {
        var effects = new EffectGraph();
        effects.Add("bloom");
        var address = $"effect:{effects.Nodes[0].InstanceId}.intensity";

        // 反例闸门：曲线本身带 NaN（曲线载入边界的治理不在本线，但求值不能把它送出去）
        var curves = new DirectorEvaluator();
        var poisoned = new FloatCurve();
        Assert.True(poisoned.TryAdd(0.0, double.NaN));
        curves.SetCurve(Addr(address), poisoned);

        var plan = CompileOk(UnlockPath(effects, address), effects);
        plan.Evaluate(Frame(0.0, 0.8, audioValid: false, curves: curves));

        var resolved = Resolve(plan, address);

        Assert.Equal(ParamValueSource.TargetDefault, resolved.Source);
        Assert.Equal((double)BloomIntensity.Default, resolved.Value, 12);
        Assert.True(double.IsFinite(resolved.Value));
        Assert.Contains(plan.Issues, i => i.Code == "eval-base-non-finite");

        // 文档未被改写：坏 key 还在，实例常量从未被写入
        Assert.Equal(1, poisoned.Count);
        Assert.True(double.IsNaN(poisoned.Keys[0].Value));
        Assert.False(effects.Nodes[0].Values.ContainsKey(BloomIntensity.Key));
    }

    [Fact]
    public void ClampToTargetRangeAndFlagClampedWithoutWritingBack()
    {
        var effects = new EffectGraph();
        effects.Add("bloom");
        var address = $"effect:{effects.Nodes[0].InstanceId}.intensity";

        var curves = new DirectorEvaluator();
        var curve = new FloatCurve();
        Assert.True(curve.TryAdd(0.0, 1.1));
        curves.SetCurve(Addr(address), curve);

        // Remap 输出区间放大到 0..4：1.1 + 0.8×4 = 4.3，越出 intensity 的 0..3
        var plan = CompileOk(UnlockPath(effects, address, outMax: 4.0), effects);
        plan.Evaluate(Frame(0.0, 0.8, curves: curves));

        var resolved = Resolve(plan, address);

        Assert.Equal(ParamValueSource.Signal, resolved.Source);
        Assert.Equal((double)BloomIntensity.Max, resolved.Value);
        Assert.True(resolved.Clamped);

        // 限幅只作用于这一帧的输出，不回写文档
        Assert.Equal(1.1, curve.Evaluate(0.0));
        Assert.False(effects.Nodes[0].Values.ContainsKey(BloomIntensity.Key));
    }

    [Fact]
    public void DisabledNodeOutputIsInvalidAndFallsBackToBase()
    {
        var effects = new EffectGraph();
        effects.Add("bloom");
        var address = $"effect:{effects.Nodes[0].InstanceId}.intensity";

        // (1) 上游 Add 被禁用
        var graph = UnlockPath(effects, address);
        var add = graph.Nodes.Single(n => n.Id == "n_add");
        add.Enabled = false;

        var plan = CompileOk(graph, effects);
        plan.Evaluate(Frame(5.0, 0.8));

        Assert.Equal(ParamValueSource.Base, Resolve(plan, address).Source);
        // 禁用是显式状态：不记 issue（不是 Bypass，也不是错误）
        Assert.DoesNotContain(plan.Issues, i => i.Code == "eval-required-unconnected");

        // (2) 出口节点自己被禁用
        add.Enabled = true;
        graph.Nodes.Single(n => n.Id == "n_out").Enabled = false;

        var plan2 = CompileOk(graph, effects);
        plan2.Evaluate(Frame(5.0, 0.8));

        Assert.Equal(ParamValueSource.Base, Resolve(plan2, address).Source);
    }

    [Fact]
    public void TimeNodeSelectsTransportOrLiveWithoutAccumulation()
    {
        var graph = new SignalGraph();
        Node(graph, "n_transport", SignalNodeKind.Time);   // source 默认 0 = Transport
        var live = Node(graph, "n_live", SignalNodeKind.Time);
        Set(graph, live, "source", 1);                     // 1 = Live

        var plan = CompileOk(graph, new EffectGraph());
        var inputs = new SignalFrameInputs(5.0, 11.0, new AudioBandSnapshot(true, 0.1, 0.2, 0.3, 0.4), null);
        plan.Evaluate(in inputs);

        Assert.Equal(5.0, Value(plan, "n_transport"));
        Assert.Equal(11.0, Value(plan, "n_live"));

        // 同一输入重复求值逐位相同
        plan.Evaluate(in inputs);
        Assert.Equal(5.0, Value(plan, "n_transport"));

        // 先求 t=5 再求 t=1，与直接求 t=1 逐位相同：纯函数、无按帧累计状态
        var five = new SignalFrameInputs(5.0, 11.0, new AudioBandSnapshot(true, 0, 0, 0, 0), null);
        var one = new SignalFrameInputs(1.0, 12.0, new AudioBandSnapshot(true, 0, 0, 0, 0), null);
        plan.Evaluate(in five);
        plan.Evaluate(in one);

        var fresh = CompileOk(graph, new EffectGraph());
        fresh.Evaluate(in one);

        Assert.Equal(BitConverter.DoubleToInt64Bits(Value(fresh, "n_transport")),
            BitConverter.DoubleToInt64Bits(Value(plan, "n_transport")));
        Assert.Equal(BitConverter.DoubleToInt64Bits(Value(fresh, "n_live")),
            BitConverter.DoubleToInt64Bits(Value(plan, "n_live")));
    }

    [Fact]
    public void StructureEditRequiresRecompileButParamChangeReusesTopology()
    {
        var effects = new EffectGraph();
        effects.Add("bloom");
        var address = $"effect:{effects.Nodes[0].InstanceId}.intensity";

        var graph = new SignalGraph();
        var constant = Constant(graph, "n_c", 1.0, ParamUnit.Dimensionless);
        var lfo = Node(graph, "n_lfo", SignalNodeKind.Lfo);   // offset 先空着，稍后用一条新边测试结构变化
        var output = Node(graph, "n_out", SignalNodeKind.ParameterOutput);
        Connect(graph, constant, output, "in");
        Bind(graph, output, address);

        var plan = CompileOk(graph, effects);
        var nodesBefore = plan.Nodes;
        var valuesBefore = plan.Values;
        var orderBefore = plan.Nodes.Select(n => n.Id).ToArray();

        // 改数值参数：SetParam 不动结构版本 ⇒ 复用拓扑，槽位数组引用不变
        Set(graph, constant, "value", 2.5);
        Assert.True(plan.TryApplyParams(graph, effects, out var issues));
        Assert.Empty(issues);
        Assert.Same(nodesBefore, plan.Nodes);
        Assert.Same(valuesBefore, plan.Values);
        Assert.Equal(orderBefore, plan.Nodes.Select(n => n.Id).ToArray());

        plan.Evaluate(Frame(0.0, 0.0));
        Assert.Equal(2.5, Resolve(plan, address).Value);

        // 新增一条边：结构版本变化 ⇒ 计划已过期，必须重编
        Assert.True(graph.TryAddEdge(Edge(constant, lfo, "offset", "e_lfo"), out var edgeError), edgeError);
        Assert.False(plan.TryApplyParams(graph, effects, out var stale));
        Assert.Contains(stale, i => i.Code == "structure-changed");

        // 重编后一切照旧（新拓扑里多了一个 Lfo 节点）
        var recompiled = CompileOk(graph, effects);
        Assert.Contains(recompiled.Nodes, n => n.Id == "n_lfo");
    }

    [Fact]
    public void TryApplyParamsRejectsStaleTargetsAndIllegalParams()
    {
        // (1) 被绑定的实例从效果图里删掉
        var effects = new EffectGraph();
        effects.Add("bloom");
        var address = $"effect:{effects.Nodes[0].InstanceId}.intensity";
        var graph = ConstantChain(address, 1.5, ParamUnit.Dimensionless);
        var plan = CompileOk(graph, effects);

        Assert.True(effects.Remove("bloom"));
        Assert.False(plan.TryApplyParams(graph, effects, out var stale));
        Assert.Contains(stale, i => i.Code == "target-missing-instance");

        // (2) 参数改成非法（Remap 零区间）：返回 false 且计划不被污染
        var effects2 = new EffectGraph();
        effects2.Add("bloom");
        var address2 = $"effect:{effects2.Nodes[0].InstanceId}.intensity";

        var graph2 = new SignalGraph();
        var constant = Constant(graph2, "n_c", 1.0, ParamUnit.Dimensionless);
        var remap = Node(graph2, "n_remap", SignalNodeKind.Remap);
        var output = Node(graph2, "n_out", SignalNodeKind.ParameterOutput);
        Connect(graph2, constant, remap, "in");
        Connect(graph2, remap, output, "in");
        Bind(graph2, output, address2);
        var plan2 = CompileOk(graph2, effects2);

        plan2.Evaluate(Frame(0.0, 0.0));
        var before = Resolve(plan2, address2).Value;

        Set(graph2, remap, "inMax", 0);
        Assert.False(plan2.TryApplyParams(graph2, effects2, out var illegal));
        Assert.Contains(illegal, i => i.Code == "remap-zero-span");

        plan2.Evaluate(Frame(0.0, 0.0));
        Assert.Equal(before, Resolve(plan2, address2).Value);
    }

    [Fact]
    public void DoubleBloomInstancesResolveIndependently()
    {
        var effects = new EffectGraph();
        effects.Add("bloom");
        effects.Add("bloom");

        var first = effects.Nodes[0].InstanceId;
        var second = effects.Nodes[1].InstanceId;
        Assert.NotEqual(first, second);

        var addressA = $"effect:{first}.intensity";
        var addressB = $"effect:{second}.intensity";

        var graph = new SignalGraph();
        var ca = Constant(graph, "n_ca", 1.5, ParamUnit.Dimensionless);
        var oa = Node(graph, "n_oa", SignalNodeKind.ParameterOutput);
        Connect(graph, ca, oa, "in");
        Bind(graph, oa, addressA, "b_a");

        var cb = Constant(graph, "n_cb", 2.5, ParamUnit.Dimensionless);
        var ob = Node(graph, "n_ob", SignalNodeKind.ParameterOutput);
        Connect(graph, cb, ob, "in");
        Bind(graph, ob, addressB, "b_b");

        var plan = CompileOk(graph, effects);

        Assert.True(plan.TryGetTarget(Addr(addressA), out var nodeA, out var paramA));
        Assert.True(plan.TryGetTarget(Addr(addressB), out var nodeB, out var paramB));
        Assert.Same(effects.Nodes[0], nodeA);
        Assert.Same(effects.Nodes[1], nodeB);
        Assert.NotSame(nodeA, nodeB);
        Assert.Equal(BloomIntensity.Key, paramA.Key);
        Assert.Equal(BloomIntensity.Key, paramB.Key);

        plan.Evaluate(Frame(0.0, 0.0));
        Assert.Equal(1.5, Resolve(plan, addressA).Value);
        Assert.Equal(2.5, Resolve(plan, addressB).Value);
    }

    [Fact]
    public void RemovingTargetCascadesBindingsAndBaseValueNodes()
    {
        var effects = new EffectGraph();
        effects.Add("bloom");
        effects.Add("glow");
        var bloom = effects.Nodes[0].InstanceId;
        var glow = effects.Nodes[1].InstanceId;
        var bloomAddress = $"effect:{bloom}.intensity";
        var glowAddress = $"effect:{glow}.intensity";

        var graph = new SignalGraph();
        var baseNode = Node(graph, "n_base", SignalNodeKind.TimelineBaseValue);
        SetAddress(graph, baseNode, bloomAddress);

        var audio = Node(graph, AudioNodeId, SignalNodeKind.AudioBands);
        var remap = Node(graph, "n_remap", SignalNodeKind.Remap);
        Set(graph, remap, "outUnit", (double)ParamUnit.Dimensionless);

        var add = Node(graph, "n_add", SignalNodeKind.Add);
        var output = Node(graph, "n_out", SignalNodeKind.ParameterOutput);

        Connect(graph, baseNode, add, "a");
        Connect(graph, audio, remap, "in");
        Connect(graph, remap, add, "b");
        Connect(graph, add, output, "in");

        Bind(graph, output, bloomAddress, "b_bloom");
        Bind(graph, output, glowAddress, "b_glow");

        CompileOk(graph, effects);   // 级联之前先确认这张图本来是合法的

        var removed = graph.RemoveTargetCascade(bloom);

        Assert.True(removed >= 2);
        Assert.DoesNotContain(graph.Bindings, b => b.Address == bloomAddress);
        Assert.DoesNotContain(graph.Nodes, n => n.Id == "n_base");
        Assert.DoesNotContain(graph.Edges, e => e.FromNode == "n_base" || e.ToNode == "n_base");

        // 其它节点/绑定保留：删一个实例不等于删整张图
        Assert.Contains(graph.Bindings, b => b.Address == glowAddress);
        Assert.Contains(graph.Nodes, n => n.Id == AudioNodeId);
        Assert.Contains(graph.Nodes, n => n.Id == "n_add");

        // 级联后不留悬空：编译通过（Add.a 缺输入只是"输出无效"的警告）
        var result = SignalCompiler.Compile(graph, effects);
        Assert.NotNull(result.Plan);
        Assert.DoesNotContain(result.Issues, i => i.Severity == SignalIssueSeverity.Error);
        Assert.Contains(result.Issues, i => i.Code == "required-input-unconnected" && i.NodeId == "n_add");
    }

    [Fact]
    public void CompileWarnsOnUnconnectedRequiredInputButStillCompiles()
    {
        var effects = new EffectGraph();
        effects.Add("bloom");
        var address = $"effect:{effects.Nodes[0].InstanceId}.intensity";

        var graph = new SignalGraph();
        var constant = Constant(graph, "n_c", 1.0, ParamUnit.Dimensionless);
        var add = Node(graph, "n_add", SignalNodeKind.Add);
        var output = Node(graph, "n_out", SignalNodeKind.ParameterOutput);
        Connect(graph, constant, add, "a");    // b 故意不连
        Connect(graph, add, output, "in");
        Bind(graph, output, address);

        var result = SignalCompiler.Compile(graph, effects);

        Assert.NotNull(result.Plan);
        Assert.DoesNotContain(result.Issues, i => i.Severity == SignalIssueSeverity.Error);

        var warning = Assert.Single(result.Issues, i => i.Code == "required-input-unconnected");
        Assert.Equal("n_add", warning.NodeId!);

        // 求值时该节点输出无效（不是拒绝编译）：绑定回落 base
        var plan = result.Plan!;
        plan.Evaluate(Frame(5.0, 0.8));

        Assert.Equal(ParamValueSource.Base, Resolve(plan, address).Source);
        Assert.Contains(plan.Issues, i => i.Code == "eval-required-unconnected" && i.NodeId == "n_add");
    }

    [Fact]
    public void EvaluateDoesNotAllocateAfterWarmup()
    {
        var effects = new EffectGraph();
        effects.Add("bloom");
        var address = $"effect:{effects.Nodes[0].InstanceId}.intensity";

        var curves = new DirectorEvaluator();
        var curve = new FloatCurve();
        Assert.True(curve.TryAdd(0.0, 1.0));
        Assert.True(curve.TryAdd(10.0, 1.2));
        curves.SetCurve(Addr(address), curve);

        var plan = CompileOk(UnlockPath(effects, address), effects);
        var inputs = Frame(5.0, 0.8, curves: curves);

        // 预热：让 Resolved/Issues 这两个复用 List 的容量涨到位
        for (var i = 0; i < 8; i++) plan.Evaluate(in inputs);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) plan.Evaluate(in inputs);
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(0L, after - before);
    }

    // ---------------- 构造与断言的小工具（刻意保持直白，让每个用例自己说明想表达什么）----------------

    /// <summary>契约 §3.3 的解锁路径算例：TimelineBaseValue(address) + Remap(AudioBands.Bass, 0..1 → 0..outMax) → Add → ParameterOutput。</summary>
    private static SignalGraph UnlockPath(EffectGraph effects, string address, double outMax = 0.04)
    {
        var graph = new SignalGraph();

        var audio = Node(graph, AudioNodeId, SignalNodeKind.AudioBands);   // band 默认 1 = Bass
        var baseNode = Node(graph, "n_base", SignalNodeKind.TimelineBaseValue);
        SetAddress(graph, baseNode, address);

        var remap = Node(graph, "n_remap", SignalNodeKind.Remap);
        Set(graph, remap, "inMin", 0);
        Set(graph, remap, "inMax", 1);
        Set(graph, remap, "outMin", 0);
        Set(graph, remap, "outMax", outMax);
        Set(graph, remap, "outUnit", (double)ParamUnit.Dimensionless);

        var add = Node(graph, "n_add", SignalNodeKind.Add);
        var output = Node(graph, "n_out", SignalNodeKind.ParameterOutput);

        Connect(graph, audio, remap, "in");
        Connect(graph, baseNode, add, "a");
        Connect(graph, remap, add, "b");
        Connect(graph, add, output, "in");
        Bind(graph, output, address);

        return graph;
    }

    /// <summary>两个常量经一个二元节点（Add/Multiply）再绑到目标。</summary>
    private static SignalGraph BinaryChain(string address, SignalNodeKind kind, ParamUnit unitA, ParamUnit unitB)
    {
        var graph = new SignalGraph();
        var a = Constant(graph, "n_a", 1, unitA);
        var b = Constant(graph, "n_b", 1, unitB);
        var op = Node(graph, "n_op", kind);
        var output = Node(graph, "n_out", SignalNodeKind.ParameterOutput);

        Connect(graph, a, op, "a");
        Connect(graph, b, op, "b");
        Connect(graph, op, output, "in");
        Bind(graph, output, address);

        return graph;
    }

    /// <summary>Constant → ParameterOutput → 绑定 address 的最小链路。</summary>
    private static SignalGraph ConstantChain(string address, double value, ParamUnit unit)
    {
        var graph = new SignalGraph();
        var constant = Constant(graph, "n_c", value, unit);
        var output = Node(graph, "n_out", SignalNodeKind.ParameterOutput);

        Connect(graph, constant, output, "in");
        Bind(graph, output, address);

        return graph;
    }

    /// <summary>造节点：CreateNode 发号后改名成测试用的可读 Id（Ordinal 顺序断言要看它）。</summary>
    private static SignalNode Node(SignalGraph graph, string id, SignalNodeKind kind)
    {
        var node = graph.CreateNode(kind);
        node.Id = id;
        Assert.True(graph.TryAddNode(node, out var error), error);
        return node;
    }

    private static SignalNode Constant(SignalGraph graph, string id, double value, ParamUnit unit)
    {
        var node = Node(graph, id, SignalNodeKind.Constant);
        Set(graph, node, "value", value);
        Set(graph, node, "unit", (double)unit);
        return node;
    }

    private static void Set(SignalGraph graph, SignalNode node, string name, double value) =>
        Assert.True(graph.SetParam(node, name, value), $"{node.Id}.{name} = {value} 应当被接受");

    private static void SetAddress(SignalGraph graph, SignalNode node, string address) =>
        Assert.True(graph.SetText(node, "address", address), $"{node.Id}.address 应当可设");

    private static SignalEdge Edge(SignalNode from, SignalNode to, string toPort, string id, string fromPort = "out") =>
        new() { Id = id, FromNode = from.Id, FromPort = fromPort, ToNode = to.Id, ToPort = toPort };

    private static void Connect(SignalGraph graph, SignalNode from, SignalNode to, string toPort, string fromPort = "out") =>
        Assert.True(graph.TryAddEdge(Edge(from, to, toPort, $"e_{from.Id}_{to.Id}_{toPort}", fromPort), out var error), error);

    private static void Bind(SignalGraph graph, SignalNode output, string address, string id = "b_out") =>
        Assert.True(graph.TryAddBinding(new SignalBinding { Id = id, NodeId = output.Id, Address = address }, out var error), error);

    private static void AssertIssue(SignalGraph graph, EffectGraph effects, string code)
    {
        var result = SignalCompiler.Compile(graph, effects);
        Assert.Null(result.Plan);
        Assert.Contains(result.Issues, i => i.Code == code);
    }

    private static CompiledSignalGraph CompileOk(SignalGraph graph, EffectGraph effects)
    {
        var result = SignalCompiler.Compile(graph, effects);
        Assert.True(result.Plan is not null,
            "编译失败：" + string.Join("；", result.Issues.Select(i => $"{i.Code}({i.NodeId}): {i.Message}")));
        return result.Plan!;
    }

    private static ResolvedParam Resolve(CompiledSignalGraph plan, string address)
    {
        var addr = Addr(address);
        Assert.True(plan.TryGetResolved(addr, out var resolved), $"没有 {address} 的求值结果");
        return resolved;
    }

    private static double Value(CompiledSignalGraph plan, string nodeId)
    {
        for (var i = 0; i < plan.Nodes.Count; i++)
            if (plan.Nodes[i].Id == nodeId)
                return plan.Values[i];

        Assert.Fail($"编译结果里没有节点 {nodeId}");
        return 0;
    }

    private static ParamAddress Addr(string text)
    {
        Assert.True(ParamAddress.TryParse(text, out var addr), $"地址不可解析：{text}");
        return addr;
    }

    private static SignalFrameInputs Frame(double transportTime, double bass, bool audioValid = true, DirectorEvaluator? curves = null) =>
        new(transportTime, transportTime, new AudioBandSnapshot(audioValid, bass, bass, bass, bass), curves);
}
