namespace JizuraLive.Pv;

/// <summary>调制层与下层的合成方式。</summary>
public enum CurveBlend
{
    /// <summary>加到下层值上（曲线取 0 = 不影响）。</summary>
    Add,

    /// <summary>乘到下层值上（曲线取 1 = 不影响）。</summary>
    Multiply,
}

/// <summary>某地址上的一条调制层：层名在同一地址内唯一。</summary>
public sealed record CurveLayer(string Name, CurveBlend Blend, FloatCurve Curve);

/// <summary>
/// 导演求值器：按参数地址取某时刻的动画值。
///
/// 每帧求值是**纯函数**（addr + time → value）：它只读曲线，**不写回** <see cref="PvScene"/> 的常量——
/// Camera.X / Scale 那类是镜头的基础（静息）值，把动画值写回去等于让工程文件里的静态取景被播放过程永久篡改。
/// 把求出来的值写进"这一帧的帧状态"是渲染管线的职责，不在本层。
///
/// 一个地址的取值分两级：基础曲线（至多一条，UI 编辑的就是它）→ 调制层（任意多条）。
/// 合成顺序固定为「基础 → 全部 Add 层 → 全部 Multiply 层」，与层的插入顺序无关，
/// 否则同一份工程读两次可能因字典顺序不同而得到两种画面。
///
/// 时间口径与全链路一致：<c>double</c> 秒，与 <see cref="PvFrameContext.Time"/> 同一根时间轴。
/// </summary>
public sealed class DirectorEvaluator
{
    private readonly Dictionary<ParamAddress, FloatCurve> _curves = new();
    private readonly Dictionary<ParamAddress, List<CurveLayer>> _layers = new();

    /// <summary>全部基础曲线（UI 迭代 / 持久化用）。只暴露只读视图：改动一律走 Set/Remove，避免外部绕过接口改字典。</summary>
    public IReadOnlyDictionary<ParamAddress, FloatCurve> Curves => _curves;

    /// <summary>设置（或替换）某地址的基础曲线。</summary>
    public void SetCurve(in ParamAddress addr, FloatCurve curve) => _curves[addr] = curve;

    /// <summary>删除某地址的基础曲线（调制层不动）。返回是否确有删除。</summary>
    public bool RemoveCurve(in ParamAddress addr) => _curves.Remove(addr);

    /// <summary>设置（或按层名替换）某地址的一条调制层。空层名拒绝：空名就是基础曲线，走 <see cref="SetCurve"/>。</summary>
    public void SetLayer(in ParamAddress addr, string name, CurveBlend blend, FloatCurve curve)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (!_layers.TryGetValue(addr, out var list)) _layers[addr] = list = new List<CurveLayer>();
        list.RemoveAll(l => string.Equals(l.Name, name, StringComparison.Ordinal));
        list.Add(new CurveLayer(name, blend, curve));
    }

    /// <summary>某地址上的全部调制层（没有则为空）。</summary>
    public IReadOnlyList<CurveLayer> LayersOf(in ParamAddress addr) =>
        _layers.TryGetValue(addr, out var list) ? list : Array.Empty<CurveLayer>();

    /// <summary>所有带调制层的地址。</summary>
    public IEnumerable<ParamAddress> LayeredAddresses => _layers.Keys;

    /// <summary>该地址是否有任何动画来源（非空基础曲线或任一非空调制层）。</summary>
    public bool Affects(in ParamAddress addr)
    {
        if (_curves.TryGetValue(addr, out var c) && c.Count > 0) return true;
        if (!_layers.TryGetValue(addr, out var list)) return false;
        foreach (var l in list)
            if (l.Curve.Count > 0) return true;
        return false;
    }

    /// <summary>
    /// 取某地址**基础曲线**在 <paramref name="time"/> 时刻的值。没有曲线（或曲线一个 key 都没有）返回 false。
    /// 不含调制层——检查器/时间轴编辑的是基础曲线，显示它自己的值；画面取值走 <see cref="Resolve"/>。
    ///
    /// 这条路径**不抛异常**：一条还没打过 key 的曲线不该把整帧抛穿
    /// （空曲线直接求值会抛，见 <see cref="FloatCurve.Evaluate"/>，那是给"确定有值"的调用方的）。
    /// </summary>
    public bool TryEvaluate(in ParamAddress addr, double time, out double value)
    {
        if (!_curves.TryGetValue(addr, out var curve) || curve.Count == 0)
        {
            value = 0;
            return false;
        }

        value = curve.Evaluate(time);
        return true;
    }

    /// <summary>
    /// 画面实际取值：基础曲线（没有则用 <paramref name="rest"/>，即文档里的静息值）→ Add 层 → Multiply 层。
    /// 帧循环每个参数都走它，所以不分配。
    /// </summary>
    public double Resolve(in ParamAddress addr, double time, double rest)
    {
        var value = TryEvaluate(addr, time, out var b) ? b : rest;
        if (!_layers.TryGetValue(addr, out var list)) return value;

        foreach (var l in list)
            if (l.Blend == CurveBlend.Add && l.Curve.Count > 0) value += l.Curve.Evaluate(time);
        foreach (var l in list)
            if (l.Blend == CurveBlend.Multiply && l.Curve.Count > 0) value *= l.Curve.Evaluate(time);
        return value;
    }

    /// <summary>
    /// 深拷贝一份求值器，供跨线程发布（UI 线程编辑 → 渲染线程每帧求值）。
    ///
    /// 为什么必须拷贝：曲线在 UI 线程上被编辑，而 <see cref="FloatCurve.Keys"/> 是可变的
    /// <see cref="List{T}"/>，不能跨线程共享——编辑线程一次 Insert / RemoveAt 就可能让求值线程
    /// 读到"容量已变、内容未更"的表。所以每个 <see cref="FloatCurve"/> 都新建实例，Keys 逐条复制进新列表
    /// （<see cref="CurveKey"/> 是 readonly record struct，复制即与源列表脱钩）。
    ///
    /// 契约：快照发布之后**永不修改**。后续编辑在源实例上继续做，再发布下一份快照覆盖；
    /// 正因如此，拿到快照的渲染线程可以完全无锁地读它。
    /// </summary>
    public DirectorEvaluator Snapshot()
    {
        var copy = new DirectorEvaluator();
        foreach (var (addr, curve) in _curves) copy._curves[addr] = CloneCurve(curve);
        foreach (var (addr, list) in _layers)
            copy._layers[addr] = list.Select(l => l with { Curve = CloneCurve(l.Curve) }).ToList();

        return copy;
    }

    private static FloatCurve CloneCurve(FloatCurve curve)
    {
        var cloned = new FloatCurve();
        foreach (var key in curve.Keys) cloned.Keys.Add(key);
        return cloned;
    }

    /// <summary>
    /// 删除某个目标（图层 Id 或效果**实例** Id）的全部曲线与调制层——删除对象时的级联：
    /// 删掉效果实例 <c>fx_glow_ab12</c>，它的 intensity 曲线必须跟着消失。留着就是一条再也没有主人的孤儿曲线，
    /// 下次同 DefId 的新实例（<c>fx_glow_9f31</c>）不该继承别人的动画。
    ///
    /// 按实例 Id 匹配、绝不用 DefId：同一种效果可以有多个实例，各自独立寻址。
    /// 空 targetId 不做任何事：镜头曲线的 Target 本来就是空串，传空进来等于"删掉所有镜头动画"，
    /// 那不是任何调用方的意图（宁可什么都不做，也不要误删）。
    /// </summary>
    public void RemoveCurvesForTarget(string targetId)
    {
        if (string.IsNullOrEmpty(targetId)) return;

        foreach (var addr in _curves.Keys.Where(a => string.Equals(a.Target, targetId, StringComparison.Ordinal)).ToList())
            _curves.Remove(addr);
        foreach (var addr in _layers.Keys.Where(a => string.Equals(a.Target, targetId, StringComparison.Ordinal)).ToList())
            _layers.Remove(addr);
    }

    /// <summary>
    /// 从工程存档装一份求值器。地址解析失败的条目跳过（ProjectStore 已治理过，这里再挡一次：
    /// 进了求值器就是一条谁也取不到、UI 上也看不见的孤儿曲线，会一直跟着存回磁盘）。
    /// </summary>
    public static DirectorEvaluator FromSaves(IEnumerable<CurveSave>? saves)
    {
        var eval = new DirectorEvaluator();
        if (saves is null) return eval;

        foreach (var save in saves)
        {
            if (save?.Curve is null) continue;
            if (!ParamAddress.TryParse(save.Address, out var addr)) continue;
            if (string.IsNullOrEmpty(save.Layer)) eval.SetCurve(addr, save.Curve);
            else eval.SetLayer(addr, save.Layer, save.Blend, save.Curve);
        }

        return eval;
    }

    /// <summary>
    /// 回写成存档条目：基础曲线在前（按地址排序），调制层在后（按地址、层名排序），存档顺序稳定、diff 友好。
    /// 共享曲线实例不拷贝——调用方在 UI 线程同步序列化，渲染线程只读它自己的快照。
    /// </summary>
    public List<CurveSave> ToSaves()
    {
        var saves = new List<CurveSave>();
        foreach (var (addr, curve) in _curves.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal))
            saves.Add(new CurveSave { Address = addr.ToString(), Curve = curve });

        foreach (var (addr, list) in _layers.OrderBy(p => p.Key.ToString(), StringComparer.Ordinal))
        foreach (var l in list.OrderBy(l => l.Name, StringComparer.Ordinal))
            saves.Add(new CurveSave { Address = addr.ToString(), Curve = l.Curve, Layer = l.Name, Blend = l.Blend });

        return saves;
    }
}
