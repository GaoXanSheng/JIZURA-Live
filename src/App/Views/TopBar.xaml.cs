using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using JizuraLive.App.I18n;
using JizuraLive.Logging;
using Microsoft.Win32;

namespace JizuraLive.App.Views;

// 顶栏（对位 TopBar.vue）：曲名/歌手输入 + 打开/保存/初始化/使用说明。
// 云音乐在「歌曲与节拍」面板，跟随在云音乐面板里，Spout 开关在「设置」页（与旧版一致）。
public partial class TopBar : UserControl
{
    private bool _syncing;
    // 输入即重规划，但防抖 300ms（对位 replanSoon(300)），免得每个键都全量重规划
    private readonly DispatcherTimer _debounce;

    public TopBar()
    {
        InitializeComponent();
        _debounce = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            PushTitleArtist();
        };
        Loaded += (_, _) =>
        {
            var main = App.MainInstance!;
            main.State.StateChanged += () => Dispatcher.Invoke(PullProject);
            PullProject();
        };
    }

    private void PullProject()
    {
        var p = App.MainInstance!.State.Project;
        if (TbTitle.Text == p.Title && TbArtist.Text == p.Artist) return;
        _syncing = true;
        TbTitle.Text = p.Title;
        TbArtist.Text = p.Artist;
        _syncing = false;
    }

    private void TbTitle_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || !IsLoaded) return;
        _debounce.Stop();
        _debounce.Start();
    }

    private void TbArtist_TextChanged(object sender, TextChangedEventArgs e) => TbTitle_TextChanged(sender, e);

    private void PushTitleArtist()
    {
        if (_syncing) return;
        var state = App.MainInstance!.State;
        if (state.Project.Title == TbTitle.Text && state.Project.Artist == TbArtist.Text) return;
        state.Mutate(p =>
        {
            p.Title = TbTitle.Text;
            p.Artist = TbArtist.Text;
        }, L.T("TopBar.Mutate.TitleArtist"), replan: true, look: true);
    }

    private void BtnOpen_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = ProjectIO.FileFilter, Title = L.T("TopBar.OpenDlg.Title") };
        if (dlg.ShowDialog(Window.GetWindow(this)!) != true) return;
        try
        {
            var project = ProjectIO.Open(dlg.FileName);
            App.MainInstance!.State.ReplaceProject(project, L.T("TopBar.Mutate.OpenProject"));
            Log.Info("[TopBar] 工程已打开：" + dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageDialog.Warn(Window.GetWindow(this)!, L.T("TopBar.OpenFailed"), ex.Message);
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var main = App.MainInstance!;
        var dlg = new SaveFileDialog
        {
            Filter = L.T("TopBar.SaveDlg.Filter"),
            FileName = (string.IsNullOrWhiteSpace(main.State.Project.Title) ? "untitled" : main.State.Project.Title) + ".jizuralive"
        };
        if (dlg.ShowDialog(Window.GetWindow(this)!) != true) return;
        try
        {
            byte[]? audio = null;
            string? audioName = null;
            var resolvedAudioPath = EditorState.ResolveAudioPath(main.State.AudioPath, main.State.AudioFileName);
            if (resolvedAudioPath != null && File.Exists(resolvedAudioPath))
            {
                audio = File.ReadAllBytes(resolvedAudioPath);
                audioName = main.State.AudioFileName ?? Path.GetFileName(resolvedAudioPath);
            }
            ProjectIO.Save(main.State.Project, dlg.FileName, audio, audioName);
            Log.Info("[TopBar] 工程已保存：" + dlg.FileName);
        }
        catch (NotImplementedException)
        {
            // 发生异常时兜底存 .json，防止丢失工程数据
            var fallback = Path.ChangeExtension(dlg.FileName, ".json");
            ProjectIO.Save(main.State.Project, fallback, null, null);
            MessageDialog.Info(Window.GetWindow(this)!, L.T("TopBar.SaveFallback.Title"), L.F("TopBar.SaveFallback.Body", fallback));
        }
        catch (Exception ex)
        {
            MessageDialog.Warn(Window.GetWindow(this)!, L.T("TopBar.SaveFailed"), ex.Message);
        }
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        var win = Window.GetWindow(this)!;
        MessageDialog.ConfirmReset(win, () => App.MainInstance!.State.ResetAll());
    }

    private void BtnTerms_Click(object sender, RoutedEventArgs e)
    {
        MessageDialog.Info(Window.GetWindow(this)!, L.T("TopBar.Terms"), BuildTermsBody());
    }

    /// <summary>
    /// 使用说明正文（对位旧前端 Dialogs.vue 的 terms 弹窗）：权利提示 → 工具出处 → 隐私说明
    /// → 可折叠的「使用的开源组件」清单 → 版本/免费声明。
    /// 内容多了会撑高卡片，所以套一层限高滚动区。
    /// </summary>
    private static UIElement BuildTermsBody()
    {
        var res = Application.Current;
        var fg = (Brush)res.FindResource("Fg");
        var mut = (Brush)res.FindResource("Mut");
        var amber = (Brush)res.FindResource("Amber");

        var root = new StackPanel();

        // 权利提示：左侧琥珀色竖条 + raised 底（对位 Vue 的 border-l-[3px] border-amber bg-raised）
        var rightsLine = new TextBlock { FontSize = 15, TextWrapping = TextWrapping.Wrap };
        rightsLine.Inlines.Add(new Run(L.T("TopBar.Terms.Rights.Lead")));
        rightsLine.Inlines.Add(new Run(L.T("TopBar.Terms.Rights.Em")) { Foreground = amber, FontWeight = FontWeights.Bold });
        var calloutStack = new StackPanel();
        calloutStack.Children.Add(rightsLine);
        calloutStack.Children.Add(new TextBlock
        {
            Text = L.T("TopBar.Terms.Rights.Note"),
            FontSize = 12,
            Foreground = mut,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });
        root.Children.Add(new Border
        {
            Background = (Brush)res.FindResource("Raised"),
            BorderBrush = amber,
            BorderThickness = new Thickness(3, 0, 0, 0),
            CornerRadius = new CornerRadius(0, 6, 6, 0),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 0, 0, 14),
            Child = calloutStack,
        });

        root.Children.Add(new TextBlock { Text = L.T("TopBar.Terms.BasedOn"), FontSize = 13, Foreground = fg });
        var link = new Hyperlink(new Run("https://github.com/852wa/JIZURA")) { Foreground = amber };
        link.RequestNavigate += (_, e2) => OpenUrl(e2.Uri.AbsoluteUri);
        root.Children.Add(new TextBlock
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 10),
            Inlines = { link },
        });
        root.Children.Add(new TextBlock
        {
            Text = L.T("TopBar.Terms.Privacy"),
            FontSize = 12,
            Foreground = mut,
            TextWrapping = TextWrapping.Wrap,
        });

        root.Children.Add(new Expander
        {
            Header = new TextBlock { Text = L.T("TopBar.Terms.Components"), FontSize = 12, Foreground = mut },
            Foreground = mut,
            IsExpanded = true,
            Margin = new Thickness(0, 12, 0, 0),
            Content = new TextBlock
            {
                Text = L.T("TopBar.Terms.ComponentsList"),
                FontSize = 12,
                Foreground = mut,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0),
            },
        });

        root.Children.Add(new TextBlock
        {
            Text = L.T("TopBar.Terms.Version") + "　" + L.T("TopBar.Terms.Free"),
            FontSize = 12,
            Foreground = mut,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0),
        });

        return new ScrollViewer
        {
            MaxHeight = 460,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = root,
        };
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Debug("打开链接失败：" + ex.Message);
        }
    }
}
