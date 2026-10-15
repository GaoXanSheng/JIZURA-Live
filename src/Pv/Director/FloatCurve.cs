using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Pv;

/// <summary>区间插值方式（由区间的**左** key 决定）。</summary>
public enum CurveInterp
{
    /// <summary>阶跃：保持左 key 的值，直到下一个 key 的时刻才突变。</summary>
    Step,

    /// <summary>线性。</summary>
    Linear,

    /// <summary>经典 SmoothStep 缓和（3u²-2u³）：两端一阶导为 0，起步和收尾都不顿。</summary>
    SmoothStep,

    /// <summary>三次贝塞尔：时间轴按 x 句柄参数化，值取 y（句柄可超出 [V0,V1] 造成超调）。</summary>
    Bezier,

    /// <summary>定阻尼弹簧：q(u)=exp(-5u)cos(12u) 归一化后插值，允许超调。</summary>
    Spring,
}

/// <summary>
/// 曲线上的一个关键帧。
///
/// 时间与全链路同口径：<c>double</c> 秒（<see cref="PvFrameContext.Time"/>、AudioClockDriver 都是这个口径）。
/// 刻意**不**引入 ticks 整数制：渲染时间轴本来就是 double 秒，换算来换算去只会在边界上凭空多出取整误差，
/// 而"准确命中端点"正是靠同一根时间轴上的精确比较守住的。
///
/// 四个句柄是**归一化**量（0..1 时间轴 / 值轴比例），只被 <see cref="CurveInterp.Bezier"/> 读：
/// 本 key 的 OutX/OutY 与下一 key 的 InX/InY 组成这一段的控制柄。X 侧会被 clamp 到 0..1
/// （保证时间轴单调，反查参数才有唯一解），Y 侧不夹——贝塞尔的超调正是从那里来的。
/// </summary>
[JsonConverter(typeof(CurveKeyJsonConverter))]
public readonly record struct CurveKey(double Time, double Value, CurveInterp Mode,
    double OutX = 0.333, double OutY = 0, double InX = 0.333, double InY = 0);

/// <summary>
/// <see cref="CurveKey"/> 的存档读写。句柄是后加的字段，旧档里没有：
/// 缺失时补契约默认值 (0.333,0,0.333,0)，而不是 0——0 是"两端都没有手柄"的合法编辑，
/// 这两种情况必须分得开，所以这里显式分辨"字段在不在"。
///
/// 不能靠 Json.NET 的"无参构造 + 逐个属性赋值"：结构体总有那个无参构造，缺字段会静默变成 0。
/// </summary>
public sealed class CurveKeyJsonConverter : JsonConverter<CurveKey>
{
    private const double DefaultHandleX = 0.333;

    public override CurveKey ReadJson(JsonReader reader, Type objectType, CurveKey existingValue,
        bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null) return default;

        var obj = JObject.Load(reader);
        return new CurveKey(
            (double?)obj["Time"] ?? 0,
            (double?)obj["Value"] ?? 0,
            (CurveInterp?)(int?)obj["Mode"] ?? CurveInterp.Step,
            (double?)obj["OutX"] ?? DefaultHandleX,
            (double?)obj["OutY"] ?? 0,
            (double?)obj["InX"] ?? DefaultHandleX,
            (double?)obj["InY"] ?? 0);
    }

    public override void WriteJson(JsonWriter writer, CurveKey value, JsonSerializer serializer)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("Time");
        writer.WriteValue(value.Time);
        writer.WritePropertyName("Value");
        writer.WriteValue(value.Value);
        writer.WritePropertyName("Mode");
        writer.WriteValue((int)value.Mode);
        writer.WritePropertyName("OutX");
        writer.WriteValue(value.OutX);
        writer.WritePropertyName("OutY");
        writer.WriteValue(value.OutY);
        writer.WritePropertyName("InX");
        writer.WriteValue(value.InX);
        writer.WritePropertyName("InY");
        writer.WriteValue(value.InY);
        writer.WriteEndObject();
    }
}

/// <summary>
/// 浮点动画曲线。Keys 按 Time 升序、时刻唯一（由 <see cref="TryAdd"/> 维持）。
///
/// 三条约定：
/// <list type="number">
/// <item>求值是**纯函数**：<see cref="Evaluate"/> 只读 Keys，不改任何状态。同一个 time 反复求值恒定，
/// 暂停 / seek 回退 / 乱序采样都不会因为"上次算到哪"而不同。</item>
/// <item>区间插值用**左 key** 的 Mode：区间 <c>[k[i], k[i+1])</c> 用 <c>k[i].Mode</c>。
/// 这正是 Step 的语义——阶跃发生在 k[i+1] 这个时刻上（此刻恰好取到 k[i+1] 的值），而不是它之前。</item>
/// <item>曲线区外的 key 不移除、不过滤，只在取值时钳制到首尾端点值。区外的 key 仍是用户的编辑内容
/// （比如一段动画被挪到画面外），求值时忽略它，存档必须原样保留。</item>
/// </list>
/// </summary>
public sealed class FloatCurve
{
    /// <summary>
    /// 判定"同一时刻"的窗口：1e-6 秒 ≈ 1µs。
    /// 必须远小于毫秒——LRC 是毫秒精度，相邻 1ms 的两个 key 是合法编辑；
    /// 同一根 double 时间轴上累加出来的时刻又可能差出 1e-15 级别的噪声。1µs 正好夹在两者之间。
    /// </summary>
    private const double TimeEpsilon = 1e-6;

    /// <summary>贝塞尔反查参数的二分容差（契约 §7.1）：1e-6 的 w 误差折到值上是 1e-6 量级。</summary>
    private const double BezierParameterTolerance = 1e-6;

    /// <summary>关键帧，按 Time 升序。<see cref="TryAdd"/> / <see cref="Remove"/> 是唯一的编辑入口。</summary>
    public List<CurveKey> Keys { get; } = new();

    /// <summary>关键帧数量。</summary>
    public int Count => Keys.Count;

    /// <summary>
    /// 插入一个关键帧。返回 false = 该时刻已有 key（窗口 <see cref="TimeEpsilon"/>），曲线保持原样。
    ///
    /// 重复时刻选择**拒绝**而不是覆盖：静默覆盖会让"打第二个 key"变成"改了第一个"，
    /// 而 UI 上出现重复时刻更可能是想加个点却没挪开位置。
    /// </summary>
    public bool TryAdd(double time, double value, CurveInterp mode = CurveInterp.Linear)
    {
        if (FindNear(time) >= 0) return false;

        Keys.Insert(InsertionIndex(time), new CurveKey(time, value, mode));
        return true;
    }

    /// <summary>删除某时刻的 key（UI 删 key 用）。返回 false = 该时刻没有 key，曲线不变。</summary>
    public bool Remove(double time)
    {
        var index = FindNear(time);
        if (index < 0) return false;

        Keys.RemoveAt(index);
        return true;
    }

    /// <summary>
    /// 恢复不变量：按时间升序排序，并合并 <see cref="TimeEpsilon"/> 窗口内的重复时刻。
    ///
    /// 正常编辑路径（<see cref="TryAdd"/> / <see cref="Remove"/>）永远维持不变量；这里是给
    /// "手改 JSON / 反序列化"之后的一次治理入口——求值靠二分，前提被破坏时不能靠运气，
    /// 否则乱序的 key 会算出时有时无的错误镜头。
    /// </summary>
    public void NormalizeKeys()
    {
        if (Keys.Count < 2) return;

        Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        for (var i = Keys.Count - 1; i > 0; i--)
        {
            if (Keys[i].Time - Keys[i - 1].Time < TimeEpsilon) Keys.RemoveAt(i);
        }
    }

    /// <summary>
    /// 求 <paramref name="time"/> 时刻的值（时间口径：double 秒，与 Keys 同一根轴）。
    ///
    /// 空曲线抛 <see cref="InvalidOperationException"/>：没有任何 key 的"曲线"没有值可取，
    /// 静默返回 0 会把镜头缩放到 0 这类荒唐结果混进渲染。需要容错的调用方走
    /// <see cref="DirectorEvaluator.TryEvaluate"/>——那条路径不会落到这里。
    /// </summary>
    public double Evaluate(double time)
    {
        if (Keys.Count == 0)
            throw new InvalidOperationException(
                "空曲线没有可求的值：先 TryAdd 关键帧，或改走 DirectorEvaluator.TryEvaluate 这类容错入口。");

        // 首尾之外钳制：t < 首 key → 首值；t >= 末 key → 末值。
        // t 恰等于端点也走这两条：端点值直接返回，精确相等，不经过任何浮点插值运算。
        var first = Keys[0];
        if (time <= first.Time) return first.Value;
        var last = Keys[^1];
        if (time >= last.Time) return last.Value;

        // 二分找最后一个 Time <= time 的 key：区间就是 [lo, lo+1]。
        // 用二分而不是线性扫描：key 数量由用户拖出来，几百上千个也正常，而求值每帧都在跑。
        int lo = 0, hi = Keys.Count - 1;
        while (hi - lo > 1)
        {
            var mid = (lo + hi) >> 1;
            if (Keys[mid].Time <= time) lo = mid;
            else hi = mid;
        }

        var a = Keys[lo];
        var b = Keys[lo + 1];
        // 精确命中左 key：不走插值算式，保证 Evaluate(k.Time) == k.Value 精确相等（不是"差不多"）
        if (time == a.Time) return a.Value;

        var span = b.Time - a.Time;
        // 兜底：Keys 是公开集合，反序列化 / 外部直接改写可能造出零长区间，
        // 0/0 = NaN 一旦流进 GPU 就是一整屏垃圾。宁可退化成取左 key 值。
        if (span <= 0) return a.Value;

        return Interpolate(a, b, (time - a.Time) / span);
    }

    /// <summary>按左 key 的 Mode 插值（<paramref name="u"/> 已归一化到 (0,1)）。</summary>
    private static double Interpolate(in CurveKey a, in CurveKey b, double u)
    {
        // 端点显式取 V0/V1：贝塞尔/弹簧在端点附近都有算式，这里是唯一保证"精确命中"的地方
        if (u <= 0) return a.Value;
        if (u >= 1) return b.Value;

        var delta = b.Value - a.Value;
        return a.Mode switch
        {
            CurveInterp.Step => a.Value,                                       // 保持到下一个 key 的时刻才突变
            CurveInterp.SmoothStep => a.Value + delta * (u * u * (3 - 2 * u)), // 3u²-2u³
            CurveInterp.Bezier => a.Value + delta * BezierRatio(u, a, b),
            CurveInterp.Spring => a.Value + delta * SpringRatio(u),
            _ => a.Value + delta * u,
        };
    }

    /// <summary>
    /// 贝塞尔段的值比例（归一化到 V0..V1 的 0..1）：时间轴用 x 控制柄参数化成 w，二分反查目标 w 再取 y。
    /// x 句柄先 clamp 到 0..1，x(w) 才是单调不减的；否则一次求值会落到多个解上。
    /// </summary>
    private static double BezierRatio(double u, in CurveKey a, in CurveKey b)
    {
        var x1 = Math.Clamp(a.OutX, 0, 1);
        var x2 = 1 - Math.Clamp(b.InX, 0, 1);

        double lo = 0, hi = 1;
        while (hi - lo > BezierParameterTolerance)
        {
            var mid = (lo + hi) * 0.5;
            if (CubicBezier(mid, x1, x2) < u) lo = mid;
            else hi = mid;
        }

        return CubicBezier((lo + hi) * 0.5, a.OutY, 1 - b.InY);
    }

    /// <summary>端点固定为 (0,0)/(1,1) 的三次贝塞尔分量：3(1-w)²w·c1 + 3(1-w)w²·c2 + w³。</summary>
    private static double CubicBezier(double w, double c1, double c2)
    {
        var inv = 1 - w;
        return 3 * inv * inv * w * c1 + 3 * inv * w * w * c2 + w * w * w;
    }

    /// <summary>弹簧段的值比例（契约 §7.1 原式）：f(u)=(1-q(u))/(1-q(1))，q(u)=exp(-5u)cos(12u)。</summary>
    private static double SpringRatio(double u)
    {
        static double Q(double x) => Math.Exp(-5 * x) * Math.Cos(12 * x);
        return (1 - Q(u)) / (1 - Q(1));
    }

    /// <summary>第一个 Time 严格大于给定时刻的下标（LowerBound）：升序表的插入位置。</summary>
    private int InsertionIndex(double time)
    {
        int lo = 0, hi = Keys.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (Keys[mid].Time < time) lo = mid + 1;
            else hi = mid;
        }

        return lo;
    }

    /// <summary>
    /// 窗口 <see cref="TimeEpsilon"/> 内已有 key 的下标；没有返回 -1。
    /// 二分一次，只看落点及其左侧邻居：升序 + 唯一性下，贴着给定时刻的 key 只可能是这两个之一（否则早被拒了）。
    /// </summary>
    private int FindNear(double time)
    {
        var index = InsertionIndex(time);
        if (index < Keys.Count && Keys[index].Time - time < TimeEpsilon) return index;
        if (index > 0 && time - Keys[index - 1].Time < TimeEpsilon) return index - 1;
        return -1;
    }
}
