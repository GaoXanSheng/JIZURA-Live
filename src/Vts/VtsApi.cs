using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using Microsoft.Win32;
using JizuraLive.App;
using JizuraLive.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace JizuraLive.Vts;

public enum VtsState
{
    Disconnected,

    NeedAuth,

    AwaitApproval,

    Ready,
}

public sealed record VtsArtMeshGroup(string GroupId, string GroupName, string[] Entries)
{
    public int Count => Entries.Length;
}

public sealed record VtsHotkey(string HotkeyId, string Name, string Type, string Description)
{
    public override string ToString() => Name;
}

public sealed record VtsFraming(double Size, double X, double Y, double Rotation);

public sealed record VtsHullBounds(double MinX, double MaxX, double MinY, double MaxY);

public sealed class VtsApi : IDisposable
{
    private const string Url = "ws://127.0.0.1:8001";
    private const string PluginName = "JIZURA Live PV";
    private const string PluginDev = "JIZURA Live PV";
    private const int RetryMs = 5_000;
    private const int RequestMs = 8_000;
    private const int ApprovalMs = 90_000;

    private const int IdleMs = 8;

    private static readonly string[] SubscribedEvents =
    [
        "ModelLoadedEvent",
        "ModelConfigChangedEvent",
        "ModelOutlineEvent",
        "TrackingStatusChangedEvent",
        "HotkeyTriggeredEvent",
    ];

    private BlockingCollection<Action<Worker>> _commands = new();
    private readonly object _driveGate = new();
    private VtsFraming? _pendingDrive;
    private Thread? _worker;
    private volatile bool _stopping;

    private volatile int _state = (int)VtsState.Disconnected;
    private volatile string _lastError = "";
    private volatile string _version = "";
    private volatile VtsHotkey[] _hotkeys = [];
    private volatile VtsFraming? _framing;
    private VtsHullBounds? _hull;
    private volatile string _modelName = "";
    private volatile string _vtsModelName = "";

    private volatile string[] _expressions = [];

    private volatile string[] _artMeshes = [];
    private volatile string[] _artMeshTags = [];
    private volatile VtsArtMeshGroup[] _artMeshGroups = [];
    private volatile string _artMeshKeys = "";

    public VtsState State => (VtsState)_state;
    public string LastError => _lastError;
    public string ModelName => _modelName;
    public IReadOnlyList<VtsHotkey> Hotkeys => _hotkeys;

    public IReadOnlyList<string> Expressions => _expressions;

    public IReadOnlyList<string> ArtMeshes => _artMeshes;

    public IReadOnlyList<string> ArtMeshTags => _artMeshTags;

    public IReadOnlyList<VtsArtMeshGroup> ArtMeshGroups => _artMeshGroups;

    public string ArtMeshResponseKeys => _artMeshKeys;

    public VtsFraming? Framing => _framing;

    public VtsHullBounds? Hull => Volatile.Read(ref _hull);

    public event Action? Changed;

    public static JObject BuildExpressionPayload(string file, bool active, double fadeSec) =>
        new()
        {
            ["expressionFile"] = file,
            ["active"] = active,
            ["fadeTime"] = Math.Clamp(fadeSec, 0, 2),
        };

    public static JObject BuildTintPayload(string target, int r, int g, int b,
        IReadOnlyList<VtsArtMeshGroup>? groups = null)
    {
        var matcher = new JObject();
        if (string.IsNullOrEmpty(target) || target == "all") matcher["tintAll"] = true;
        else if (target.StartsWith("tag:", StringComparison.Ordinal))
            matcher["tagExact"] = new JArray(target[4..]);
        else if (target.StartsWith("group:", StringComparison.Ordinal))
        {
            var id = target[6..];
            var group = groups?.FirstOrDefault(item => item.GroupId == id);
            if (group is { Entries.Length: > 0 })
                matcher["nameExact"] = new JArray(group.Entries);
            else
                matcher["artMeshGroupIDExact"] = new JArray(id);
        }
        else if (target.StartsWith("name:", StringComparison.Ordinal))
            matcher["nameExact"] = new JArray(target[5..]);
        else matcher["nameExact"] = new JArray(target);

        return new JObject
        {
            ["colorTint"] = new JObject
            {
                ["colorR"] = Math.Clamp(r, 0, 255),
                ["colorG"] = Math.Clamp(g, 0, 255),
                ["colorB"] = Math.Clamp(b, 0, 255),
                ["colorA"] = 255,
                ["mixWithSceneLightingColor"] = 1.0,
            },
            ["artMeshMatcher"] = matcher,
        };
    }

    private void Notify() => Changed?.Invoke();

    public void Start()
    {
        if (_worker != null) return;
        if (_commands.IsAddingCompleted) _commands = new BlockingCollection<Action<Worker>>();
        _stopping = false;
        _worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "VtsApi",
        };
        _worker.Start();
    }

    public void Stop()
    {
        if (_stopping) return;

        var worker = _worker;
        if (worker is { IsAlive: true } && worker != Thread.CurrentThread)
        {
            using var done = new ManualResetEventSlim();
            try { _commands.Add(w => { w.ResetDrive(); w.ResetTints(); done.Set(); }); }
            catch (InvalidOperationException) { }
            done.Wait(1000);
        }

        _stopping = true;
        try { _commands.CompleteAdding(); } catch {           }
        var w = _worker;
        _worker = null;
        if (w is { IsAlive: true } && w != Thread.CurrentThread) w.Join(2000);
        _state = (int)VtsState.Disconnected;
        _framing = null;
    }

    public void Dispose() => Stop();

    public void RequestAuth() => Post(w => w.RequestToken());

    public void Reconnect() => Post(w => w.DropConnection("手动重连"));

    public void SetDriveOffset(double size, double x, double y, double rotation)
    {
        if (_stopping) return;
        lock (_driveGate) _pendingDrive = new VtsFraming(size, x, y, rotation);
    }

    public void SetExpression(string name, bool active, double fadeSec = 0.4)
    {
        if (string.IsNullOrEmpty(name)) return;
        Post(w => w.SetExpression(name, active, fadeSec));
    }

    public void TriggerHotkey(string hotkeyId)
    {
        if (string.IsNullOrEmpty(hotkeyId)) return;
        Post(w => w.TriggerHotkey(hotkeyId));
    }

    public void CalibrateBase() => Post(w => w.CalibrateBase());

    public void ApplyBase(VtsFraming framing) => Post(w => w.ApplyBase(framing));

    public void RefreshArtMeshes() => Post(w => w.FetchArtMeshes());

    public void SetArtMeshTint(string target, int r, int g, int b)
        => Post(w => w.SetArtMeshTint(target, r, g, b));

    public void ClearArtMeshTint(string target = "all") => SetArtMeshTint(target, 255, 255, 255);

    private void Post(Action<Worker> cmd)
    {
        if (_stopping) return;
        try { _commands.Add(cmd); }
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
        private const double DriveTransitionSec = 0.05;

        private readonly VtsApi _api;
        private readonly byte[] _recv = new byte[64 * 1024];
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

        private bool _refreshModel;
        private bool _refreshHotkeys;
        private bool _refreshArtMeshes;
        private bool _faceFound;
        private bool _leftHandFound;
        private bool _rightHandFound;

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

            long drainGap = _noReplyPending ? 500 : 1000;
            if (now - _lastDrainAt >= drainGap)
            {
                _lastDrainAt = now;
                _noReplyPending = false;
                Send("APIStateRequest", new JObject(), 1500);
            }

            if (_refreshModel)
            {
                _refreshModel = false;
                _refreshHotkeys = false;
                _refreshArtMeshes = false;
                _baseCaptured = false;
                _lastDrive = null;
                _drive = new VtsFraming(0, 0, 0, 0);
                lock (_api._driveGate) _api._pendingDrive = null;
                FetchFraming();
                FetchHotkeys();
                FetchExpressions();
                FetchArtMeshes();
            }
            else if (_refreshHotkeys)
            {
                _refreshHotkeys = false;
                FetchHotkeys();
            }

            if (_refreshArtMeshes)
            {
                _refreshArtMeshes = false;
                FetchArtMeshes();
            }

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
                _baseCaptured = false;
                _refreshModel = false;
                _refreshHotkeys = false;
                _drive = new VtsFraming(0, 0, 0, 0);
                var state = Send("APIStateRequest", new JObject());
                _api._version = (string?)state?["vTubeStudioVersion"] ?? "";
                var authed = (bool?)state?["currentSessionAuthenticated"] ?? false;
                Log.Info($"[VTS] 已连接插件 API（VTS {_api._version}，{(authed ? "会话已鉴权" : "需要鉴权")}）");

                if (authed || TryAuthenticate())
                {
                    SetState(VtsState.Ready);
                    FetchFraming();
                    FetchHotkeys();
                    FetchExpressions();
                    FetchArtMeshes();
                    SubscribeEvents();
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
                FetchHotkeys();
                FetchExpressions();
                FetchArtMeshes();
                SubscribeEvents();
            }
            else
            {
                SetError("授权令牌鉴权失败");
                SetState(VtsState.NeedAuth);
            }
        }

        private void FlushMove()
        {
            var f = new VtsFraming(
                _manual.Size + _drive.Size,
                Math.Clamp(_manual.X + _drive.X, -1000, 1000),
                Math.Clamp(_manual.Y + _drive.Y, -1000, 1000),
                Math.Clamp(_manual.Rotation + _drive.Rotation, -360, 360));
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
            if (resp is null) return;
            if ((bool?)resp["modelLoaded"] == false)
            {
                _api._modelName = "";
                _api._framing = null;
                _api.Notify();
                return;
            }

            _api._modelName = (string?)resp["modelName"] ?? "";
            _api._vtsModelName = (string?)resp["vtsModelName"] ?? "";
            if (resp["modelPosition"] is JObject p)
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

        public void CalibrateBase()
        {
            var resp = Send("CurrentModelRequest", new JObject());
            if (resp is null) return;
            if ((bool?)resp["modelLoaded"] == false)
            {
                SetError("VTS 没有加载模型");
                return;
            }
            if (resp["modelPosition"] is not JObject p) return;

            var framing = new VtsFraming(
                (double?)p["size"] ?? 1,
                (double?)p["positionX"] ?? 0,
                (double?)p["positionY"] ?? 0,
                (double?)p["rotation"] ?? 0);
            _manual = framing;
            _api._framing = framing;
            _baseCaptured = true;
            _drive = new VtsFraming(0, 0, 0, 0);
            _lastDrive = null;
            lock (_api._driveGate) _api._pendingDrive = null;
            _api.Notify();
            Log.Info($"[VTS] 已校准基准：X {framing.X:F2} Y {framing.Y:F2}"
                     + $" 缩放 {framing.Size:F2} 旋转 {framing.Rotation:F2}");
        }

        public void ApplyBase(VtsFraming framing)
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;

            _manual = framing;
            _api._framing = framing;
            _baseCaptured = true;
            _drive = new VtsFraming(0, 0, 0, 0);
            _lastDrive = null;
            lock (_api._driveGate) _api._pendingDrive = null;
            Send("MoveModelRequest", new JObject
            {
                ["timeInSeconds"] = 0.15,
                ["valuesAreRelativeToModel"] = false,
                ["positionX"] = framing.X,
                ["positionY"] = framing.Y,
                ["rotation"] = framing.Rotation,
                ["size"] = framing.Size,
            });
            _api.Notify();
            Log.Info($"[VTS] 已应用工程基准：X {framing.X:F3} Y {framing.Y:F3}"
                     + $" 缩放 {framing.Size:F3} 旋转 {framing.Rotation:F1}");
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

        public void FetchArtMeshes()
        {
            var resp = Send("ArtMeshListRequest", new JObject());
            if (resp is null) return;
            var names = StringsOf(resp["artMeshNames"]);
            var tags = StringsOf(resp["artMeshTags"]);
            var groups = new List<VtsArtMeshGroup>();
            if (resp["artMeshGroups"] is JArray groupArray)
                foreach (var group in groupArray)
                {
                    var id = group["groupID"]?.ToString() ?? "";
                    if (id.Length == 0) continue;
                    var name = group["groupName"]?.ToString() ?? id;
                    var entries = (group["artMeshNames"] as JArray)?
                        .Select(e => e?.ToString() ?? "")
                        .Where(s => s.Length > 0)
                        .ToArray() ?? [];
                    groups.Add(new VtsArtMeshGroup(id, name, entries));
                }

            if (groups.Count == 0 && _api._vtsModelName.Length > 0)
                groups = LoadModelGroups(_api._vtsModelName);

            _api._artMeshes = names;
            _api._artMeshTags = tags;
            _api._artMeshGroups = groups.ToArray();
            _api._artMeshKeys = string.Join(",", resp.Properties().Select(p => p.Name));
            Log.Info($"[VTS] ArtMesh：{names.Length} 个，标签 {tags.Length} 个，组 {groups.Count} 个"
                     + $"（响应字段：{_api._artMeshKeys}）");
            _api.Notify();
        }

        private static string? _groupsRoot;

        private static List<VtsArtMeshGroup> LoadModelGroups(string vtsModelName)
        {
            var root = FindGroupsRoot();
            if (root is null)
            {
                Log.Warn("[VTS] 未找到 VTS 模型目录，无法从 .vtube.json 读取图层组");
                return [];
            }

            var file = FindModelFile(root, vtsModelName);
            if (file is null)
            {
                Log.Warn($"[VTS] 未找到模型文件：{vtsModelName}");
                return [];
            }

            try
            {
                var json = JObject.Parse(File.ReadAllText(file));
                if (json.SelectToken("$..ModelArtMeshGroups") is not JArray arr) return [];

                var list = new List<VtsArtMeshGroup>();
                foreach (var group in arr)
                {
                    var id = group["ID"]?.ToString() ?? "";
                    if (id.Length == 0) continue;
                    var name = group["Name"]?.ToString() ?? id;
                    var entries = (group["Entries"] as JArray)?
                        .Select(e => e?.ToString() ?? "")
                        .Where(s => s.Length > 0)
                        .ToArray() ?? [];
                    list.Add(new VtsArtMeshGroup(id, name, entries));
                }

                Log.Info($"[VTS] 从模型文件读取图层组：{list.Count} 个（{file}）");
                return list;
            }
            catch (Exception ex)
            {
                Log.Warn("[VTS] 读取模型图层组失败：" + ex.Message);
                return [];
            }
        }

        private static string? FindGroupsRoot()
        {
            if (_groupsRoot is not null && Directory.Exists(_groupsRoot)) return _groupsRoot;

            var candidates = new List<string>();
            try
            {
                foreach (var process in Process.GetProcesses())
                {
                    try
                    {
                        if (!process.ProcessName.Contains("VTube Studio", StringComparison.OrdinalIgnoreCase))
                            continue;

                        var path = process.MainModule?.FileName;
                        if (path is null) continue;
                        var dir = Path.GetDirectoryName(path);
                        if (dir is null) continue;
                        candidates.Add(Path.Combine(dir, "VTube Studio_Data", "StreamingAssets", "Live2DModels"));
                    }
                    catch
                    {
                    }
                    finally
                    {
                        process.Dispose();
                    }
                }
            }
            catch
            {
            }

            var steam = TryGetSteamPath();
            if (steam is not null)
            {
                candidates.Add(Path.Combine(steam, "steamapps", "common", "VTube Studio",
                    "VTube Studio_Data", "StreamingAssets", "Live2DModels"));
                candidates.AddRange(ReadSteamLibraries(steam).Select(lib =>
                    Path.Combine(lib, "steamapps", "common", "VTube Studio",
                        "VTube Studio_Data", "StreamingAssets", "Live2DModels")));
            }

            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (!drive.IsReady) continue;
                    var root = drive.RootDirectory.FullName;
                    candidates.Add(Path.Combine(root, "SteamLibrary", "steamapps", "common", "VTube Studio",
                        "VTube Studio_Data", "StreamingAssets", "Live2DModels"));
                    candidates.Add(Path.Combine(root, "Program Files (x86)", "Steam", "steamapps", "common",
                        "VTube Studio", "VTube Studio_Data", "StreamingAssets", "Live2DModels"));
                }
            }
            catch
            {
            }

            foreach (var candidate in candidates)
            {
                if (!Directory.Exists(candidate)) continue;
                _groupsRoot = candidate;
                return candidate;
            }

            return null;
        }

        private static string? FindModelFile(string root, string vtsModelName)
        {
            var direct = Path.Combine(root, vtsModelName);
            if (File.Exists(direct)) return direct;

            var folder = Path.GetFileNameWithoutExtension(vtsModelName);
            if (folder.Length > 0)
            {
                var byFolder = Path.Combine(root, folder, Path.GetFileName(vtsModelName));
                if (File.Exists(byFolder)) return byFolder;
            }

            try
            {
                return Directory.EnumerateFiles(root, Path.GetFileName(vtsModelName),
                    SearchOption.AllDirectories).FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        private static string? TryGetSteamPath()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                return key?.GetValue("SteamPath") as string;
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<string> ReadSteamLibraries(string steam)
        {
            var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) yield break;

            foreach (var line in File.ReadLines(vdf))
            {
                int key = line.IndexOf("\"path\"", StringComparison.OrdinalIgnoreCase);
                if (key < 0) continue;
                int first = line.IndexOf('"', key + 6);
                if (first < 0) continue;
                int second = line.IndexOf('"', first + 1);
                if (second < 0) continue;

                var path = line[(first + 1)..second].Replace("\\\\", "\\");
                if (Directory.Exists(path)) yield return path;
            }
        }

        public void ResetDrive()
        {
            _drive = new VtsFraming(0, 0, 0, 0);
            _lastDrive = null;
            lock (_api._driveGate) _api._pendingDrive = null;
            if (_ws == null || _ws.State != WebSocketState.Open) return;

            Send("MoveModelRequest", new JObject
            {
                ["timeInSeconds"] = 0.05,
                ["valuesAreRelativeToModel"] = false,
                ["positionX"] = Math.Clamp(_manual.X, -1000, 1000),
                ["positionY"] = Math.Clamp(_manual.Y, -1000, 1000),
                ["rotation"] = Math.Clamp(_manual.Rotation, -360, 360),
                ["size"] = _manual.Size,
            });
        }

        public void ResetTints()
        {
            if (_ws == null || _ws.State != WebSocketState.Open) return;

            var resp = Send("ColorTintRequest", VtsApi.BuildTintPayload("all", 255, 255, 255));
            if (resp is not null) Log.Info("[VTS] 已恢复默认染色（退出前）");
        }

        public void SetArtMeshTint(string target, int r, int g, int b)
        {
            var resp = Send("ColorTintRequest",
                VtsApi.BuildTintPayload(target, r, g, b, _api._artMeshGroups));
            if (resp is null) return;
            int matched = (int?)resp["matchedArtMeshes"] ?? 0;
            if (matched == 0) Log.Warn($"[VTS] 染色没有匹配到 ArtMesh：{target}");
        }

        private static string[] StringsOf(JToken? token) =>
            token is JArray arr
                ? arr.Select(e => (string?)e).Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToArray()
                : [];

        private void SubscribeEvents()
        {
            foreach (var name in SubscribedEvents)
                Send("EventSubscriptionRequest", new JObject { ["eventName"] = name });
            Log.Info($"[VTS] 已订阅事件：{string.Join("、", SubscribedEvents)}");
        }

        private void HandleEvent(string type, JObject? data)
        {
            switch (type)
            {
                case "ModelLoadedEvent":
                    _api._modelName = (bool?)data?["modelLoaded"] == true
                        ? (string?)data?["modelName"] ?? ""
                        : "";
                    _refreshModel = true;
                    _api.Notify();
                    break;

                case "ModelConfigChangedEvent":
                    _refreshHotkeys = true;
                    _refreshArtMeshes = true;
                    break;

                case "ModelOutlineEvent":
                    if (data?["convexHull"] is not JArray hull || hull.Count == 0) return;
                    double minX = double.PositiveInfinity, maxX = double.NegativeInfinity;
                    double minY = double.PositiveInfinity, maxY = double.NegativeInfinity;
                    foreach (var point in hull)
                    {
                        double px = (double?)point["x"] ?? 0;
                        double py = (double?)point["y"] ?? 0;
                        if (!double.IsFinite(px) || !double.IsFinite(py)) continue;
                        minX = Math.Min(minX, px);
                        maxX = Math.Max(maxX, px);
                        minY = Math.Min(minY, py);
                        maxY = Math.Max(maxY, py);
                    }
                    if (maxX > minX && maxY > minY)
                        Volatile.Write(ref _api._hull, new VtsHullBounds(minX, maxX, minY, maxY));
                    break;

                case "TrackingStatusChangedEvent":
                    bool face = (bool?)data?["faceFound"] ?? false;
                    bool left = (bool?)data?["leftHandFound"] ?? false;
                    bool right = (bool?)data?["rightHandFound"] ?? false;
                    if (face == _faceFound && left == _leftHandFound && right == _rightHandFound) return;
                    _faceFound = face;
                    _leftHandFound = left;
                    _rightHandFound = right;
                    Log.Info($"[VTS] 追踪：脸{(face ? "在" : "丢失")}，"
                             + $"左手{(left ? "在" : "无")}，右手{(right ? "在" : "无")}");
                    break;

                case "HotkeyTriggeredEvent":
                    var hotkey = (string?)data?["hotkeyName"] ?? (string?)data?["hotkeyID"] ?? "?";
                    Log.Info($"[VTS] 热键触发：{hotkey}");
                    break;
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

                var buf = _recv;
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
                    var type = (string?)resp["messageType"] ?? "";
                    if (type.EndsWith("Event", StringComparison.Ordinal))
                    {
                        HandleEvent(type, (JObject?)resp["data"]);
                        continue;
                    }

                    if ((string?)resp["requestID"] != id) continue;

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
