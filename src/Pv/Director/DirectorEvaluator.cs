namespace JizuraLive.Pv;

/// <summary>
/// 导演求值器：按参数地址取某时刻的动画值。
///
/// 每帧求值是**纯函数**（addr + time → value）：它只读曲线，**不写回** <see cref="PvScene"/> 的常量——
/// Camera.X / Scale 那类是镜头的基础（静息）值，把动画值写回去等于让工程文件里的静态取景被播放过程永久篡改。
/// 把求出来的值写进"这一帧的帧状态"是渲染管线的职责，不在本层。
///
/// 时间口径与全链路一致：<c>double</c> 秒，与 <see cref="PvFrameContext.Time"/> 同一根时间轴。
/// </summary>
public sealed class DirectorEvaluator
{
    private readonly Dictionary<ParamAddress, FloatCurve> _curves = new();

    /// <summary>全部曲线（UI 迭代 / 持久化用）。只暴露只读视图：改动一律走 Set/Remove，避免外部绕过接口改字典。</summary>
    public IReadOnlyDictionary<ParamAddress, FloatCurve> Curves => _curves;

    /// <summary>设置（或替换）某地址的曲线。</summary>
    public void SetCurve(in ParamAddress addr, FloatCurve curve) => _curves[addr] = curve;

    /// <summary>删除某地址的曲线。返回是否确有删除。</summary>
    public bool RemoveCurve(in ParamAddress addr) => _curves.Remove(addr);

    /// <summary>
    /// 取某地址在 <paramref name="time"/> 时刻的值。没有曲线（或曲线一个 key 都没有）返回 false，调用方回落基础值。
    ///
    /// 这条路径**不抛异常**：帧循环里每个参数都会走它，一条还没打过 key 的曲线不该把整帧抛穿
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
        foreach (var (addr, curve) in _curves)
        {
            var cloned = new FloatCurve();
            foreach (var key in curve.Keys) cloned.Keys.Add(key);
            copy._curves[addr] = cloned;
        }

        return copy;
    }

    /// <summary>
    /// 删除某个目标（图层 Id 或效果**实例** Id）的全部曲线——删除对象时的级联：
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

        // 先收集再删：字典不能在遍历中改
        List<ParamAddress>? doomed = null;
        foreach (var addr in _curves.Keys)
        {
            if (!string.Equals(addr.Target, targetId, StringComparison.Ordinal)) continue;
            (doomed ??= new List<ParamAddress>()).Add(addr);
        }

        if (doomed is null) return;
        foreach (var addr in doomed) _curves.Remove(addr);
    }
}
