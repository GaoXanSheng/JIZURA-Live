using NAudio.Dsp;

namespace JizuraLive.Engine;

/// <summary>
/// 首版音频分析配置（契约 DOC/PV-DIRECTOR-SYSTEM.md §13.4：原采样率、窗口 2048、步长 512、Hann 窗）。
/// 为什么带 Version：窗口/步长/频带/归一化规则任一改动，旧缓存里的数值就不再等价，
/// 只有抬 Version 才能让 <see cref="Key"/> 换出新键、旧缓存不得复用（§13.4 缓存失效条件）。
/// </summary>
public readonly record struct AudioAnalysisConfig(
    int WindowSize,
    int Hop,
    float BassLowHz,
    float BassHighHz,
    float MidHighHz,
    float HighCapHz,
    float DbFloor,
    int Version)
{
    /// <summary>首版固定配置：2048/512、Hann、20–250 / 250–4000 / 4000–16000Hz、1e-5 底噪、版本 1。</summary>
    public static readonly AudioAnalysisConfig Default = new(2048, 512, 20f, 250f, 4000f, 16000f, 1e-5f, 1);

    /// <summary>
    /// 分析缓存键：内容 hash / 分析版本 / 采样率（层级与频带规则折进 Version）。
    /// 本轮只冻结键的形状；缓存、会话校验与取消由 reader 阶段按同一规则建键。
    /// </summary>
    public AudioAnalysisKey Key(string contentHash, int sampleRate, int channels)
        => new(contentHash, Version, sampleRate, channels);
}

/// <summary>分析缓存键（形状冻结，尚未建缓存）。</summary>
public readonly record struct AudioAnalysisKey(string ContentHash, int AnalysisVersion, int SampleRate, int Channels);

/// <summary>单个窗口的频带结果。Valid=false 表示序列本身没有可用数据（空序列/分析未产出），不是"静音"。</summary>
public readonly record struct AudioBands(float Rms, float Bass, float Mid, float High, bool Valid);

/// <summary>
/// 固定窗口的 rms/bass/mid/high 序列（契约 §13.4）：窗口 j 的中心时刻 = j×hop/sampleRate。
/// 查询是纯函数——同一 t 在任何预览帧率下逐位相同，不读播放位置、不依赖上一帧游标。
/// 只保存降采样结果，不保留整首 PCM（§15.2）。
/// </summary>
public sealed class AudioBandSeries
{
    private readonly float[] _rms;
    private readonly float[] _bass;
    private readonly float[] _mid;
    private readonly float[] _high;
    private readonly int _hop;

    internal AudioBandSeries(float[] rms, float[] bass, float[] mid, float[] high, int hop, int sampleRate, int configVersion, bool isComplete)
    {
        _rms = rms;
        _bass = bass;
        _mid = mid;
        _high = high;
        _hop = hop;
        SampleRate = sampleRate;
        ConfigVersion = configVersion;
        IsComplete = isComplete;
        StepSeconds = (double)hop / sampleRate;
    }

    /// <summary>窗口步长的秒数，== Hop / sampleRate。</summary>
    public double StepSeconds { get; }

    public int SampleRate { get; }

    /// <summary>产生本序列的分析规则版本：规则改变后旧序列不得与新序列混用。</summary>
    public int ConfigVersion { get; }

    /// <summary>整首是否分析完成；未完成时尾部窗口缺失，查询按现有窗口钳位（留给 reader 阶段表达"未完成"）。</summary>
    public bool IsComplete { get; }

    /// <summary>窗口数。</summary>
    public int Count => _rms.Length;

    public float[] Rms => _rms;
    public float[] Bass => _bass;
    public float[] Mid => _mid;
    public float[] High => _high;

    /// <summary>
    /// 按时刻查询：j = round(t×sampleRate/hop) 是离 t 最近的窗口中心，再在相邻两窗之间线性插值。
    /// t 早于首窗取首窗值、晚于末窗取末窗值（越界钳位）；空序列返回 Valid=false。
    /// </summary>
    public bool Sample(double timeSeconds, out AudioBands bands)
    {
        int n = _rms.Length;
        if (n == 0)
        {
            // 没有窗口时给 Valid=false，让上层回落到 base(t)，而不是把 0 当成真实静音
            bands = default;
            return false;
        }

        double f = timeSeconds * SampleRate / _hop;   // 连续窗口坐标：整数处正好落在窗口中心
        if (!double.IsFinite(f))
        {
            // 非有限时间不该被当成"取首窗"这种看似有效的数值
            bands = default;
            return false;
        }

        if (f <= 0) f = 0;
        else if (f >= n - 1) f = n - 1;               // 先钳位，顺带避免超大 t 转 int 溢出

        int j = (int)Math.Round(f);
        double frac = f - j;                          // 相对窗口中心的偏移，用于相邻两窗插值
        int k = frac > 0 ? Math.Min(j + 1, n - 1) : Math.Max(j - 1, 0);

        float t = (float)Math.Abs(frac);
        bands = new AudioBands(
            Lerp(_rms[j], _rms[k], t),
            Lerp(_bass[j], _bass[k], t),
            Lerp(_mid[j], _mid[k], t),
            Lerp(_high[j], _high[k], t),
            true);
        return true;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

/// <summary>
/// 纯函数音频分析器：float PCM 进、序列出。不碰文件 IO、不接播放器、不从渲染帧临时采样（§13.4）。
/// </summary>
public static class AudioAnalyzer
{
    /// <summary>
    /// 切窗、加 Hann、FFT、按频带汇总，产出固定窗口的 rms/bass/mid/high 序列。
    /// 窗口 j 覆盖 [j×hop − WindowSize/2, j×hop + WindowSize/2)，越界样本补 0，中心时刻 = j×hop/sampleRate。
    /// </summary>
    public static AudioBandSeries Analyze(ReadOnlySpan<float> interleaved, int channels, int sampleRate, AudioAnalysisConfig config = default)
    {
        // 默认参数是零值结构体（Hop=0 会除零）：省略 config 时按首版固定配置
        if (config.WindowSize <= 0 || config.Hop <= 0) config = AudioAnalysisConfig.Default;
        if (!IsPowerOfTwo(config.WindowSize))
            throw new ArgumentException("WindowSize 必须是 2 的幂：NAudio FFT 只接受 2^m 个点", nameof(config));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));

        int frames = interleaved.Length / channels;   // 交错数据尾部不足一帧的样本忽略
        if (frames == 0)
            return new AudioBandSeries([], [], [], [], config.Hop, sampleRate, config.Version, true);

        int w = config.WindowSize;
        int half = w / 2;
        int hop = config.Hop;

        // 末窗下标：最小满足 last×hop + half >= frames，保证最后一个样本也落在某个窗口内
        int last = frames <= half ? 0 : (frames - half + hop - 1) / hop;
        int count = last + 1;

        var hann = new float[w];
        double windowEnergy = 0;
        for (int i = 0; i < w; i++)
        {
            double v = FastFourierTransform.HannWindow(i, w);
            hann[i] = (float)v;
            windowEnergy += v * v;                    // Σw²：单边谱功率按窗能量归一，常数才与窗长无关
        }

        int m = Log2Exact(w);
        var buffer = new NAudio.Dsp.Complex[w];        // 与 FastFourierTransform.FFT 同一类型，逐窗复用
        float highCap = Math.Min(config.HighCapHz, sampleRate * 0.5f);   // 空频带（cap<=lo）是有效 0

        var rms = new float[count];
        var bass = new float[count];
        var mid = new float[count];
        var high = new float[count];

        for (int j = 0; j < count; j++)
        {
            int start = j * hop - half;               // 窗口左端样本下标，可为负 → 前段补 0
            double rmsPower = 0, bassPower = 0, midPower = 0, highPower = 0;

            for (int c = 0; c < channels; c++)
            {
                double timePower = 0;
                for (int i = 0; i < w; i++)
                {
                    long idx = (long)start + i;
                    float x = idx >= 0 && idx < frames ? interleaved[(int)(idx * channels + c)] : 0f;
                    float y = x * hann[i];
                    timePower += (double)y * y;
                    // NAudio.Dsp.Complex 只有 X/Y 两个公开字段（3.1.0 无 (float,float) 构造函数）
                    buffer[i].X = y;
                    buffer[i].Y = 0f;
                }

                // 每声道各自算功率再平均：先相加会让反相立体声相消（§13.4）
                rmsPower += timePower / windowEnergy;

                // 任务书/契约固定 FFT(false,…)：NAudio 只在 forward=true 时做 1/N 缩放（已核对 NAudio.Core 3.1.0 反编译），
                // false 是同一内核的无缩放逆变换；实输入下与无缩放正变换共轭对称、幅度谱相同，
                // 所以功率常数按无缩放单边谱 2|V|²/(W·Σw²) 定标，并由已知幅值正弦测试钉住。
                FastFourierTransform.FFT(false, m, buffer);

                for (int k = 1; k < w / 2; k++)       // 只扫单边中间 bin：DC 与 Nyquist 不在任何频带内，也不该乘 2
                {
                    float freq = (float)(k * (double)sampleRate / w);
                    if (freq < config.BassLowHz || freq >= highCap) continue;
                    double re = buffer[k].X, im = buffer[k].Y;
                    double p = 2.0 * (re * re + im * im) / (w * windowEnergy);
                    if (freq < config.BassHighHz) bassPower += p;       // 边界 bin 只计入一侧
                    else if (freq < config.MidHighHz) midPower += p;
                    else highPower += p;
                }
            }

            // 累加出来的是**均方**（功率），而 −60..0 dBFS 是**幅值**口径：0 dBFS 是满幅正弦的峰值，
            // 该正弦的 RMS = 1/√2、因此读 −3.01dBFS（20log10(0.7071)）。进 dB 映射前必须先开方，
            // 否则满幅正弦会落到 −6.02dBFS（映射成 0.90），整条参数链偏低 3dB。
            double inv = 1.0 / channels;
            rms[j] = ToLevel(Math.Sqrt(rmsPower * inv), config.DbFloor);
            bass[j] = ToLevel(Math.Sqrt(bassPower * inv), config.DbFloor);
            mid[j] = ToLevel(Math.Sqrt(midPower * inv), config.DbFloor);
            high[j] = ToLevel(Math.Sqrt(highPower * inv), config.DbFloor);
        }

        return new AudioBandSeries(rms, bass, mid, high, hop, sampleRate, config.Version, true);
    }

    /// <summary>
    /// 20log10(max(x, floor)) 的 −60..0 dBFS → 0..1 映射（§13.4；首版产品约定，不是响度标准）。
    /// <paramref name="power"/> 收的是**幅值口径**（RMS，0 dBFS = 满幅正弦峰值），不是均方——
    /// 均方要在调用点先开方（满幅正弦 → −3.01dBFS，而不是 −6.02dBFS）。
    /// 非有限值按底噪处理：NaN 一旦进入渲染会污染整条参数链。
    /// </summary>
    private static float ToLevel(double power, float dbFloor)
    {
        double x = double.IsFinite(power) && power > dbFloor ? power : dbFloor;
        return (float)Math.Clamp((20.0 * Math.Log10(x) + 60.0) / 60.0, 0.0, 1.0);
    }

    private static bool IsPowerOfTwo(int value) => value > 0 && (value & (value - 1)) == 0;

    private static int Log2Exact(int powerOfTwo)
    {
        int m = 0;
        while ((1 << m) < powerOfTwo) m++;
        return m;
    }
}
