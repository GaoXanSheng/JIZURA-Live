using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using JizuraLive.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace JizuraLive.App;

// 主程序侧：拉起/守护 --mirror-host 子进程，经命名管道转发播放锚点、编辑页消息、管线命令。
// 关联保障：Job 兜底主死宿亡；令牌握手防新旧宿主并存；崩溃重启限频；退出 shutdown→3s→Kill
public sealed class MirrorHostBridge : IDisposable
{
    public const string PipeName = "jizura-mirror-host";
    public const string HostMutexName = "JizuraLive_MirrorHost";
    public const string ShutdownEventName = "JizuraLive_MirrorHost_Shutdown";

    public static MirrorHostBridge? Instance { get; private set; }

    private readonly Settings _settings;
    private readonly string _url;
    private readonly string _token = Guid.NewGuid().ToString("N");
    private readonly object _gate = new();
    private readonly Queue<DateTime> _restartStamps = new();
    private readonly CancellationTokenSource _cts = new();

    private Process? _process;
    private IntPtr _job = IntPtr.Zero;
    private NamedPipeServerStream? _pipe;
    private StreamWriter? _writer;
    private volatile bool _connected;
    private volatile bool _disposed;

    // 断线期排队，握手后按序重放；cmd 同名取最新，play 只留最新（旧锚点重放无意义）
    private readonly List<(string? Name, string Line)> _pending = new();

    // 最近一次 start/restart：宿主自愈重连后重放它管线才能恢复；stop 清掉
    private string? _lastStartCmd;

    public Action? PokeSync { get; set; }
    public Action? NeedEditorState { get; set; }

    public bool Connected => _connected;

    // 拆分模式标记（MainWindow 据此把管线命令改走 IPC）
    public bool Enabled => true;

    public MirrorHostBridge(Settings settings, string url)
    {
        _settings = settings;
        _url = url;
        Instance = this;
    }

    // JIZURA_MIRROR_HOST=0 一票否决
    public static bool IsEnabled(Settings settings)
    {
        return settings.SplitMirrorHost &&
               !string.Equals(Environment.GetEnvironmentVariable("JIZURA_MIRROR_HOST"), "0", StringComparison.OrdinalIgnoreCase);
    }

    // 孤儿清场：宿主互斥被占而主互斥空闲 ⇒ 宿主是孤儿。发事件催退，等不到也继续（它有父进程监视，秒级自灭）
    public static void CleanupStaleHost()
    {
        Mutex? probe;
        try
        {
            probe = Mutex.OpenExisting(HostMutexName);
        }
        catch
        {
            return;
        }

        Log.Info("发现孤儿镜像宿主，请求其退出…");
        try
        {
            using var ev = EventWaitHandle.OpenExisting(ShutdownEventName);
            ev.Set();
        }
        catch
        {
        }

        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                probe.Dispose();
                probe = Mutex.OpenExisting(HostMutexName);
            }
            catch
            {
                Log.Info("孤儿镜像宿主已退出");
                return;
            }

            Thread.Sleep(200);
        }

        Log.Warn("孤儿镜像宿主 5s 未退出，继续启动");
    }

    public void Start()
    {
        ResetShutdownSignal();
        CleanupStaleHost();
        new Thread(PipeLoop) { IsBackground = true, Name = "MirrorHostPipe" }.Start();
        Spawn();
    }

    private static void ResetShutdownSignal()
    {
        try
        {
            // 预创建并复位，陈旧的 Set 信号不能带到新宿主
            using var ev = new EventWaitHandle(false, EventResetMode.ManualReset, ShutdownEventName, out _);
            ev.Reset();
        }
        catch (Exception ex)
        {
            Log.Warn("复位宿主 shutdown 事件失败：" + ex.Message);
        }
    }

    private void Spawn()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Log.Error("无法定位自身 exe，镜像宿主未拉起");
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
            Arguments =
                $"--mirror-host --url=\"{_url}\" --pipe={PipeName} " +
                $"--parent-pid={Environment.ProcessId} --token={_token}"
        };
        // 宿主调试端口从设置走（开关关掉不传 env，宿主侧 cdpOff 兜底），默认 9223 与主进程错开
        var hostCdpOn = _settings.MirrorCdpEnabled;
        var hostCdp = Math.Clamp(_settings.MirrorCdpPort, 0, 65535);
        if (hostCdpOn && hostCdp > 0)
            psi.EnvironmentVariables["JIZURA_CDP_PORT"] = hostCdp.ToString();

        var p = Process.Start(psi);
        if (p == null)
        {
            Log.Error("镜像宿主进程启动失败");
            return;
        }

        AssignToJob(p);
        p.EnableRaisingEvents = true;
        p.Exited += OnHostExited;
        _process = p;
        Log.Info($"镜像宿主已拉起：pid={p.Id}, cdp={(hostCdpOn && hostCdp > 0 ? hostCdp.ToString() : "关闭")}");
    }

    private void OnHostExited(object? sender, EventArgs e)
    {
        var p = _process;
        var code = p != null && p.HasExited ? p.ExitCode : -1;

        if (_disposed)
            return;

        var now = DateTime.UtcNow;
        while (_restartStamps.Count > 0 && (now - _restartStamps.Peek()).TotalSeconds > 60)
            _restartStamps.Dequeue();

        if (_restartStamps.Count >= 5)
        {
            Log.Fatal($"镜像宿主反复崩溃（exit={code}），停止自动重启");
            return;
        }

        _restartStamps.Enqueue(now);
        Log.Warn($"镜像宿主退出（exit={code}），2s 后重启");

        Task.Delay(2000).ContinueWith(_ =>
        {
            if (!_disposed)
                Spawn();
        });
    }

    // ---------------- 管道 ----------------

    private void PipeLoop()
    {
        while (!_disposed)
        {
            // 缓冲必须显式给大：缺省近乎零缓冲，写读错开即互卡，state 大包也过不去
            const int bufSize = 1024 * 1024;
            var server = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous, bufSize, bufSize);
            _pipe = server;

            try
            {
                server.WaitForConnectionAsync(_cts.Token).GetAwaiter().GetResult();
                Log.Info("镜像宿主已连接管道");
                _writer = new StreamWriter(server) { AutoFlush = true };
                _connected = true;

                var reader = new StreamReader(server);
                string? line;
                while ((line = reader.ReadLine()) != null)
                    HandleHostLine(line);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                if (!_disposed)
                    Log.Warn("镜像宿主管道断开：" + ex.Message);
            }

            _connected = false;
            _writer = null;
            try
            {
                server.Dispose();
            }
            catch
            {
            }

            if (_disposed)
                return;
        }
    }

    private void HandleHostLine(string line)
    {
        try
        {
            var obj = JObject.Parse(line);
            switch (obj["type"]?.ToString())
            {
                case "hello":
                    if (!string.Equals(obj["token"]?.ToString(), _token, StringComparison.Ordinal))
                    {
                        Log.Warn("镜像宿主令牌不匹配，拒绝连接");
                        return;
                    }

                    Log.Info("镜像宿主握手通过");
                    FlushPending();
                    break;
                case "ready":
                    NeedEditorState?.Invoke();
                    PokeSync?.Invoke();
                    break;
                case "need-state":
                    NeedEditorState?.Invoke();
                    break;
                case "poke-sync":
                    PokeSync?.Invoke();
                    break;
                case "pipeline":
                    Log.Info($"镜像宿主管线：{obj["name"]}={obj["running"]}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("宿主消息处理失败：" + ex.Message);
        }
    }

    private bool SendLine(string line)
    {
        lock (_gate)
        {
            if (!_connected || _writer == null)
                return false;

            try
            {
                _writer.WriteLine(line);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private void FlushPending()
    {
        lock (_gate)
        {
            if (!_connected || _writer == null)
                return;

            try
            {
                foreach (var (_, line) in _pending)
                    _writer.WriteLine(line);
                if (_lastStartCmd != null && _pending.All(p => p.Line != _lastStartCmd))
                    _writer.WriteLine(_lastStartCmd);
            }
            catch (Exception ex)
            {
                Log.Warn("重放排队消息失败：" + ex.Message);
            }

            _pending.Clear();
        }
    }

    // 断线只留最新一条，镜像端外推+下一条锚点自会重锚
    public void SendPlay(bool playing, double t)
    {
        var line = JsonSerializer.Serialize(new { type = "play", playing, t });
        lock (_gate)
        {
            if (!_connected || _writer == null)
            {
                _pending.RemoveAll(p => p.Name == null);
                _pending.Add((null, line));
                return;
            }

            try
            {
                _writer.WriteLine(line);
            }
            catch
            {
            }
        }
    }

    // 编辑页 BC 消息原文转发（state/project/sub），宿主转 __jlApplyMsg
    public void SendMsg(IDictionary<string, object> dict)
    {
        try
        {
            SendLine("{\"type\":\"msg\",\"json\":" + JsonConvert.SerializeObject(dict) + "}");
        }
        catch (Exception ex)
        {
            Log.Warn("工程消息转发失败：" + ex.Message);
        }
    }

    // 设置快照随命令走，宿主不读运行期变更的 settings 文件
    public void SendCmd(string name)
    {
        var line = JsonSerializer.Serialize(new { type = "cmd", name, settings = _settings, url = _url });
        lock (_gate)
        {
            if (name is "start" or "restart")
                _lastStartCmd = line;
            else if (name == "stop")
                _lastStartCmd = null;

            if (!_connected || _writer == null)
            {
                _pending.RemoveAll(p => p.Name == name);
                _pending.Add((name, line));
                Log.Info($"宿主未连接，命令 {name} 排队");
                return;
            }

            try
            {
                _writer.WriteLine(line);
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        Instance = null;

        SendLine("{\"type\":\"shutdown\"}");
        _cts.Cancel();

        var p = _process;
        if (p != null && !p.HasExited)
        {
            try
            {
                if (!p.WaitForExit(3000))
                {
                    Log.Warn("镜像宿主 3s 未退出，强杀");
                    p.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("等待镜像宿主退出失败：" + ex.Message);
            }
        }

        try
        {
            _pipe?.Dispose();
        }
        catch
        {
        }

        if (_job != IntPtr.Zero)
        {
            CloseHandle(_job);
            _job = IntPtr.Zero;
        }
    }

    // ---------------- Job ----------------

    private void AssignToJob(Process p)
    {
        try
        {
            // KILL_ON_JOB_CLOSE 只在 Extended 里被接受，Basic 版会参数错误
            _job = CreateJobObject(IntPtr.Zero, null);
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                }
            };
            var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ptr, (uint)size))
                    throw new Win32Exception();
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            if (!AssignProcessToJobObject(_job, p.Handle))
                throw new Win32Exception();
        }
        catch (Exception ex)
        {
            Log.Warn("Job 关联失败（主进程崩溃时宿主可能残留）：" + ex.Message);
        }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpInfo, uint cbInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
