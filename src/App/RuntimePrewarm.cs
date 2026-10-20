using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace JizuraLive.App;

internal static class RuntimePrewarm
{
    [ModuleInitializer]
    internal static void Init()
    {
        new ConcurrentDictionary<int, int>().Clear();
    }
}
