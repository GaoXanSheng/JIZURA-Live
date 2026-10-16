namespace JizuraLive.Engine;

public sealed class Section
{
    public double Start;
    public double End;
    public string Kind = "intro";
}

public sealed class BeatMap
{
    public double Bpm;
    public List<double> Beats = new();
    public List<Section> Sections = new();
    public List<double> Peaks = new();
}

public static class BeatAnalysis
{
    public static BeatMap Analyze(AudioAnalysis analysis, double durationSeconds)
    {
        if (analysis == null || analysis.Count == 0 || durationSeconds <= 0 || analysis.StepSeconds <= 0)
        {
            var empty = new BeatMap { Bpm = 0 };
            if (durationSeconds > 0)
                empty.Sections.Add(new Section { Start = 0, End = durationSeconds, Kind = "intro" });
            return empty;
        }

        double duration = Math.Min(durationSeconds, analysis.Count * analysis.StepSeconds);
        if (duration <= 0)
            return new BeatMap { Bpm = 0 };

        double dt = analysis.StepSeconds;
        int n = Math.Min(analysis.Count, (int)Math.Ceiling(duration / dt));

        float maxRms = 0f;
        for (int i = 0; i < n; i++)
            if (analysis.Rms[i] > maxRms) maxRms = analysis.Rms[i];

        if (maxRms < 1e-4f)
        {
            return new BeatMap
            {
                Bpm = 0,
                Sections = new List<Section> { new Section { Start = 0, End = duration, Kind = "intro" } }
            };
        }

        float[] flux = new float[n];
        flux[0] = Math.Max(0f, analysis.Rms[0]);
        for (int i = 1; i < n; i++)
        {
            float dRms = Math.Max(0f, analysis.Rms[i] - analysis.Rms[i - 1]);
            float dBass = (analysis.Bass != null && analysis.Bass.Length > i) ? Math.Max(0f, analysis.Bass[i] - analysis.Bass[i - 1]) : 0f;
            float dMid = (analysis.Mid != null && analysis.Mid.Length > i) ? Math.Max(0f, analysis.Mid[i] - analysis.Mid[i - 1]) : 0f;
            float dHigh = (analysis.High != null && analysis.High.Length > i) ? Math.Max(0f, analysis.High[i] - analysis.High[i - 1]) : 0f;
            flux[i] = dRms + 1.2f * dBass + 0.4f * dMid + 0.2f * dHigh;
        }

        double bpm = 0;
        double beatPeriod = 0;
        int minLag = Math.Max(2, (int)Math.Floor(60.0 / (180.0 * dt)));
        int maxLag = (int)Math.Ceiling(60.0 / (60.0 * dt));
        if (maxLag >= n - 2) maxLag = n - 3;

        if (minLag < maxLag)
        {
            double sumFlux = 0;
            for (int i = 0; i < n; i++) sumFlux += flux[i];
            double meanFlux = sumFlux / n;

            double[] r = new double[maxLag + 2];
            for (int lag = minLag - 1; lag <= maxLag + 1 && lag < n; lag++)
            {
                double num = 0, denA = 0, denB = 0;
                int limit = n - lag;
                for (int i = 0; i < limit; i++)
                {
                    double a = flux[i] - meanFlux;
                    double b = flux[i + lag] - meanFlux;
                    num += a * b;
                    denA += a * a;
                    denB += b * b;
                }
                double den = Math.Sqrt(denA * denB);
                r[lag] = den > 1e-12 ? (num / den) : 0.0;
            }

            int bestLag = -1;
            double bestR = 0;
            for (int lag = minLag; lag <= maxLag; lag++)
            {
                if (r[lag] > r[lag - 1] && r[lag] > r[lag + 1] && r[lag] > 0.15)
                {
                    if (r[lag] > bestR)
                    {
                        bestR = r[lag];
                        bestLag = lag;
                    }
                }
            }

            if (bestLag > 0)
            {
                int halfLag = (int)Math.Round(bestLag / 2.0);
                if (halfLag >= minLag)
                {
                    int checkStart = Math.Max(minLag, halfLag - 2);
                    int checkEnd = Math.Min(maxLag, halfLag + 2);
                    int localHalf = -1;
                    double localHalfR = 0;
                    for (int l = checkStart; l <= checkEnd; l++)
                    {
                        if (r[l] > r[l - 1] && r[l] > r[l + 1] && r[l] > localHalfR)
                        {
                            localHalfR = r[l];
                            localHalf = l;
                        }
                    }
                    if (localHalf > 0 && localHalfR >= 0.55 * bestR)
                    {
                        bestLag = localHalf;
                        bestR = localHalfR;
                    }
                }

                double y0 = r[bestLag - 1];
                double y1 = r[bestLag];
                double y2 = r[bestLag + 1];
                double denom = 2.0 * (y0 - 2.0 * y1 + y2);
                double delta = Math.Abs(denom) > 1e-12 ? (y0 - y2) / denom : 0.0;
                delta = Math.Clamp(delta, -0.5, 0.5);

                double refinedLag = bestLag + delta;
                beatPeriod = refinedLag * dt;
                bpm = 60.0 / beatPeriod;
            }
        }

        var beats = new List<double>();
        if (bpm >= 59.0 && bpm <= 185.0 && beatPeriod > 0)
        {
            int phaseSteps = 100;
            double stepPhi = beatPeriod / phaseSteps;
            double bestScore = -1.0;
            double bestPhi = 0.0;

            for (int s = 0; s < phaseSteps; s++)
            {
                double candPhi = s * stepPhi;
                double score = 0;
                for (double t = candPhi; t < duration; t += beatPeriod)
                {
                    double idx = t / dt;
                    int i0 = (int)idx;
                    if (i0 >= 0 && i0 < n)
                    {
                        float f0 = flux[i0];
                        float f1 = (i0 + 1 < n) ? flux[i0 + 1] : f0;
                        double frac = idx - i0;
                        score += (1.0 - frac) * f0 + frac * f1;
                    }
                }
                if (score > bestScore)
                {
                    bestScore = score;
                    bestPhi = candPhi;
                }
            }

            double bt = bestPhi;
            while (bt - beatPeriod >= 0) bt -= beatPeriod;
            while (bt < duration)
            {
                if (bt >= 0)
                    beats.Add(Math.Round(bt, 6));
                bt += beatPeriod;
            }
        }
        else
        {
            bpm = 0;
        }

        var cuts = new List<double> { 0.0 };
        int w = Math.Max(3, (int)Math.Round(1.5 / dt));
        float minR = float.MaxValue, maxR = float.MinValue;
        for (int i = 0; i < n; i++)
        {
            if (analysis.Rms[i] < minR) minR = analysis.Rms[i];
            if (analysis.Rms[i] > maxR) maxR = analysis.Rms[i];
        }
        float rng = maxR - minR;

        if (duration >= 4.0 && n >= 2 * w && rng >= 0.10f)
        {
            int stride = Math.Max(1, (int)Math.Round(0.05 / dt));
            var diffs = new List<(int frame, double diff)>();

            for (int i = w; i <= n - w; i += stride)
            {
                float mLeft = FastMedian(analysis.Rms, i - w, w);
                float mRight = FastMedian(analysis.Rms, i, w);
                diffs.Add((i, Math.Abs(mRight - mLeft)));
            }

            double threshold = Math.Max(0.12, rng * 0.25);
            var candidateCuts = new List<(double time, double diff)>();

            int curStart = -1;
            double curMax = 0;
            for (int j = 0; j < diffs.Count; j++)
            {
                if (diffs[j].diff >= threshold)
                {
                    if (curStart < 0) curStart = diffs[j].frame;
                    if (diffs[j].diff > curMax) curMax = diffs[j].diff;
                }
                else
                {
                    if (curStart >= 0)
                    {
                        int midFrame = (curStart + diffs[j - 1].frame) / 2;
                        candidateCuts.Add((midFrame * dt, curMax));
                        curStart = -1;
                        curMax = 0;
                    }
                }
            }
            if (curStart >= 0)
            {
                int midFrame = (curStart + diffs[^1].frame) / 2;
                candidateCuts.Add((midFrame * dt, curMax));
            }

            candidateCuts.Sort((a, b) => b.diff.CompareTo(a.diff));
            double minSectionSec = Math.Min(3.0, duration / 3.0);
            var acceptedTimes = new List<double>();
            foreach (var cut in candidateCuts)
            {
                if (cut.time < minSectionSec || cut.time > duration - minSectionSec) continue;
                bool tooClose = false;
                foreach (var acc in acceptedTimes)
                {
                    if (Math.Abs(cut.time - acc) < minSectionSec)
                    {
                        tooClose = true;
                        break;
                    }
                }
                if (!tooClose) acceptedTimes.Add(cut.time);
            }

            foreach (var rawT in acceptedTimes)
            {
                double snapped = rawT;
                if (beats.Count > 0 && bpm > 0)
                {
                    double bestDist = double.MaxValue;
                    double bestB = rawT;
                    foreach (var b in beats)
                    {
                        double dist = Math.Abs(b - rawT);
                        if (dist < bestDist)
                        {
                            bestDist = dist;
                            bestB = b;
                        }
                    }
                    if (bestDist <= 60.0 / bpm) snapped = bestB;
                }
                cuts.Add(snapped);
            }
        }

        cuts.Add(duration);
        cuts.Sort();

        var uniqueCuts = new List<double> { cuts[0] };
        for (int i = 1; i < cuts.Count; i++)
        {
            if (cuts[i] - uniqueCuts[^1] > 0.5)
                uniqueCuts.Add(cuts[i]);
            else
                uniqueCuts[^1] = cuts[i];
        }
        uniqueCuts[^1] = duration;

        int numSec = uniqueCuts.Count - 1;
        var secMedians = new double[numSec];
        for (int m = 0; m < numSec; m++)
        {
            double tStart = uniqueCuts[m];
            double tEnd = uniqueCuts[m + 1];
            int fStart = Math.Clamp((int)(tStart / dt), 0, n - 1);
            int fEnd = Math.Clamp((int)(tEnd / dt), fStart + 1, n);
            secMedians[m] = FastMedian(analysis.Rms, fStart, fEnd - fStart);
        }

        double minMed = secMedians.Min();
        double maxMed = secMedians.Max();
        double medRange = maxMed - minMed;

        var sections = new List<Section>();
        for (int m = 0; m < numSec; m++)
        {
            double start = uniqueCuts[m];
            double end = uniqueCuts[m + 1];
            double u = medRange > 0.05 ? (secMedians[m] - minMed) / medRange : 0.5;

            string kind;
            if (numSec == 1)
            {
                kind = maxRms < 0.05f ? "break" : "intro";
            }
            else if (numSec == 2)
            {
                if (m == 0) kind = (u >= 0.7 && secMedians[0] > secMedians[1]) ? "chorus" : "intro";
                else kind = (secMedians[1] >= secMedians[0]) ? "chorus" : "outro";
            }
            else
            {
                if (m == 0) kind = u >= 0.8 ? "chorus" : "intro";
                else if (m == numSec - 1) kind = u >= 0.8 ? "chorus" : (u <= 0.3 ? "outro" : "verse");
                else
                {
                    if (u >= 0.65) kind = "chorus";
                    else if (u <= 0.25) kind = "break";
                    else kind = "verse";
                }
            }

            sections.Add(new Section
            {
                Start = Math.Round(start, 6),
                End = Math.Round(end, 6),
                Kind = kind
            });
        }

        var peaks = new List<double>();
        for (int m = 0; m < sections.Count; m++)
        {
            var sec = sections[m];
            if (sec.Kind == "chorus" || secMedians[m] >= maxMed * 0.75)
            {
                int fStart = Math.Clamp((int)(sec.Start / dt), 0, n - 1);
                int fEnd = Math.Clamp((int)(sec.End / dt), fStart + 1, n);
                float bestVal = -1f;
                int bestFrame = fStart;
                for (int i = fStart; i < fEnd; i++)
                {
                    float v = analysis.Rms[i];
                    if (analysis.Bass != null && analysis.Bass.Length > i) v += 0.5f * analysis.Bass[i];
                    if (v > bestVal)
                    {
                        bestVal = v;
                        bestFrame = i;
                    }
                }

                if (bestVal > 0.2f)
                {
                    double peakT = bestFrame * dt;
                    if (beats.Count > 0 && bpm > 0)
                    {
                        double bestDist = double.MaxValue;
                        double bestB = peakT;
                        foreach (var b in beats)
                        {
                            double d = Math.Abs(b - peakT);
                            if (d < bestDist)
                            {
                                bestDist = d;
                                bestB = b;
                            }
                        }
                        if (bestDist <= 0.5 * (60.0 / bpm)) peakT = bestB;
                    }
                    peaks.Add(Math.Round(peakT, 6));
                }
            }
        }
        peaks.Sort();
        var uniquePeaks = peaks.Distinct().ToList();

        return new BeatMap
        {
            Bpm = Math.Round(bpm, 2),
            Beats = beats,
            Sections = sections,
            Peaks = uniquePeaks
        };
    }

    private static float FastMedian(float[] arr, int start, int count)
    {
        if (count <= 0) return 0f;
        var buf = new float[count];
        Array.Copy(arr, start, buf, 0, count);
        Array.Sort(buf);
        return buf[count / 2];
    }
}
