using JizuraLive.Render.Effects;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 预设选择器的下标契约（启动不许套用预设）。
///
/// <c>PvStageWindow</c> 的预设下拉框在构造期只能做一次「当前状态」回填；这次回填若落在某个预设上，
/// <c>SelectionChanged</c> 会清空工程里已存的效果图并立刻存盘——用户上次存的效果图就这么没了
/// （实测：启动即套 cinematic，且随后的自动存盘把它写回磁盘）。
/// 选择器因此多一个占位项（下标 0 = 当前效果图，不套预设），本组用例钉住这条映射。
///
/// 边界说明：窗口自己的 <c>SelectionChanged</c> 处理是 WPF 的，这里不实例化窗口——
/// 构造函数会读写真实用户配置目录（ProjectStore + 5 秒一次的自动存盘），在测试里跑等于改用户数据。
/// 所以本组用例钉的是"启动选中项 → 不改图"这条纯映射；窗口接线由人工读码复核。
/// </summary>
public class EffectPresetPickerTests
{
    /// <summary>启动回填的选中项必须落在占位项上，即「不改动效果图」。</summary>
    [Fact]
    public void InitialSelection_IsPlaceholder_NotAPreset()
    {
        Assert.Equal(0, EffectPresets.PickerInitialIndex);
        Assert.Null(EffectPresets.PickerItemAt(EffectPresets.PickerInitialIndex));
    }

    /// <summary>占位项 + 全部预设：下标与预设一一对应，越界/未选中返回 null（不改图）而不是抛。</summary>
    [Fact]
    public void PickerItems_MapToPresetsWithPlaceholderOffset()
    {
        var labels = EffectPresets.PickerLabels().ToList();
        Assert.Equal(EffectPresets.All.Count + 1, labels.Count);
        Assert.Equal(EffectPresets.PickerPlaceholder, labels[0]);

        for (var i = 0; i < EffectPresets.All.Count; i++)
        {
            Assert.Equal(EffectPresets.All[i].Id, labels[i + 1]);
            Assert.Same(EffectPresets.All[i], EffectPresets.PickerItemAt(i + 1));
        }

        Assert.Null(EffectPresets.PickerItemAt(-1));                          // 未选中
        Assert.Null(EffectPresets.PickerItemAt(EffectPresets.All.Count + 1)); // 越界
    }

    /// <summary>占位项不能撞上任何预设 Id，否则「选它不改图」会把那个预设挡掉。</summary>
    [Fact]
    public void Placeholder_IsNotAPresetId()
    {
        Assert.DoesNotContain(EffectPresets.PickerPlaceholder, EffectPresets.All.Select(p => p.Id));
    }
}
