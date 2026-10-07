using System.Diagnostics;
using System.IO;
using JizuraLive.Engine;
using JizuraLive.Logging;
using JizuraLive.Spout;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Newtonsoft.Json.Linq;
using SkiaSharp;

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
/// 播放钟：无音频 = 墙钟外推（Stopwatch）：Play 记基准时间 + 墙钟戳，CurrentTime 按流逝时间外推；
/// Pause 冻结基准；Seek 平移基准。有音频 = 波形位置钟：NAudio 链上的计数提供器按已读帧数
/// 推算歌曲位置（见 _audio 段注释），墙钟只在音频链不可用时兜底。
///
/// 音频播放链（M3，对位 state.ts 的 AP 语义）：
///   MediaFoundationReader（解码，支持 Position 重定位）
///   → 声道归一（mono→stereo 上混 / 多声道取前 2）
///   → WdlResampling 统一到 48k（FxInlet 要求图与音源同采样率）
///   → AudioFx.Build(project.AudioFx) 特效图（units 全关返回 null = 直通）
///   → WdlResampling 变速（48000/speed，对位 BufferSource.playbackRate；speed==1 跳过）
///   → VolumeSampleProvider（固定 0.8²，对位 AP 的 vol*vol；M3 无音量 UI）
///   → CountingSampleProvider（读帧计数 = 波形位置钟）
///   → WaveOutEvent
/// 输出设备打不开（无声卡/独占占用）时 Log.Warn 并整体退回墙钟，不崩。
///
/// 实现 ISpoutSource：SpoutOut 泵线程经它读当前 plan / 播放时间 / 暂停脏标记（Version）。
/// </summary>
public sealed class PreviewEngine : ISpoutSource, IDisposable
{
    private volatile Plan _plan = null!;

    public Project Project { get; private set; }
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

    // ---------------- 音频播放（对位 state.ts 的 AP：src → fx → gain → destination） ----------------
    //
    // 锁的粒度（Play/Pause/Seek 在 UI 线程，Read 在 WaveOut 回调线程，两把锁绝不嵌套持有）：
    //   _chainLock：波形输出/解码器的生死（Stop 会等回调线程退出，持锁时不能再去拿 _clockLock）
    //   _clockLock：位置钟状态（计数器读改写、重定位），Read 每次只短暂拿它
    private readonly object _chainLock = new();
    private readonly object _clockLock = new();

    /// <summary>设备输出率（特效图按它建；变速靠重采样改变流时长，见 BuildChain）。</summary>
    private const int OutRate = 48000;

    /// <summary>预览音量（对位 AP.vol*vol，vol 固定 0.8；导出侧不经这里）。</summary>
    private const float PreviewVolume = 0.64f;

    /// <summary>WaveOut 缓冲参数：3 块 × 50ms。位置钟扣掉「已入队未播出」的
    /// (块数-1)×50ms 让读数贴近扬声器实际进度；无回读接口，残差 ≈ 一个块时长（±50ms）。</summary>
    private const int BufferMs = 50;
    private const int WaveBuffers = 3;
    private static double QueuedSeconds => (WaveBuffers - 1) * BufferMs / 1000.0;

    private AudioData? _audio;        // 分析数据：Replan 传给 Planner（时长/能量/节拍），Transport 经 SetAudioSource 注入
    private string? _audioPath;
    private byte[]? _audioFileBytes;  // 原始文件字节缓存：保存工程包（ProjectIO.Save 的 audioFile 参数）时直接取用

    private WaveOut? _waveOut;
    private MediaFoundationReader? _reader;
    private CountingSampleProvider? _counter;
    private JObject? _fxToken;        // 建链时的 project.AudioFx 快照（DeepEquals 判「配置变了要重建」）
    private double _speed = 1;        // J.fxSpeed（变速不出 DSP 图，靠重采样，对位 playbackRate）
    private double _posBase;          // 计数器零点对应的歌曲时间（重定位/回卷时更新）
    private volatile bool _audioActive; // 波形位置钟可用（链建成功且未被降级）

    private volatile bool _scrubbing; // 对位 S.scrubbing：拖动中画面钉在 seek 目标，音频静默

    /// <summary>已装载的分析音频（null = 无音频，纯墙钟播放）。</summary>
    public AudioData? Audio => _audio;

    /// <summary>音频文件原始字节（保存进 .jizuralive 工程包用；未装载/读盘失败为 null）。</summary>
    public byte[]? AudioFileBytes => _audioFileBytes;

    /// <summary>scrub 拖动中（时间轴/进度条按下到松开）：画面跟手、不推进、不出声。</summary>
    public bool Scrubbing => _scrubbing;

    /// <summary>
    /// 音频投入/卸载（对位 EditorState.SetAudio 的引擎侧回执；由中栏常驻控件在 State.AudioChanged
    /// 时调用）。装载成功即重建播放链并重规划（EditorState 里那次 Replan 发生在 AudioChanged 之前，
    /// 当时还拿不到音频——这里补一次带音频的，双重规划成本可忽略）。失败降级为墙钟。
    /// </summary>
    public void SetAudioSource(AudioData? audio, string? path)
    {
        TeardownAudio();
        _audio = audio;
        _audioPath = path;
        _audioFileBytes = null;
        if (path != null && File.Exists(path))
        {
            try { _audioFileBytes = File.ReadAllBytes(path); }
            catch (Exception ex) { Log.Warn("[Preview] 音频文件字节缓存失败（保存时需重读）：" + ex.Message); }
        }
        if (audio == null || path == null || !File.Exists(path))
        {
            if (path != null) Log.Warn("[Preview] 音频文件不存在，预览退回墙钟：" + path);
            Replan();
            return;
        }
        BuildChain(path, ClampPlayPos(_base));
        Replan();
    }

    /// <summary>解码并装配整条播放链（不启动播放；定位到 startT）。任何一步失败整体退回墙钟。</summary>
    private void BuildChain(string path, double startT)
    {
        try
        {
            var reader = new MediaFoundationReader(path);
            ISampleProvider node = reader.ToSampleProvider();
            if (node.WaveFormat.Channels == 1) node = new MonoToStereoSampleProvider(node);
            else if (node.WaveFormat.Channels > 2) node = new MultiplexingSampleProvider(new[] { node }, 2);
            if (node.WaveFormat.SampleRate != OutRate) node = new WdlResamplingSampleProvider(node, OutRate);

            // 先定位再 Init：WaveOut 建缓冲时就开始从链上读帧，定位晚了首块内容就错位了
            reader.Position = (long)(Math.Clamp(startT, 0, Math.Max(0, reader.TotalTime.TotalSeconds))
                                     * reader.WaveFormat.AverageBytesPerSecond);

            // 特效图（J.buildAudioFx）：units 全关 → null = 不建图直通；anchorT 用当前歌曲时刻，seek 后 LFO 相位确定
            _fxToken = Project.AudioFx;
            var graph = AudioFx.Build(_fxToken, new FxBuildOpts { SampleRate = OutRate, AnchorT = startT });
            if (graph != null) graph.Input.Source = node;
            node = graph?.Output ?? node;

            // 变速（J.fxSpeed）：重采样到 OutRate/speed，设备仍按 OutRate 消费 → 时长 ÷speed（对位 playbackRate）
            var speed = AudioFx.FxSpeedOf(_fxToken);
            if (Math.Abs(speed - 1) > 1e-6) node = new WdlResamplingSampleProvider(node, (int)Math.Round(OutRate / speed));

            var counter = new CountingSampleProvider(new VolumeSampleProvider(node) { Volume = PreviewVolume }, _clockLock);
            // NAudio 3.x：WaveOutEvent 更名 WaveOut，延迟参数改为 BufferMilliseconds×NumberOfBuffers
            var wo = new WaveOut { BufferMilliseconds = BufferMs, NumberOfBuffers = WaveBuffers };
            wo.Init(counter); // 此时已填入首块缓冲，计数器初始化完成后再归零
            lock (_clockLock)
            {
                _counter = counter;
                _counter.Frames = 0;
                _posBase = startT;
            }
            lock (_chainLock)
            {
                _reader = reader;
                _waveOut = wo;
            }
            _speed = speed;
            _audioActive = true;
        }
        catch (Exception ex)
        {
            Log.Warn("[Preview] 音频链装配失败，预览退回墙钟：" + ex.Message);
            TeardownAudio();
        }
    }

    /// <summary>停掉并丢弃整条音频链（幂等；Stop/Dispose 都可能因设备已消失抛错，逐一吞掉）。</summary>
    private void TeardownAudio()
    {
        lock (_chainLock)
        {
            try { if (_waveOut != null && _waveOut.PlaybackState != PlaybackState.Stopped) _waveOut.Stop(); } catch { /* 设备可能已失效 */ }
            try { _waveOut?.Dispose(); } catch { /* 同上 */ }
            try { _reader?.Dispose(); } catch { /* 同上 */ }
            _waveOut = null;
            _reader = null;
        }
        lock (_clockLock)
        {
            _counter = null;
            _posBase = 0;
        }
        _audioActive = false;
    }

    /// <summary>project.AudioFx 变了就重建特效链（对位 applyAudioFx：播放中立即重建，未播放只作废缓存）。</summary>
    private void RebuildFxIfConfigChanged()
    {
        var cfg = Project.AudioFx;
        if (JToken.DeepEquals(cfg, _fxToken))
            return;
        if (!_audioActive)
        {
            _fxToken = cfg; // 无音频链：只记快照，下次建链自然生效
            return;
        }
        var resume = IsPlaying;
        var t = CurrentTime;
        lock (_chainLock)
        {
            try { if (_waveOut != null && _waveOut.PlaybackState != PlaybackState.Stopped) _waveOut.Stop(); } catch { /* 设备失效 */ }
        }
        BuildChain(_audioPath!, t);
        if (resume && _audioActive) SafePlay();
    }

    /// <summary>启动输出；设备打不开时降级为墙钟（只报一次，之后不再尝试）。</summary>
    private void SafePlay()
    {
        lock (_chainLock)
        {
            var wo = _waveOut;
            if (wo == null) return;
            try
            {
                if (wo.PlaybackState != PlaybackState.Playing) wo.Play();
            }
            catch (Exception ex)
            {
                Log.Warn("[Preview] 音频输出启动失败（无输出设备？），预览退回墙钟：" + ex.Message);
                lock (_clockLock)
                {
                    if (IsPlaying) _base = _posBase + Math.Max(0, (_counter?.Frames ?? 0)) * _speed / OutRate;
                }
                TeardownAudio();
            }
        }
    }

    /// <summary>音频静默跳转：冲掉排队缓冲 → 解码器重定位 → 位置钟归零 →（可选）续播。锁不嵌套：Stop 与重定位分段拿锁。</summary>
    private void AudioJump(double t, bool resume)
    {
        if (!_audioActive) return;
        lock (_chainLock)
        {
            try { if (_waveOut != null && _waveOut.PlaybackState != PlaybackState.Stopped) _waveOut.Stop(); } catch { /* 设备失效 */ }
        }
        lock (_clockLock)
        {
            try
            {
                if (_reader != null)
                    _reader.Position = (long)(Math.Max(0, t) * _reader.WaveFormat.AverageBytesPerSecond);
            }
            catch (Exception ex) { Log.Debug("[Preview] 音频重定位失败：" + ex.Message); }
            if (_counter != null) _counter.Frames = 0;
            _posBase = Math.Max(0, t);
        }
        if (resume) SafePlay();
    }

    /// <summary>波形位置钟：计数器帧数（扣掉已入队未播出的缓冲）换算回歌曲时间。</summary>
    private double AudioClock()
    {
        lock (_clockLock)
        {
            var frames = _counter?.Frames ?? 0;
            var audible = Math.Max(0, frames - QueuedSeconds * OutRate);
            return _posBase + audible * _speed / OutRate;
        }
    }

    /// <summary>当前时钟原值（未做循环/钳位）：scrub 中钉在 _base；有音频走波形钟；否则墙钟外推。</summary>
    private double RawTime
    {
        get
        {
            if (_scrubbing) return _base;
            if (IsPlaying && _audioActive) return AudioClock();
            var t = _base;
            if (IsPlaying)
                t += (Stopwatch.GetTimestamp() - _startTs) * (1.0 / Stopwatch.Frequency);
            return t;
        }
    }

    /// <summary>把任意时刻钳到当前 plan 的可播范围（空 plan 为 0）。</summary>
    private double ClampPlayPos(double t) => Math.Clamp(t, 0, Math.Max(0, Duration));

    public PreviewEngine()
    {
        EngineBoot.Install(); // 先装配：JFonts 目录由 Install 填充，字体兜底要在有目录之后跑
        InstallFontFallback();
        Project = Project.NewDefault();
        Replan();
    }

    /// <summary>M3：EditorState 装载工程（打开/撤销重做）时整体换实例并重规划。</summary>
    public void SetProject(Project project)
    {
        Project = project ?? Project.NewDefault();
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
                    using var t = SKTypeface.FromFamilyName(f);
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

    /// <summary>当前播放时间。有音频走波形位置钟；scrub 拖动中钉在 seek 目标；
    /// Loop 循环回卷；不 Loop 到尾钳在结尾。</summary>
    public double CurrentTime
    {
        get
        {
            var t = RawTime;
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
    /// 播放节拍维护（UI 定时器驱动，~10–60Hz 随意）：循环回卷 / 到尾自动暂停 / 特效配置重建。
    /// 回卷通过重置基准实现，t 不会无限增大（保住 double 精度）；有音频时回卷/到尾同步驱动波形链。
    /// </summary>
    public void Tick()
    {
        RebuildFxIfConfigChanged();
        if (!IsPlaying) return;
        var dur = Duration;
        if (dur <= 0) return;
        var raw = RawTime;
        if (Loop)
        {
            if (raw >= dur)
            {
                _base = 0;
                _startTs = Stopwatch.GetTimestamp();
                AudioJump(0, resume: true); // 波形钟回卷到 0 续播
                _version++;
            }
        }
        else if (raw >= dur)
        {
            _base = dur;
            _startTs = Stopwatch.GetTimestamp();
            _isPlaying = false;
            lock (_chainLock)
            {
                try { if (_waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing) _waveOut.Pause(); } catch { /* 设备失效 */ }
            }
            _version++;
        }
    }

    public void Play()
    {
        if (IsPlaying) return;
        RebuildFxIfConfigChanged();
        // 结尾再按播放 → 从头（不循环时 CurrentTime 钳在 dur，体验上是「重播」）
        if (!Loop && Duration > 0 && _base >= Duration - 1e-3)
        {
            _base = 0;
            AudioJump(0, resume: false); // 先定位，SafePlay 统一启动
        }
        if (_audioActive) SafePlay();
        _startTs = Stopwatch.GetTimestamp();
        _isPlaying = true;
    }

    public void Pause()
    {
        if (!IsPlaying) return;
        _base = CurrentTime;
        _startTs = Stopwatch.GetTimestamp();
        _isPlaying = false;
        // Pause()（而非 Stop）：保留排队缓冲，恢复播放无缝续上；位置钟随之冻结
        lock (_chainLock)
        {
            try { if (_waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing) _waveOut.Pause(); } catch { /* 设备失效 */ }
        }
    }

    public void TogglePlay()
    {
        if (IsPlaying) Pause();
        else Play();
    }

    public void Seek(double t)
    {
        _base = ClampPlayPos(t);
        _startTs = Stopwatch.GetTimestamp();
        _version++;
        // scrub 拖动中音频已被 BeginScrub 压住：只挪位置不出声（对位「scrub 中 seek 不重启音频」）
        if (_audioActive) AudioJump(_base, resume: IsPlaying && !_scrubbing);
    }

    /// <summary>scrub 开始（时间轴/进度条按下）：画面跟手、音频静默（对位 beginScrub）。</summary>
    public void BeginScrub()
    {
        if (_scrubbing) return;
        _scrubbing = true;
        lock (_chainLock)
        {
            try { if (_waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing) _waveOut.Pause(); } catch { /* 设备失效 */ }
        }
    }

    /// <summary>scrub 结束：从松手位置恢复出声（播放中），对位 endScrub。</summary>
    public void EndScrub()
    {
        if (!_scrubbing) return;
        _scrubbing = false;
        if (_audioActive) AudioJump(_base, resume: IsPlaying);
    }

    /// <summary>
    /// 重新规划：EngineBoot.Install（幂等）后 Planner.Plan(project, audio)——音频在装载时经
    /// SetAudioSource 注入（plan 时长/能量/节拍随音频）。失败保留旧 plan（编辑器里坏歌词不应弄黑预览）。
    /// 位置越界则回零。
    /// </summary>
    public void Replan()
    {
        Plan plan;
        try
        {
            lock (EngineRenderGate.Lock)
            {
                EngineBoot.Install();
                plan = Planner.Plan(Project, _audio);
            }
        }
        catch (Exception ex)
        {
            Log.Error("[Preview] Replan 失败，保留旧 plan：" + ex.Message, ex);
            return;
        }
        SetPlan(plan);
    }

    /// <summary>
    /// 换 plan（后台重规划完成后由 UI 线程调用；调用方负责 last-wins 丢弃过期结果）。
    /// 计划计算本身在大工程的成本 ~700ms，绝不能在 UI 线程做——见 EditorState.ReplanAsync。
    /// </summary>
    public void SetPlan(Plan plan)
    {
        _plan = plan;
        if (_base > Duration || double.IsNaN(_base))
        {
            _base = 0;
            _startTs = Stopwatch.GetTimestamp();
            if (_audioActive) AudioJump(0, resume: IsPlaying);
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
        TeardownAudio();
        Renderer.Dispose();
    }
}

/// <summary>
/// 波形位置钟：包住整条链的最后一个环节，统计 WaveOut 已消费的帧数。
/// 上游欠数/到尾（EOF）时补零给满——WaveOut 的缓冲不清零，回 0 会把旧内容当新块重播（NAudio 老坑）；
/// 帧数照常累计（按给到设备的算），这样音频播完后位置钟继续走向 plan.Duration，循环/到尾停不卡死。
/// </summary>
public sealed class CountingSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly object _lock;

    public CountingSampleProvider(ISampleProvider source, object clockLock)
    {
        _source = source;
        _lock = clockLock;
        WaveFormat = source.WaveFormat;
    }

    public WaveFormat WaveFormat { get; }

    /// <summary>已交给输出设备的帧数（双声道计帧；这是位置钟的读数来源）。</summary>
    public long Frames;

    public int Read(Span<float> buffer)
    {
        var got = _source.Read(buffer);
        if (got < buffer.Length) buffer.Slice(got).Clear();
        lock (_lock)
        {
            Frames += buffer.Length / 2;
        }
        return buffer.Length;
    }
}
