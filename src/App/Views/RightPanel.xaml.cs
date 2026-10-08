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

    private void Tab_Click(object sender, RoutedEventArgs e) => ShowTab(
        ReferenceEquals(sender, TabFx) ? "fx"
        : ReferenceEquals(sender, TabTech) ? "tech"
        : ReferenceEquals(sender, TabSettings) ? "settings"
        : ReferenceEquals(sender, TabOut) ? "out"
        : "style");

    /// <summary>切页（key：style/fx/tech/settings/out）。</summary>
    public void ShowTab(string key)
    {
        var on = key switch
        {
            "fx" => TabFx,
            "tech" => TabTech,
            "settings" => TabSettings,
            "out" => TabOut,
            _ => TabStyle,
        };
        foreach (var b in new[] { TabStyle, TabFx, TabTech, TabSettings, TabOut })
            b.Tag = ReferenceEquals(b, on) ? "on" : null;
        HostStyle.Visibility = ReferenceEquals(on, TabStyle) ? Visibility.Visible : Visibility.Collapsed;
        HostFx.Visibility = ReferenceEquals(on, TabFx) ? Visibility.Visible : Visibility.Collapsed;
        HostTech.Visibility = ReferenceEquals(on, TabTech) ? Visibility.Visible : Visibility.Collapsed;
        HostSettings.Visibility = ReferenceEquals(on, TabSettings) ? Visibility.Visible : Visibility.Collapsed;
        HostOut.Visibility = ReferenceEquals(on, TabOut) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>打开「特效」页并滚到音频特效区（Transport 的「特效」按钮落点）。</summary>
    public void OpenAudioFx()
    {
        ShowTab("fx");
        Fx.ScrollToAudio();
    }
}
