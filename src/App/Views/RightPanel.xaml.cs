using System.Windows;
using System.Windows.Controls;

namespace JizuraLive.App.Views;

// 右栏页签容器（对位 RightPanel.vue）：等宽页签 + 内容切换（PV 页签为本仓新增，默认激活）。
// 旧版 StyleTab/FxTab 常驻（v-show）、TechTab/SettingsTab 整体卸载（v-if）；
// 这里统一用 Visibility 切换——TechTab 的卡片是按需生成的，卸载重建的代价比常驻高。
public partial class RightPanel : UserControl
{
    public RightPanel()
    {
        InitializeComponent();
    }

    private void Tab_Click(object sender, RoutedEventArgs e) => ShowTab(
        ReferenceEquals(sender, TabFx) ? "fx"
        : ReferenceEquals(sender, TabTech) ? "tech"
        : ReferenceEquals(sender, TabPv) ? "pv"
        : ReferenceEquals(sender, TabSettings) ? "settings"
        : "style");

    /// <summary>切页（key：style/fx/tech/pv/settings）。</summary>
    public void ShowTab(string key)
    {
        var on = key switch
        {
            "fx" => TabFx,
            "tech" => TabTech,
            "pv" => TabPv,
            "settings" => TabSettings,
            _ => TabStyle,
        };
        foreach (var b in new[] { TabStyle, TabFx, TabTech, TabPv, TabSettings })
            b.Tag = ReferenceEquals(b, on) ? "on" : null;
        ShowHost(HostStyle, ReferenceEquals(on, TabStyle));
        ShowHost(HostFx, ReferenceEquals(on, TabFx));
        ShowHost(HostTech, ReferenceEquals(on, TabTech));
        ShowHost(HostPv, ReferenceEquals(on, TabPv));
        ShowHost(HostSettings, ReferenceEquals(on, TabSettings));
        // PV 面板的卡片是代码建的，且要开一条 1s 状态计时器：由切页方驱动，不赌折叠容器的可见性事件时序
        if (ReferenceEquals(on, TabPv)) Pv.EnsureReady();
    }

    /// <summary>页签内容显隐：显示时淡入 + 轻微上浮（页签下划线由 Theme 的 TabHeader 自己淡）。
    /// 隐藏保持即时 Collapsed —— TechTab 等以 IsVisibleChanged 为信号暂停缩略图渲染，
    /// 延迟收起会破坏那个语义（见 TechTab.xaml.cs）。</summary>
    private static void ShowHost(UIElement host, bool on) => JzAnim.ShowFade(host, on, 5);
}
