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
    /// 建一个分块构建器（见 <see cref="Builder"/>）：波形装载不必先把整曲 PCM 攒下来。
    /// </summary>
    public static Builder CreateBuilder(int channels, int baseWindow = DefaultBaseWindow) =>
        new(channels, baseWindow);

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

    /// <summary>
    /// 分块构建入口：与 <see cref="Build"/> **逐值一致**（同一套窗口边界、同一套两两合并），
    /// 但内存 O(输出)，不要求整曲 PCM 驻留（§15.2）。尾部不足一帧的样本与 <see cref="Build"/> 一样丢弃。
    /// </summary>
    public sealed class Builder
    {
        private readonly int _channels;
        private readonly int _baseWindow;
        private readonly List<float> _min = new();   // 窗口主序：i * channels + c
        private readonly List<float> _max = new();
        private readonly float[] _openMin;           // 还没满一窗的当前窗极值
        private readonly float[] _openMax;
        private readonly float[] _carry;             // 上一次 Feed 结尾不足一帧的样本
        private int _openFrames;
        private int _carryCount;
        private long _frames;

        internal Builder(int channels, int baseWindow)
        {
            if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
            if (baseWindow <= 0) throw new ArgumentOutOfRangeException(nameof(baseWindow));

            _channels = channels;
            _baseWindow = baseWindow;
            _openMin = new float[channels];
            _openMax = new float[channels];
            _carry = new float[channels];
            ResetOpen();
        }

        /// <summary>喂一段交错 PCM。任意长度：可以跨帧、跨窗口、跨块边界切分。</summary>
        public void Feed(ReadOnlySpan<float> interleaved)
        {
            var i = 0;

            if (_carryCount > 0)
            {
                while (_carryCount < _channels && i < interleaved.Length) _carry[_carryCount++] = interleaved[i++];
                if (_carryCount < _channels) return;

                AddFrame(_carry);
                _carryCount = 0;
            }

            while (interleaved.Length - i >= _channels)
            {
                AddFrame(interleaved.Slice(i, _channels));
                i += _channels;
            }

            _carryCount = interleaved.Length - i;
            for (var k = 0; k < _carryCount; k++) _carry[k] = interleaved[i + k];
        }

        /// <summary>出末窗、交出多级极值。之后本实例不再可用。</summary>
        public WaveformPeaks Finish()
        {
            if (_openFrames > 0) FlushWindow();

            var count = _min.Count / _channels;
            // 窗口主序 → Build 的布局（下标 = channel × count + i）
            var min = new List<float[]> { new float[_channels * count] };
            var max = new List<float[]> { new float[_channels * count] };
            for (var c = 0; c < _channels; c++)
            {
                for (var i = 0; i < count; i++)
                {
                    min[0][c * count + i] = _min[i * _channels + c];
                    max[0][c * count + i] = _max[i * _channels + c];
                }
            }

            while (count > 1)
            {
                int next = (count + 1) / 2;                 // 奇数时末窗只并自己，尾段不丢
                float[] prevMin = min[^1], prevMax = max[^1];
                var nextMin = new float[_channels * next];
                var nextMax = new float[_channels * next];
                for (int c = 0; c < _channels; c++)
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

            return new WaveformPeaks([.. min], [.. max], _frames, _channels, _baseWindow);
        }

        private void AddFrame(ReadOnlySpan<float> frame)
        {
            for (var c = 0; c < _channels; c++)
            {
                var v = frame[c];
                if (v < _openMin[c]) _openMin[c] = v;
                if (v > _openMax[c]) _openMax[c] = v;
            }

            _frames++;
            if (++_openFrames >= _baseWindow) FlushWindow();
        }

        private void FlushWindow()
        {
            _min.AddRange(_openMin);
            _max.AddRange(_openMax);
            _openFrames = 0;
            ResetOpen();
        }

        private void ResetOpen()
        {
            Array.Fill(_openMin, float.PositiveInfinity);
            Array.Fill(_openMax, float.NegativeInfinity);
        }
    }
}
