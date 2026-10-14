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
/// <summary>
/// 表演驱动的输出口：<see cref="VtsApi"/> 实现它（真发 VTS），测试用替身。
/// 分离出来是为了让 <see cref="VtsPerformanceDriver"/> 的节拍/能量/归位逻辑可单测。
/// </summary>
public interface IVtsDriveTarget
{
    /// <summary>取景偏移（叠在手动基准之上；全 0 = 归位）。</summary>
    void SetDriveOffset(double size, double x, double y, double rotation);

    /// <summary>参数注入（weight 0 = 把注入过的参数放回追踪源）。</summary>
    void InjectParameters(IReadOnlyDictionary<string, double> values, double weight);

    /// <summary>触发热键（按拍触发模型动作/表情用）。</summary>
    void TriggerHotkey(string hotkeyId);
}

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
public sealed class VtsApi : IDisposable, IVtsDriveTarget
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
    private volatile Dictionary<string, (double Min, double Max)> _params = new(StringComparer.Ordinal);

    public VtsState State => (VtsState)_state;
    public string LastError => _lastError;
    public string Version => _version;
    public string ModelName => _modelName;
    public IReadOnlyList<VtsHotkey> Hotkeys => _hotkeys;

    /// <summary>VTS 里模型当前的取景（连上后回读；null = 还没读到）。</summary>
    /// <summary>手动基准取景（不掺表演偏移；面板回显它）。</summary>
    public VtsFraming? Framing => _framing;

    /// <summary>
    /// 模型可注入的参数及其取值范围（InputParameterListRequest 拉取；空 = 未知，注入时不筛不缩放）。
    /// 取值范围必须留着：VTS 参数各按自己的量纲给值（MouthOpen 是 0~1，FaceAngleZ 是 -90~90 度），
    /// 驱动侧只算 0~1 的归一化量，注入前按这张表映射。
    /// </summary>
    public IReadOnlyDictionary<string, (double Min, double Max)> ParameterRanges => _params;

    public event Action? Changed;
    /// <summary>
    /// 注入负载（纯函数，形状与量纲映射可单测）：
    /// 归一化 0~1 → 参数自身范围（MouthOpen 0~1、FaceAngleZ -90~90 度…）；
    /// 表里没有的参数直接跳过（别发过去换一条 APIError 回来刷日志）；
    /// weight≈0 = 收手：把 <paramref name="injected"/> 里记过的参数按 weight 0 放回追踪源。
    /// 返回 null = 无事可做。
    /// </summary>
    public static JObject? BuildInjectPayload(
        IReadOnlyDictionary<string, double> values, double weight,
        IReadOnlyDictionary<string, (double Min, double Max)> ranges,
        HashSet<string> injected)
    {
        var arr = new JArray();
        if (weight > 0.0005)
        {
            foreach (var kv in values)
            {
                double lo = 0, hi = 1;
                if (ranges.Count > 0)
                {
                    if (!ranges.TryGetValue(kv.Key, out var r)) continue;
                    (lo, hi) = (r.Min, r.Max);
                }

                arr.Add(new JObject
                {
                    ["id"] = kv.Key,
                    ["value"] = lo + (hi - lo) * Math.Clamp(kv.Value, 0, 1),
                    ["weight"] = Math.Clamp(weight, 0, 1),
                });
                injected.Add(kv.Key);
            }
        }
        else
        {
            foreach (var name in injected)
                arr.Add(new JObject { ["id"] = name, ["value"] = 0, ["weight"] = 0 });
            injected.Clear();
        }

        if (arr.Count == 0) return null;
        return new JObject
        {
            ["faceFound"] = false,
            ["mode"] = "set",
            ["parameterValues"] = arr,
        };
    }


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
    /// 这是**基准取景**——表演驱动（<see cref="SetDriveOffset"/>）的偏移叠在它之上，互不覆盖。
    /// </summary>
    public void MoveModel(double size, double x, double y, double rotation, double seconds)
    {
        var target = new VtsFraming(size, x, y, rotation);
        Post(w => w.MoveModel(target, seconds));
    }

    /// <summary>
    /// 表演驱动的取景偏移（节拍律动/摇摆/跟随镜头）：叠在手动基准之上。
    /// 驱动线程每拍调用，发送端做合并（不会把 VTS 淹掉）。全 0 = 回到手动基准。
    /// </summary>
    public void SetDriveOffset(double size, double x, double y, double rotation)
        => PostLossy(w => w.SetDriveOffset(new VtsFraming(size, x, y, rotation)));

    /// <summary>
    /// 注入模型参数（口型/表情/身体角度）：weight 0~1 与 VTS 自身的追踪值混合，0 = 完全放开。
    /// <paramref name="values"/> 的键是 VTS 参数名（MouthOpen / FaceAngleZ …），值是 **0~1 归一化量**——
    /// 发送端按该参数自己的取值范围（0~1 / -90~90 度 …）映射后再发，调用方不用关心量纲。
    /// weight = 0 时只用于「收手」：把之前注入过的参数放回追踪源。
    /// </summary>
    public void InjectParameters(IReadOnlyDictionary<string, double> values, double weight)
        => PostLossy(w => w.InjectParameters(values, weight));

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

    /// <summary>
    /// 可丢命令（表演驱动每拍给的值）：队列积压时直接丢掉这一拍——下一拍带的是更新的值，
    /// 没人需要一份过期的模型姿态。这样采样线程永远不会把命令队列堆爆。
    /// </summary>
    private void PostLossy(Action<Worker> cmd)
    {
        if (_stopping) return;
        try
        {
            if (_commands.Count > 6) return;
            _commands.Add(cmd);
        }
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
                // 连接维护与合并补发**每轮都做**：表演驱动会持续投递命令（每拍都有），
                // 只在「队列空」时才 Idle 的话，连接永远建不起来（有命令就一直不进这条分支）。
                w.Idle();
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
        /// <summary>取景合并窗口：驱动线程 30Hz 采样，实际最多这么多毫秒发一次（VTS 不会被淹）。</summary>
        private const int MoveIntervalMs = 60;

        /// <summary>参数注入合并窗口（口型/表情不需要比这更密）。</summary>
        private const int ParamIntervalMs = 100;

        /// <summary>驱动拍的过渡时长（秒）：略大于发送间隔，VTS 侧就是连续插值。</summary>
        private const double DriveTransitionSec = 0.09;

        private readonly VtsApi _api;
        private ClientWebSocket? _ws;
        private long _nextConnectAt;
        private string _token = "";
        private int _seq;
        // 取景分两层：手动基准（面板/JIZURA 设定） + 表演偏移（驱动线程每拍给）。
        // 合成值才是发出去的；面板回显只读基准，免得滑块跟着律动抖。
        private string _connectFail = "";   // 上次连接失败原因（同因只记一条，不每 5s 刷）
        private VtsFraming _manual = new(0, 0, 0, 1);
        private VtsFraming _drive = new(0, 0, 0, 0);
        private bool _moveDirty;
        private bool _driveMove;            // 本次待发是驱动拍（用更短的过渡时长，动作才跟手）
        private double _pendingMoveSec = 0.12; // 手动基准的过渡时长
        private long _lastMoveAt;

        // 参数注入（口型/表情/身体角度）：同拍合并 + 限速
        private IReadOnlyDictionary<string, double> _pendingParams = new Dictionary<string, double>();
        private double _pendingParamWeight;
        private bool _paramDirty;
        private long _lastParamAt;
        private readonly HashSet<string> _injected = new(StringComparer.Ordinal); // 注入过的参数名（收手时放回）

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
            // 合并窗口里剩下的那一拍要补发（拖滑块松手的位置、驱动停手时的最后一格）
            var now = Environment.TickCount64;
            if (_moveDirty && now - _lastMoveAt >= MoveIntervalMs) FlushMove();
            if (_paramDirty && now - _lastParamAt >= ParamIntervalMs) FlushParams();

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
                _connectFail = "";
                var state = Send("APIStateRequest", new JObject());
                _api._version = (string?)state?["vTubeStudioVersion"] ?? "";
                var authed = (bool?)state?["currentSessionAuthenticated"] ?? false;
                Log.Info($"[VTS] 已连接插件 API（VTS {_api._version}，{(authed ? "会话已鉴权" : "需要鉴权")}）");

                if (authed || TryAuthenticate())
                {
                    SetState(VtsState.Ready);
                    FetchFraming();
                    FetchParameters();
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
                else if (_connectFail != ex.Message)
                    Log.Warn("[VTS] 连接失败（5s 后重试）：" + ex.Message); // 首次/换原因记一条
                _connectFail = ex.Message;
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
                FetchParameters();
                FetchHotkeys();
            }
            else
            {
                SetError("授权令牌鉴权失败");
                SetState(VtsState.NeedAuth);
            }
        }

        // ---- 业务请求 ----

        /// <summary>手动基准取景（面板滑块 / 一键全身）：改的是基准，驱动偏移不受影响。</summary>
        public void MoveModel(VtsFraming f, double seconds)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;
            // 合并：上一拍还没发出去的移动直接丢掉（拖动滑块 60Hz 发过去只会把 VTS 淹掉）
            _manual = f;
            _api._framing = f;          // 面板回显基准（不掺驱动偏移）
            _driveMove = false;
            _moveDirty = true;
            _pendingMoveSec = seconds;
            if (Environment.TickCount64 - _lastMoveAt >= MoveIntervalMs) FlushMove();
        }

        /// <summary>表演偏移（驱动线程每拍给）：只改偏移那一层。</summary>
        public void SetDriveOffset(VtsFraming off)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;
            _drive = off;
            _driveMove = true;
            _moveDirty = true;
            if (Environment.TickCount64 - _lastMoveAt >= MoveIntervalMs) FlushMove();
        }

        private void FlushMove()
        {
            if (!_moveDirty) return;
            _moveDirty = false;
            _lastMoveAt = Environment.TickCount64;
            var f = new VtsFraming(
                _manual.Size + _drive.Size,
                _manual.X + _drive.X,
                _manual.Y + _drive.Y,
                _manual.Rotation + _drive.Rotation);
            // 驱动拍用固定短过渡（配合 ~20Hz 节拍是连续插值，看着才是"动"而不是"跳"）
            var seconds = _driveMove ? DriveTransitionSec : _pendingMoveSec;
            Send("MoveModelRequest", new JObject
            {
                ["timeInSeconds"] = seconds,
                ["valuesAreRelativeToModel"] = false,
                ["positionX"] = f.X,
                ["positionY"] = f.Y,
                ["rotation"] = f.Rotation,
                ["size"] = f.Size,
            });
        }

        public void FetchFraming()
        {
            var resp = Send("CurrentModelRequest", new JObject());
            _api._modelName = (string?)resp?["modelName"] ?? "";
            if (resp?["modelPosition"] is JObject p)
            {
                var f = new VtsFraming(
                    (double?)p["size"] ?? 1,
                    (double?)p["positionX"] ?? 0,
                    (double?)p["positionY"] ?? 0,
                    (double?)p["rotation"] ?? 0);
                _manual = f;        // 从 VTS 读回来的就是「当前基准」
                _api._framing = f;
                _api.Notify();
            }
        }

        /// <summary>拉取模型可用参数名（注入前筛一遍，避免给不存在的参数发请求刷屏报错）。</summary>
        public void FetchParameters()
        {
            var resp = Send("InputParameterListRequest", new JObject());
            var map = new Dictionary<string, (double Min, double Max)>(StringComparer.Ordinal);
            foreach (var key in new[] { "defaultParameters", "customParameters" })
                if (resp?[key] is JArray arr)
                    foreach (var d in arr)
                    {
                        var n = (string?)d["name"];
                        if (string.IsNullOrEmpty(n)) continue;
                        map[n!] = ((double?)d["min"] ?? 0, (double?)d["max"] ?? 1);
                    }
            _api._params = map;
            Log.Info($"[VTS] 可注入参数：{map.Count} 个");
            _api.Notify();
        }

        /// <summary>注入参数值（驱动线程每拍给）：合并 + 限速；weight=0 表示收手放回追踪源。</summary>
        public void InjectParameters(IReadOnlyDictionary<string, double> values, double weight)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;
            _pendingParams = values;
            _pendingParamWeight = weight;
            _paramDirty = true;
            if (Environment.TickCount64 - _lastParamAt >= ParamIntervalMs) FlushParams();
        }

        private void FlushParams()
        {
            if (!_paramDirty) return;
            _paramDirty = false;
            _lastParamAt = Environment.TickCount64;

            var payload = BuildInjectPayload(_pendingParams, _pendingParamWeight, _api._params, _injected);
            if (payload == null) return;
            Send("InjectParameterDataRequest", payload);
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
