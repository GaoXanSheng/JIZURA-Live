using JizuraLive.Logging;

namespace JizuraLive.Engine;

/// <summary>
/// 语言标签包门面：按语言把注册表/Omakase 条目的显示名覆盖为目标语言。
/// zh-Hans = 现有 ZhLabels（表与覆盖逻辑都在那边）；新增语言 = 在此登记对应包实现。
/// 幂等，可在全部包注册完成后重放（EngineBoot 全量装配末尾的重放即走这里）。
/// 注册表的日文 Name 是内部基表/兜底，标签包只覆盖显示名，不动语义。
/// </summary>
public static class LabelPacks
{
    public static string DefaultLang => "zh-Hans";

    public static void Apply(string lang)
    {
        if (lang != DefaultLang)
            Log.Warn($"[LabelPacks] 无 {lang} 标签包，回退 {DefaultLang}");
        ZhLabels.Apply(); // 当前唯一的包实现
    }
}
