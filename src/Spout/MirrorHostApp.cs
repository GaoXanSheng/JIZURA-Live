using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive;

// 镜像宿主进程主体：持有两条 Spout 管线（独立 GPU 进程），经命名管道接受主程序驱动。
// 浏览器必须在本进程 Cef.Initialize 的线程（UI 线程）创建，管线操作一律经 Dispatcher；
// 管道收发在后台线程，别让它的阻塞传染 UI。
public sealed class MirrorHostApp
{
    public static MirrorHostApp? Instance { get; private set; }

    private readonly SpoutPipeline _mainPipeline = new();
    private readonly SpoutPipeline _lyricsPipeline = new();
    private readonly Dispatcher _dispatcher;
    private readonly string _token;
    private readonly object _gate = new();

    // 最近收到的工程消息：镜像页（重）加载后先重放缓存，再向主程序要新 state
    private readonly Dictionary<string, string> _lastMsg = new();

    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private volatile bool _pipeUp;

    public MirrorHostApp(Dispatcher dispatcher, string token)
    {
        _dispatcher = dispatcher;
        _token = token;
        Instance = this;
    }

    public void Run(string url, string pipeName, int parentPid)
    {
        WatchParent(parentPid);
        WatchShutdownEvent();
        // 必须切后台线程：从 UI 线程直接起，await 续体会被 SynchronizationContext
        // 抓回 UI 线程，ReadLine/WriteLine 一阻塞整个宿主就锁死
        _ = Task.Run(() => ConnectLoopAsync(url, pipeName));
        Log.Info("镜像宿主就绪：等待主程序指令");
    }

    // ---------------- 自灭通道：父进程句柄 + shutdown 事件 ----------------

    private const uint PROCESS_SYNCHRONIZE = 0x00100000;
    private const uint INFINITE = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    private void WatchParent(int parentPid)
    {
        if (parentPid <= 0)
            return;

        try
        {
            var handle = OpenProcess(PROCESS_SYNCHRONIZE, inheritHandle: false, parentPid);
            if (handle == IntPtr.Zero)
            {
                Log.Warn($"父进程 {parentPid} 打不开（可能已死），立即退出");
                ExitGracefully();
                return;
            }

            Task.Run(() =>
            {
                WaitForSingleObject(handle, INFINITE);
                CloseHandle(handle);
                Log.Warn("父进程已死亡，镜像宿主自灭");
                ExitGracefully();
            });
        }
        catch (Exception ex)
        {
            Log.Warn("父进程监视失败：" + ex.Message);
        }
    }

    private void WatchShutdownEvent()
    {
        try
        {
            var ev = new EventWaitHandle(false, EventResetMode.ManualReset,
                MirrorHostBridge.ShutdownEventName, out _);
            ThreadPool.RegisterWaitForSingleObject(
                ev,
                (_, _) =>
                {
                    Log.Info("收到清场信号，镜像宿主退出");
                    ExitGracefully();
                },
                null,
                Timeout.Infinite,
                executeOnlyOnce: false);
        }
        catch (Exception ex)
        {
            Log.Warn("shutdown 事件监视失败：" + ex.Message);
        }
    }

    private void ExitGracefully()
    {
        try
        {
            _dispatcher.Invoke(() =>
            {
                StopAllPipelines();
                Application.Current.Shutdown();
            });
        }
        catch
        {
            Environment.Exit(0);
        }
    }

    // ---------------- 管道 ----------------

    private async Task ConnectLoopAsync(string url, string pipeName)
    {
        while (true)
        {
            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                await pipe.ConnectAsync(8000);
            }
            catch (Exception ex)
            {
                Log.Warn($"连不上主程序管道（2s 后重试）：{ex.Message}");
                await Task.Delay(2000);
                continue;
            }

            _pipe = pipe;
            _writer = new StreamWriter(pipe) { AutoFlush = true };
            _pipeUp = true;
            SendLine(JsonConvert.SerializeObject(new { type = "hello", token = _token }));
            SendLine(JsonConvert.SerializeObject(new { type = "ready" }));
            Log.Info("已连接主程序管道");

            try
            {
                var reader = new StreamReader(pipe);
                string? line;
                while ((line = reader.ReadLine()) != null)
                    HandleLine(line, url);
            }
            catch (Exception ex)
            {
                Log.Warn("主程序管道断开：" + ex.Message);
            }

            _pipeUp = false;
            try
            {
                pipe.Dispose();
            }
            catch
            {
            }

            // 父进程死会先自灭；活着的父进程会重拉新宿主，这里等新管道
            await Task.Delay(1000);
        }
    }

    private bool SendLine(string line)
    {
        lock (_gate)
        {
            if (!_pipeUp || _writer == null)
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

    private void HandleLine(string line, string url)
    {
        var obj = JObject.Parse(line);
        switch (obj["type"]?.ToString())
        {
            case "play":
            {
                var playing = obj["playing"]?.Value<bool>() ?? false;
                var t = obj["t"]?.Value<double>() ?? 0;
                _dispatcher.BeginInvoke(() =>
                {
                    _mainPipeline.ApplyPlaybackSync(playing, t);
                    _lyricsPipeline.ApplyPlaybackSync(playing, t);
                });
                break;
            }
            case "msg":
            {
                var json = obj["json"];
                if (json == null)
                    return;

                var inner = json.ToString(Formatting.None);
                var name = json["type"]?.ToString() ?? "";
                if (name is "state" or "project" or "sub")
                    lock (_lastMsg)
                    {
                        _lastMsg[name] = inner;
                    }

                _dispatcher.BeginInvoke(() => InjectToAll(inner));
                break;
            }
            case "cmd":
                _dispatcher.Invoke(() => HandleCmd(obj["name"]?.ToString() ?? "", obj["settings"], obj["url"]?.ToString() ?? url));
                break;
            case "shutdown":
                Log.Info("收到主程序 shutdown，优雅退出");
                ExitGracefully();
                break;
        }
    }

    // ---------------- 管线命令（与 MainWindow 同构）----------------

    private void HandleCmd(string name, JToken? settingsToken, string url)
    {
        Settings settings;
        try
        {
            settings = settingsToken?.ToObject<Settings>() ?? Settings.Load();
        }
        catch (Exception ex)
        {
            Log.Error("cmd 设置快照解析失败，忽略：" + ex.Message);
            return;
        }

        switch (name)
        {
            case "start":
                StartSpout(settings, url);
                break;
            case "stop":
                StopAllPipelines();
                break;
            case "restart":
                _mainPipeline.Stop();
                StartSpout(settings, url);
                break;
            case "apply-lyrics":
                ApplySpoutLyrics(settings, url);
                break;
        }

        SendLine(JsonConvert.SerializeObject(new { type = "pipeline", name = "main", running = _mainPipeline.IsRunning }));
        SendLine(JsonConvert.SerializeObject(new { type = "pipeline", name = "lyrics", running = _lyricsPipeline.IsRunning }));
    }

    private void StartSpout(Settings settings, string url)
    {
        if (_mainPipeline.IsRunning)
        {
            // 只在 ID 变了才需要重建
            if (!string.Equals(_mainPipeline.SenderName, settings.SpoutName, StringComparison.Ordinal))
            {
                _mainPipeline.Stop();
                StartMain(settings, url);
                ApplySpoutLyrics(settings, url);
            }

            return;
        }

        StartMain(settings, url);
        ApplySpoutLyrics(settings, url);
    }

    private void StartMain(Settings settings, string url)
    {
        WirePipeline(_mainPipeline);
        _mainPipeline.Start(url + "render",
            settings.SpoutName,
            settings.RenderFps,
            settings.Width,
            settings.Height,
            settings.SpoutTransparent,
            settings.SpoutLyricsEnabled,
            settings);
    }

    // 字幕通道保持 30fps 上限
    private void ApplySpoutLyrics(Settings settings, string url)
    {
        _lyricsPipeline.Stop();
        if (settings.SpoutLyricsEnabled)
        {
            WirePipeline(_lyricsPipeline);
            _lyricsPipeline.Start(url + "render?layer=lyric",
                settings.SpoutName + "-LYRICS",
                Math.Min(settings.RenderFps, 30),
                settings.Width,
                settings.Height,
                true,
                false,
                settings);
        }
    }

    private void StopAllPipelines()
    {
        _mainPipeline.Stop();
        _lyricsPipeline.Stop();
    }

    private void WirePipeline(SpoutPipeline pipeline)
    {
        pipeline.SyncPoke = () => SendLine(JsonConvert.SerializeObject(new { type = "poke-sync" }));
        pipeline.PageLoaded = () =>
        {
            ReplayCached();
            SendLine(JsonConvert.SerializeObject(new { type = "need-state" }));
        };
    }

    private void ReplayCached()
    {
        lock (_lastMsg)
        {
            foreach (var key in new[] { "state", "project", "sub" })
            {
                if (_lastMsg.TryGetValue(key, out var json))
                    InjectToAll(json);
            }
        }
    }

    private void InjectToAll(string jsonText)
    {
        var script = $"window.__jlApplyMsg && window.__jlApplyMsg({jsonText});";
        _mainPipeline.EvaluateScript(script);
        _lyricsPipeline.EvaluateScript(script);
    }

    // 供 App.OnExit：停管线释放 Spout 发送器
    public void ShutdownAll()
    {
        _dispatcher.Invoke(StopAllPipelines);
    }
}
