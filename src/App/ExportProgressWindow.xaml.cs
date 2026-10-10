using System.IO;
using System.Windows;
using System.Windows.Threading;
using JizuraLive.App.I18n;
using JizuraLive.Export;
using JizuraLive.Logging;

namespace JizuraLive.App;

/// <summary>
/// 导出进度窗：后台 Task 跑 ExportJob，进度回组到 UI；取消 = CTS + 关闭窗口。
/// 导出期间预览渲染泵让路（ExportBusy），保证编码线程优先吃引擎闸。
/// </summary>
public partial class ExportProgressWindow : Window
{
    private readonly ExportRequest _req;
    private readonly CancellationTokenSource _cts = new();
    private readonly DispatcherTimer _timer;
    private volatile bool _done;
    private volatile bool _ok;   // 真的跑完（无异常、未被取消）——取消/失败都不能报「导出完成」
    private bool _closed;        // 窗口已关（取消 / X）：收尾的 tick 不能再设 DialogResult
    private Exception? _error;
    private double _pct;
    private string _stage = L.T("ExportProgress.Preparing");

    public ExportProgressWindow(ExportRequest req)
    {
        InitializeComponent();
        _req = req;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _timer.Tick += (_, _) =>
        {
            Bar.Value = Math.Clamp(_pct * 100, 0, 100);
            TblPct.Text = $"{_pct * 100:F0}%";
            TblStage.Text = _stage;
            if (!_done || _closed) return;
            _timer.Stop();
            DialogResult = _ok; // 取消 = false：OutTab 静默返回，只把进度文字落成「已取消」
            Close();
        };
        Loaded += (_, _) =>
        {
            JzAnim.FadeIn(Content as FrameworkElement, JzAnim.Dialog); // 打开动效（进度条本身照旧 150ms 刷）
            StartExport();
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _timer.Stop();
            _cts.Cancel();
            if (App.MainInstance != null) App.MainInstance.ExportBusy = false;
        };
    }

    private void StartExport()
    {
        var main = App.MainInstance;
        if (main != null) main.ExportBusy = true;
        var progress = new Progress<(double Pct, string Stage)>(p =>
        {
            _pct = p.Pct;
            _stage = p.Stage;
        });
        var ct = _cts.Token;
        var req = _req;
        // 不把 ct 交给 Task.Run：取消若发生在任务起跑前，delegate 根本不会执行，
        // _done 就永远翻不到 true，进度窗再也关不掉。取消由 ExportJob 帧间检查协作完成。
        Task.Run(() =>
        {
            try
            {
                ExportJob.Run(req, progress, ct);
                if (req.WavToo) ExportJob.ExportSideWav(req);
                _ok = true;
            }
            catch (OperationCanceledException)
            {
                _stage = L.T("ExportProgress.Canceled");
                CleanupPartial(req.OutputPath);
            }
            catch (Exception ex)
            {
                _error = ex;
                Log.Error("[Export] 导出失败", ex);
            }
            finally
            {
                _done = true;
            }
        });
        _timer.Start();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        TblStage.Text = L.T("ExportProgress.Canceling");
    }

    /// <summary>取消后清掉半成品输出：mp4/webm 没写 moov、png zip 没写中央目录，留着也播不出来，
    /// 而路径又正是用户选定的成品名。只在这一次导出确实被取消时调用（正常完成由 _ok 挡住，失败不删）。</summary>
    private static void CleanupPartial(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            Log.Warn("[Export] 半成品清理失败：" + ex.Message);
        }
    }

    public Exception? Error => _error;
}
