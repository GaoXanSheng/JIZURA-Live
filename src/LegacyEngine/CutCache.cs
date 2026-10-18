using System.Runtime.CompilerServices;

namespace JizuraLive.LegacyEngine;

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
