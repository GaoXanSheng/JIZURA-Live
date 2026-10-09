using System.Diagnostics;
using System.IO;
using JizuraLive.App.Cloud;
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
///   → SpeedSampleProvider 变速（对位 BufferSource.playbackRate；speed==1 跳过）
///   → VolumeSampleProvider（预览音量/消音，对位 AP 的 vol²/muted，见 SetVolume；导出侧不经这里）
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

    /// <summary>循环档位（对位 state.ts 的 S.loop：all/line/cut/false；Range = 循环导出范围，C# 扩展档）。
    /// 默认整首。档位只在会话内有效，不落盘（同 Vue）。</summary>
    public LoopMode Loop { get; private set; } = LoopMode.All;

    /// <summary>Line/Cut/Range 的冻结范围（对位 S.loopHold）：play/seek/replan/换档时刷新，不逐帧重取——
    /// 逐帧重取的话播放中范围会随播放头漂移（一整行会被越切越碎）。</summary>
    private (double Start, double End)? _loopHold;

    /// <summary>打点会话进行中（TapSession 置位）：到界不回卷而是停住，交打点会话收尾（对位 S.tap）。</summary>
    public bool TapActive { get; set; }

    /// <summary>打点到界（对位 bus 'tapend' → look.ts stopTap）：Tick 触发，订阅方结束会话。</summary>
    public event Action? TapEnded;

    private volatile bool _isPlaying;
    public bool IsPlaying => _isPlaying;

    private long _version = 1;

    /// <summary>内容版本号：Seek / Replan / 循环回卷时 +1。Spout 泵在暂停态据此判断「画面会不会变」，不变则不重绘。</summary>
    public long Version => _version;

    // ---------------- 字幕层数据（独立 Spout 字幕通道与导出烤字幕同一来源） ----------------

    /// <summary>云字幕时间轴（KRC 逐字；云音乐装载 / 跟随匹配写入，null = 没有字幕层——
    /// 网易云 LRC 无逐字不产生）。</summary>
    public List<KrcLine>? Subtitles { get; private set; }

    /// <summary>字幕层配置（与导出共用同一实例：改一处两处都变）。</summary>
    public SubtitleConfig SubtitleCfg { get; } = new() { Content = SubContent.Both };

    private long _subVersion = 1;

    /// <summary>字幕版本号：换歌/装载/清空字幕、改字幕样式时 +1；字幕通道据它重建字幕层，
    /// 预览泵据它在暂停态重出一帧（跨线程读，读端 Volatile）。</summary>
    public long SubtitleVersion => Volatile.Read(ref _subVersion);

    public void SetSubtitles(List<KrcLine>? lines)
    {
        Subtitles = lines is { Count: > 0 } ? lines : null;
        Interlocked.Increment(ref _subVersion);
    }

    /// <summary>字幕样式（SubtitleCfg）改动后的通知：版本号 +1，让预览泵与 Spout 字幕通道
    /// 在暂停态也重绘一帧（配置是就地改的共享对象，没有别的变更信号）。</summary>
    public void NotifySubtitleCfgChanged() => Interlocked.Increment(ref _subVersion);

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

    /// <summary>预览音量/消音（对位 AP.vol 与 AP.muted；导出侧不经这里，导出始终原电平）。</summary>
    private double _vol = 0.8;
    private bool _muted;
    private VolumeSampleProvider? _gain; // 主音量环节：链上最后一个 DSP，随链重建（对位 AP.gain）

    /// <summary>预览音量 0..1（对位 AP.vol）。</summary>
    public double Volume => _vol;

    /// <summary>预览消音（对位 AP.muted）。</summary>
    public bool Muted => _muted;

    /// <summary>
    /// 设预览音量/消音（对位 AP.setVol：v 为 null 表示只改消音）。增益 = muted ? 0 : vol²，
    /// 与旧版 AP.gain 的写死口径一致；无链时只记状态，下次建链自然生效。
    /// </summary>
    public void SetVolume(double? v, bool? muted = null)
    {
        if (v != null) _vol = Math.Clamp(v.Value, 0, 1);
        if (muted != null) _muted = muted.Value;
        var g = _gain;
        if (g != null) g.Volume = _muted ? 0f : (float)(_vol * _vol); // float 写原子，Read 侧不上锁
    }

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

    // ---------------- 跟随模式（M6，对位 frontend 的 S.follow：外部播放器出声，这里只同步进度） ----------------
    //
    // 跟随开启期间本地音频链一律忽略（RawTime 走墙钟路径，同旧版「不碰 AP」）：
    // FollowController 每 300ms 拿 SMTC 快照调 FollowAnchor 重锚定 _base/_startTs，
    // 两次锚定之间本地按墙钟平滑推进，漂移有界；FollowEnd 冻结并退出。

    private volatile bool _follow;

    /// <summary>跟随模式中（外部播放器决定进度/暂停，本地循环与到尾暂停被跳过）。</summary>
    public bool FollowMode => _follow;

    /// <summary>
    /// 跟随重锚定：SMTC 快照的「此刻进度」 t（秒）与播放态 run 直接落到墙钟上；
    /// 不碰音频链。t 变化或播放态翻转才 +Version（暂停且进度冻结时不制造无谓重绘）。
    /// </summary>
    public void FollowAnchor(double t, bool run)
    {
        _follow = true;
        t = Math.Max(0, t);
        var changed = Math.Abs(t - CurrentTime) > 1e-4 || IsPlaying != run;
        _base = t;
        _startTs = Stopwatch.GetTimestamp();
        if (IsPlaying != run) _isPlaying = run; // 只翻时钟位，绝不动 WaveOut（音频由播放器自己出）
        if (changed) _version++;
    }

    /// <summary>跟随结束：时间轴冻结在当前位置并暂停（不恢复本地音频，交用户手动播放）。</summary>
    public void FollowEnd()
    {
        if (!_follow) return;
        _base = CurrentTime;
        _startTs = Stopwatch.GetTimestamp();
        _isPlaying = false;
        _follow = false;
        _version++;
    }

    /// <summary>
    /// 音频投入/卸载（对位 EditorState.SetAudio 的引擎侧回执；由中栏常驻控件在 State.AudioChanged
    /// 时调用）。装载成功即重建播放链。replan 为 false 时跳过同步重规划（EditorState.SetAudio 内部
    /// 已发起后台异步规划 ReplanAsync，避免在 UI 线程重复阻塞数百毫秒）。失败降级为墙钟。
    /// </summary>
    public void SetAudioSource(AudioData? audio, string? path, bool keepCurrentTime = false, bool replan = true)
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
            if (replan) Replan();
            return;
        }
        // 恢复上次音频时从当前时刻续播，避免跳回开头；新歌载入一律从 0 起（对位旧前端 songLoader 的 loadAudioFile + seek(0)）
        var startT = keepCurrentTime && CurrentTime > 0.05
            ? Math.Clamp(CurrentTime, 0, Math.Max(0, audio.Duration))
            : 0;
        _base = startT;
        _startTs = Stopwatch.GetTimestamp();
        BuildChain(path, startT);
        // 装载时若播放器已处于播放态 → 新链跟着起跑；暂停态保持暂停
        if (IsPlaying && !_follow && _audioActive) SafePlay();
        if (replan) Replan();
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

            // 变速（J.fxSpeed）：playbackRate 等价实现——时长 ÷speed、音调同升（对位 BufferSource.playbackRate）
            var speed = AudioFx.FxSpeedOf(_fxToken);
            if (Math.Abs(speed - 1) > 1e-6) node = new SpeedSampleProvider(node, speed);

            // 主音量（对位 AP.gain）：muted 直接 0，否则 vol²
            var gain = new VolumeSampleProvider(node) { Volume = _muted ? 0f : (float)(_vol * _vol) };
            var counter = new CountingSampleProvider(gain, _clockLock);
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
                _gain = gain;
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
            _gain = null;
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
        if (resume && _audioActive && !_follow) SafePlay(); // 跟随中不出本地声
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

    /// <summary>当前时钟原值（未做循环/钳位）：scrub 中钉在 _base；跟随模式走墙钟（忽略本地音频链）；
    /// 有音频走波形钟；否则墙钟外推。</summary>
    private double RawTime
    {
        get
        {
            if (_scrubbing) return _base;
            if (_follow)
            {
                var tf = _base;
                if (IsPlaying)
                    tf += (Stopwatch.GetTimestamp() - _startTs) * (1.0 / Stopwatch.Frequency);
                return tf;
            }

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
        EngineBoot.InstallPacks(); // 先装配：JFonts 目录由 Install 填充
        JFonts.ScanFontDirs();     // 字体目录入索引（exe 旁 fonts/ 与用户字体库）
        Project = Project.NewDefault();
        Replan();
    }

    /// <summary>M3：EditorState 装载工程（打开/撤销重做）时整体换实例并重规划。
    /// replan=false 只换实例、由调用方自己发起后台重规划——首次 Plan 加首帧预热（冷缓存 0.7~1.8s）
    /// 压在 UI 线程上合计 ~2-3s，正是「打开应用就卡住」的来源。</summary>
    public void SetProject(Project project, bool replan = true)
    {
        Project = project ?? Project.NewDefault();
        if (replan) Replan();
    }

    /// <summary>
    /// 字体管线预热（EnsureFonts）：plan 用到的脸先解析好（首帧不卡在字脸解析上），
    /// 目录/系统都没有的家族由 FontFetch 后台按需拉（老前端「网络拉取 + 本地缓存」）。
    /// 拉到后 FontFetch 清缓存并回调 OnFetched，由 App 侧重规划重绘。
    /// </summary>
    private void WarmFonts(Plan plan)
    {
        try
        {
            // 与渲染/导出串行：解析预热可能触发字体目录重扫（网络拉取落盘后），进而清共享字形缓存
            lock (EngineRenderGate.Lock)
                _ = JFonts.EnsureFonts(Project?.Lyrics, JFonts.FontsOfPlan(plan));
        }
        catch (Exception ex)
        {
            Log.Warn("[字体] 管线预热失败（继续用系统字体栈）：" + ex.Message);
        }
    }

    public double Duration => _plan?.Duration ?? 0;

    // ---------------- 循环档位与范围（对位 state.ts 的 LOOP_CYCLE / loopRange / refreshLoopHold / activeLoopRange） ----------------

    /// <summary>换档（对位 Transport.cycleLoop）：立即按当前位置取新范围——不然播放中切档会沿用上一档
    /// 留下的范围（如从切分循环切到行循环，整行范围一直在，切不开）。</summary>
    public void SetLoop(LoopMode mode)
    {
        if (Loop == mode) return;
        Loop = mode;
        RefreshLoopHold();
        _version++;
    }

    /// <summary>重取冻结范围（对位 refreshLoopHold）：play / seek / replan / 换档时调用。</summary>
    public void RefreshLoopHold()
    {
        _loopHold = Loop is LoopMode.Line or LoopMode.Cut or LoopMode.Range
            ? Ranges.LoopRange(_plan, Loop, Project?.ExportRange, CurrentTime)
            : null;
    }

    /// <summary>当前生效的循环范围（对位 activeLoopRange）：Line/Cut/Range 用冻结范围，其余按当前时刻现算。</summary>
    public (double Start, double End) ActiveLoopRange()
    {
        if (Loop is LoopMode.Line or LoopMode.Cut or LoopMode.Range && _loopHold is { } hold)
            return hold;
        return Ranges.LoopRange(_plan, Loop, Project?.ExportRange, CurrentTime);
    }

    /// <summary>当前播放时间。有音频走波形位置钟；scrub 拖动中钉在 seek 目标；
    /// 整首循环取模回卷；分段循环钳在范围末端（回卷由 Tick 负责，两拍之间不跳）；
    /// 不循环钳在结尾。</summary>
    public double CurrentTime
    {
        get
        {
            var t = RawTime;
            var dur = Duration;
            if (dur <= 0) return Math.Max(0, t);
            if (Loop == LoopMode.Off) return Math.Clamp(t, 0, dur);
            if (Loop == LoopMode.All) return PositiveMod(t, dur);
            var (_, end) = ActiveLoopRange();
            return Math.Clamp(t, 0, Math.Clamp(end, 0, dur));
        }
    }

    private static double PositiveMod(double v, double m)
    {
        v %= m;
        return v < 0 ? v + m : v;
    }

    /// <summary>
    /// 播放节拍维护（UI 定时器驱动，~10–60Hz 随意）：到界回卷 / 到尾自动暂停 / 特效配置重建。
    /// 回卷通过重置基准实现，t 不会无限增大（保住 double 精度）；有音频时回卷/到尾同步驱动波形链。
    /// 对位 engine.ts 的 tick：打点中不回卷（停住 + tapend），跟随模式整段跳过。
    /// </summary>
    public void Tick()
    {
        RebuildFxIfConfigChanged();
        if (_follow) return; // 跟随模式：外部播放器决定到尾/回卷，本地循环与到尾暂停不参与
        if (!IsPlaying) return;
        var dur = Duration;
        if (dur <= 0) return;
        var raw = RawTime;
        // 到界点：不循环 = 整首末尾；其余档位 = 当前冻结/现算范围末端
        var (rangeStart, rangeEnd) = Loop == LoopMode.Off ? (0.0, dur) : ActiveLoopRange();
        if (rangeEnd <= rangeStart) rangeEnd = dur; // 空范围兜底（等价整首）
        if (raw < rangeEnd - 1e-3) return;

        if (Loop != LoopMode.Off && !TapActive)
        {
            _base = rangeStart;
            _startTs = Stopwatch.GetTimestamp();
            AudioJump(rangeStart, resume: true); // 波形钟回卷到范围起点续播
            _version++;
            return;
        }

        // 到界暂停（不循环 / 打点中）：停在范围末端不越过，打点会话由 tapend 收尾
        _base = Math.Min(Math.Max(raw, 0), dur);
        _startTs = Stopwatch.GetTimestamp();
        _isPlaying = false;
        lock (_chainLock)
        {
            try { if (_waveOut != null && _waveOut.PlaybackState == PlaybackState.Playing) _waveOut.Pause(); } catch { /* 设备失效 */ }
        }
        _version++;
        if (TapActive) TapEnded?.Invoke();
    }

    public void Play()
    {
        if (_follow) return; // 跟随模式：播放态由外部播放器驱动（Transport 不抢）
        if (IsPlaying) return;
        RebuildFxIfConfigChanged();
        RefreshLoopHold(); // 对位 play()：起播时按当前位置重取范围
        // 结尾再按播放 → 从头（不循环时 CurrentTime 钳在 dur，体验上是「重播」）
        if (Loop == LoopMode.Off && Duration > 0 && _base >= Duration - 1e-3)
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
        if (_follow) return; // 跟随模式：暂停态由外部播放器驱动
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
        if (_follow) return; // 跟随模式：进度由外部播放器驱动（时间轴拖动不生效，避免打架）
        _base = ClampPlayPos(t);
        _startTs = Stopwatch.GetTimestamp();
        _version++;
        RefreshLoopHold(); // 对位 seek()：定位后按新位置重取范围
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
                EngineBoot.InstallPacks();
                plan = Planner.Plan(Project, _audio);
                WarmFirstFrame(plan); // 同步重规划（MCP 写工具路径）同样预热首帧，见 WarmFirstFrame
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
    /// 重规划在途标志（EditorState 置位、换 plan 时清除）：预览泵据此**抑制 CPU 兜底渲染**。
    /// 原委（实测）：新 plan 的首帧是冷缓存（排版/字体/shader 一次成型），预览兜底渲染一帧要
    /// 0.7–1.8 秒，且整段持有 EngineRenderGate——重规划期间逐帧兜底会把规划和 Spout 泵一起拖住，
    /// 镜像断供更久 → 更多兜底，形成秒级冻结回环。新 plan 最多 1–2 秒就到，保持旧镜像帧即可。
    /// </summary>
    public volatile bool ReplanPending;

    // 预热面（长边 480）：持久复用；换尺寸时先清整帧缓存再释放旧面
    private SKBitmap? _warmBmp;
    private SKSurface? _warmSurf;
    private int _warmW = -1, _warmH = -1;

    /// <summary>
    /// 新 plan 首帧预热（性能专项）：换 plan 后的第一帧是**冷缓存**——全部条目的排版重建、
    /// 新字体装载、Skia shader 首次成型，实测预览侧首帧 0.7–1.8 秒。这些成本只依赖设计尺寸
    /// （env.W/H = plan.W/H）与内容，**与渲染 scale 无关**，所以用低分辨率（长边 480）烘一帧就能
    /// 覆盖绝大部分；像素栅格化那块留给真正的消费方（它本来就只占几毫秒）。
    /// 调用方须持 EngineRenderGate（与渲染/规划串行，本方法内部也会取一次，Monitor 可重入）。
    /// </summary>
    public void WarmFirstFrame(Plan plan)
    {
        const double warmLong = 480.0;
        var scale = Math.Min(1.0, warmLong / Math.Max(1, Math.Max(plan.W, plan.H)));
        var w = Math.Max(2, (int)Math.Round(plan.W * scale));
        var h = Math.Max(2, (int)Math.Round(plan.H * scale));
        if (_warmSurf == null || _warmW != w || _warmH != h)
        {
            lock (EngineRenderGate.Lock)
                Renderer.ClearFrameCache();
            _warmSurf?.Dispose();
            _warmBmp?.Dispose();
            _warmBmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            _warmSurf = SKSurface.Create(_warmBmp.Info, _warmBmp.GetPixels(), _warmBmp.RowBytes);
            _warmW = w;
            _warmH = h;
        }
        _warmSurf.Canvas.Clear(SKColors.Black);
        lock (EngineRenderGate.Lock)
        {
            Renderer.Frame(_warmSurf.Canvas, plan, 0, new RenderOpts { Scale = scale, Stage = true, NoPost = true, NoTrans = true });
            Renderer.ClearFrameCache(); // 预热只为成型字体/排版，不保留 480 低清整帧快照
        }
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
        RefreshLoopHold(); // 对位 replan()：换 plan 后按新切分重取冻结范围
        WarmFonts(plan); // 字体管线热身 + 缺字体按需拉（拉到后 FontFetch 回调重规划）
    }

    // ---------------- ISpoutSource（SpoutOut 泵线程读） ----------------

    Plan ISpoutSource.Plan => Plan;
    double ISpoutSource.CurrentTime => CurrentTime;
    bool ISpoutSource.IsPlaying => IsPlaying;
    long ISpoutSource.Version => Version;
    IReadOnlyList<KrcLine>? ISpoutSource.Subtitles => Subtitles;
    SubtitleConfig ISpoutSource.SubtitleConfig => SubtitleCfg;
    long ISpoutSource.SubtitleVersion => SubtitleVersion;

    public void Dispose()
    {
        TeardownAudio();
        _warmSurf?.Dispose();
        _warmSurf = null;
        _warmBmp?.Dispose();
        _warmBmp = null;
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
