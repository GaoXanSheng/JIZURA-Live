namespace JizuraLive.Engine;

/// <summary>
/// 多级 min/max 波形（契约 §13.4）：level0 每 <see cref="DefaultBaseWindow"/> 个样本、每声道取极值，
/// 逐级两两合并成 512/1024/…；按视区像素宽选择层级，只读可见段。
/// 存极值而不是抽点：抽点会漏掉落在采样点之间的尖峰（§15.2 ALG-EXACT）。
/// 只保存降采样结果，不保留整首 PCM（§15.2）。
/// 为什么改动基础窗口或层级规则必须抬 AudioAnalysisConfig.Version：缓存失效条件是
/// 内容 hash / 解码版本 / 采样率 / 层级规则，旧规则算出的峰值与新规则不可比（§13.4）。
/// </summary>
public sealed class WaveformPeaks
{
    public const int DefaultBaseWindow = 256;

    private readonly float[][] _min;   // [level][channel * WindowCount(level) + i]
    private readonly float[][] _max;
    private readonly long _frames;     // 每声道样本数

    private WaveformPeaks(float[][] min, float[][] max, long frames, int channels, int baseWindow)
    {
        _min = min;
        _max = max;
        _frames = frames;
        Channels = channels;
        BaseWindow = baseWindow;
    }

    public int Channels { get; }

    public int BaseWindow { get; }

    /// <summary>层级数：level0 起，逐级两两合并到只剩一个窗口。</summary>
    public int LevelCount => _min.Length;

    /// <summary>每声道样本数（交错数据尾部不足一帧的样本不计）。</summary>
    public long SampleCount => _frames;

    /// <summary>level 的窗口覆盖样本数：baseWindow × 2^level。</summary>
    public int WindowSize(int level) => BaseWindow << level;

    /// <summary>level 的窗口数（末窗按实际样本数，不补齐）。</summary>
    public int WindowCount(int level) => _min[level].Length / Channels;

    /// <summary>level 的 min 平铺数组：下标 = channel × WindowCount(level) + i。</summary>
    public float[] Min(int level) => _min[level];

    /// <summary>level 的 max 平铺数组：下标 = channel × WindowCount(level) + i。</summary>
    public float[] Max(int level) => _max[level];

    /// <summary>
    /// 从交错 PCM 建多级极值。level0 每 baseWindow 个样本取一次 min/max；
    /// level n 由相邻两个 level n−1 窗口两两合并（min 取 min、max 取 max）。
    /// </summary>
    public static WaveformPeaks Build(ReadOnlySpan<float> interleaved, int channels, int baseWindow = DefaultBaseWindow)
    {
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        if (baseWindow <= 0) throw new ArgumentOutOfRangeException(nameof(baseWindow));

        long frames = interleaved.Length / channels;
        int count = (int)((frames + baseWindow - 1) / baseWindow);

        var min = new List<float[]> { new float[channels * count] };
        var max = new List<float[]> { new float[channels * count] };
        for (int c = 0; c < channels; c++)
        {
            for (int i = 0; i < count; i++)
            {
                int from = i * baseWindow;
                int to = (int)Math.Min((long)from + baseWindow, frames);   // 末窗按实际样本数，不越界
                float mn = float.PositiveInfinity, mx = float.NegativeInfinity;
                for (int s = from; s < to; s++)
                {
                    float v = interleaved[s * channels + c];
                    if (v < mn) mn = v;
                    if (v > mx) mx = v;
                }
                min[0][c * count + i] = mn;
                max[0][c * count + i] = mx;
            }
        }

        while (count > 1)
        {
            int next = (count + 1) / 2;                 // 奇数时末窗只并自己，尾段不丢
            float[] prevMin = min[^1], prevMax = max[^1];
            var nextMin = new float[channels * next];
            var nextMax = new float[channels * next];
            for (int c = 0; c < channels; c++)
            {
                for (int i = 0; i < next; i++)
                {
                    int a = c * count + 2 * i;
                    int b = c * count + Math.Min(2 * i + 1, count - 1);
                    nextMin[c * next + i] = Math.Min(prevMin[a], prevMin[b]);
                    nextMax[c * next + i] = Math.Max(prevMax[a], prevMax[b]);
                }
            }
            min.Add(nextMin);
            max.Add(nextMax);
            count = next;
        }

        return new WaveformPeaks([.. min], [.. max], frames, channels, baseWindow);
    }

    /// <summary>
    /// 选层级：取窗口覆盖样本数不小于 sampleCount/pixelWidth 的最细层级（视区越窄选越粗），
    /// pixelWidth >= sampleCount 时是 level0。层级不足时退到最粗可用层级。
    /// firstSample 只用于固定调用形状（可见段起点不参与选级：选级只看可见样本数）。
    /// </summary>
    public int SelectLevel(long firstSample, long sampleCount, int pixelWidth)
    {
        if (sampleCount <= 0) return 0;
        if (pixelWidth <= 0) pixelWidth = 1;                                  // 宽度 0/负：当作 1 像素，避免除零
        long perPixel = (sampleCount + pixelWidth - 1) / pixelWidth;          // 向上取整：窗口不小于它
        int maxLevel = LevelCount - 1;
        int level = 0;
        while (level < maxLevel && (long)BaseWindow << level < perPixel) level++;
        return level;
    }

    /// <summary>
    /// 读可见段的 min/max：只聚合与 [firstSample, firstSample+sampleCount) 相交的窗口（§13.4「波形只读取可见段」）。
    /// 越界范围钳到有效区间；钳位后为空、层级/声道非法或没有数据时返回 false。
    /// </summary>
    public bool TryRead(int level, int channel, long firstSample, long sampleCount, out float min, out float max)
    {
        min = 0f;
        max = 0f;
        if (level < 0 || level >= LevelCount || channel < 0 || channel >= Channels) return false;

        int count = WindowCount(level);
        if (count <= 0) return false;

        long lo = Math.Max(0L, firstSample);
        long hi = sampleCount > _frames ? _frames : Math.Min(_frames, lo + sampleCount);
        if (hi <= lo) return false;

        int size = WindowSize(level);
        int first = (int)(lo / size);
        int last = (int)((hi - 1) / size);
        float[] mins = _min[level], maxs = _max[level];
        float mn = float.PositiveInfinity, mx = float.NegativeInfinity;
        for (int i = first; i <= last; i++)
        {
            int idx = channel * count + i;
            if (mins[idx] < mn) mn = mins[idx];
            if (maxs[idx] > mx) mx = maxs[idx];
        }
        min = mn;
        max = mx;
        return true;
    }
}
