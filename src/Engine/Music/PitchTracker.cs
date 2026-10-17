namespace JizuraLive.Engine.Music;

public static class PitchTracker
{
    private const float EnergyGate = 0.04f;
    private const float ConfThreshold = 0.45f;
    private const float Fmin = 70f;
    private const float Fmax = 1000f;

    public static (float[] Pitch, int FrameCount) Extract(
        ReadOnlySpan<float> mono, int sampleRate, ReadOnlySpan<float> energy, int hop)
    {
        var n = energy.Length;
        var pitch = new float[n];
        if (n == 0 || sampleRate <= 0 || hop <= 0) return (pitch, n);

        var win = 2 * hop;
        var minLag = Math.Max(2, (int)Math.Round(sampleRate / Fmax));
        var maxLag = Math.Min(win - 4, (int)Math.Round(sampleRate / Fmin));
        if (maxLag <= minLag + 2) return (pitch, n);

        var wbuf = new float[win];
        var cc = new double[maxLag + 2];

        var gate = EnergyGate;
        var sorted = new float[n];
        energy.CopyTo(sorted);
        Array.Sort(sorted);
        var p95 = sorted[Math.Min(n - 1, (int)(0.95 * (n - 1)))];
        if (p95 > 0) gate = p95 * EnergyGate;

        for (var f = 0; f < n; f++)
        {
            if (energy[f] < gate) continue;
            var start = f * hop;
            if (start + win > mono.Length) start = mono.Length - win;
            if (start < 0) break;

            double mean = 0;
            for (var i = 0; i < win; i++) mean += mono[start + i];
            mean /= win;
            double e0 = 0;
            for (var i = 0; i < win; i++)
            {
                var v = mono[start + i] - (float)mean;
                wbuf[i] = v;
                e0 += v * (double)v;
            }

            if (e0 < 1e-9) continue;

            double bestC = 0;
            var bestLag = -1;
            for (var lag = minLag; lag <= maxLag; lag++)
            {
                double dot = 0, e1 = 0;
                for (var i = lag; i < win; i++)
                {
                    dot += wbuf[i] * (double)wbuf[i - lag];
                    e1 += wbuf[i - lag] * (double)wbuf[i - lag];
                }

                var c = e1 > 1e-12 ? dot / Math.Sqrt(e0 * e1) : 0;
                cc[lag] = c;
                if (c > bestC)
                {
                    bestC = c;
                    bestLag = lag;
                }
            }

            if (bestLag < 0 || bestC < ConfThreshold) continue;

            var chosen = bestLag;
            var thresh = bestC * 0.88;
            for (var lag = minLag + 1; lag < bestLag; lag++)
                if (cc[lag] >= thresh && cc[lag] >= cc[lag - 1] && cc[lag] >= cc[lag + 1])
                {
                    chosen = lag;
                    break;
                }

            double lagF = chosen;
            if (chosen > minLag && chosen < maxLag)
            {
                double a = cc[chosen - 1], b = cc[chosen], c2 = cc[chosen + 1];
                var d = a - 2 * b + c2;
                if (d != 0) lagF = chosen + 0.5 * (a - c2) / d;
            }

            pitch[f] = (float)(sampleRate / lagF);
        }

        return (MedianSmooth(pitch), n);
    }

    private static float[] MedianSmooth(float[] pitch)
    {
        var n = pitch.Length;
        var smoothed = new float[n];
        for (var f = 0; f < n; f++)
        {
            if (pitch[f] <= 0) continue;
            var a = f > 0 ? pitch[f - 1] : pitch[f];
            var b = pitch[f];
            var c3 = f < n - 1 ? pitch[f + 1] : pitch[f];
            if (a > 0 && c3 > 0)
            {
                if (a > b) (a, b) = (b, a);
                if (b > c3) (b, c3) = (c3, b);
                if (a > b) (a, b) = (b, a);
                smoothed[f] = b;
            }
            else
            {
                smoothed[f] = b;
            }
        }

        return smoothed;
    }
}
