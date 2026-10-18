using System.Diagnostics;

namespace JizuraLive.LegacyEngine;

/// <summary>
/// 叶子级性能归因槽（性能专项基建；Renderer.StageHook 的下一层）：
/// Stage 级钩子只能给到 cutpass/bgdef 两个大站，逐帧分配要钉到具体站点（哪个 layout/decor/
/// DrawFx 分支/字形阶段）需要一层嵌套读数。线程不安全（渲染都在本线程，与 StageHook 同约定）。
/// 用法：using var _ = new Prof.Scope("layout", cut.Layout); —— 关钩子时零分配零计时；
/// kind/name 分开存，只有钩子在线时才拼串。
/// </summary>
public static class Prof
{
    /// <summary>(站点全名, 累计 ms, 累计本线程分配字节, 调用次数)。消费方自己聚合、自己清。</summary>
    public static Action<string, double, long, int>? LeafHook;

    /// <summary>作用域：构造时取起点，Dispose 时上报。关钩子时全部字段空转。</summary>
    public readonly struct Scope : IDisposable
    {
        private readonly string? _kind;
        private readonly string? _name;
        private readonly long _h0;
        private readonly long _a0;

        public Scope(string kind, string name)
        {
            if (LeafHook == null)
            {
                _kind = null;
                _name = null;
                _h0 = 0;
                _a0 = 0;
                return;
            }

            _kind = kind;
            _name = name;
            _h0 = Stopwatch.GetTimestamp();
            _a0 = GC.GetAllocatedBytesForCurrentThread();
        }

        public void Dispose()
        {
            if (_kind == null || LeafHook == null) return;
            var now = Stopwatch.GetTimestamp();
            var alloc = GC.GetAllocatedBytesForCurrentThread();
            LeafHook(_kind + " " + _name, (now - _h0) * 1000.0 / Stopwatch.Frequency, alloc - _a0, 1);
        }
    }
}
