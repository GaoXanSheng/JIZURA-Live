using System.Diagnostics;

namespace JizuraLive.LegacyEngine;

public static class Prof
{
        public static Action<string, double, long, int>? LeafHook;

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
