using JizuraLive.Pv;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 关键帧数据层（<see cref="ParamAddress"/> / <see cref="FloatCurve"/> / <see cref="DirectorEvaluator"/>）的纯模型测试，
/// 不需要 GPU / 窗口。
/// 时间口径：double 秒，与 <see cref="PvFrameContext.Time"/> 同一根时间轴。
/// </summary>
public class DirectorCurveTests
{
    // ---------- 空曲线 ----------

    [Fact]
    public void EmptyCurveThrowsOnEvaluate()
    {
        var curve = new FloatCurve();
        Assert.Equal(0, curve.Count);

        // 没有 key 的"曲线"没有值可取：静默返回 0 会把"镜头缩放到 0"这种结果混进渲染
        Assert.Throws<InvalidOperationException>(() => curve.Evaluate(0));
    }

    [Fact]
    public void EvaluatorWithoutCurveReportsNoValue()
    {
        var eval = new DirectorEvaluator();
        var addr = Address("camera.scale");

        Assert.False(eval.TryEvaluate(addr, 3.5, out var value));
        Assert.Equal(0, value);

        // 空曲线同样返回 false：帧路径每个参数都会走这里，一条还没打过 key 的曲线不该把整帧抛穿
        eval.SetCurve(addr, new FloatCurve());
        Assert.False(eval.TryEvaluate(addr, 3.5, out _));
    }

    // ---------- 单 key / 重复 / 相邻 ----------

    [Fact]
    public void SingleKeyIsConstantEverywhere()
    {
        var curve = new FloatCurve();
        Assert.True(curve.TryAdd(5, 2.5));

        Assert.Equal(2.5, curve.Evaluate(0));
        Assert.Equal(2.5, curve.Evaluate(5));
        Assert.Equal(2.5, curve.Evaluate(1000));
    }

    [Fact]
    public void DuplicateTimeIsRejectedAndCurveUnchanged()
    {
        var curve = new FloatCurve();
        Assert.True(curve.TryAdd(1, 1));
        Assert.True(curve.TryAdd(2, 2));

        Assert.False(curve.TryAdd(1, 9));         // 同一时刻已有 key：拒绝
        Assert.False(curve.TryAdd(1.0000005, 9)); // 1µs 窗口内的"几乎同一时刻"也算重复
        Assert.Equal(2, curve.Count);
        Assert.Equal(1, curve.Evaluate(1));       // 曲线内容一字未改
        Assert.Equal(2, curve.Evaluate(2));
    }

    [Fact]
    public void KeysOneMillisecondApartAreAllowed()
    {
        // 判定窗口 1µs 必须远小于毫秒：LRC 是毫秒精度，相邻 1ms 的两个 key 是合法编辑
        var curve = new FloatCurve();
        Assert.True(curve.TryAdd(0.000, 0));
        Assert.True(curve.TryAdd(0.001, 1));
        Assert.Equal(2, curve.Count);
        Assert.Equal(0.5, curve.Evaluate(0.0005)); // 线性中点
    }

    [Fact]
    public void RemoveDeletesKeyWithinTimeWindow()
    {
        var curve = Curve(CurveInterp.Linear, (1, 5), (2, 7), (3, 9));

        Assert.False(curve.Remove(1.5));      // 该时刻没有 key：曲线不变
        Assert.Equal(3, curve.Count);

        Assert.True(curve.Remove(2.0000002)); // 1µs 窗口内视为同一个 key
        Assert.Equal(2, curve.Count);
        Assert.Equal(1, curve.Keys[0].Time);  // 剩下的 key 顺序不乱
        Assert.Equal(3, curve.Keys[1].Time);
    }

    // ---------- 首尾精确命中 ----------

    [Theory]
    [InlineData(CurveInterp.Step)]
    [InlineData(CurveInterp.Linear)]
    [InlineData(CurveInterp.SmoothStep)]
    [InlineData(CurveInterp.Bezier)]
    [InlineData(CurveInterp.Spring)]
    public void KeyTimesReturnKeyValuesExactly(CurveInterp mode)
    {
        var curve = Curve(mode, (0.1, -1.1), (1.7, 2.7), (4.05, 0.3), (9.25, -0.7));

        // 逐个 key 精确命中。这里断言的是**不带精度参数**的重载 = 精确相等（不是容差比较）：
        // 首 key 走钳制分支、中间 key 走二分分支、末 key 走钳制分支，三条路都不许"差不多"。
        foreach (var key in curve.Keys)
            Assert.Equal(key.Value, curve.Evaluate(key.Time));
    }

    // ---------- 五种插值 ----------

    [Fact]
    public void StepChangesOnlyAtTheNextKeyTime()
    {
        // 区间插值用左 key 的 Mode：k0 是 Step，则 [k0, k1) 全程保持 k0 的值
        var curve = Curve(CurveInterp.Step, (0, 0), (2, 10), (4, 20));

        Assert.Equal(0, curve.Evaluate(0));
        Assert.Equal(0, curve.Evaluate(1.999));
        Assert.Equal(10, curve.Evaluate(2));  // 恰好在 k1 的时刻才突变
        Assert.Equal(10, curve.Evaluate(3.5));
        Assert.Equal(20, curve.Evaluate(4));
        Assert.Equal(20, curve.Evaluate(99));
    }

    [Fact]
    public void SegmentUsesLeftKeyModeNotRightKeyMode()
    {
        var curve = new FloatCurve();
        Assert.True(curve.TryAdd(0, 0, CurveInterp.Step));
        Assert.True(curve.TryAdd(1, 100, CurveInterp.Linear));
        Assert.True(curve.TryAdd(2, 200, CurveInterp.Step));

        Assert.Equal(0, curve.Evaluate(0.5));     // [0,1) 用 k0 的 Step：整段保持 0，不提前向 k1 爬
        Assert.Equal(150, curve.Evaluate(1.5));   // [1,2) 用 k1 的 Linear：整段线性爬向 k2
    }

    [Fact]
    public void LinearMidpointIsAverage()
    {
        var curve = Curve(CurveInterp.Linear, (0, 0), (2, 10));

        Assert.Equal(5, curve.Evaluate(1));
        Assert.Equal(2.5, curve.Evaluate(0.5));
    }

    [Fact]
    public void SmoothStepIsEasedAndHitsMidpoint()
    {
        var curve = Curve(CurveInterp.SmoothStep, (0, 0), (1, 1));

        Assert.Equal(0.5, curve.Evaluate(0.5));      // 中点 = 线性中值
        Assert.Equal(0.15625, curve.Evaluate(0.25)); // u=0.25 → 3u²-2u³ = 0.15625
        Assert.True(curve.Evaluate(0.25) < 0.25);    // 起步比线性更慢（严格小于）
        Assert.True(curve.Evaluate(0.75) > 0.75);    // 收尾同理
    }

    // ---------- Bezier ----------

    [Fact]
    public void BezierWithDefaultHandlesMatchesSmoothStep()
    {
        // 默认句柄 (0.333,0,0.333,0) 等于"没编辑过手柄"：x(w)≈w、y(w)=3w²-2w³，与 SmoothStep 同形
        // （0.333 与 1/3 的差在 1e-3 量级，所以这里按 2 位小数比）
        var bezier = Curve(CurveInterp.Bezier, (0, 0), (1, 1));
        var smooth = Curve(CurveInterp.SmoothStep, (0, 0), (1, 1));

        for (var i = 1; i < 10; i++)
        {
            var u = i / 10.0;
            Assert.Equal(smooth.Evaluate(u), bezier.Evaluate(u), 2);
        }

        Assert.Equal(0.5, bezier.Evaluate(0.5), 5);  // 对称手柄的中点精确落在中值上
    }

    [Fact]
    public void BezierHonoursExplicitHandles()
    {
        // OutX=InX=0.5：x 的控制点关于中点对称 ⇒ x(0.5)=0.5 ⇒ u=0.5 反查到的就是 w=0.5。
        // 归一化值 y(0.5) = 0.375·(OutY + (1−InY)) + 0.125 = 0.375×1.5 + 0.125 = 0.6875（手算 oracle）
        var curve = new FloatCurve();
        AddKey(curve, 0, 0, CurveInterp.Bezier, outX: 0.5, outY: 0.5, inX: 0.5, inY: 0);
        AddKey(curve, 1, 1, CurveInterp.Bezier, outX: 0.5, outY: 0.5, inX: 0.5, inY: 0);

        Assert.Equal(0.6875, curve.Evaluate(0.5), 5);
        Assert.True(curve.Evaluate(0.5) > 0.5);      // 手柄把中段拉高：不是"换个算式算线性"
    }

    [Fact]
    public void BezierHandleXIsClampedToUnitRange()
    {
        // OutX=5 / InX=-3 是手改 JSON 才有机会出现的越界手柄：clamp 进 0..1 后必须与 (1, 0) 那段逐点相同，
        // 否则 x(w) 不再单调，反查参数会落到多个解上（求值随 u 抖）
        var wild = new FloatCurve();
        AddKey(wild, 0, 0, CurveInterp.Bezier, outX: 5, outY: 0.3, inX: -3, inY: 0.2);
        AddKey(wild, 1, 1, CurveInterp.Bezier, outX: 5, outY: 0.3, inX: -3, inY: 0.2);

        var clamped = new FloatCurve();
        AddKey(clamped, 0, 0, CurveInterp.Bezier, outX: 1, outY: 0.3, inX: 0, inY: 0.2);
        AddKey(clamped, 1, 1, CurveInterp.Bezier, outX: 1, outY: 0.3, inX: 0, inY: 0.2);

        for (var i = 1; i < 10; i++)
        {
            var u = i / 10.0;
            Assert.Equal(clamped.Evaluate(u), wild.Evaluate(u));
        }
    }

    [Fact]
    public void BezierSegmentIsMonotonicInTime()
    {
        var curve = new FloatCurve();
        AddKey(curve, 0, 0, CurveInterp.Bezier, outX: 1, outY: 0, inX: 0.2, inY: 0);
        AddKey(curve, 1, 1, CurveInterp.Bezier, outX: 1, outY: 0, inX: 0.2, inY: 0);

        // x(w) 单调是 clamp 的目的：同一段内值必须随时刻单调不减（这里 y 也是递增的）
        var previous = double.NegativeInfinity;
        for (var i = 0; i <= 100; i++)
        {
            var value = curve.Evaluate(i / 100.0);
            Assert.True(value >= previous, $"u={i / 100.0} 处回退：{value} < {previous}");
            previous = value;
        }

        Assert.Equal(1, previous);
    }

    // ---------- Spring ----------

    [Fact]
    public void SpringMatchesTheNormalisedFormula()
    {
        // 契约 §7.1 原式：q(u)=exp(-5u)cos(12u)，f(u)=(1-q(u))/(1-q(1))，V=V0+ΔV·f(u)。
        // 公式在测试里独立重写一遍：改实现（例如换阻尼）必须在这里现形。
        var curve = Curve(CurveInterp.Spring, (0, 0), (1, 1));

        foreach (var u in new[] { 0.1, 0.25, 0.5, 0.75, 0.9 })
        {
            static double Q(double x) => Math.Exp(-5 * x) * Math.Cos(12 * x);
            Assert.Equal((1 - Q(u)) / (1 - Q(1)), curve.Evaluate(u), 6);
        }
    }

    [Fact]
    public void SpringOvershootsAndStillEndsOnTheValue()
    {
        var rise = Curve(CurveInterp.Spring, (0, 0), (1, 1));
        var peak = double.NegativeInfinity;
        for (var i = 0; i < 1000; i++) peak = Math.Max(peak, rise.Evaluate(i / 1000.0));

        // 允许超调、UI 不限幅到 [V0,V1]（限幅是写目标时按 schema 打 clamp 标的事）
        Assert.True(peak > 1.0, $"弹簧段应越过末值，实测峰值 {peak}");

        // 端点显式取 V0/V1：u=0/1 不经过任何浮点算式
        Assert.Equal(0, rise.Evaluate(0));
        Assert.Equal(1, rise.Evaluate(1));

        // 下落段对称地向负向超调
        var fall = Curve(CurveInterp.Spring, (0, 1), (1, 0));
        var trough = double.PositiveInfinity;
        for (var i = 0; i < 1000; i++) trough = Math.Min(trough, fall.Evaluate(i / 1000.0));
        Assert.True(trough < 0.0, $"下落段应低于末值，实测谷值 {trough}");
    }

    [Fact]
    public void BezierAndSpringClampOutsideTheSegment()
    {
        // 区外取值仍钳到端点（与三种既有档位同一语义），且不动 key 本身
        foreach (var mode in new[] { CurveInterp.Bezier, CurveInterp.Spring })
        {
            var curve = Curve(mode, (1, 5), (2, 7));

            Assert.Equal(5, curve.Evaluate(0));
            Assert.Equal(5, curve.Evaluate(0.999));
            Assert.Equal(5, curve.Evaluate(1));
            Assert.Equal(7, curve.Evaluate(2));
            Assert.Equal(7, curve.Evaluate(99));
            Assert.Equal(2, curve.Count);
        }
    }

    // ---------- 区外钳制与 key 保留 ----------

    [Fact]
    public void OutsideRangeClampsToEndpointsAndKeepsKeys()
    {
        var curve = Curve(CurveInterp.Linear, (1, 5), (2, 7));

        // 区外取值钳制到端点：把一段动画的 key 拖到播放区之外是常见编辑，不能因此变成"无动画"
        Assert.Equal(5, curve.Evaluate(0));
        Assert.Equal(5, curve.Evaluate(0.999));
        Assert.Equal(7, curve.Evaluate(3));

        // 区外 key 不移除、不过滤，只在取值时钳制——它们仍是用户的编辑内容，存档必须原样保留
        Assert.Equal(2, curve.Count);
        Assert.Equal(1, curve.Keys[0].Time);
        Assert.Equal(2, curve.Keys[1].Time);
    }

    // ---------- 纯函数 / 暂停 seek / 乱序采样 ----------

    [Fact]
    public void SamplingIsOrderIndependentAndRepeatable()
    {
        var curve = Curve(CurveInterp.SmoothStep, (0, 0), (1.5, 10), (3, -4), (5, 2));
        double[] times = [-1, 0, 0.25, 0.75, 1.5, 2.2, 3, 4.9, 5, 12];

        var forward = times.Select(curve.Evaluate).ToArray();
        var backward = times.Reverse().Select(curve.Evaluate).Reverse().ToArray();

        // 求值与调用顺序无关：倒着采样同一组时刻，结果逐个相同
        Assert.Equal(forward, backward);

        // 暂停：同一时刻反复求值恒等；seek 回退后回到此前那个值
        Assert.Equal(curve.Evaluate(2.2), curve.Evaluate(2.2));
        Assert.Equal(forward[5], curve.Evaluate(2.2));
        Assert.Equal(forward[3], curve.Evaluate(0.75));
    }

    // ---------- ParamAddress ----------

    [Theory]
    [InlineData("camera.scale", ParamAddressKind.Camera, "", "scale")]
    [InlineData("camera.parallax.depth", ParamAddressKind.Camera, "", "parallax.depth")]
    [InlineData("layer:character.offsetX", ParamAddressKind.Layer, "character", "offsetX")]
    [InlineData("layer:lyrics_pv.parallax.depth", ParamAddressKind.Layer, "lyrics_pv", "parallax.depth")]
    [InlineData("layer:src_1.opacity", ParamAddressKind.Layer, "src_1", "opacity")]
    [InlineData("effect:fx_glow_ab12.intensity", ParamAddressKind.Effect, "fx_glow_ab12", "intensity")]
    public void AddressFormsParseAndRoundTrip(string text, ParamAddressKind kind, string target, string param)
    {
        Assert.True(ParamAddress.TryParse(text, out var addr));
        Assert.Equal(kind, addr.Kind);
        Assert.Equal(target, addr.Target);
        Assert.Equal(param, addr.Param);

        // 往返稳定：解析后再打印必须回到原文本（否则存档里的地址会逐次保存越走越偏）
        Assert.Equal(text, addr.ToString());
    }

    [Theory]
    [InlineData("")]                       // 空串
    [InlineData("camera")]                 // 无点
    [InlineData("scale")]                  // 无点
    [InlineData("camera.")]                // 空参数名
    [InlineData("layer:")]                 // 空 Id，且无点
    [InlineData("layer:.depth")]           // 空 Id
    [InlineData("layer:lyrics_pv")]        // 无点 = 没有参数名
    [InlineData("layer:lyrics_pv.")]       // 空参数名
    [InlineData("effect:")]                // 空实例 Id
    [InlineData("effect:.intensity")]      // 空实例 Id
    [InlineData("effect:fx_glow_ab12")]    // 无参数名
    [InlineData("stage.scale")]            // 未知前缀
    [InlineData("bogus")]                  // 未知前缀，且无点
    public void MalformedAddressesAreRejected(string text)
    {
        Assert.False(ParamAddress.TryParse(text, out var addr));
        Assert.Equal(default(ParamAddress), addr);
    }

    [Fact]
    public void NullAddressIsRejectedWithoutThrowing()
    {
        // 存档里 "Address": null 完全可能：解析入口要能吞下它，不能把整份工程炸掉
        Assert.False(ParamAddress.TryParse(null, out _));
    }

    [Fact]
    public void EqualAddressesShareOneDictionarySlot()
    {
        var a = Address("effect:fx_glow_ab12.intensity");
        var b = Address("effect:fx_glow_ab12.intensity");
        var other = Address("effect:fx_glow_ff00.intensity"); // 同 DefId 的另一实例
        var camera = Address("camera.scale");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, other);
        Assert.NotEqual(a, camera);

        // 值相等 = 可直接做字典键：换一个解析出来的实例查得到同一条目
        var map = new Dictionary<ParamAddress, int> { [a] = 1 };
        Assert.True(map.ContainsKey(b));
        Assert.False(map.ContainsKey(other));
    }

    // ---------- DirectorEvaluator ----------

    [Fact]
    public void SetCurveReplacesAndRemoveCurveReportsDeletion()
    {
        var eval = new DirectorEvaluator();
        var addr = Address("camera.scale");

        eval.SetCurve(addr, Curve(CurveInterp.Linear, (0, 1)));
        eval.SetCurve(addr, Curve(CurveInterp.Linear, (0, 9), (1, 9)));

        Assert.Equal(2, eval.Curves[addr].Count);
        Assert.True(eval.TryEvaluate(addr, 0, out var value));
        Assert.Equal(9, value);

        Assert.True(eval.RemoveCurve(addr));
        Assert.False(eval.RemoveCurve(addr));
        Assert.Empty(eval.Curves);
    }

    [Fact]
    public void RemovingEffectInstanceCascadesItsCurvesOnly()
    {
        var eval = new DirectorEvaluator();
        var fx1Intensity = Address("effect:fx_glow_ab12.intensity");
        var fx1Amount = Address("effect:fx_glow_ab12.amount");
        var fx2Intensity = Address("effect:fx_bloom_77cd.intensity");
        var cameraScale = Address("camera.scale");

        eval.SetCurve(fx1Intensity, Curve(CurveInterp.Linear, (0, 1), (2, 2)));
        eval.SetCurve(fx1Amount, Curve(CurveInterp.Step, (0, 0)));
        eval.SetCurve(fx2Intensity, Curve(CurveInterp.Linear, (0, 0.5)));
        eval.SetCurve(cameraScale, Curve(CurveInterp.SmoothStep, (0, 1), (4, 1.4)));

        eval.RemoveCurvesForTarget("fx_glow_ab12");

        // 被删实例的两条曲线都消失：按实例 Id 级联，漏掉哪条都会留下没有主人的孤儿曲线
        Assert.False(eval.Curves.ContainsKey(fx1Intensity));
        Assert.False(eval.Curves.ContainsKey(fx1Amount));
        // 另一个实例与镜头曲线不受影响
        Assert.True(eval.Curves.ContainsKey(fx2Intensity));
        Assert.True(eval.Curves.ContainsKey(cameraScale));
        Assert.Equal(2, eval.Curves.Count);

        // 空 targetId 不误伤镜头曲线（镜头曲线的 Target 本来就是空串）
        eval.RemoveCurvesForTarget("");
        Assert.True(eval.Curves.ContainsKey(cameraScale));
        Assert.Equal(2, eval.Curves.Count);
    }

    [Fact]
    public void EvaluatorValueMatchesCurveDirectly()
    {
        var eval = new DirectorEvaluator();
        var addr = Address("layer:lyrics_pv.parallax.depth");
        var curve = Curve(CurveInterp.Linear, (0, 0), (2, 8));
        eval.SetCurve(addr, curve);

        Assert.True(eval.TryEvaluate(addr, 1.5, out var value));
        Assert.Equal(6, value);
        Assert.Equal(curve.Evaluate(1.5), value); // 求值是转发给曲线本身，没有第二套插值
    }

    [Fact]
    public void SnapshotIsIndependentOfOriginal()
    {
        var eval = new DirectorEvaluator();
        var addr = Address("camera.scale");
        var curve = Curve(CurveInterp.Linear, (0, 0.8), (2, 1.5));
        eval.SetCurve(addr, curve);

        var snap = eval.Snapshot();

        // 快照要给渲染线程无锁读，就必须是发布那一刻的深拷贝而不是共享视图：
        // 曲线实例与 Keys 列表都得是新的，否则编辑线程的 Insert / RemoveAt 会当着求值线程的面改表
        Assert.NotSame(curve, snap.Curves[addr]);
        Assert.NotSame(curve.Keys, snap.Curves[addr].Keys);
        Assert.Equal(2, snap.Curves[addr].Count);
        Assert.True(snap.TryEvaluate(addr, 1, out var atOne));
        Assert.Equal(1.15, atOne, 3);

        // 发布之后源实例继续编辑——这正是生产路径的用法，快照必须停在按下那一刻
        Assert.True(curve.TryAdd(4, 3.0));
        Assert.True(curve.Remove(0));
        Assert.Equal(2, curve.Count);

        Assert.Equal(2, snap.Curves[addr].Count);
        Assert.True(snap.TryEvaluate(addr, 1, out var unchangedAtOne));
        Assert.Equal(atOne, unchangedAtOne); // 逐位相同，不是容差内近似
        Assert.True(snap.TryEvaluate(addr, 4, out var atFour));
        Assert.Equal(1.5, atFour, 3);        // 新加的 (4, 3.0) 没渗进来：快照仍钳制在末 key

        // 反方向同样独立：改快照不能回渗到源曲线，否则 UI 上的编辑会被"渲染线程那边"悄悄改写
        Assert.True(snap.Curves[addr].TryAdd(6, 9.0));
        Assert.True(snap.Curves[addr].Remove(0));
        Assert.Equal(new[] { 2.0, 6.0 }, snap.Curves[addr].Keys.Select(k => k.Time).ToArray());

        Assert.Equal(new[] { 2.0, 4.0 }, curve.Keys.Select(k => k.Time).ToArray());
        Assert.Equal(1.5, curve.Evaluate(0), 3); // 源曲线首 key 仍是 (2, 1.5)，区外取值照旧钳制
        Assert.Equal(3, curve.Evaluate(4));      // 源曲线自己的 (4, 3.0) 还没被谁动过
    }

    // ---------- helpers ----------

    private static ParamAddress Address(string text)
    {
        Assert.True(ParamAddress.TryParse(text, out var addr), $"测试前置地址应能解析：{text}");
        return addr;
    }

    /// <summary>一组同模式的关键帧。每次 TryAdd 都断言成功：重复时刻在测试里就是失败，不该被静默吞掉。</summary>
    private static FloatCurve Curve(CurveInterp mode, params (double Time, double Value)[] keys)
    {
        var curve = new FloatCurve();
        foreach (var (time, value) in keys) Assert.True(curve.TryAdd(time, value, mode));
        return curve;
    }

    /// <summary>
    /// 打一个**带句柄**的 key：TryAdd 只管时刻唯一性（它按契约不带句柄），句柄在插入后按值替换上去。
    /// 句柄是归一化量，只被该段的左 key 出柄 / 右 key 入柄读取。
    /// </summary>
    private static void AddKey(FloatCurve curve, double time, double value, CurveInterp mode,
        double outX = 0.333, double outY = 0, double inX = 0.333, double inY = 0)
    {
        Assert.True(curve.TryAdd(time, value, mode));
        var index = curve.Keys.FindIndex(k => k.Time == time);
        curve.Keys[index] = new CurveKey(time, value, mode, outX, outY, inX, inY);
    }
}
