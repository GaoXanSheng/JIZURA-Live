using JizuraLive.App.I18n;
using JizuraLive.Render.Effects;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 效果目录的文案键必须都在默认词库里。
///
/// 缺一条的后果不是崩溃而是降级：<c>L.T</c> 原样返回键名并记一条 Warn，界面退回
/// <c>PvStageWindow.LocName</c> 的「去前缀 + 拆驼峰」英文名（<c>Fx.GlassShatter</c> → Glass Shatter）。
/// 启动时效果架与两个下拉会把整个目录过一遍，缺键就是几十条 Warn + 半中半英的列表，
/// 所以这条映射用用例钉住，而不是靠启动日志事后发现。
/// </summary>
public class I18nCatalogKeysTests
{
    /// <summary>EffectCatalog/EffectPresets 里的每个 NameKey/LabelKey/DescriptionKey 都要有中文词条。</summary>
    [Fact]
    public void EveryCatalogAndPresetKeyExistsInDefaultPack()
    {
        var pack = L.Pack(L.DefaultLang);
        Assert.NotEmpty(pack); // 词库没装进来时下面的断言会全绿，先把这一条钉住

        var missing = new List<string>();
        foreach (var def in EffectCatalog.All)
        {
            if (!pack.ContainsKey(def.NameKey)) missing.Add(def.NameKey);
            missing.AddRange(def.Params.Select(p => p.LabelKey).Where(k => !pack.ContainsKey(k)));
        }

        foreach (var preset in EffectPresets.All)
        {
            if (!pack.ContainsKey(preset.LabelKey)) missing.Add(preset.LabelKey);
            if (!pack.ContainsKey(preset.DescriptionKey)) missing.Add(preset.DescriptionKey);
        }

        Assert.True(missing.Count == 0, "默认词库（" + L.DefaultLang + "）缺这些键：" + string.Join("、", missing));
    }
}
