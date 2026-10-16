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

    // 追踪轮询间隔（~15Hz）：够驱动视觉联动，又不至于把 WebSocket 占满。
    // 它同时是命令队列的等待上限：没事干时 Idle 就按这个节拍跑。写成 500ms 的话追踪被拖到 2Hz 一次，
    // 快照年龄超过新鲜度上限（VtsTrackingProbe.StaleMs），镜头会有一半时间在回落基础取景。
    private const int TrackIntervalMs = 66;

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

    /// <summary>实时追踪快照（TrackingDataRequest 轮询写入；渲染侧读它做效果联动）。</summary>
    private readonly VtsTrackingProbe _tracking = new();

    private volatile string[] _expressions = [];
    private volatile HashSet<string> _createdParams = new(StringComparer.Ordinal);

    private volatile bool _trackingOn = true;

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

    /// <summary>实时追踪快照（脸部/手部姿态与 Live2D 参数）。</summary>
    public VtsTrackingProbe Tracking => _tracking;

    /// <summary>模型自带的表情名列表（ExpressionStateRequest 拉取）。</summary>
    public IReadOnlyList<string> Expressions => _expressions;

    /// <summary>已由我们创建的自定义参数名（ParameterCreationRequest 的结果）。</summary>
    public IReadOnlyCollection<string> CreatedParameters => _createdParams;

    /// <summary>是否轮询追踪数据（默认开；关掉可省掉每个节拍一次往返）。</summary>
    public bool TrackingEnabled
    {
        get => _trackingOn;
        set => _trackingOn = value;
    }

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


    /// <summary>
    /// 解析 **InputParameterListRequest** 的响应 → 追踪快照（纯函数，形状可单测）。
    ///
    /// 为什么不直接「问追踪数据」：VTS 插件 API 里**没有那种消息**。之前发的是
    /// <c>TrackingDataRequest</c>，VTS 1.35 直接回 <c>Unknown messageType</c>——
    /// 这条路从建立起就是死的，而且一直没被发现，因为解析函数与它的单测是按**臆想的响应形状**
    /// 一起写的（position/rotation/handLeft…），两边自洽，跟真实 API 毫无关系。
    /// 教训：解析器的单测只证明「解析器和测试里的形状一致」；形状本身必须来自真实响应。
    ///
    /// 真实可用的出口是**模型的输入参数**：VTS 自己的追踪结果就落在这些参数上
    /// （转头 → <c>ParamAngleX/Y/Z</c>，眨眼 → <c>ParamEyeLOpen/ROpen</c>，
    /// 口型 → <c>ParamMouthOpenY</c>）。<c>InputParameterListRequest</c> 是本文件里
    /// 早就在用的消息（拉参数名时用过），返回 defaultParameters / customParameters 两串，
    /// 每项带 name / value。
    ///
    /// **实测拿到的**（VTS 1.35.10）：头角 FaceAngleX/Y/Z、口型 MouthOpen/MouthSmile、
    /// 眼睛 EyeOpenLeft/Right + EyeLeftX/Y、眉毛、手 HandLeft/RightFound + 位置、
    /// 甚至语音口型 VoiceA/I/U/E/O 与穿戴体捕 Mocopi*。
    ///
    /// **还没确认的**（不装作有）：① <c>FacePositionX/Y/Z</c> 的量纲（实测 X0.50/Y-4.02/Z2.14，
    /// 不知道 1.0 代表多少）——所以头部位移仍用角度驱动，不用它；
    /// ② 两次相隔数秒的采样**逐位相同**，怀疑这些 value 是快照而不是实时流——
    /// 这一条被 VTS 侧的鉴权状态挡住没能验证（那一轮连接停在「需要鉴权」，参数列表都没取到）。
    /// 若确认是快照，实时追踪就得换路子（EventSubscriptionRequest 或参数注入回读）。
    /// 代码里的「追踪已接通 / 追踪参数读不到」两条日志就是为验证这件事留的。
    /// </summary>
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

        // 「有追踪」= 至少认出**一个**已知的追踪参数，而不是「响应里有参数」：
        // 换到参数名不标准的模型时，全 0 的姿态会让状态条谎称追踪在跑。
        var recognised = 0;
        foreach (var name in TrackingParamNames)
            if (values.ContainsKey(name)) recognised++;

        // 角度是度数（实测 FaceAngleX≈7.9），标称 ±30 → 归一化到 ±1。
        // 映射侧的 HeadRange 就是按归一化单位写的。
        static double Angle(double deg) => Math.Clamp(deg / VtsTrackingMap.AngleRangeDeg, -1, 1);

        return new VtsTrackingState(
            FaceFound: recognised > 0,
            // 头部位移用「角度」而不是 FacePositionX/Y/Z：后者的量纲没确认
            // （实测值 X0.50 / Y-4.02 / Z2.14，不知道 1.0 代表多少），
            // 硬套 HeadRange 会饱和。转头驱动视差本来就够用，且量纲已知。
            HeadX: Angle(V("FaceAngleX")),
            HeadY: Angle(V("FaceAngleY")),
            HeadZ: 0,
            HeadAngleX: V("FaceAngleX"),
            HeadAngleY: V("FaceAngleY"),
            HeadAngleZ: V("FaceAngleZ"),
            // 列表里没有 BodyAngle*（只有 Mocopi* 的体角，属于穿戴设备）；恒 0
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

    /// <summary>参数列表响应里的两个数组名（default / custom，后者是插件自建的）。</summary>
    internal static readonly string[] ParamListKeys = ["defaultParameters", "customParameters"];

    /// <summary>
    /// 承载追踪结果的输入参数名。**这是实测出来的，不是照 Live2D 惯例猜的**：
    /// VTS 1.35 用的是 <c>FaceAngleX/Y/Z</c> 与 <c>MouthOpen</c>，
    /// 而不是模型里的 <c>ParamAngleX</c> / <c>ParamMouthOpenY</c>。
    /// （从 VTS 1.35.10 的 InputParameterListRequest 响应里读到的完整名单，共 128 项，
    /// 还包含 FacePositionX/Y/Z、EyeOpenLeft/Right、EyeLeftX/Y、BrowLeftY/RightY、
    /// HandLeftFound/HandRightFound、HandLeft/RightPositionX/Y/Z、VoiceVolume/VoiceA-I-U-E-O、
    /// Mocopi*（穿戴体捕）、Controller*（手柄）。）
    /// 拿来判断「这个模型有没有可用的追踪数据」——名字对不上就是没有，不能拿全 0 冒充。
    /// </summary>
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

    /// <summary>立刻轮询一次追踪数据（平时由工作线程按节拍自动轮询，这个方法用于手动催一次）。</summary>
    public void PollTracking() => Post(w => w.PollTracking(force: true));

    /// <summary>刷新表情列表（连上后自动刷一次；换模型后可手动刷）。</summary>
    public void RefreshExpressions() => Post(w => w.FetchExpressions());

    /// <summary>
    /// 开关一个表情（按段落/节拍驱动情绪用）。<paramref name="fadeSec"/> = VTS 侧的过渡时长。
    /// </summary>
    public void SetExpression(string name, bool active, double fadeSec = 0.4)
    {
        if (string.IsNullOrEmpty(name)) return;
        PostLossy(w => w.SetExpression(name, active, fadeSec));
    }

    /// <summary>
    /// 确保这些自定义参数存在（不存在就建）。用途：不再挤占 MouthOpen / FaceAngleZ 这类
    /// VTS 自己的追踪参数——建专属参数后，我们注入的值与真人的追踪值互不干扰。
    /// </summary>
    public void EnsureParameters(IEnumerable<string> names)
    {
        var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToArray();
        if (list.Length == 0) return;
        Post(w => w.EnsureParameters(list));
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
                // 带超时地取命令，好让「该重连了」这类周期性动作有机会跑。
                // 超时值 = 追踪轮询间隔：追踪的节拍不能被「队列里没命令」拖慢（见 TrackIntervalMs）。
                if (_commands.TryTake(out var cmd, TrackIntervalMs)) cmd(w);
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

        private bool _trackOk;
        private long _trackMisses;

        private long _lastTrackAt;

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
            PollTracking(force: false);

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



        /// <summary>拉取表情列表（换模型后名字会变，所以每次连接都刷）。</summary>
        public void FetchExpressions()
        {
            var resp = Send("ExpressionStateRequest", new JObject());
            if (resp?["expressions"] is not JArray arr)
            {
                _api._expressions = [];
                _api.Notify();
                return;
            }

            var names = arr.Select(e => (string?)e["name"])
                .Where(n => !string.IsNullOrEmpty(n))
                .Select(n => n!)
                .ToArray();
            _api._expressions = names;
            _api.Notify();
            Log.Info($"[VTS] 表情：{names.Length} 个");
        }

        public void SetExpression(string name, bool active, double fadeSec)
        {
            var resp = Send("SetExpressionStateRequest", new JObject
            {
                ["expression"] = name,
                ["active"] = active,
                ["fadeTime"] = fadeSec,
            });
            if (resp == null) Log.Warn($"[VTS] 表情「{name}」设置失败：{_api._lastError}");
        }

        /// <summary>创建缺失的自定义参数（已存在的跳过，避免每次连接都刷一串 APIError）。</summary>
        public void EnsureParameters(string[] names)
        {
            var existing = new HashSet<string>(_api._params.Keys, StringComparer.Ordinal);
            foreach (var name in names)
            {
                if (existing.Contains(name) || _api._createdParams.Contains(name)) continue;
                var resp = Send("ParameterCreationRequest", new JObject
                {
                    ["parameterName"] = name,
                    ["explanation"] = "created by JIZURA Live PV",
                    ["min"] = 0.0,
                    ["max"] = 1.0,
                    ["defaultValue"] = 0.0,
                });
                if (resp == null) continue;   // 失败不阻断其余参数
                var set = new HashSet<string>(_api._createdParams, StringComparer.Ordinal) { name };
                _api._createdParams = set;
            }

            // 新参数要重新拉一遍范围表，注入时才认得出量纲
            FetchParameters();
        }

        /// <summary>
        /// 轮询实时追踪数据。节流到 <see cref="TrackIntervalMs"/>：追踪是持续量，
        /// 每个节拍都发一次会把 WebSocket 占满（而且 VTS 侧也要算），~15Hz 足够驱动视觉联动。
        /// </summary>
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

            // 「接通」与「读不到」都必须留痕：这条链路曾经整条是死的（发的是不存在的消息，
            // 解析器又只对着自己臆想的形状自测），日志上看不出任何异常。
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
                // 不只报「读不到」，还要报**收到了什么**：否则只能靠猜参数名。
                // 这段诊断就是这次修 bug 的产物——当初它要是在，就不会以为链路是通的。
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
