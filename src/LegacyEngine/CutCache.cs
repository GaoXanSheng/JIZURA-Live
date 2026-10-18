using System.Runtime.CompilerServices;

namespace JizuraLive.LegacyEngine;

/// <summary>
/// layout/pack 按切分的「帧不变量」缓存（性能专项）：
/// 各版式 Draw 里有一批只依赖 (cut, W, H) 与 plan 静态量（配色/字体/params）的派生物——分词表、
/// 槽位、FitSize/Measure 结果——此前逐帧重算（实测 60-160KB/帧/版式的分配大头）。提到这里按 Cut 挂缓存。
/// 生命周期：ConditionalWeakTable 挂在 Cut 上，plan 一换旧 Cut 全体消亡、缓存随之回收，无跨 plan 泄漏；
/// 渲染全程持 EngineRenderGate（预览/Spout/TechTab 同一把闸），单线程访问，普通 Dictionary 即可。
/// 用法（稳态零分配）：
///   var inv = CutCache.Get&lt;Inv&gt;(cut, "key");
///   if (inv == null || inv.W != W) CutCache.Put(cut, "key", inv = Compute(W, H));
/// 铁律：只准放帧不变量——任何依赖 env.Lt/t/energy/beat 的量进这里就是渲染错误。
/// </summary>
public static class CutCache
{
    private static readonly ConditionalWeakTable<Cut, Dictionary<string, object>> Map = new();

    public static T? Get<T>(Cut cut, string key)
    {
        if (!Map.TryGetValue(cut, out var d)) return default;
        return d.TryGetValue(key, out var v) && v is T hit ? hit : default;
    }

    public static void Put(Cut cut, string key, object value)
    {
        var d = Map.GetOrCreateValue(cut);
        d[key] = value;
    }
}
