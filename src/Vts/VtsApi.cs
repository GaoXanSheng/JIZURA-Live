using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using JizuraLive.App;
using JizuraLive.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Vts;

public interface IVtsDriveTarget
{
    void SetDriveOffset(double size, double x, double y, double rotation);

    void InjectParameters(IReadOnlyDictionary<string, double> values, double weight);

    void TriggerHotkey(string hotkeyId);
}

public enum VtsState
{
    Disconnected,

    NeedAuth,

    AwaitApproval,

    Ready,
}

public sealed record VtsHotkey(string HotkeyId, string Name, string Type, string Description)
{
    public override string ToString() => Name;
}

public sealed record VtsFraming(double Size, double X, double Y, double Rotation);

public sealed class VtsApi : IDisposable, IVtsDriveTarget, IVtsCueSink
{
    private const string Url = "ws://127.0.0.1:8001";
    private const string PluginName = "JIZURA Live PV";
    private const string PluginDev = "JIZURA Live PV";
    private const int RetryMs = 5_000;
    private const int RequestMs = 8_000;
    private const int ApprovalMs = 90_000;

    private const int TrackIntervalMs = 66;

    private const int IdleMs = 8;

    private readonly BlockingCollection<Action<Worker>> _commands = new();
    private readonly object _driveGate = new();
    private VtsFraming? _pendingDrive;
    private Thread? _worker;
    private volatile bool _stopping;

    private volatile int _state = (int)VtsState.Disconnected;
    private volatile string _lastError = "";
    private volatile string _version = "";
    private volatile VtsHotkey[] _hotkeys = [];
    private volatile VtsFraming? _framing;
    private volatile string _modelName = "";
    private volatile Dictionary<string, (double Min, double Max)> _params = new(StringComparer.Ordinal);

    private readonly VtsTrackingProbe _tracking = new();

    private volatile string[] _expressions = [];

    private volatile bool _trackingOn = true;

    public VtsState State => (VtsState)_state;
    public string LastError => _lastError;
    public string ModelName => _modelName;
    public IReadOnlyList<VtsHotkey> Hotkeys => _hotkeys;

    public VtsFraming? Framing => _framing;

    public bool TrackingEnabled
    {
        get => _trackingOn;
        set => _trackingOn = value;
    }

    public event Action? Changed;
    public static JObject? BuildInjectPayload(
        IReadOnlyDictionary<string, double> values, double weight,
        IReadOnlyDictionary<string, (double Min, double Max)> ranges,
        HashSet<string> injected, string mode = "set")
    {
        var arr = new JArray();
        if (mode == "add")
        {
            foreach (var kv in values)
            {
                if (ranges.Count > 0 && !ranges.ContainsKey(kv.Key)) continue;
                arr.Add(new JObject { ["id"] = kv.Key, ["value"] = kv.Value, ["weight"] = 1.0 });
            }
        }
        else if (weight > 0.0005)
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
            ["mode"] = mode,
            ["parameterValues"] = arr,
        };
    }

    public static JObject BuildExpressionPayload(string file, bool active, double fadeSec) =>
        new()
        {
            ["expressionFile"] = file,
            ["active"] = active,
            ["fadeTime"] = Math.Clamp(fadeSec, 0, 2),
        };

    public static VtsTrackingState ParseTracking(JObject? d)
    {
        if (d is null) return VtsTrackingState.Empty;
        var at = Environment.TickCount64;

        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var key in ParamListKeys)
        {
            if (d[key] is not JArray arr) continue;
            foreach (var e in arr)
            {
                var name = (string?)e["name"];
                if (string.IsNullOrEmpty(name)) continue;
                if (e["value"] is null) continue;
                values[name!] = (double?)e["value"] ?? 0;
            }
        }

        if (values.Count == 0) return VtsTrackingState.Empty with { AtMs = at };

        double V(string name) => values.TryGetValue(name, out var v) ? v : 0;
        bool Has(string name) => values.ContainsKey(name);

        var recognised = 0;
        foreach (var name in TrackingParamNames)
            if (values.ContainsKey(name)) recognised++;

        static double Angle(double deg) => Math.Clamp(deg / VtsTrackingMap.AngleRangeDeg, -1, 1);

        return new VtsTrackingState(
            FaceFound: recognised > 0,
            HeadX: Angle(V("FaceAngleX")),
            HeadY: Angle(V("FaceAngleY")),
            HeadZ: 0,
            HeadAngleX: V("FaceAngleX"),
            HeadAngleY: V("FaceAngleY"),
            HeadAngleZ: V("FaceAngleZ"),
            BodyAngleX: V("MocopiBodyAngleX"),
            BodyAngleZ: V("MocopiBodyAngleZ"),
            EyeOpenLeft: Math.Clamp(V("EyeOpenLeft"), 0, 1),
            EyeOpenRight: Math.Clamp(V("EyeOpenRight"), 0, 1),
            MouthOpen: Math.Clamp(V("MouthOpen"), 0, 1),
            MouthSmile: Math.Clamp(V("MouthSmile"), 0, 1),
            HandLeftFound: Has("HandLeftFound") && V("HandLeftFound") > 0.5,
            HandRightFound: Has("HandRightFound") && V("HandRightFound") > 0.5,
            HandLeftX: V("HandLeftPositionX"),
            HandLeftY: V("HandLeftPositionY"),
            HandRightX: V("HandRightPositionX"),
            HandRightY: V("HandRightPositionY"),
            AtMs: at);
    }

    internal static readonly string[] ParamListKeys = ["defaultParameters", "customParameters"];

    private static readonly string[] TrackingParamNames =
    [
        "FaceAngleX", "FaceAngleY", "FaceAngleZ",
        "MouthOpen", "MouthSmile",
        "EyeOpenLeft", "EyeOpenRight",
    ];

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
        if (_worker != null && !_stopping)
        {
            using var done = new ManualResetEventSlim();
            try { _commands.Add(w => { w.ResetDrive(); done.Set(); }); }
            catch (InvalidOperationException) { }
            done.Wait(400);
        }

        _stopping = true;
        try { _commands.CompleteAdding(); } catch {           }
        var w = _worker;
        _worker = null;
        if (w is { IsAlive: true } && w != Thread.CurrentThread) w.Join(2000);
    }

    public void Dispose() => Stop();

    public void RequestAuth() => Post(w => w.RequestToken());

    public void Reconnect() => Post(w => w.DropConnection("手动重连"));

    public void SetDriveOffset(double size, double x, double y, double rotation)
    {
        if (_stopping) return;
        lock (_driveGate) _pendingDrive = new VtsFraming(size, x, y, rotation);
        PostLossy(_ => { });
    }

    public void InjectParameters(IReadOnlyDictionary<string, double> values, double weight)
        => PostLossy(w => w.InjectParameters(values, weight));

    public void SetExpression(string name, bool active, double fadeSec = 0.4)
    {
        if (string.IsNullOrEmpty(name)) return;
        Post(w => w.SetExpression(name, active, fadeSec));
    }

    public void MoveModelRelative(double x, double y, double rotation, double size, double seconds)
    {
        var delta = new VtsFraming(size, x, y, rotation);
        Post(w => w.MoveModelRelative(delta, seconds));
    }

    public void InjectAdd(IReadOnlyDictionary<string, double> values)
        => Post(w => w.InjectAdd(values));

    public void TriggerHotkey(string hotkeyId)
    {
        if (string.IsNullOrEmpty(hotkeyId)) return;
        Post(w => w.TriggerHotkey(hotkeyId));
    }

    private void Post(Action<Worker> cmd)
    {
        if (_stopping) return;
        try { _commands.Add(cmd); }
        catch (InvalidOperationException) {                            }
    }

    private void PostLossy(Action<Worker> cmd)
    {
        if (_stopping) return;
        try
        {
            if (_commands.Count > 6) return;
            _commands.Add(cmd);
        }
        catch (InvalidOperationException) {                            }
    }

    private void WorkerLoop()
    {
        var w = new Worker(this);
        while (!_stopping)
        {
            try
            {
                if (_commands.TryTake(out var cmd, IdleMs)) cmd(w);
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

    private sealed class Worker
    {
        private const int ParamIntervalMs = 100;

        private bool _trackOk;
        private long _trackMisses;

        private long _lastTrackAt;

        private const double DriveTransitionSec = 0.05;

        private readonly VtsApi _api;
        private ClientWebSocket? _ws;
        private long _nextConnectAt;
        private string _token = "";
        private int _seq;
        private string _connectFail = "";
        private VtsFraming _manual = new(0, 0, 0, 1);
        private VtsFraming _drive = new(0, 0, 0, 0);
        private bool _baseCaptured;
        private VtsFraming? _lastDrive;
        private bool _noReplyPending;
        private long _lastDrainAt;

        private IReadOnlyDictionary<string, double> _pendingParams = new Dictionary<string, double>();
        private double _pendingParamWeight;
        private bool _paramDirty;
        private long _lastParamAt;
        private readonly HashSet<string> _injected = new(StringComparer.Ordinal);

        public Worker(VtsApi api)
        {
            _api = api;
            _token = LoadToken();
        }

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

        public void Idle()
        {
            var now = Environment.TickCount64;

            VtsFraming? pending;
            lock (_api._driveGate)
            {
                pending = _api._pendingDrive;
                _api._pendingDrive = null;
            }
            if (pending is not null && _baseCaptured && _ws is { State: WebSocketState.Open }
                && !Equals(pending, _lastDrive))
            {
                _drive = pending;
                _lastDrive = pending;
                FlushMove();
            }

            if (_noReplyPending && now - _lastDrainAt >= 500)
            {
                _lastDrainAt = now;
                _noReplyPending = false;
                Send("APIStateRequest", new JObject(), 1500);
            }

            if (_paramDirty && now - _lastParamAt >= ParamIntervalMs) FlushParams();
            PollTracking(force: false);

            if (_ws != null && _ws.State == WebSocketState.Open) return;
            if (Environment.TickCount64 < _nextConnectAt) return;
            Connect();
        }

        public void DropConnection(string why)
        {
            Log.Info($"[VTS] {why}");
            try { _ws?.Dispose(); } catch {           }
            _ws = null;
            _nextConnectAt = 0;
            SetState(VtsState.Disconnected);
        }

        public void Close()
        {
            try { _ws?.Dispose(); } catch {              }
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
                _lastDrive = null;
                _noReplyPending = false;
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
                    FetchExpressions();
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
                    Log.Warn("[VTS] 连接失败（5s 后重试）：" + ex.Message);
                _connectFail = ex.Message;
                SetState(VtsState.Disconnected);
            }
        }

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

        public void ResetDrive()
        {
            if (_ws == null || _ws.State != WebSocketState.Open || !_baseCaptured) return;
            lock (_api._driveGate) _api._pendingDrive = null;
            _drive = new VtsFraming(0, 0, 0, 0);
            _lastDrive = _drive;
            Send("MoveModelRequest", new JObject
            {
                ["timeInSeconds"] = 0.15,
                ["valuesAreRelativeToModel"] = false,
                ["positionX"] = _manual.X,
                ["positionY"] = _manual.Y,
                ["rotation"] = _manual.Rotation,
                ["size"] = _manual.Size,
            });
        }

        private void FlushMove()
        {
            var f = new VtsFraming(
                _manual.Size + _drive.Size,
                _manual.X + _drive.X,
                _manual.Y + _drive.Y,
                _manual.Rotation + _drive.Rotation);
            SendNoReply("MoveModelRequest", new JObject
            {
                ["timeInSeconds"] = DriveTransitionSec,
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
                if (!_baseCaptured)
                {
                    _manual = f;
                    _api._framing = f;
                    _baseCaptured = true;
                }
                _api.Notify();
            }
        }

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

        public void FetchExpressions()
        {
            var resp = Send("ExpressionStateRequest", new JObject());
            if (resp?["expressions"] is not JArray arr)
            {
                _api._expressions = [];
                _api.Notify();
                return;
            }

            var files = arr.Select(e => (string?)e["file"])
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToArray();
            _api._expressions = files;
            _api.Notify();
            Log.Info($"[VTS] 表情：{files.Length} 个");
        }

        public void SetExpression(string file, bool active, double fadeSec)
        {
            var resp = Send("ExpressionActivationRequest", BuildExpressionPayload(file, active, fadeSec));
            if (resp == null) Log.Warn($"[VTS] 表情「{file}」设置失败：{_api._lastError}");
        }

        public void MoveModelRelative(VtsFraming delta, double seconds)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;
            Send("MoveModelRequest", new JObject
            {
                ["timeInSeconds"] = Math.Clamp(seconds, 0, 2),
                ["valuesAreRelativeToModel"] = true,
                ["positionX"] = delta.X,
                ["positionY"] = delta.Y,
                ["rotation"] = delta.Rotation,
                ["size"] = delta.Size,
            });
        }

        public void InjectAdd(IReadOnlyDictionary<string, double> values)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;
            var payload = BuildInjectPayload(values, 1, _api._params, _injected, "add");
            if (payload != null) Send("InjectParameterDataRequest", payload);
        }

        public void PollTracking(bool force)
        {
            if (!_api.TrackingEnabled) return;
            if (_ws == null || _ws.State != WebSocketState.Open) return;
            var now = Environment.TickCount64;
            if (!force && now - _lastTrackAt < TrackIntervalMs) return;
            _lastTrackAt = now;

            var resp = Send("InputParameterListRequest", new JObject(), 1500);
            if (resp is null) return;
            var state = VtsApi.ParseTracking(resp);
            _api._tracking.Set(state);

            if (state.FaceFound)
            {
                if (!_trackOk)
                {
                    _trackOk = true;
                    Log.Info($"[VTS] 追踪已接通：头角 X{state.HeadAngleX:F1} Y{state.HeadAngleY:F1}"
                             + $" Z{state.HeadAngleZ:F1}°，口 {state.MouthOpen:F2}"
                             + $"，眼 {state.EyeOpenLeft:F2}/{state.EyeOpenRight:F2}"
                             + $"，手 {(state.HandLeftFound ? "左" : "")}{(state.HandRightFound ? "右" : "")}"
                             + (state.HandLeftFound || state.HandRightFound ? "" : "无"));
                }
            }
            else if (!_trackOk && ++_trackMisses % 60 == 1)
            {
                var keys = string.Join(",", resp.Properties().Select(q => q.Name));
                var names = new List<string>();
                foreach (var key in VtsApi.ParamListKeys)
                    if (resp[key] is JArray arr)
                        names.AddRange(arr.Take(16).Select(e => (string?)e["name"] ?? "?"));
                Log.Warn($"[VTS] 追踪参数读不到（缺 FaceAngleX/Y）。响应顶层 [{keys}]，"
                         + $"参数名样例 [{string.Join(", ", names)}]");
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

        private void SendNoReply(string messageType, JObject data)
        {
            var ws = _ws;
            if (ws == null || ws.State != WebSocketState.Open) return;

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
                var bytes = Encoding.UTF8.GetBytes(req.ToString(Formatting.None));
                ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None)
                    .GetAwaiter().GetResult();
                _noReplyPending = true;
            }
            catch (Exception ex)
            {
                SetError($"{messageType} 发送失败：{ex.Message}");
                DropConnection("发送出错");
            }
        }

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
                while (true)
                {
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
                    if ((string?)resp["requestID"] != id) continue;

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
