// VTube Studio 插件 API 客户端（VTS Public API：ws://127.0.0.1:8001）。
//
// 为什么需要它：VTS 的 Spout 输出就是它在窗口里**当前取景**的画面——模型在 VTS 里被放大/偏到一边时，
// 「渲染不全面」（半身、被窗口裁掉）在我们这边怎么调都救不回来（源本身就没有全身像素）。
// 插件 API 的 MoveModelRequest 能直接改模型在场景里的 大小/位置/旋转，于是「完整取景」由我们说了算；
// 热键 API（HotkeysInCurrentModel/ HotkeyTrigger）则用来按歌单触发表情/动作。
//
// 协议（字段名从 VTS 程序集元数据核对过，不是猜的）：
//   请求 {"apiName":"VTubeStudioPublicAPI","apiVersion":"1.0","requestID":"...","messageType":"...","data":{...}}
//   鉴权  AuthenticationTokenRequest{pluginName,pluginDeveloper} → 用户在 VTS 里点「允许」→
//         AuthenticationTokenResponse{authenticationToken}（存盘复用）→ AuthenticationRequest{...,authenticationToken}
//   APIStateResponse{active,vTubeStudioVersion,currentSessionAuthenticated}
//   MoveModelRequest{timeInSeconds,valuesAreRelativeToModel,positionX,positionY,rotation,size} → MoveModelResponse{}
//   CurrentModelResponse{...,modelPosition{...}}（回读当前取景）
//   HotkeysInCurrentModelResponse{availableHotkeys[{name,type,hotkeyID,...}]}
//   HotkeyTriggerRequest{hotkeyID,itemInstanceID}
//
// 线程模型：一条工作线程独占 WebSocket（连接、鉴权、请求-响应都在上面），UI/定时器只投命令（投完即返回）。
// 状态与数据放在 volatile 字段里给 UI 读，变化时抛 Changed（宿主自行切回 UI 线程）。
// 连不上（VTS 没开）不是错误：退避重连，状态显示「未连接」。

using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using JizuraLive.App;
using JizuraLive.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Vts;

/// <summary>VTS 连接/鉴权状态。</summary>
public enum VtsState
{
    /// <summary>VTS 没开 / 连不上（会自己退避重连）。</summary>
    Disconnected,

    /// <summary>连上了但还没鉴权：需要用户在 VTS 里点一次「允许」。</summary>
    NeedAuth,

    /// <summary>等用户点允许（弹窗已经弹出去了）。</summary>
    AwaitApproval,

    /// <summary>已鉴权，可以下发命令。</summary>
    Ready,
}

/// <summary>一个 VTS 热键（AvailableHotkey 的投影）。</summary>
public sealed record VtsHotkey(string HotkeyId, string Name, string Type, string Description)
{
    public override string ToString() => Name;
}

/// <summary>模型当前取景（MoveModel 的参数语义，也是 CurrentModel 回读的结果）。</summary>
public sealed record VtsFraming(double Size, double X, double Y, double Rotation);

/// <summary>VTube Studio 插件 API 客户端。线程安全：外部随便调，命令排队在工作线程上执行。</summary>
public sealed class VtsApi : IDisposable
{
    private const string Url = "ws://127.0.0.1:8001";
    private const string PluginName = "JIZURA Live PV";
    private const string PluginDev = "JIZURA Live PV";
    private const int RetryMs = 5_000;      // 连不上时的重连间隔（VTS 没开是常态，别刷）
    private const int RequestMs = 8_000;    // 普通请求超时
    private const int ApprovalMs = 90_000;  // 等用户在 VTS 里点允许的超时

    private readonly BlockingCollection<Action<Worker>> _commands = new();
    private Thread? _worker;
    private volatile bool _stopping;

    // ---- UI 读的状态（volatile 原始类型/引用；Changed 通知重读） ----
    private volatile int _state = (int)VtsState.Disconnected;
    private volatile string _lastError = "";
    private volatile string _version = "";
    private volatile VtsHotkey[] _hotkeys = [];
    private volatile VtsFraming? _framing;
    private volatile string _modelName = "";

    public VtsState State => (VtsState)_state;
    public string LastError => _lastError;
    public string Version => _version;
    public string ModelName => _modelName;
    public IReadOnlyList<VtsHotkey> Hotkeys => _hotkeys;

    /// <summary>VTS 里模型当前的取景（连上后回读；null = 还没读到）。</summary>
    public VtsFraming? Framing => _framing;

    public event Action? Changed;

    private void Notify() => Changed?.Invoke();

    public void Start()
    {
        if (_worker != null) return;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "VtsApi",
        };
        _worker.Start();
    }

    public void Stop()
    {
        _stopping = true;
        try { _commands.CompleteAdding(); } catch { /* 已关闭 */ }
        var w = _worker;
        _worker = null;
        if (w is { IsAlive: true } && w != Thread.CurrentThread) w.Join(2000);
    }

    public void Dispose() => Stop();

    // ---------------- 对外命令（投递即返回） ----------------

    /// <summary>触发 VTS 的插件授权弹窗；用户点「允许」后自动存 token 并鉴权。</summary>
    public void RequestAuth() => Post(w => w.RequestToken());

    /// <summary>重新连一次（面板上的「重连」）。</summary>
    public void Reconnect() => Post(w => w.DropConnection("手动重连"));

    /// <summary>刷新热键列表（连上后自动刷一次；换模型后可手动刷）。</summary>
    public void RefreshHotkeys() => Post(w => w.FetchHotkeys());

    /// <summary>
    /// 设置模型取景（大小/位置/旋转）。<paramref name="seconds"/> = VTS 侧的过渡时长（0 = 立即到）。
    /// 面板拖动时会高频调用：客户端做「最新值胜出」合并，不会把 VTS 淹掉。
    /// </summary>
    public void MoveModel(double size, double x, double y, double rotation, double seconds)
    {
        var target = new VtsFraming(size, x, y, rotation);
        Post(w => w.MoveModel(target, seconds));
    }

    public void TriggerHotkey(string hotkeyId)
    {
        if (string.IsNullOrEmpty(hotkeyId)) return;
        Post(w => w.TriggerHotkey(hotkeyId));
    }

    private void Post(Action<Worker> cmd)
    {
        if (_stopping) return;
        try { _commands.Add(cmd); }
        catch (InvalidOperationException) { /* 已 CompleteAdding：退出中 */ }
    }

    // ---------------- 工作线程 ----------------

    private void WorkerLoop()
    {
        var w = new Worker(this);
        while (!_stopping)
        {
            try
            {
                // 带超时地取命令，好让「该重连了」这类周期性动作有机会跑
                if (_commands.TryTake(out var cmd, 500)) cmd(w);
                else w.Idle();
            }
            catch (Exception ex)
            {
                Log.Warn("[VTS] 命令执行异常：" + ex.Message);
                w.SetError(ex.Message);
            }
        }

        w.Close();
    }

    /// <summary>工作线程内部：连接 + 请求响应（只能在这条线程上碰）。</summary>
    private sealed class Worker
    {
        private readonly VtsApi _api;
        private ClientWebSocket? _ws;
        private long _nextConnectAt;
        private string _token = "";
        private int _seq;
        private VtsFraming? _pendingMove;   // 拖滑块时的高频命令：只发最后一拍
        private double _pendingMoveSec = 0.12;
        private long _lastMoveAt;

        public Worker(VtsApi api)
        {
            _api = api;
            _token = LoadToken();
        }

        // ---- 状态 ----

        private void SetState(VtsState st)
        {
            if ((VtsState)_api._state == st) return;
            _api._state = (int)st;
            _api.Notify();
        }

        public void SetError(string msg)
        {
            _api._lastError = msg;
            _api.Notify();
        }

        // ---- 连接维护 ----

        public void Idle()
        {
            // 拖动停止后的最后一拍：合并窗口里剩下的那次移动要补发（否则松手位置差一格）
            if (_pendingMove != null && Environment.TickCount64 - _lastMoveAt >= 120) FlushMove(_pendingMoveSec);

            if (_ws != null && _ws.State == WebSocketState.Open) return;
            if (Environment.TickCount64 < _nextConnectAt) return;
            Connect();
        }

        public void DropConnection(string why)
        {
            Log.Info($"[VTS] {why}");
            try { _ws?.Dispose(); } catch { /* 已在关 */ }
            _ws = null;
            _nextConnectAt = 0;
            SetState(VtsState.Disconnected);
        }

        public void Close()
        {
            try { _ws?.Dispose(); } catch { /* 进程退出路径 */ }
            _ws = null;
        }

        private void Connect()
        {
            try
            {
                var ws = new ClientWebSocket();
                using var cts = new CancellationTokenSource(RequestMs);
                ws.ConnectAsync(new Uri(Url), cts.Token).GetAwaiter().GetResult();
                _ws = ws;
                _api._lastError = "";
                var state = Send("APIStateRequest", new JObject());
                _api._version = (string?)state?["vTubeStudioVersion"] ?? "";
                var authed = (bool?)state?["currentSessionAuthenticated"] ?? false;
                Log.Info($"[VTS] 已连接插件 API（VTS {_api._version}，{(authed ? "会话已鉴权" : "需要鉴权")}）");

                if (authed || TryAuthenticate())
                {
                    SetState(VtsState.Ready);
                    FetchFraming();
                    FetchHotkeys();
                }
                else
                {
                    SetState(VtsState.NeedAuth);
                }
            }
            catch (Exception ex)
            {
                _ws = null;
                _nextConnectAt = Environment.TickCount64 + RetryMs;
                if ((VtsState)_api._state != VtsState.Disconnected)
                    Log.Info("[VTS] 连接断开：" + ex.Message);
                SetState(VtsState.Disconnected);
            }
        }

        /// <summary>用存下的 token 鉴权；没有 token / 失败返回 false（面板上会亮「请求授权」）。</summary>
        private bool TryAuthenticate()
        {
            if (_token.Length == 0) return false;
            var resp = Send("AuthenticationRequest", new JObject
            {
                ["pluginName"] = PluginName,
                ["pluginDeveloper"] = PluginDev,
                ["authenticationToken"] = _token,
            });
            if ((bool?)resp?["authenticated"] == true) return true;
            Log.Warn("[VTS] token 鉴权失败（可能在 VTS 里被撤销）：" + ((string?)resp?["reason"] ?? ""));
            _token = "";
            SaveToken("");
            return false;
        }

        /// <summary>请求授权：VTS 弹窗 → 用户点允许 → 拿 token 存盘 + 鉴权。</summary>
        public void RequestToken()
        {
            if (_ws == null || _ws.State != WebSocketState.Open)
            {
                SetError("VTS 未连接，先把 VTube Studio 打开");
                return;
            }

            SetState(VtsState.AwaitApproval);
            Log.Info("[VTS] 已请求插件授权：请在 VTube Studio 里点「允许」");
            var resp = Send("AuthenticationTokenRequest", new JObject
            {
                ["pluginName"] = PluginName,
                ["pluginDeveloper"] = PluginDev,
                ["pluginIcon"] = "",
            }, ApprovalMs);

            var token = (string?)resp?["authenticationToken"];
            if (string.IsNullOrEmpty(token))
            {
                SetError("VTS 没有返回授权令牌（弹窗被拒绝？）");
                SetState(VtsState.NeedAuth);
                return;
            }

            _token = token;
            SaveToken(token);
            Log.Info("[VTS] 授权成功，令牌已保存");
            if (TryAuthenticate())
            {
                SetState(VtsState.Ready);
                FetchFraming();
                FetchHotkeys();
            }
            else
            {
                SetError("授权令牌鉴权失败");
                SetState(VtsState.NeedAuth);
            }
        }

        // ---- 业务请求 ----

        public void MoveModel(VtsFraming f, double seconds)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;
            // 合并：上一拍还没发出去的移动直接丢掉（拖动滑块 60Hz 发过去只会把 VTS 淹掉）
            _pendingMove = f;
            _pendingMoveSec = seconds;
            var now = Environment.TickCount64;
            if (now - _lastMoveAt < 80) return;
            FlushMove(seconds);
        }

        private void FlushMove(double seconds)
        {
            if (_pendingMove is not { } f) return;
            _pendingMove = null;
            _lastMoveAt = Environment.TickCount64;
            Send("MoveModelRequest", new JObject
            {
                ["timeInSeconds"] = seconds,
                ["valuesAreRelativeToModel"] = false,
                ["positionX"] = f.X,
                ["positionY"] = f.Y,
                ["rotation"] = f.Rotation,
                ["size"] = f.Size,
            });
            _api._framing = f;
            _api.Notify();
        }

        public void FetchFraming()
        {
            var resp = Send("CurrentModelRequest", new JObject());
            _api._modelName = (string?)resp?["modelName"] ?? "";
            if (resp?["modelPosition"] is JObject p)
            {
                _api._framing = new VtsFraming(
                    (double?)p["size"] ?? 1,
                    (double?)p["positionX"] ?? 0,
                    (double?)p["positionY"] ?? 0,
                    (double?)p["rotation"] ?? 0);
                _api.Notify();
            }
        }

        public void FetchHotkeys()
        {
            var resp = Send("HotkeysInCurrentModelRequest", new JObject());
            if (resp?["availableHotkeys"] is not JArray arr)
            {
                _api._hotkeys = [];
                _api.Notify();
                return;
            }

            var list = new List<VtsHotkey>();
            foreach (var h in arr)
            {
                var id = (string?)h["hotkeyID"] ?? "";
                if (id.Length == 0) continue;
                list.Add(new VtsHotkey(
                    id,
                    (string?)h["name"] ?? id,
                    (string?)h["type"] ?? "",
                    (string?)h["description"] ?? ""));
            }

            _api._hotkeys = list.ToArray();
            _api.Notify();
            Log.Info($"[VTS] 热键列表：{list.Count} 个");
        }

        public void TriggerHotkey(string id)
        {
            var resp = Send("HotkeyTriggerRequest", new JObject { ["hotkeyID"] = id });
            if (resp == null) Log.Warn($"[VTS] 热键触发失败：{_api._lastError}");
        }

        // ---- 收发 ----

        /// <summary>发一条请求并等它自己的响应（同一线程串行；事件订阅不用，故响应一一对应）。</summary>
        private JObject? Send(string messageType, JObject data, int timeoutMs = RequestMs)
        {
            var ws = _ws;
            if (ws == null || ws.State != WebSocketState.Open) return null;

            var id = "jizura-" + Interlocked.Increment(ref _seq).ToString();
            var req = new JObject
            {
                ["apiName"] = "VTubeStudioPublicAPI",
                ["apiVersion"] = "1.0",
                ["requestID"] = id,
                ["messageType"] = messageType,
                ["data"] = data,
            };

            try
            {
                using var cts = new CancellationTokenSource(timeoutMs);
                var bytes = Encoding.UTF8.GetBytes(req.ToString(Formatting.None));
                ws.SendAsync(bytes, WebSocketMessageType.Text, true, cts.Token).GetAwaiter().GetResult();

                var buf = new byte[256 * 1024];
                var sb = new StringBuilder();
                while (true)
                {
                    var r = ws.ReceiveAsync(buf, cts.Token).GetAwaiter().GetResult();
                    if (r.MessageType == WebSocketMessageType.Close)
                    {
                        DropConnection("VTS 关闭了连接");
                        return null;
                    }

                    sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count));
                    if (r.EndOfMessage) break;
                }

                var resp = JObject.Parse(sb.ToString());
                var type = (string?)resp["messageType"] ?? "";
                if (type == "APIError")
                {
                    var msg = (string?)resp["data"]?["message"] ?? "未知错误";
                    SetError($"{messageType}：{msg}");
                    Log.Warn($"[VTS] {messageType} 出错：{msg}");
                    return null;
                }

                return (JObject?)resp["data"] ?? new JObject();
            }
            catch (OperationCanceledException)
            {
                SetError($"{messageType} 超时");
                return null;
            }
            catch (Exception ex)
            {
                SetError($"{messageType} 失败：{ex.Message}");
                DropConnection("收发出错");
                return null;
            }
        }

        // ---- token 落盘 ----

        private static string TokenPath => Path.Combine(AppDirs.ConfigDir, "vts_token.json");

        private static string LoadToken()
        {
            try
            {
                if (!File.Exists(TokenPath)) return "";
                var obj = JObject.Parse(File.ReadAllText(TokenPath));
                return (string?)obj["token"] ?? "";
            }
            catch (Exception ex)
            {
                Log.Warn("[VTS] 读取授权令牌失败：" + ex.Message);
                return "";
            }
        }

        private static void SaveToken(string token)
        {
            try
            {
                Directory.CreateDirectory(AppDirs.ConfigDir);
                File.WriteAllText(TokenPath, new JObject
                {
                    ["pluginName"] = PluginName,
                    ["token"] = token,
                }.ToString(Formatting.Indented));
            }
            catch (Exception ex)
            {
                Log.Warn("[VTS] 保存授权令牌失败：" + ex.Message);
            }
        }
    }
}
