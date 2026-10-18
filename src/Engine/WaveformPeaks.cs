namespace JizuraLive.Engine;

public sealed class WaveformPeaks
{
    public const int DefaultBaseWindow = 256;

    private readonly float[][] _min;
    private readonly float[][] _max;
    private readonly long _frames;

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

    public int LevelCount => _min.Length;

    public long SampleCount => _frames;

    public int WindowSize(int level) => BaseWindow << level;

    public int WindowCount(int level) => _min[level].Length / Channels;

    public static Builder CreateBuilder(int channels, int baseWindow = DefaultBaseWindow) =>
        new(channels, baseWindow);

    public int SelectLevel(long firstSample, long sampleCount, int pixelWidth)
    {
        if (sampleCount <= 0) return 0;
        if (pixelWidth <= 0) pixelWidth = 1;
        long perPixel = (sampleCount + pixelWidth - 1) / pixelWidth;
        int maxLevel = LevelCount - 1;
        int level = 0;
        while (level < maxLevel && (long)BaseWindow << level < perPixel) level++;
        return level;
    }

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

    public sealed class Builder
    {
        private readonly int _channels;
        private readonly int _baseWindow;
        private readonly List<float> _min = new();
        private readonly List<float> _max = new();
        private readonly float[] _openMin;
        private readonly float[] _openMax;
        private readonly float[] _carry;
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

        public WaveformPeaks Finish()
        {
            if (_openFrames > 0) FlushWindow();

            var count = _min.Count / _channels;
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
                int next = (count + 1) / 2;
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
