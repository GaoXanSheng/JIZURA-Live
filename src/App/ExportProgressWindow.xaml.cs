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
            if (!_done) return;
            _timer.Stop();
            DialogResult = _error == null;
            Close();
        };
        Loaded += (_, _) => StartExport();
        Closed += (_, _) =>
        {
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
        Task.Run(() =>
        {
            try
            {
                ExportJob.Run(req, progress, ct);
                if (req.WavToo) ExportJob.ExportSideWav(req);
            }
            catch (OperationCanceledException)
            {
                _stage = L.T("ExportProgress.Canceled");
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
        }, ct);
        _timer.Start();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _cts.Cancel();
        TblStage.Text = L.T("ExportProgress.Canceling");
    }

    public Exception? Error => _error;
}
