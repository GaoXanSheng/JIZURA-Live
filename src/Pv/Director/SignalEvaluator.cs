using JizuraLive.Render.Effects;
using JizuraLive.Vts;

namespace JizuraLive.Pv;

/// <summary>
/// 音频分析在本帧的快照（契约 §8.1：有效位与值一起传）。<see cref="Valid"/> = false 表示"没有数据"，
/// 与"数据是 0"是两件事——静音段的有效 0 必须照常传播，失效才回落到基础值。
/// </summary>
public readonly record struct AudioBandSnapshot(bool Valid, double Rms, double Bass, double Mid, double High);

/// <summary>
/// 本帧的 VTS 姿态快照：姿态值 + 采集时刻（**与 <see cref="SignalFrameInputs.LiveTime"/> 同一单调时钟域的秒**）。
/// 时间域只在输入边界归一一次，帧内不拿两个时钟域相减（契约 §5）。
/// 默认值 = 没有姿态（无脸、时刻为 0 ⇒ 恒过期），所以既没接 VTS 也没人喂快照时通道一律失效。
/// </summary>
public readonly record struct VtsPoseSnapshot(VtsTrackingState State, double AtLiveSeconds)
{
    /// <summary>首版新鲜度阈值：超过 250ms 未更新即失效（契约 §8.3）。</summary>
    public const double MaxAgeSeconds = 0.25;

    /// <summary>
    /// 从追踪探针取本帧快照：探针为空（未连 VTS）或快照超过 <see cref="MaxAgeSeconds"/> 未更新，
    /// 都返回 <c>default</c>（= 无效姿态）——**不编造 FaceFound=true 的假数据**。
    /// <paramref name="liveSeconds"/> 必须与同一帧传给 <see cref="SignalFrameInputs.LiveTime"/> 的**同一个**读数，
    /// 否则判龄会拿两个时钟域的差去比（契约 §5：时间域只在输入边界归一一次）。
    ///
    /// 三处生产调用点（PvPipeline.ApplySignals、PvStageWindow 的参数行徽标与蓝图诊断）都必须走这一份：
    /// 手工传 vts 的求值用例钉不住调用点，各写一遍这个条件迟早分叉成"预览有、实机没有"。
    /// </summary>
    public static VtsPoseSnapshot Read(VtsTrackingProbe? probe, double liveSeconds) =>
        probe is not null && probe.TryRead((int)(MaxAgeSeconds * 1000), out var state)
            ? new VtsPoseSnapshot(state, liveSeconds)
            : default;
}

/// <summary>
/// 一帧的输入：两个时间口径 + 音频与姿态快照 + 冻结只读的曲线求值器。
/// <paramref name="TransportTime"/> 是工程时间轴（与帧时间戳同轴），<paramref name="LiveTime"/> 是单调时钟
/// （暂停也在走，供实时输入判龄）；曲线一律按 transport 口径采样。
///
/// <paramref name="vts"/> **故意没有默认值**：VtsPose 节点在实机里恒失效过——三处生产调用点都"没传就当 default"，
/// 而 default 恰好是"无姿态"，于是求值侧全绿、屏幕上什么都不动。要接 VTS 就必须显式写下快照
/// （<see cref="VtsPoseSnapshot.Read"/>），没有姿态就显式写 <c>default</c>，由编译器盯着这件事。
///
/// <paramref name="frame"/> 是**渲染线程本帧的草稿**帧状态（相机/图层域的 base 与写入目标都在它上面）。
/// 传 null 表示调用方没有草稿：相机/图层域的 base 读所有者场景当前**已发布**的快照（UI 徽标求值走这条）。
/// 帧状态每帧整份换新，所以这里必须逐帧给，不能存实例。
/// </summary>
public readonly struct SignalFrameInputs
{
    public SignalFrameInputs(double transportTime, double liveTime, AudioBandSnapshot audio, DirectorEvaluator? curves,
        VtsPoseSnapshot vts, PvFrameState? frame = null)
    {
        TransportTime = transportTime;
        LiveTime = liveTime;
        Audio = audio;
        Curves = curves;
        Vts = vts;
        Frame = frame;
    }

    public double TransportTime { get; }

    public double LiveTime { get; }

    public AudioBandSnapshot Audio { get; }

    public DirectorEvaluator? Curves { get; }

    /// <summary>本帧的 VTS 姿态快照（判龄用 <see cref="LiveTime"/>，见 <see cref="VtsPoseSnapshot"/>）。</summary>
    public VtsPoseSnapshot Vts { get; }

    /// <summary>本帧的草稿帧状态（渲染线程）；null = 读所有者场景已发布的快照。</summary>
    public PvFrameState? Frame { get; }
}

/// <summary>本帧某个目标参数的值来自哪里（UI 的"显示值来源"就是它）。</summary>
public enum ParamValueSource
{
    /// <summary>信号图输出的值。</summary>
    Signal,

    /// <summary>信号无效，回落到基础值（常量 + 曲线）。</summary>
    Base,

    /// <summary>基础值也非有限，回落到目标 schema 的默认值。</summary>
    TargetDefault,
}

/// <summary>一个目标参数本帧的最终值：地址、值、来源、是否被目标范围限幅、以及是哪个节点给出的。</summary>
public readonly record struct ResolvedParam(
    ParamAddress Address,
    double Value,
    ParamValueSource Source,
    bool Clamped,
    string NodeId);

/// <summary>求值期问题：动态输入造成的失效（与编译期静态错误分开，两者处置完全不同）。</summary>
public readonly record struct SignalEvalIssue(string Code, string NodeId);

/// <summary>
/// 编译产物（帧内求值部分）：按拓扑序依次走节点，把值与有效位写进可复用缓冲，最后一次写 Resolved。
///
/// 三条不变量：
/// <list type="number">
/// <item>本路径**不抛异常**：帧循环里一个节点的坏数据不该把整帧抛穿（配置错误在编译期已经拒了）。</item>
/// <item>非有限值绝不进入有效输出：每个节点算完都查 <see cref="double.IsFinite"/>，NaN/∞ 只表现为"无效 + 一条 issue"。</item>
/// <item>稳态 0 分配：值与有效位写在计划自己的数组里，Resolved/Issues 是复用 List（Clear 后重填），
/// 路径上不排序、不解析地址文本、不做字符串→枚举解析。</item>
/// </list>
/// </summary>
public sealed partial class CompiledSignalGraph
{
    private const string CodeRequiredInputUnconnected = "eval-required-unconnected";
    private const string CodeNonFinite = "eval-non-finite";
    private const string CodeSourceInvalid = "eval-source-invalid";
    private const string CodeBaseNonFinite = "eval-base-non-finite";

    /// <summary>本帧全部绑定目标的最终值（一条绑定一项，顺序 = 绑定在文档里的顺序）。</summary>
    public IReadOnlyList<ResolvedParam> Resolved => _resolved;

    /// <summary>本帧的动态问题（每次求值清空重填）。</summary>
    public IReadOnlyList<SignalEvalIssue> Issues => _issueList;

    /// <summary>
    /// 求一帧。输入按值传入（<c>in</c>），不改文档、不改输入、不持有跨帧状态：
    /// 同一个输入重复求值逐位相同，暂停/seek/重绘都不会因为"上次算到哪"而不同。
    /// </summary>
    public void Evaluate(in SignalFrameInputs inputs)
    {
        _resolved.Clear();
        _issueList.Clear();

        for (var i = 0; i < _nodes.Length; i++)
        {
            // 禁用 = 显式"输出无效"，不记 issue，也不采用适用于纹理节点的通用 Bypass（契约 §8.2.5）
            if (!_enabled[i])
            {
                _valid[i] = false;
                _values[i] = 0;
                continue;
            }

            var value = 0.0;
            var valid = true;

            switch (_kind[i])
            {
                case SignalNodeKind.Constant:
                    value = _params[i * NodeParamSlots + SlotConstantValue];
                    break;

                case SignalNodeKind.TimelineBaseValue:
                    value = SignalTargets.BaseValue(in _targets[_timelineTarget[i]], inputs.Curves, inputs.TransportTime,
                        inputs.Frame);
                    if (!double.IsFinite(value))
                    {
                        // 曲线/常量本身坏了：本节点无效；绑定随后回落目标默认值，绝不把 NaN 送到下游
                        _issueList.Add(new SignalEvalIssue(CodeBaseNonFinite, _nodes[i].Id));
                        valid = false;
                    }

                    break;

                case SignalNodeKind.Time:
                    value = _params[i * NodeParamSlots + SlotTimeSource] >= 0.5 ? inputs.LiveTime : inputs.TransportTime;
                    break;

                case SignalNodeKind.AudioBands:
                    if (inputs.Audio.Valid)
                    {
                        value = BandOf(inputs.Audio, (int)_params[i * NodeParamSlots + SlotBand]);
                    }
                    else
                    {
                        // 失效不是 0：回落到基础值由绑定负责，这里只声明"本节点无效"
                        _issueList.Add(new SignalEvalIssue(CodeSourceInvalid, _nodes[i].Id));
                        valid = false;
                    }

                    break;

                case SignalNodeKind.VtsPose:
                {
                    // 过期与丢脸都标记失效：不保留上一个姿态偏移（契约 §8.3）；
                    // 判龄用 LiveTime，所以暂停音乐也照常过期（§13.7「暂停时仍用 LiveTime 判断过期」）。
                    // 已列通道全部出自脸部追踪，所以门就是 FaceFound——快照里没有逐通道"这一项在不在响应里"
                    // 的粒度（ParseTracking 把缺项折成 0），手位/位置通道又因量纲未实测不进节点（见 VtsChannel）
                    var pose = inputs.Vts;
                    if (pose.State.FaceFound && inputs.LiveTime - pose.AtLiveSeconds <= VtsPoseSnapshot.MaxAgeSeconds)
                    {
                        value = VtsChannelValue(pose.State, (VtsChannel)(int)_params[i * NodeParamSlots + SlotChannel]);
                    }
                    else
                    {
                        _issueList.Add(new SignalEvalIssue(CodeSourceInvalid, _nodes[i].Id));
                        valid = false;
                    }

                    break;
                }

                case SignalNodeKind.Add:
                {
                    var okA = TryReadInput(i, PortPrimary, out var a);
                    var okB = TryReadInput(i, PortSecondary, out var b);
                    value = a + b;
                    valid = okA && okB;
                    break;
                }

                case SignalNodeKind.Multiply:
                {
                    var okA = TryReadInput(i, PortPrimary, out var a);
                    var okB = TryReadInput(i, PortSecondary, out var b);
                    value = a * b;
                    valid = okA && okB;
                    break;
                }

                case SignalNodeKind.Clamp:
                {
                    var ok = TryReadInput(i, PortPrimary, out var v);
                    var lo = _params[i * NodeParamSlots + SlotClampMin];
                    var hi = _params[i * NodeParamSlots + SlotClampMax];
                    // 手写夹取而不是 Math.Clamp：后者在 lo > hi 时抛异常，帧循环绝不能抛
                    // （lo > hi 在编译期已被 clamp-inverted 拒绝，这里只保证不抛）
                    value = v < lo ? lo : v > hi ? hi : v;
                    valid = ok;
                    break;
                }

                case SignalNodeKind.Remap:
                {
                    var ok = TryReadInput(i, PortPrimary, out var v);
                    var inMin = _params[i * NodeParamSlots + SlotRemapInMin];
                    var inMax = _params[i * NodeParamSlots + SlotRemapInMax];
                    var outMin = _params[i * NodeParamSlots + SlotRemapOutMin];
                    var outMax = _params[i * NodeParamSlots + SlotRemapOutMax];

                    var u = (v - inMin) / (inMax - inMin);
                    // 先把比例 clamp 到 0..1 再映射到输出单位（契约 §8.1）；零区间在编译期被拒，
                    // 万一漏到这里会得到非有限值，由下面的有限性检查兜住
                    if (u < 0) u = 0;
                    else if (u > 1) u = 1;

                    value = outMin + u * (outMax - outMin);
                    valid = ok;
                    break;
                }

                case SignalNodeKind.Lfo:
                {
                    var okTime = TryReadInput(i, PortPrimary, out var time);
                    var okOffset = TryReadInput(i, PortSecondary, out var offset);
                    var okAmplitude = TryReadInput(i, PortAmplitude, out var amplitude);
                    var frequency = _params[i * NodeParamSlots + SlotLfoFrequency];
                    var phase = _params[i * NodeParamSlots + SlotLfoPhase];

                    // 由显式时间输入算相位，不累计帧增量：暂停/seek/重绘都不会漂移（契约 §8.1）
                    value = offset + amplitude * Math.Sin(2 * Math.PI * frequency * time + phase);
                    valid = okTime && okOffset && okAmplitude;
                    break;
                }

                case SignalNodeKind.ParameterOutput:
                {
                    var ok = TryReadInput(i, PortPrimary, out var v);
                    value = v;
                    valid = ok;
                    break;
                }
            }

            if (valid && !double.IsFinite(value))
            {
                // 溢出/零除/坏输入都在这里现形：只表现为"本节点无效 + 一条 issue"，NaN/∞ 不进有效输出
                _issueList.Add(new SignalEvalIssue(CodeNonFinite, _nodes[i].Id));
                valid = false;
            }

            _values[i] = valid ? value : 0.0;
            _valid[i] = valid;
        }

        for (var b = 0; b < _bindingAddress.Length; b++)
        {
            var slot = _bindingNode[b];
            var target = _targets[_bindingTarget[b]];

            double value;
            ParamValueSource source;

            if (_valid[slot])
            {
                value = _values[slot];
                source = ParamValueSource.Signal;
            }
            else
            {
                // 输出无效 ⇒ 回落 base(t)；文档常量与两个 Key 始终不变（契约 §3.3 步骤 3/4）
                value = SignalTargets.BaseValue(in target, inputs.Curves, inputs.TransportTime, inputs.Frame);
                if (double.IsFinite(value))
                {
                    source = ParamValueSource.Base;
                }
                else
                {
                    _issueList.Add(new SignalEvalIssue(CodeBaseNonFinite, _nodes[slot].Id));
                    value = target.Default;
                    source = ParamValueSource.TargetDefault;
                }
            }

            var clamped = false;
            if (!double.IsFinite(value))
            {
                // 不变量兜底：Resolved 里每个值恒为有限——这就是"动态非有限值不进 GPU"在本线的可断言形态
                value = target.Default;
                source = ParamValueSource.TargetDefault;
            }
            else if (value < target.Min)
            {
                value = target.Min;
                clamped = true;
            }
            else if (value > target.Max)
            {
                value = target.Max;
                clamped = true;
            }

            // 限幅只发生在这一帧的输出上，**不回写文档**（§3.3 步骤 4）
            _resolved.Add(new ResolvedParam(target.Address, value, source, clamped, _nodes[slot].Id));
        }
    }

    /// <summary>取某地址本帧的解析结果（UI 的"显示值来源"）；没有这条绑定返回 false。</summary>
    public bool TryGetResolved(in ParamAddress address, out ResolvedParam resolved)
    {
        for (var i = 0; i < _resolved.Count; i++)
        {
            if (_resolved[i].Address != address) continue;
            resolved = _resolved[i];
            return true;
        }

        resolved = default;
        return false;
    }

    /// <summary>
    /// 取某地址解析到的实例与参数定义（引用相同 = 同一个目标）。
    /// 两个同 DefId 不同 InstanceId 的实例各有一条绑定时，这里必须解析到各自的 <see cref="EffectNode"/>。
    /// </summary>
    public bool TryGetTarget(in ParamAddress address, out EffectNode node, out EffectParam param)
    {
        for (var b = 0; b < _bindingAddress.Length; b++)
        {
            if (_bindingAddress[b] != address) continue;
            var target = _targets[_bindingTarget[b]];
            node = target.Node;
            param = target.Param;
            return true;
        }

        node = null!;
        param = null!;
        return false;
    }

    /// <summary>
    /// 读一个输入端口。未连：可选端口取声明默认值且有效；必要端口 ⇒ 本节点无效并记 issue。
    /// 已连：上游有效位原样传递——**已连而无效绝不回落到默认值**（契约 §8.1，避免"看起来有信号"的假象）。
    /// </summary>
    private bool TryReadInput(int slot, int port, out double value)
    {
        var src = _inputSource[slot * MaxInputs + port];
        if (src >= 0)
        {
            value = _values[src];
            return _valid[src];
        }

        value = _inputDefault[slot * MaxInputs + port];
        if (!_inputRequired[slot * MaxInputs + port]) return true;

        _issueList.Add(new SignalEvalIssue(CodeRequiredInputUnconnected, _nodes[slot].Id));
        return false;
    }

    /// <summary>频带选择：0 = Rms，1 = Bass（默认），2 = Mid，3 = High。</summary>
    private static double BandOf(in AudioBandSnapshot audio, int band) => band switch
    {
        0 => audio.Rms,
        2 => audio.Mid,
        3 => audio.High,
        _ => audio.Bass,
    };

    /// <summary>
    /// 通道取值（通道集合与量纲见 <see cref="VtsChannel"/>）：通道号在编译期已按枚举域校验过，
    /// 所以这里的 default 只是"到不了"的兜底而不是容错分支。
    /// </summary>
    private static double VtsChannelValue(VtsTrackingState state, VtsChannel channel) => channel switch
    {
        VtsChannel.HeadAngleX => state.HeadAngleX,
        VtsChannel.HeadAngleY => state.HeadAngleY,
        VtsChannel.HeadAngleZ => state.HeadAngleZ,
        VtsChannel.EyeOpenLeft => state.EyeOpenLeft,
        VtsChannel.EyeOpenRight => state.EyeOpenRight,
        VtsChannel.MouthOpen => state.MouthOpen,
        VtsChannel.MouthSmile => state.MouthSmile,
        _ => 0,
    };
}
