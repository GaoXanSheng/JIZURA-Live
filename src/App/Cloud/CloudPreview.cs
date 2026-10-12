using System.Windows;
using JizuraLive.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace JizuraLive.App.Cloud;

/// <summary>
/// 云音乐试听播放链（独立于工程预览的那条链）：MediaFoundationReader(本地缓存文件) → 声道归一 →
/// 48k 重采样 → 音量 → WaveOut。工程预览链和它各用一台 WaveOut，互不干扰；谁出声由面板仲裁
/// （试听开始先暂停工程预览）。
///
/// 纪律照抄 PreviewEngine 那份（真实踩过的坑）：先定位再 Init、补零包装挡 EOF、回调线程异常要收住
/// —— 否则设备静默停机，表现为"以为在播、其实没声"。
/// </summary>
public sealed class CloudPreview : IDisposable
{
    private const int OutRate = 48000;

    private readonly object _lock = new();
    private WaveOut? _waveOut;
    private MediaFoundationReader? _reader;
    private VolumeSampleProvider? _gain;
    private int _seq;                 // 换代：在途建链作废
    private double _volume = 0.85;
    private double _durationSec;

    /// <summary>设备/解码意外停机（NAudio 的 PlaybackStopped 带异常时才有这个信号；正常放完不带位置，
    /// 由调用方按「原本在播 + 现在停了 + 位置到底」判断自然播完）。</summary>
    public event Action? Failed;

    public bool HasTrack => _reader != null;
    public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;

    /// <summary>当前播放位置（秒）。</summary>
    public double Position
    {
        get
        {
            lock (_lock) return _reader?.CurrentTime.TotalSeconds ?? 0;
        }
    }

    public double Duration => Volatile.Read(ref _durationSec);

    /// <summary>
    /// 解码到头了。注意：链上套了 ZeroPad（挡 WDL 重采样在源 EOF 时越界），源放完它就一直补零，
    /// 设备不会自己停、PlaybackStopped 也不会来——所以「放完了」只能看读数位置到没到头。
    /// </summary>
    public bool AtEnd
    {
        get
        {
            var dur = Duration;
            return dur > 0 && Position >= dur - 0.15;
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            var g = _gain;
            if (g != null) g.Volume = (float)_volume;
        }
    }

    /// <summary>放一首本地文件（后台建链，回 UI 挂链）。失败返回 false，调用方负责提示。</summary>
    public async Task<bool> PlayAsync(string path, double startSec = 0)
    {
        Teardown(); // 先拆旧链（内部换代：旧链回调线程的收尾异常就此作废，见 BuildChain 的守卫）
        var seq = Interlocked.Increment(ref _seq);
        var chain = await Task.Run(() => BuildChain(path, Math.Max(0, startSec), seq));
        if (chain == null) return false;
        if (seq != Volatile.Read(ref _seq)) // 期间又换歌了
        {
            chain.Dispose();
            return false;
        }
        Install(seq, chain);
        return true;
    }

    private sealed class Chain(MediaFoundationReader reader, WaveOut output, VolumeSampleProvider gain) : IDisposable
    {
        public MediaFoundationReader Reader { get; } = reader;
        public WaveOut Out { get; } = output;
        public VolumeSampleProvider Gain { get; } = gain;

        public void Dispose()
        {
            try { Out.Stop(); } catch { /* 已失效 */ }
            try { Out.Dispose(); } catch { /* 已失效 */ }
            try { Reader.Dispose(); } catch { /* 已失效 */ }
        }
    }

    private Chain? BuildChain(string path, double startSec, int seq)
    {
        MediaFoundationReader? reader = null;
        WaveOut? wo = null;
        try
        {
            reader = new MediaFoundationReader(path);
            ISampleProvider node = new ZeroPadSampleProvider(reader.ToSampleProvider());
            if (node.WaveFormat.Channels == 1) node = new MonoToStereoSampleProvider(node);
            else if (node.WaveFormat.Channels > 2) node = new MultiplexingSampleProvider(new[] { node }, 2);
            if (node.WaveFormat.SampleRate != OutRate) node = new WdlResamplingSampleProvider(node, OutRate);

            // 先定位再 Init：WaveOut 建缓冲时就开始从链上读帧，定位晚了首块内容就错位
            var total = reader.TotalTime.TotalSeconds;
            if (startSec > 0.05) reader.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(startSec, 0, total));

            var gain = new VolumeSampleProvider(node) { Volume = (float)_volume };
            // NAudio 3.x：WaveOutEvent 更名 WaveOut，延迟参数改为 BufferMilliseconds×NumberOfBuffers
            var wo2 = new WaveOut { BufferMilliseconds = 50, NumberOfBuffers = 3 };
            wo2.Init(gain);
            wo2.PlaybackStopped += (_, se) =>
            {
                if (se.Exception == null) return; // 我方 Stop/拆链的正常停机
                // 换代守卫：waveOutReset/close 期间回调线程的 waveOutWrite 会报 InvalidParameter，
                // 这条异常属于已被替换的旧链。上报它会经 OnDeviceFailed 把刚挂上的新链一起拆掉，
                // 并让新链的时长停在 0（AtEnd 要求 dur>0 → 播完不换歌、时长显示成 00:00）。
                // 判定放在真正上报的那一刻（含派发回 UI 之后）：换歌间隙里排队中的旧异常也不误伤新链。
                void Report()
                {
                    if (seq != Volatile.Read(ref _seq)) return;
                    Log.Warn("[Cloud] 试听输出意外停止：" + se.Exception.Message);
                    Failed?.Invoke();
                }

                var d = Application.Current?.Dispatcher;
                if (d == null || d.CheckAccess()) Report();
                else d.BeginInvoke(Report);
            };
            wo = wo2;
            return new Chain(reader, wo2, gain);
        }
        catch (Exception ex)
        {
            Log.Warn("[Cloud] 试听链装配失败：" + ex.Message);
            try { reader?.Dispose(); } catch { /* 已失效 */ }
            try { wo?.Dispose(); } catch { /* 已失效 */ }
            return null;
        }
    }

    private void Install(int seq, Chain chain)
    {
        if (seq != Volatile.Read(ref _seq))
        {
            chain.Dispose();
            return;
        }
        lock (_lock)
        {
            _reader = chain.Reader;
            _waveOut = chain.Out;
            _gain = chain.Gain;
            // 时长与 reader 同锁写入：只有真正挂上的链才有已知时长。写在 BuildChain 里会被
            // 建链途中插入的 Teardown 清零，留下「reader 在放、Duration=0」的错位状态
            //（AtEnd 永不成立 → 播完不换歌；显示成 已播 x / 00:00）。
            Volatile.Write(ref _durationSec, chain.Reader.TotalTime.TotalSeconds);
        }
        chain.Out.Play();
    }

    public void Pause()
    {
        var wo = _waveOut;
        if (wo == null) return;
        try { wo.Pause(); } catch (Exception ex) { Log.Debug("[Cloud] 试听暂停失败：" + ex.Message); }
    }

    public void Resume()
    {
        var wo = _waveOut;
        if (wo == null) return;
        try { wo.Play(); } catch (Exception ex) { Log.Debug("[Cloud] 试听续播失败：" + ex.Message); }
    }

    public void Toggle()
    {
        if (IsPlaying) Pause();
        else Resume();
    }

    /// <summary>拖进度：直接重定位解码器（文件在本地，零成本）。</summary>
    public void Seek(double sec)
    {
        lock (_lock)
        {
            if (_reader == null) return;
            try { _reader.CurrentTime = TimeSpan.FromSeconds(Math.Clamp(sec, 0, Duration)); }
            catch (Exception ex) { Log.Debug("[Cloud] 试听定位失败：" + ex.Message); }
        }
    }

    /// <summary>停掉并拆链（换歌、关面板、退出）。</summary>
    public void Teardown()
    {
        Interlocked.Increment(ref _seq); // 换代：在途建链、以及被拆链的回调异常（见 BuildChain 守卫）一并作废
        Chain? toDispose = null;
        lock (_lock)
        {
            if (_reader != null && _waveOut != null && _gain != null)
                toDispose = new Chain(_reader, _waveOut, _gain);
            _reader = null;
            _waveOut = null;
            _gain = null;
            Volatile.Write(ref _durationSec, 0);
        }
        toDispose?.Dispose();
    }

    public void Dispose()
    {
        Interlocked.Increment(ref _seq);
        Teardown();
    }
}
