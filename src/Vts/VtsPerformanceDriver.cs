using JizuraLive.Engine;
using JizuraLive.Logging;

namespace JizuraLive.Vts;

/// <summary>
/// 一帧表演状态（驱动采样）：宿主从预览引擎 / 规划 / 相机探针里取，全是只读值。
/// </summary>
public readonly record struct VtsPerfSample(
    double Time,
    bool Playing,
    int PlanW,
    int PlanH,
    double[] Beats,
    float[]? Energy,
    double EnergyRate,
    double CamX,
    double CamY,
    bool HasCamera);

/// <summary>
/// VTS 直接驱动：把画面与音乐的运动**映射到 VTube Studio 的模型本体**——
/// 节拍律动（每拍下沉回弹）、左右摇摆（含侧身旋转）、按音乐能量开合嘴（参数注入）、
/// 跟着 PV 镜头位移（镜头推出去，模型往画面里挪），而不是只在本地叠画面特效。
///
/// 线程模型：本类自带一条 ~30Hz 采样线程。每个 bit 只做纯计算 + 往 VtsApi 投递
/// 「可丢命令」（<see cref="VtsApi.SetDriveOffset"/> / <see cref="VtsApi.InjectParameters"/>），
/// 实际收发在 VTS 的工作线程上串行合并——采样线程既不碰 socket，也不会把队列堆爆。
/// 关掉开关 / 无歌 / 暂停：偏移平滑归零并把注入过的参数放回追踪源（模型回到手动取景）。
/// </summary>
public sealed class VtsPerformanceDriver : IDisposable
{
    /// <summary>采样间隔（~30Hz）：比 VTS 发送间隔略密，保证发送端每拍都有最新值可用。</summary>
    private const int TickMs = 33;

    /// <summary>偏移平滑系数（每拍）：播放时起手快、停手慢，模型回位不突兀。</summary>
    private const double GainAttack = 0.22;
    private const double GainRelease = 0.07;

    /// <summary>口型跟随：起音快、收音慢（像说话，不是随波形抖）。</summary>
    private const double MouthAttack = 0.55;
    private const double MouthRelease = 0.10;

    /// <summary>节拍冲击衰减速率（每秒）：约 1/6 秒内落回，像一次踩点。</summary>
    private const double BeatDecay = 6.0;

    private readonly IVtsDriveTarget _vts;
    private readonly Func<VtsDriveOptions?> _options;
    private readonly Func<VtsPerfSample?> _sample;

    private Thread? _thread;
    private volatile bool _running;
    private bool _released = true;   // 注入参数是否已放回（避免停手后反复发 weight=0）
    private bool _driving;           // 是否正在驱动（起停各记一条日志，诊断用）
    private double _gain;
    private double _mouth;
    private double[]? _beatsRef;     // 换歌/重规划后拍点表换对象：重置按拍热键的去重
    private int _lastHotkeyBeat = -1;

    public VtsPerformanceDriver(IVtsDriveTarget vts, Func<VtsDriveOptions?> options, Func<VtsPerfSample?> sample)
    {
        _vts = vts;
        _options = options;
        _sample = sample;
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _thread = new Thread(Loop) { IsBackground = true, Name = "VtsDrive" };
        _thread.Start();
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        var t = _thread;
        _thread = null;
        if (t != null && t.IsAlive && t != Thread.CurrentThread)
            t.Join(500);
        Idle(); // 停手：偏移归零 + 参数放回
    }

    public void Dispose() => Stop();

    private void Loop()
    {
        while (_running)
        {
            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                Log.Debug("[VTS驱动] 采样异常：" + ex.Message);
            }

            Thread.Sleep(TickMs);
        }
    }

    /// <summary>推进一步（驱动线程每拍调；测试直接调）。</summary>
    public void Tick()
    {
        var opt = _options();
        var sample = opt is { Enabled: true } ? _sample() : null;
        if (opt == null || sample is not { } s)
        {
            Idle();
            return;
        }

        // ---- 增益包络：播放才有动作，暂停/无歌平滑回到基准 ----
        var target = s.Playing ? 1.0 : 0.0;
        _gain += (target - _gain) * (target > _gain ? GainAttack : GainRelease);
        if (_gain < 0.004 && !s.Playing)
        {
            Idle(); // 已经衰减到底：归位收手
            return;
        }

        // ---- 节拍网格：当前拍到哪、这一拍走了多少 ----
        var (beatIdx, since, len) = BeatAt(s.Beats, s.Time);
        var frac = len > 0 ? Math.Clamp(since / len, 0, 1) : 0;
        var pulse = Math.Exp(-since * BeatDecay);                    // 踩点冲击（拍点 = 1，之后迅速回落）
        var barPhase = 2 * Math.PI * ((beatIdx % 4) + frac) / 4.0;   // 4 拍一个来回的摇摆相位

        // ---- 音乐能量（规划器给的能量包络，与渲染器同一个口径） ----
        var energy = EnergyAt(s, s.Time);

        // ---- 取景偏移（叠加在手动基准之上） ----
        var bounce = -opt.Bounce * pulse * _gain * (0.45 + 0.55 * energy); // 拍点下沉，再回弹
        var sway = opt.Sway * Math.Sin(barPhase) * _gain;
        var rotation = opt.SwayRotation * Math.Sin(barPhase) * _gain;

        // 镜头跟随：镜头往右推，模型往左挪（人就"钉"在场景里，而不是跟着画面一起飘）
        if (s.HasCamera && opt.CameraFollow > 0.0005 && s.PlanW > 0 && s.PlanH > 0)
        {
            sway += -opt.CameraFollow * (s.CamX / s.PlanW) * 2.0 * _gain;
            bounce += opt.CameraFollow * (s.CamY / s.PlanH) * 2.0 * _gain;
        }

        if (!_driving)
        {
            _driving = true;
            Log.Info("[VTS驱动] 开始驱动模型（节拍/能量/镜头 → VTS）");
        }

        if (!ReferenceEquals(_beatsRef, s.Beats)) _beatsRef = s.Beats; // 换歌/重规划：拍点表换对象
        FireBeatHotkey(opt, beatIdx, since);

        _vts.SetDriveOffset(0, sway, bounce, rotation);

        // ---- 参数注入（口型 / 头部摆动）：weight 0 = 不注入（交回 VTS 自己的口型源） ----
        var mouthWeight = Math.Clamp(opt.MouthWeight, 0, 1);
        var headWeight = Math.Clamp(opt.HeadWeight, 0, 1);
        if (mouthWeight <= 0.0005 && headWeight <= 0.0005)
        {
            ReleaseInjection(); // 没开参数注入：把之前注入过的放回追踪源（偏移照常驱动）
            return;
        }

        _released = false;
        var values = new Dictionary<string, double>(2, StringComparer.Ordinal);
        if (mouthWeight > 0.0005)
        {
            _mouth += (energy - _mouth) * (energy > _mouth ? MouthAttack : MouthRelease);
            values["MouthOpen"] = Math.Clamp(_mouth, 0, 1);
            values["MouthSmile"] = Math.Clamp(0.35 + 0.3 * Math.Sin(barPhase), 0, 1);
        }

        if (headWeight > 0.0005)
        {
            // 0~1 归一化：0.5 = 中位；摆幅取 ±0.15 / ±0.12（映射到 FaceAngleZ 的 -90~90° 约 ±13°，
            // 是"跟着节奏点头"的量级，不是甩头）
            values["FaceAngleZ"] = 0.5 + 0.15 * Math.Sin(barPhase) * _gain;
            values["BodyAngleZ"] = 0.5 + 0.12 * Math.Sin(barPhase) * _gain;
        }

        _vts.InjectParameters(values, Math.Max(mouthWeight, headWeight));
    }

    /// <summary>
    /// 按拍触发模型热键（每 N 拍一次，同一拍只触发一次）：让 VTS 侧的动作/表情跟着节拍走。
    /// 只在拍点附近触发（迟到太多说明这一拍已经过去，不补发）；倒带时靠拍号回退自动重新计。
    /// </summary>
    private void FireBeatHotkey(VtsDriveOptions opt, int beatIdx, double since)
    {
        var id = opt.BeatHotkeyId;
        if (string.IsNullOrEmpty(id))
        {
            _lastHotkeyBeat = beatIdx;
            return;
        }

        var every = Math.Clamp(opt.BeatHotkeyEvery, 1, 32);
        if (beatIdx <= _lastHotkeyBeat || since > 0.3) return;
        _lastHotkeyBeat = beatIdx;
        if (beatIdx % every != 0) return;
        _vts.TriggerHotkey(id);
    }

    /// <summary>开关关掉 / 无歌：偏移归零 + 参数放回（各只发一次）。</summary>
    private void Idle()
    {
        _gain = 0;
        _mouth = 0;
        _lastHotkeyBeat = -1;
        if (_driving)
        {
            _driving = false;
            Log.Info("[VTS驱动] 停止驱动（模型回到手动取景）");
        }

        _vts.SetDriveOffset(0, 0, 0, 0);
        ReleaseInjection();
    }

    /// <summary>只放回参数注入（偏移不动）：没开注入 / 关掉注入开关时走这里。</summary>
    private void ReleaseInjection()
    {
        if (_released) return;
        _released = true;
        _vts.InjectParameters(new Dictionary<string, double>(), 0);
    }

    /// <summary>能量包络取值（与渲染器 eIdxIdx 同口径：t × EnergyRate 取样本）。</summary>
    public static double EnergyAt(in VtsPerfSample s, double t)
    {
        if (s.Energy is not { Length: > 0 } en || s.EnergyRate <= 0) return 0.5;
        var i = Math.Clamp((int)Math.Floor(t * s.EnergyRate), 0, en.Length - 1);
        return Math.Clamp(en[i], 0, 1);
    }

    /// <summary>二分找当前拍：返回（拍序号，距拍点秒数，本拍时长）。无拍点表时按 0.5s 一拍兜底。</summary>
    public static (int Index, double Since, double Len) BeatAt(double[] beats, double t)
    {
        if (beats.Length == 0) return ((int)(t / 0.5), t % 0.5, 0.5);
        int lo = 0, hi = beats.Length - 1, i = -1;
        while (lo <= hi)
        {
            var m = (lo + hi) >> 1;
            if (beats[m] <= t)
            {
                i = m;
                lo = m + 1;
            }
            else
            {
                hi = m - 1;
            }
        }

        if (i < 0) return (0, Math.Max(0, t), beats.Length > 0 ? beats[0] : 0.5);
        var len = i + 1 < beats.Length ? beats[i + 1] - beats[i] : i > 0 ? beats[i] - beats[i - 1] : 0.5;
        if (len <= 0.05) len = 0.5;
        return (i, Math.Max(0, t - beats[i]), len);
    }
}
