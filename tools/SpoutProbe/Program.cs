using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using JizuraLive.Logging;
using JizuraLive.Render.Draw;
using JizuraLive.Render.Gpu;
using JizuraLive.Spout;

namespace SpoutProbe;

/// <summary>
/// M1 路线图第 6 步「SpoutIn 可终止性核实」的实测探针。
///
///   Q1 无人发送时 native ReceiveTexturePtr 是否立即返回——不开任何 Spout sender，
///      连续 Pump 跑满 N 秒，逐次计时 + 抓「等待 Spout 源」日志节拍。
///   Q2 关闭安全性——起接收 → Pump 若干次 → Dispose/Close，三个场景分别计时。
///   Q3 PumpFrame 实际量级——工作台真实路径逐次耗时，min/max/p50。
///   Q4 发送端用着用着消失（VTS 被关掉）后，取帧是否仍立即返回——停机握手最关心的一拍。
///   Q5 Q4 离群点定位：sender 释放后「第一帧」的代价是否确定性。
///
/// 用法：SpoutProbe.exe [--seconds N] [--max-pumps N] [--sender 名] [--self-send] [--no-exit]
///   --seconds N    Q1/Q3 各自跑多少秒（默认 Q1=12s，覆盖 10s 的等待日志节拍；Q3=6s）
///   --max-pumps N  Q1 的 Pump 次数上限（默认 20000，防失控）
///   --sender 名    Q3/Q4 用的 sender 名（默认 SpoutProbeSelfTest）
///   --self-send    先用 SpoutOut 自己发一路，测「有源」路径下 PumpFrame 的真实量级
///   --no-exit      跑完不自动关窗（人工观察用；默认自动关，退出码即结论）
/// 退出码：0 = 全部断言通过；3 = 取帧阻塞；4 = Q2 关闭不安全；5 = GPU/初始化失败。
/// </summary>
internal static class Program
{
    private const string DefaultSender = "SpoutProbeSelfTest";

    [STAThread]
    private static int Main(string[] args)
    {
        // 数据目录隔离：探针的日志/配置绝不写用户真实目录（测试进程踩过 autosave 的坑）
        var isoDir = Path.Combine(Path.GetTempPath(), "jz-spoutprobe-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(isoDir);
        Environment.SetEnvironmentVariable("JIZURA_PV_DATA_DIR", isoDir);
        Log.Init("spoutprobe.log");

        // 未处理异常必须留下痕迹：Q2 的「无未处理异常」断言靠它取证
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            Console.WriteLine($"[Probe][FATAL] 未处理异常（IsTerminating={e.IsTerminating}）：{ex}");
            Log.Error("[Probe] 未处理异常", ex ?? new Exception("unknown"));
        };

        var q1Seconds = 12.0;
        var q3Seconds = 6.0;
        var maxPumps = 20000;
        var sender = DefaultSender;
        var selfSend = false;
        var autoExit = true;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--seconds" && i + 1 < args.Length)
            {
                if (double.TryParse(args[i + 1], out var s)) { q1Seconds = s; q3Seconds = Math.Max(2, s / 2); }
            }
            if (args[i] == "--max-pumps" && i + 1 < args.Length) int.TryParse(args[i + 1], out maxPumps);
            if (args[i] == "--sender" && i + 1 < args.Length) sender = args[i + 1];
            if (args[i] == "--self-send") selfSend = true;
            if (args[i] == "--no-exit") autoExit = false;
        }

        Console.WriteLine("[Probe] SpoutIn 可终止性实测（M1 路线图第 6 步）");
        Console.WriteLine($"[Probe] Q1={q1Seconds}s（覆盖 10s 等待日志节拍）Q3={q3Seconds}s maxPumps={maxPumps} sender='{sender}' selfSend={selfSend}");
        Console.WriteLine($"[Probe] 数据目录（隔离）：{isoDir}");

        // 只需要一个 D3D11 设备 + D2D 上下文（SpoutSourceProvider 构造要 ID2D1DeviceContext）。
        // 复用主工程的 D3DDevice/D2DHost：回退链独显→硬件→WARP。WARP 下 ReceiveTexturePtr
        // 走的是同一条 native 调用路径，对「是否阻塞」的结论仍然有效。
        var gpu = D3DDevice.TryCreate(out var err);
        if (gpu is null)
        {
            Console.WriteLine($"[Probe][FAIL] D3D11 设备创建失败：{err}");
            return 5;
        }

        Console.WriteLine($"[Probe] GPU：{gpu.AdapterName}｜feature level {gpu.FeatureLevel}｜软件光栅={gpu.IsSoftware}");

        using var d2d = D2DHost.Create(gpu);
        using var outSend = selfSend ? new SpoutOut(gpu, sender, 640, 480) : null;
        var probe = new Probe(gpu, d2d.Ctx, sender);

        var q1 = probe.NoSenderReturns(q1Seconds, maxPumps);
        var q3 = probe.PumpFrameStats(q3Seconds, selfSend, outSend);
        var q2 = probe.CloseSafety();
        var q4 = probe.SenderDiesMidStream();
        var q5 = probe.FirstPumpAfterSenderGone(6);
        var q6 = new NativeProbe(gpu, sender).Run(40);

        var report = new Report(q1, q3, q2, q4, q5, q6, gpu.AdapterName, gpu.IsSoftware, maxPumps, selfSend);
        report.Print();

        if (autoExit)
        {
            // 走 WPF Application 的正常关闭路径再退：native 收尾若卡在进程退出上，这里会挂住不放
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Application.Current?.Shutdown(report.ExitCode);
            };
            timer.Start();
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            return app.Run();
        }

        return report.ExitCode;    }
}
