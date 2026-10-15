using System.Windows;
using System.Windows.Threading;
using JizuraLive.Render.Gpu;

namespace RenderProbe;

/// <summary>
/// P0 验证入口：开一个窗口，里面放 <see cref="PresentHost"/>（子窗口 + D3D11 flip 交换链），
/// 由 <see cref="ProbeRenderer"/> 在渲染线程上用 Direct2D/DirectWrite 画渐变与文字。
///
/// 用法：RenderProbe.exe [--seconds N] [--nosync] [--fx id1,id2]
///   --seconds N  跑 N 秒后自动退出（无人值守验证用）
///   --nosync     关垂直同步（Present(0,0)，测撕裂/上限帧率）
///   --fx a,b     指定效果链（目录里的 id，逗号分隔）；缺省用温和的整帧后期
///   --vts-sim    用合成追踪源（没有 VTS 时验证「追踪 → 效果」接线）
/// 退出码：0 = 正常；2 = GPU 路初始化失败（此时会打印原因）。
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var seconds = 0.0;
        var vsync = true;
        var fx = new List<string>();
        var vtsSim = false;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--seconds" && i + 1 < args.Length)
                double.TryParse(args[i + 1], out seconds);
            if (args[i] == "--nosync") vsync = false;
            if (args[i] == "--fx" && i + 1 < args.Length) fx.Add(args[i + 1]);
            if (args[i] == "--vts-sim") vtsSim = true;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new Window
        {
            Title = "JIZURA Render Core Probe",
            Width = 1280,
            Height = 720,
            Background = System.Windows.Media.Brushes.Black,
        };

        var host = new PresentHost
        {
            InitialWidth = 1280,
            InitialHeight = 720,
            VSync = vsync,
        };
        var renderer = new ProbeRenderer(host, fx.ToArray(), vtsSim);
        window.Content = host;

        if (seconds > 0)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                window.Close();
            };
            timer.Start();
        }

        window.Show();
        var code = app.Run(window);
        renderer.Dispose();

        Console.WriteLine($"[Probe] 结束：总帧数 {host.Frames}，最后 FPS {host.Fps:F1}，"
                          + $"GPU={renderer.AdapterName}，模式={renderer.Mode}，错误={host.Error ?? "无"}");
        return host.Error is null ? code : 2;
    }
}
