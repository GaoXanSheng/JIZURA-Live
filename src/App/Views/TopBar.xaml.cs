using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
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
        }, "曲名/歌手", replan: true, look: true);
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
            MessageDialog.Warn(Window.GetWindow(this)!, "打开失败", ex.Message);
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
            // .jizuralive 写入未落地时兜底存 .json，不丢工程
            var fallback = Path.ChangeExtension(dlg.FileName, ".json");
            ProjectIO.Save(main.State.Project, fallback, null, null);
            MessageDialog.Info(Window.GetWindow(this)!, "保存", "工程包写入尚未就绪，已保存为 JSON：\n" + fallback);
        }
        catch (Exception ex)
        {
            MessageDialog.Warn(Window.GetWindow(this)!, "保存失败", ex.Message);
        }
    }

    private void BtnReset_Click(object sender, RoutedEventArgs e)
    {
        var win = Window.GetWindow(this)!;
        MessageDialog.ConfirmReset(win, () => App.MainInstance!.State.ResetAll());
    }

    private void BtnTerms_Click(object sender, RoutedEventArgs e)
    {
        MessageDialog.Info(Window.GetWindow(this)!, "使用说明",
            "字面 Live（JIZURA-live）\n\n"
            + "输出物（视频/图片/Spout 画面）的著作权归工程的创作者所有。\n"
            + "自托管字体与引擎代码的许可证详见安装目录 LICENSE 文件。\n"
            + "导出歌曲请确认你拥有相应权利。");
    }
}
