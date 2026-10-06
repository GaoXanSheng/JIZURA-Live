using System.Diagnostics;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Spout;

namespace JizuraLive.App;

/// <summary>
/// 引擎渲染串行闸：JText/JLang/JStyles 的静态缓存（LayCache/PatCache/MMemo/FaceCache…）非线程安全，
/// 而渲染器本身「每画布/每管线各建一个」只保证实例状态隔离。WPF 预览（UI 线程）与 Spout 直出
/// （泵线程）并发 Frame 会撞这些静态缓存——所有 Frame / Planner.Plan 调用都过这把锁串行。
/// 成本可接受：引擎有整帧缓存（koma 量化步内逐像素相同直接贴图），锁竞争只发生在真渲染的帧上。
/// </summary>
public static class EngineRenderGate
{
    public static readonly object Lock = new();
}

/// <summary>
/// 引擎预览服务：持有 Project（默认工程 + SampleLyrics）/ Plan / Renderer 与播放钟，
/// 给 WPF 预览画布（MainWindow）和 Spout 直出（SpoutOut）提供同一份内容。
///
/// 播放钟 = 墙钟外推（Stopwatch）：Play 记基准时间 + 墙钟戳，CurrentTime 按流逝时间外推；
/// Pause 冻结基准；Seek 平移基准。音频尚未接入（后续里程碑），Plan 用无音频规划
/// （Planner.Plan(project, null)，时长 = 末行终点 + tail）。
///
/// 实现 ISpoutSource：SpoutOut 泵线程经它读当前 plan / 播放时间 / 暂停脏标记（Version）。
/// </summary>
public sealed class PreviewEngine : ISpoutSource, IDisposable
{
    private volatile Plan _plan = null!;

    public Project Project { get; }
    public Renderer Renderer { get; } = new();

    /// <summary>当前 plan（Replan 后换新对象；Spout 泵每帧取一次引用，换 plan 时旧对象仍有效）。</summary>
    public Plan Plan => _plan;

    public bool Loop { get; set; } = true;

    private volatile bool _isPlaying;
    public bool IsPlaying => _isPlaying;

    private long _version = 1;

    /// <summary>内容版本号：Seek / Replan / 循环回卷时 +1。Spout 泵在暂停态据此判断「画面会不会变」，不变则不重绘。</summary>
    public long Version => _version;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _startTs; // Play/Seek/回卷时的墙钟戳（Stopwatch.GetTimestamp() 原始刻度）
    private double _base;  // 该墙钟戳对应的工程时间

    public PreviewEngine()
    {
        EngineBoot.Install(); // 先装配：JFonts 目录由 Install 填充，字体兜底要在有目录之后跑
        InstallFontFallback();
        Project = Project.NewDefault();
        Replan();
    }

    /// <summary>
    /// 字体兜底（同 tests RenderSmoke.SmokeFonts）：仓库自托管字体是 woff2 分片，M2 字体管线未接，
    /// 目录里的 family 本机都没有 → ResolveTypefaceChain 全部落到 SKTypeface.Default——
    /// 实测该路径在 SkiaSharp 3.119 下渲染会原生崩（AV，obj/repro 复现）。
    /// 把字体目录全部条目改指系统 CJK 字体绕开，预览字形也可读。仅本进程内生效。
    /// TODO(M2)：字体管线接通（LoadFontFile/UserFaces 优先）后移除这段。
    /// </summary>
    private static void InstallFontFallback()
    {
        try
        {
            var fam = new[] { "Yu Gothic", "Meiryo", "MS Gothic", "Microsoft YaHei UI", "SimSun", "Arial" }
                .FirstOrDefault(f =>
                {
                    using var t = SkiaSharp.SKTypeface.FromFamilyName(f);
                    return t != null && string.Equals(t.FamilyName, f, StringComparison.OrdinalIgnoreCase);
                }) ?? "Arial";
            foreach (var kv in JFonts.Table.ToArray())
                JFonts.AddUserFont(kv.Key, kv.Value.Label, fam, kv.Value.Weight, kv.Value.Kind);
        }
        catch (Exception ex)
        {
            Log.Warn("[Preview] 字体兜底失败（继续用引擎默认解析）：" + ex.Message);
        }
    }

    public double Duration => _plan?.Duration ?? 0;

    /// <summary>当前播放时间。播放中墙钟外推；Loop 循环回卷；不 Loop 到尾钳在结尾。</summary>
    public double CurrentTime
    {
        get
        {
            var t = _base;
            if (IsPlaying)
                t += (Stopwatch.GetTimestamp() - _startTs) * (1.0 / Stopwatch.Frequency);
            var dur = Duration;
            if (dur <= 0) return Math.Max(0, t);
            if (Loop) return PositiveMod(t, dur);
            return Math.Clamp(t, 0, dur);
        }
    }

    private static double PositiveMod(double v, double m)
    {
        v %= m;
        return v < 0 ? v + m : v;
    }

    /// <summary>
    /// 播放节拍维护（UI 定时器驱动，~10–60Hz 随意）：循环回卷 / 到尾自动暂停。
    /// 回卷通过重置基准实现，t 不会无限增大（保住 double 精度）。
    /// </summary>
    public void Tick()
    {
        if (!IsPlaying) return;
        var dur = Duration;
        if (dur <= 0) return;
        var raw = _base + (Stopwatch.GetTimestamp() - _startTs) * (1.0 / Stopwatch.Frequency);
        if (Loop)
        {
            if (raw >= dur)
            {
                _base = 0;
                _startTs = Stopwatch.GetTimestamp();
                _version++;
            }
        }
        else if (raw >= dur)
        {
            _base = dur;
            _startTs = Stopwatch.GetTimestamp();
            _isPlaying = false;
            _version++;
        }
    }

    public void Play()
    {
        if (IsPlaying) return;
        // 结尾再按播放 → 从头（不循环时 CurrentTime 钳在 dur，体验上是「重播」）
        if (!Loop && Duration > 0 && _base >= Duration - 1e-3) _base = 0;
        _startTs = Stopwatch.GetTimestamp();
        _isPlaying = true;
    }

    public void Pause()
    {
        if (!IsPlaying) return;
        _base = CurrentTime;
        _isPlaying = false;
    }

    public void TogglePlay()
    {
        if (IsPlaying) Pause();
        else Play();
    }

    public void Seek(double t)
    {
        _base = Math.Clamp(t, 0, Math.Max(0, Duration));
        _startTs = Stopwatch.GetTimestamp();
        _version++;
    }

    /// <summary>
    /// 重新规划：EngineBoot.Install（幂等）后 Planner.Plan(project, null)。
    /// 失败保留旧 plan（编辑器里坏歌词不应弄黑预览）。位置越界则回零。
    /// </summary>
    public void Replan()
    {
        try
        {
            lock (EngineRenderGate.Lock)
            {
                EngineBoot.Install();
                _plan = Planner.Plan(Project, null);
            }
        }
        catch (Exception ex)
        {
            Log.Error("[Preview] Replan 失败，保留旧 plan：" + ex.Message, ex);
            return;
        }

        if (_base > Duration || double.IsNaN(_base))
        {
            _base = 0;
            _startTs = Stopwatch.GetTimestamp();
        }
        _version++;
    }

    // ---------------- ISpoutSource（SpoutOut 泵线程读） ----------------

    Plan ISpoutSource.Plan => Plan;
    double ISpoutSource.CurrentTime => CurrentTime;
    bool ISpoutSource.IsPlaying => IsPlaying;
    long ISpoutSource.Version => Version;

    public void Dispose()
    {
        Renderer.Dispose();
    }
}
