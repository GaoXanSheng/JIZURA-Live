using System.IO;
using System.Windows;
using System.Windows.Controls;
using JizuraLive.Engine;
using JizuraLive.Logging;
using Microsoft.Win32;

namespace JizuraLive.App.Views;

// 顶栏（对位 TopBar.vue）：曲名/歌手、打开/保存/初始化/使用说明、Spout 开关、状态行
public partial class TopBar : UserControl
{
    private bool _syncing;

    public TopBar()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            var main = App.MainInstance!;
            ChkSpout.IsChecked = App.SettingsOf()?.SpoutEnabled == true;
            main.StatusChanged += RefreshStatus;
            main.State.StateChanged += () => Dispatcher.Invoke(PullProject);
            PullProject();
            RefreshStatus();
        };
    }

    private void PullProject()
    {
        var p = App.MainInstance!.State.Project;
        _syncing = true;
        TbTitle.Text = p.Title;
        TbArtist.Text = p.Artist;
        _syncing = false;
    }

    private void PushTitleArtist()
    {
        if (_syncing) return;
        var state = App.MainInstance!.State;
        if (state.Project.Title == TbTitle.Text && state.Project.Artist == TbArtist.Text) return;
        state.Mutate(p =>
        {
            p.Title = TbTitle.Text;
            p.Artist = TbArtist.Text;
        }, "曲名/歌手", replan: true, look: true);
    }

    private void RefreshStatus()
    {
        var main = App.MainInstance!;
        var plan = main.Preview.Plan;
        if (plan == null) return;
        TblStatus.Text =
            $"{plan.W}×{plan.H} · {plan.Cuts.Count} 切分 · seed {plan.Seed}"
            + (main.Spout.IsRunning ? $" · Spout→{main.Spout.SenderName}" : "");
    }

    private void BtnOpen_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = ProjectIO.FileFilter, Title = "打开工程" };
        if (dlg.ShowDialog(Window.GetWindow(this)!) != true) return;
        try
        {
            var project = ProjectIO.Open(dlg.FileName);
            App.MainInstance!.State.ReplaceProject(project, "打开工程");
            Log.Info("[TopBar] 工程已打开：" + dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, "打开失败：" + ex.Message, "字面 Live",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        var main = App.MainInstance!;
        var dlg = new SaveFileDialog
        {
            Filter = "字面 Live 工程 (*.jizuralive)|*.jizuralive",
            FileName = (string.IsNullOrWhiteSpace(main.State.Project.Title) ? "untitled" : main.State.Project.Title) + ".jizuralive"
        };
        if (dlg.ShowDialog(Window.GetWindow(this)!) != true) return;
        try
        {
            byte[]? audio = null;
            string? audioName = null;
            if (main.State.AudioPath != null && File.Exists(main.State.AudioPath))
            {
                audio = File.ReadAllBytes(main.State.AudioPath);
                audioName = main.State.AudioFileName ?? Path.GetFileName(main.State.AudioPath);
            }
            ProjectIO.Save(main.State.Project, dlg.FileName, audio, audioName);
            Log.Info("[TopBar] 工程已保存：" + dlg.FileName);
        }
        catch (NotImplementedException)
        {
            // .jizuralive 写入（M3-A）未落地时兜底存 .json，不丢工程
            var fallback = Path.ChangeExtension(dlg.FileName, ".json");
            ProjectIO.Save(main.State.Project, fallback, null, null);
            MessageBox.Show(Window.GetWindow(this)!, "工程包写入尚未就绪，已保存为 JSON：\n" + fallback, "字面 Live",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this)!, "保存失败：" + ex.Message, "字面 Live",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(Window.GetWindow(this)!, "清空歌词、歌曲、设置和历史，回到初始状态？", "初始化",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        App.MainInstance!.State.ResetAll();
    }

    private void BtnTerms_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(Window.GetWindow(this)!,
            "字面 Live（JIZURA-live）\n\n" +
            "输出物（视频/图片/Spout 画面）的著作权归工程的创作者所有。\n" +
            "自托管字体与引擎代码的许可证详见安装目录 LICENSE 文件。\n" +
            "导出歌曲请确认你拥有相应权利。",
            "使用说明", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ChkSpout_Changed(object sender, RoutedEventArgs e)
    {
        var main = App.MainInstance;
        if (main == null || !IsLoaded) return; // XAML 初始化期
        main.SetSpoutEnabled(ChkSpout.IsChecked == true);
        RefreshStatus();
    }
}
