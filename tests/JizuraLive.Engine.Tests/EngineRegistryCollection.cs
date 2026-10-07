using JizuraLive.Engine;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 全局注册表是进程级状态（Reg.* 静态字典/顺序表 + Reg.RandomOk 委托）。
/// 对拍夹具（core-only 黄金值）与表现包测试（Install 全量包）在同一进程内互相污染——
/// 所有触碰注册表的测试统一归入本 collection：串行执行，互不并发。
/// </summary>
[CollectionDefinition("EngineRegistry", DisableParallelization = true)]
public sealed class EngineRegistryCollection
{
}

/// <summary>
/// core-only 净化器：对拍夹具的每个用例构造时快照全量注册表 → 移除表现包条目 + 停用 RandomOk
/// （与生成黄金值的 JS harness「只加载核心文件」表面一致）→ Dispose 时整体还原。
/// 包的 _installed 幂等标记与「还原后包条目仍在」天然一致，不受影响。
/// </summary>
public sealed class CoreOnlyRegistry : IDisposable
{
    private readonly Func<Project, string, string, bool>? _randomOk;
    private readonly Dictionary<string, Dictionary<string, object>> _snaps = new();
    private readonly Dictionary<string, List<string>> _orders = new();

    public CoreOnlyRegistry()
    {
        _randomOk = Reg.RandomOk;
        Reg.RandomOk = null;

        Purge("layout", Reg.Layouts, Reg.LayoutOrder);
        Purge("enter", Reg.Enter, Reg.EnterOrder);
        Purge("hold", Reg.Hold, Reg.HoldOrder);
        Purge("exit", Reg.Exit, Reg.ExitOrder);
        Purge("decor", Reg.Decor, Reg.DecorOrder);
        Purge("treat", Reg.Treat, Reg.TreatOrder);
        Purge("bg", Reg.Bg, Reg.BgOrder);
        Purge("cam", Reg.Camera, Reg.CameraOrder);
        Purge("fx", Reg.Fxe, Reg.FxeOrder);
        Purge("trans", Reg.Trans, Reg.TransOrder);
    }

    private void Purge<T>(string key, Dictionary<string, T> dict, List<string> order) where T : class, IRegEntry
    {
        _snaps[key] = dict.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        _orders[key] = new List<string>(order);
        foreach (var k in dict.Where(kv => kv.Value.Pack is not (null or "core")).Select(kv => kv.Key).ToList())
            dict.Remove(k);
        order.RemoveAll(k => !dict.ContainsKey(k));
    }

    private void Restore<T>(string key, Dictionary<string, T> dict, List<string> order) where T : class, IRegEntry
    {
        if (!_snaps.TryGetValue(key, out var snap)) return;
        dict.Clear();
        foreach (var kv in snap) dict[kv.Key] = (T)kv.Value;
        order.Clear();
        order.AddRange(_orders[key]);
    }

    public void Dispose()
    {
        Reg.RandomOk = _randomOk;
        Restore("layout", Reg.Layouts, Reg.LayoutOrder);
        Restore("enter", Reg.Enter, Reg.EnterOrder);
        Restore("hold", Reg.Hold, Reg.HoldOrder);
        Restore("exit", Reg.Exit, Reg.ExitOrder);
        Restore("decor", Reg.Decor, Reg.DecorOrder);
        Restore("treat", Reg.Treat, Reg.TreatOrder);
        Restore("bg", Reg.Bg, Reg.BgOrder);
        Restore("cam", Reg.Camera, Reg.CameraOrder);
        Restore("fx", Reg.Fxe, Reg.FxeOrder);
        Restore("trans", Reg.Trans, Reg.TransOrder);
    }
}
