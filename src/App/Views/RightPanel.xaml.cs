using System.Windows;
using System.Windows.Controls;

namespace JizuraLive.App.Views;

// 右栏页签容器（对位 RightPanel.vue）：五等分页签 + 内容切换。
// 旧版 StyleTab/FxTab/OutTab 常驻（v-show）、TechTab/SettingsTab 整体卸载（v-if）；
// 这里统一用 Visibility 切换——TechTab 的卡片是按需生成的，卸载重建的代价比常驻高。
public partial class RightPanel : UserControl
{
    public RightPanel()
    {
        InitializeComponent();
    }

    private void Tab_Click(object sender, RoutedEventArgs e)
    {
        var tab = (Button)sender;
        foreach (var b in new[] { TabStyle, TabFx, TabTech, TabSettings, TabOut })
            b.Tag = ReferenceEquals(b, tab) ? "on" : null;
        HostStyle.Visibility = ReferenceEquals(tab, TabStyle) ? Visibility.Visible : Visibility.Collapsed;
        HostFx.Visibility = ReferenceEquals(tab, TabFx) ? Visibility.Visible : Visibility.Collapsed;
        HostTech.Visibility = ReferenceEquals(tab, TabTech) ? Visibility.Visible : Visibility.Collapsed;
        HostSettings.Visibility = ReferenceEquals(tab, TabSettings) ? Visibility.Visible : Visibility.Collapsed;
        HostOut.Visibility = ReferenceEquals(tab, TabOut) ? Visibility.Visible : Visibility.Collapsed;
    }
}
