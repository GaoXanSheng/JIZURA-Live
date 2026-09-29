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

        CmbFps.SelectedIndex =
            settings.RenderFps >= 55
                ? 1
                : 0;

        TxtSpoutName.Text =
            settings.SpoutName;

        ChkSpout.IsChecked =
            settings.SpoutEnabled;
    }

    private void Persist()
    {
        _settings?.Save();
    }

    private void ApplyResolution()
    {
        if (_settings == null)
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
        if (_settings == null)
            return;

        if (CmbFps.SelectedIndex < 0)
            return;

        int fps =
            CmbFps.SelectedItem as string ==
            "60"
                ? 60
                : 30;

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
        if (_settings == null)
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