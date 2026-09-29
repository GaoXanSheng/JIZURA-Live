using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CefSharp;

namespace JizuraLive;

public partial class SettingsPanel : UserControl
{
    private Settings? _settings;

    private bool _initialized;

    // 程序填充 UI 初始值时置 true，屏蔽 Change 事件回调——
    // 否则每次打开设置窗口（新面板 SelectedIndex 从 -1 赋值）
    // 都会触发 ApplyFps/ApplyResolution/ToggleSpout，导致镜像被误重启
    private bool _loadingUi;

    private static readonly (
        int W,
        int H,
        string Label
        )[] Resolutions =
        {
            (1280, 720, "1280 × 720 (16:9)"),
            (1920, 1080, "1920 × 1080 (16:9)"),
            (2560, 1440, "2560 × 1440 (16:9)"),
            (1080, 1920, "1080 × 1920 (9:16 竖屏)"),
            (2160, 3840, "2160 × 3840 (9:16 竖屏)")
        };

    private readonly DispatcherTimer _statusTimer =
        new()
        {
            Interval =
                TimeSpan.FromMilliseconds(500)
        };

    public SettingsPanel()
    {
        InitializeComponent();
    }

    public void Initialize(
        Settings settings)
    {
        _settings = settings;

        if (!_initialized)
        {
            _initialized = true;

            foreach (var r in Resolutions)
            {
                CmbRes.Items.Add(
                    r.Label);
            }

            CmbFps.Items.Add("30");
            CmbFps.Items.Add("60");
            CmbFps.Items.Add("120");

            CmbRes.SelectionChanged +=
                (_, _) =>
                    ApplyResolution();

            CmbFps.SelectionChanged +=
                (_, _) =>
                    ApplyFps();

            ChkSpout.Checked +=
                (_, _) =>
                    ToggleSpout();

            ChkSpout.Unchecked +=
                (_, _) =>
                    ToggleSpout();

            TxtSpoutName.LostFocus +=
                (_, _) =>
                {
                    if (
                        ChkSpout.IsChecked ==
                        true
                    )
                    {
                        ToggleSpout();
                    }
                };

            TxtSpoutName.KeyDown +=
                (_, e) =>
                {
                    if (
                        e.Key ==
                        Key.Enter
                    )
                    {
                        ToggleSpout();
                    }
                };

            ChkCdp.Checked +=
                (_, _) => ApplyCdp();

            ChkCdp.Unchecked +=
                (_, _) => ApplyCdp();

            TxtCdpPort.LostFocus +=
                (_, _) => ApplyCdp();

            TxtCdpPort.KeyDown +=
                (_, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        ApplyCdp();
                    }
                };

            BtnRestartRender.Click +=
                (_, _) =>
                {
                    var main =
                        Application.Current
                                .MainWindow
                            as MainWindow;

                    if (main == null)
                        return;

                    // 只重启 Spout 镜像（子界面）浏览器，
                    // 主窗口浏览器保持不动
                    if (
                        !SpoutPipeline
                            .Instance
                            .IsRunning
                    )
                    {
                        Log.Info(
                            "Spout 管线未运行，" +
                            "无需重启渲染引擎");

                        return;
                    }

                    Log.Info(
                        "重启渲染引擎" +
                        "（Spout 镜像浏览器）…");

                    main.RestartSpout();
                };

            _statusTimer.Start();
        }

        _loadingUi = true;

        try
        {
            int idx =
                Array.FindIndex(
                    Resolutions,
                    r =>
                        r.W == settings.Width &&
                        r.H == settings.Height);

            CmbRes.SelectedIndex =
                idx >= 0
                    ? idx
                    : 1;

            int fpsIdx =
                CmbFps.Items.IndexOf(
                    settings.RenderFps.ToString());

            CmbFps.SelectedIndex =
                fpsIdx >= 0
                    ? fpsIdx
                    : 1;

            TxtSpoutName.Text =
                settings.SpoutName;

            ChkSpout.IsChecked =
                settings.SpoutEnabled;

            ChkCdp.IsChecked =
                settings.CdpEnabled;

            TxtCdpPort.Text =
                settings.CdpPort.ToString();
        }
        finally
        {
            _loadingUi = false;
        }
    }

    private void ApplyCdp()
    {
        if (_loadingUi || _settings == null)
            return;

        bool on =
            ChkCdp.IsChecked == true;

        int port =
            int.TryParse(TxtCdpPort.Text.Trim(), out int p)
                ? Math.Clamp(p, 1, 65535)
                : 9222;

        if (_settings.CdpEnabled == on &&
            _settings.CdpPort == port)
        {
            return;
        }

        _settings.CdpEnabled = on;
        _settings.CdpPort = port;

        TxtCdpPort.Text = port.ToString();

        Persist();

        Log.Info(
            $"CDP 调试端口设置：" +
            $"{(on ? $"启用，端口 {port}" : "关闭")}" +
            $"（重启应用后生效）");
    }

    private void Persist()
    {
        _settings?.Save();
    }

    private void ApplyResolution()
    {
        if (_loadingUi || _settings == null)
            return;

        if (CmbRes.SelectedIndex < 0)
            return;

        var (
                w,
                h,
                _
                ) =
            Resolutions[
                CmbRes.SelectedIndex];

        _settings.Width = w;
        _settings.Height = h;

        Persist();

        Log.Info(
            $"Spout 分辨率设置：{w}x{h}");

        // 如果 Spout 正在运行，直接调整 mirror 浏览器尺寸
        if (
            SpoutPipeline.Instance.IsRunning &&
            SpoutPipeline.Instance._browser != null
        )
        {
            SpoutPipeline
                    .Instance
                    ._browser
                    .Size =
                new System.Drawing.Size(
                    w,
                    h);
        }
    }

    private void ApplyFps()
    {
        if (_loadingUi || _settings == null)
            return;

        if (CmbFps.SelectedIndex < 0)
            return;

        int fps =
            int.TryParse(CmbFps.SelectedItem as string, out int f)
                ? Math.Clamp(f, 1, 240)
                : 30;

        // 值没变就不重启管线（避免设置窗口打开时的初始赋值触发）
        if (_settings.RenderFps == fps)
            return;

        _settings.RenderFps =
            fps;

        Persist();

        Log.Info(
            $"渲染帧率设置：{fps}fps");

        var main =
            Application.Current.MainWindow
                as MainWindow;

        if (
            main != null &&
            SpoutPipeline.Instance.IsRunning
        )
        {
            main.RestartSpout();
        }
    }

    private void ToggleSpout()
    {
        if (_loadingUi || _settings == null)
            return;

        bool on =
            ChkSpout.IsChecked ==
            true;

        string name =
            string.IsNullOrWhiteSpace(
                TxtSpoutName.Text)
                ? "JIZURA-live"
                : TxtSpoutName.Text.Trim();

        bool nameChanged =
            !string.Equals(
                _settings.SpoutName,
                name,
                StringComparison.Ordinal);

        _settings.SpoutEnabled =
            on;

        _settings.SpoutName =
            name;

        Persist();

        Log.Info(
            $"Spout 设置：" +
            $"启用={on}, " +
            $"通道={name}");

        var main =
            Application.Current.MainWindow
                as MainWindow;

        if (main == null)
            return;

        if (on)
        {
            if (
                SpoutPipeline.Instance.IsRunning &&
                nameChanged
            )
            {
                main.RestartSpout();
            }
            else
            {
                main.StartSpout();
            }
        }
        else
        {
            main.StopSpout();
        }
    }
}