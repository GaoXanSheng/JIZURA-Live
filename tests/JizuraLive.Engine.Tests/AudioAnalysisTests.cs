using System.IO;
using JizuraLive.App.PvStage.Timeline;
using JizuraLive.Engine;
using JizuraLive.Pv;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// 波形与频带分析（<see cref="AudioAnalyzer"/> / <see cref="AudioFileAnalyzer"/> / <see cref="WaveformPeaks"/>）的纯数值测试：
/// 合成 PCM 进、序列出，只在整文件入口那条用例碰一次临时 WAV。
/// </summary>
public class AudioAnalysisTests
{
    private const int Rate = 48000;
    private const int Hop = 512;
    private const int HalfWindow = 1024;   // Default 配置：WindowSize/2

    // ---------- 频带分析 ----------

    [Fact]
    public void SilenceIsZeroWithoutNaN()
    {
        var series = AudioAnalyzer.Analyze(new float[Rate], 1, Rate);
        Assert.True(series.Count > 0);

        for (int j = 0; j < series.Count; j++)
        {
            Assert.Equal(0f, series.Rms[j]);
            Assert.Equal(0f, series.Bass[j]);
            Assert.Equal(0f, series.Mid[j]);
            Assert.Equal(0f, series.High[j]);
        }

        // 静音走的是 20log10(max(x, 1e-5)) 的底噪分支：不能出现 NaN/Infinity 污染参数链
        Assert.True(series.Sample(0.5, out var bands));
        Assert.True(bands.Valid);
        Assert.Equal(0f, bands.Rms);
        Assert.True(float.IsFinite(bands.Rms) && float.IsFinite(bands.Bass) && float.IsFinite(bands.Mid) && float.IsFinite(bands.High));
    }

    [Fact]
    public void FullScaleSineMapsToNearZeroDbfs()
    {
        // 3000Hz 正好是 48000/2048 的 bin 128：单边谱归一化常数由这条已知幅值正弦标定，不靠猜
        var series = AudioAnalyzer.Analyze(SineMono(Rate, 3000, 1.0), 1, Rate);
        int j = series.Count / 2;   // 取中部窗口，避开首末窗的零填充稀释

        // 幅值 1.0 正弦的真实 RMS = 1/√2 → 20log10 = −3.0119dB → 映射 0.94980
        // （任务书的 ">= 0.95" 是按 −3.00dB 整算的近似；这里按公式实算值给容差，仍钉住常数：
        //   漏掉单边 ×2 系数会掉到 0.90，漏掉窗能量归一化则随窗长漂移）
        Assert.Equal(0.9498f, series.Mid[j], 3);
        Assert.InRange(series.Mid[j], 0.94f, 1f);
        Assert.True(series.Bass[j] < 0.05f);   // 能量集中在 Mid，没有漏进别的频带
        Assert.True(series.High[j] < 0.05f);
    }

    [Fact]
    public void LowMidHighSinesLandInExpectedBands()
    {
        // 187.5 / 1500 / 6000Hz 都是 48000/2048 的 bin 中心，分别落在 Bass / Mid / High
        var bass = AudioAnalyzer.Analyze(SineMono(Rate, 187.5, 0.5), 1, Rate);
        var mid = AudioAnalyzer.Analyze(SineMono(Rate, 1500, 0.5), 1, Rate);
        var high = AudioAnalyzer.Analyze(SineMono(Rate, 6000, 0.5), 1, Rate);
        int j = bass.Count / 2;

        Assert.True(bass.Bass[j] > bass.Mid[j] && bass.Bass[j] > bass.High[j]);
        Assert.True(mid.Mid[j] > mid.Bass[j] && mid.Mid[j] > mid.High[j]);
        Assert.True(high.High[j] > high.Bass[j] && high.High[j] > high.Mid[j]);
    }

    [Fact]
    public void AntiPhaseStereoIsNotSilent()
    {
        var positive = SineMono(Rate, 1500, 0.8);
        var negative = new float[positive.Length];
        for (int i = 0; i < positive.Length; i++) negative[i] = -positive[i];   // 精确反相

        var inPhase = AudioAnalyzer.Analyze(Interleave(positive, positive), 2, Rate);
        var antiPhase = AudioAnalyzer.Analyze(Interleave(positive, negative), 2, Rate);
        int j = antiPhase.Count / 2;

        // 先相加再算谱的实现会在这里得到全 0：每声道各自算功率再平均才不会相消（§13.4）
        Assert.True(antiPhase.Rms[j] > 0.5f);
        Assert.Equal(inPhase.Rms[j], antiPhase.Rms[j], 5);
        Assert.Equal(inPhase.Bass[j], antiPhase.Bass[j], 5);
        Assert.Equal(inPhase.Mid[j], antiPhase.Mid[j], 5);
        Assert.Equal(inPhase.High[j], antiPhase.High[j], 5);
    }

    [Fact]
    public void WindowCentersAndHopAreExact()
    {
        const int frames = 12288;
        const int impulseAt = 5220;          // 距 hop 中心 5120 为 100，明显偏向窗口 10
        var pcm = new float[frames];
        pcm[impulseAt] = 8f;                 // 给到 8.0：单样本脉冲在窗口边缘窗后 RMS 会低于 −60dB 底噪而映射成 0
        var series = AudioAnalyzer.Analyze(pcm, 1, Rate);

        Assert.Equal(Hop / (double)Rate, series.StepSeconds);
        Assert.Equal(23, series.Count);      // 末窗中心 22×512 已覆盖到最后一个样本

        // 只有中心距脉冲不超过半窗的窗口看得到它，其余窗口必须精确为 0（窗口 [j×hop−1024, j×hop+1024) 与零填充）
        int best = 0;
        for (int j = 0; j < series.Count; j++)
        {
            Assert.Equal(Math.Abs(j * Hop - impulseAt) < HalfWindow, series.Rms[j] > 0f);
            if (series.Rms[j] > series.Rms[best]) best = j;
        }

        // 脉冲被计入中心时刻最接近它的窗口
        Assert.Equal((int)Math.Round(impulseAt / (double)Hop), best);
        Assert.Equal(10, best);

        // 查询正好落在窗口中心时不做插值：取到的就是该窗口自身的值
        Assert.True(series.Sample(best * (double)Hop / Rate, out var bands));
        Assert.Equal(series.Rms[best], bands.Rms, 6);
    }

    [Fact]
    public void EmptyHighBandAtLowSampleRateIsValidZero()
    {
        // sampleRate=8000 → Nyquist 4000 → High 段 [4000, min(16000,4000)) 无 bin：有效 0，不是无效
        var series = AudioAnalyzer.Analyze(SineMono(8000, 1000, 0.5, rate: 8000), 1, 8000);
        Assert.True(series.Sample(0.5, out var bands));
        Assert.True(bands.Valid);
        Assert.Equal(0f, bands.High);
        Assert.True(bands.Mid > 0f);
    }

    [Fact]
    public void QueryIsFrameRateIndependent()
    {
        var pcm = new float[2 * Rate];
        for (int n = 0; n < pcm.Length; n++)
        {
            double t = (double)n / Rate;
            pcm[n] = (float)(0.4 * Math.Sin(2 * Math.PI * 187.5 * t)
                             + 0.3 * Math.Sin(2 * Math.PI * 1500 * t)
                             + 0.2 * Math.Sin(2 * Math.PI * 6000 * t));
        }
        var series = AudioAnalyzer.Analyze(pcm, 1, Rate);

        var byFps = new List<Dictionary<double, AudioBands>>();
        foreach (int fps in new[] { 15, 60, 144 })
        {
            var map = new Dictionary<double, AudioBands>();
            for (int frame = 0; frame <= fps * 2; frame++)
            {
                double t = (double)frame / fps;      // 帧率不同 → tick 序列不同
                Assert.True(series.Sample(t, out var bands));
                map[t] = bands;
            }
            byFps.Add(map);
        }

        var shared = byFps[0].Keys.Intersect(byFps[1].Keys).Intersect(byFps[2].Keys).ToList();
        Assert.True(shared.Count >= 4);              // 0、1/3、2/3、… 各帧率共有
        foreach (double t in shared)
        {
            // 查询只依赖 t：同一时刻在 15/60/144fps 下逐位相同
            Assert.Equal(byFps[0][t].Rms, byFps[1][t].Rms);
            Assert.Equal(byFps[0][t].Rms, byFps[2][t].Rms);
            Assert.Equal(byFps[0][t].Bass, byFps[2][t].Bass);
            Assert.Equal(byFps[0][t].Mid, byFps[2][t].Mid);
            Assert.Equal(byFps[0][t].High, byFps[2][t].High);
            Assert.Equal(byFps[0][t].Valid, byFps[2][t].Valid);
        }
    }

    [Fact]
    public void QueryClampsOutsideRangeAndReportsInvalidWhenEmpty()
    {
        var series = AudioAnalyzer.Analyze(SineMono(Rate, 1500, 0.5), 1, Rate);

        Assert.True(series.Sample(-10, out var before));       // 早于首窗
        Assert.Equal(series.Rms[0], before.Rms);
        Assert.Equal(series.Mid[0], before.Mid);
        Assert.True(before.Valid);

        Assert.True(series.Sample(1e6, out var after));        // 晚于末窗
        int last = series.Count - 1;
        Assert.Equal(series.Rms[last], after.Rms);
        Assert.Equal(series.Mid[last], after.Mid);
        Assert.True(after.Valid);

        var empty = AudioAnalyzer.Analyze([], 1, Rate);
        Assert.Equal(0, empty.Count);
        Assert.False(empty.Sample(0.5, out var none));
        Assert.False(none.Valid);
    }

    [Fact]
    public void AnalysisKeyChangesWithRules()
    {
        var config = AudioAnalysisConfig.Default;
        var key = config.Key("hash-A", 48000, 2);
        Assert.Equal(new AudioAnalysisKey("hash-A", config.Version, 48000, 2), key);   // 形状冻结：四个字段都进键

        Assert.NotEqual(key, config.Key("hash-A", 44100, 2));                                    // 采样率变化
        Assert.NotEqual(key, config.Key("hash-A", 48000, 1));                                    // 声道变化
        Assert.NotEqual(key, config.Key("hash-B", 48000, 2));                                    // 内容 hash 变化
        Assert.NotEqual(key, (config with { Version = config.Version + 1 }).Key("hash-A", 48000, 2));   // 规则版本变化

        // 序列自带规则版本：换规则后旧缓存不得与新数值混用
        var series = AudioAnalyzer.Analyze(SineMono(Rate, 1500, 0.5), 1, Rate, config with { Version = 7 });
        Assert.Equal(7, series.ConfigVersion);
    }

    [Fact]
    public void AudioBandSeriesIsAudioAnalysisAndViewExposesProperties()
    {
        var series = AudioAnalyzer.Analyze(SineMono(Rate, 1500, 0.5), 1, Rate);
        AudioAnalysis analysis = series;
        Assert.Equal(series.Count, analysis.Count);
        Assert.Equal(series.StepSeconds, analysis.StepSeconds);
        Assert.Same(series.Rms, analysis.Rms);
        Assert.Same(series.Bass, analysis.Bass);
        Assert.Same(series.Mid, analysis.Mid);
        Assert.Same(series.High, analysis.High);
    }

    // ---------- 整文件入口与管线快照 ----------

    [Fact]
    public void WholeFileAnalysisDecodesAndLandsInTheExpectedBand()
    {
        var path = Path.Combine(Path.GetTempPath(), "jizura-audio-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            WriteSineWav(path, 1500, 0.5, seconds: 1.0);

            var series = AudioFileAnalyzer.Analyze(path);

            // 整文件入口与"喂 PCM"是同一条分析：采样率/步长/窗口对齐规则一字不差
            Assert.Equal(Rate, series.SampleRate);
            Assert.Equal(Hop / (double)Rate, series.StepSeconds);
            Assert.True(series.IsComplete);
            Assert.True(series.Count > 0);

            int j = series.Count / 2;
            Assert.True(series.Mid[j] > series.Bass[j] && series.Mid[j] > series.High[j]);
            Assert.True(series.Mid[j] > 0.5f);   // 1500Hz 正弦落在 Mid
            Assert.True(float.IsFinite(series.Mid[j]));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// 流式整文件入口：分块解码（内存 O(窗口)，不整曲驻留）后，数值必须与"把同一份 PCM 整批喂进去"
    /// 逐位相同——窗口/步长/频带/归一化规则是同一条链，分块边界不该改变任何一窗。
    /// 取消在每个解码块之前检查：预先取消的令牌当场抛出，不先解完整曲。
    /// </summary>
    [Fact]
    public void WholeFileStreamingMatchesBatchAnalysisAndHonoursCancellation()
    {
        var path = Path.Combine(Path.GetTempPath(), "jizura-audio-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            WriteSineWav(path, 700, 0.4, seconds: 1.5);

            var streamed = AudioFileAnalyzer.Analyze(path);

            // 整批对照：同一个解码器把 PCM 一次读出来喂纯函数入口（16bit 量化后的同一份样本）
            var pcm = DecodeAll(path, out var channels, out var rate);
            var batch = AudioAnalyzer.Analyze(pcm, channels, rate);

            Assert.True(streamed.IsComplete);
            Assert.Equal(batch.Count, streamed.Count);
            Assert.Equal(batch.SampleRate, streamed.SampleRate);
            Assert.Equal(batch.StepSeconds, streamed.StepSeconds);
            for (int j = 0; j < batch.Count; j++)
            {
                Assert.Equal(batch.Rms[j], streamed.Rms[j]);
                Assert.Equal(batch.Bass[j], streamed.Bass[j]);
                Assert.Equal(batch.Mid[j], streamed.Mid[j]);
                Assert.Equal(batch.High[j], streamed.High[j]);
            }

            // 取消：令牌已取消时不得先解完整曲再返回
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Assert.Throws<OperationCanceledException>(() => AudioFileAnalyzer.Analyze(path, cts.Token));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>
    /// 取消在运行中途也要生效：令牌是在分析过程中被取消的（进入时并未取消），
    /// 长文件必须在跑完之前抛 <see cref="OperationCanceledException"/>。
    /// </summary>
    [Fact]
    public void WholeFileStreamingCancelsMidRun()
    {
        var path = Path.Combine(Path.GetTempPath(), "jizura-audio-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            // 60s：解码 + 数千窗 FFT 远不止 50ms，取消点必然落在分析中途
            WriteSineWav(path, 700, 0.4, seconds: 60.0);

            using var cts = new CancellationTokenSource();
            cts.CancelAfter(50);
            // 前提：进入分析时令牌还没取消，否则下面的抛出只证明了"入口检查"而不是"中途生效"
            Assert.False(cts.IsCancellationRequested);
            Assert.Throws<OperationCanceledException>(() => AudioFileAnalyzer.Analyze(path, cts.Token));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    /// <summary>整文件入口的失败带原因：空路径当场抛出，不是"分析出一份空序列"。</summary>
    [Fact]
    public void WholeFileAnalysisRejectsEmptyPath()
    {
        var ex = Assert.Throws<ArgumentException>(() => AudioFileAnalyzer.Analyze(""));
        Assert.Contains("音频路径为空", ex.Message);
    }

    /// <summary>把文件整段解码成交错 PCM（整批入口的输入）：与流式入口共用同一个解码器。</summary>
    private static float[] DecodeAll(string path, out int channels, out int sampleRate)
    {
        using var reader = new NAudio.Wave.AudioFileReader(path);
        var format = reader.WaveFormat;
        channels = format.Channels;
        sampleRate = format.SampleRate;

        var samples = (NAudio.Wave.ISampleProvider)reader;
        var all = new List<float>();
        var chunk = new float[16384];
        while (true)
        {
            var read = samples.Read(chunk.AsSpan());
            if (read <= 0) break;
            for (int i = 0; i < read; i++) all.Add(chunk[i]);
        }

        return all.ToArray();
    }

    [Fact]
    public void PipelineSnapshotQueriesTheTransportTimeAndReportsNotReadyAsInvalid()
    {
        var series = AudioAnalyzer.Analyze(SineMono(Rate, 1500, 0.5), 1, Rate);

        // 管线按 transport 时刻查询序列：快照就是该时刻的查询结果（不再是把一路能量填进四个频带）
        Assert.True(series.Sample(0.5, out var bands));
        var snapshot = PvPipeline.SampleAudioBands(series, 0.5);
        Assert.True(snapshot.Valid);
        Assert.Equal((double)bands.Rms, snapshot.Rms);
        Assert.Equal((double)bands.Bass, snapshot.Bass);
        Assert.Equal((double)bands.Mid, snapshot.Mid);
        Assert.Equal((double)bands.High, snapshot.High);

        // 同一时刻重复查询逐位相同：帧率/顺序都不改变结果
        Assert.Equal(snapshot, PvPipeline.SampleAudioBands(series, 0.5));

        // 分析未就绪：Valid=false（回落到基础值），而不是四个 0 的"静音"
        var notReady = PvPipeline.SampleAudioBands(null, 0.5);
        Assert.False(notReady.Valid);
        Assert.Equal(0, notReady.Rms);

        var empty = AudioAnalyzer.Analyze([], 1, Rate);
        Assert.False(PvPipeline.SampleAudioBands(empty, 0.5).Valid);
    }

    // ---------- 波形极值 ----------

    [Fact]
    public void PeaksCatchSingleSpikeThatDecimationWouldMiss()
    {
        var pcm = new float[4096];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)(0.1 * Math.Sin(i * 0.01));
        pcm[37] = 0.9f;      // 不在任何"每 16 个取一点"的位置
        pcm[1013] = -0.8f;

        var peaks = WaveformPeaks.Build(pcm, 1, 256);
        int count = peaks.WindowCount(0);
        Assert.Equal(16, count);

        // 线性扫描 oracle（§15.2 ALG-EXACT）：极值必须与逐样本扫描一致，抽点会漏
        for (int i = 0; i < count; i++)
        {
            var (mn, mx) = Scan(pcm, i * 256, Math.Min((i + 1) * 256, pcm.Length));
            Assert.Equal(mn, peaks.Min(0)[i]);
            Assert.Equal(mx, peaks.Max(0)[i]);
        }

        Assert.Equal(0.9f, peaks.Max(0)[0]);
        Assert.Equal(-0.8f, peaks.Min(0)[3]);

        float decimated = float.NegativeInfinity;
        for (int s = 0; s < 256; s += 16) decimated = Math.Max(decimated, pcm[s]);
        Assert.True(decimated < 0.9f);       // 抽点实现会报 ≈0.1，漏掉第 37 个样本的尖峰
    }

    [Fact]
    public void PeaksMergeHalvesExactly()
    {
        var pcm = new float[4096];
        for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)(Math.Sin(i * 0.37) * (i % 7 == 0 ? 1.0 : 0.3));
        var peaks = WaveformPeaks.Build(pcm, 1, 256);

        Assert.Equal(512, peaks.WindowSize(1));
        Assert.Equal(16, peaks.WindowCount(0));
        Assert.Equal(8, peaks.WindowCount(1));

        for (int level = 1; level < peaks.LevelCount; level++)
        {
            int prev = peaks.WindowCount(level - 1);
            int cur = peaks.WindowCount(level);
            Assert.Equal((prev + 1) / 2, cur);
            for (int i = 0; i < cur; i++)
            {
                int a = 2 * i, b = Math.Min(2 * i + 1, prev - 1);
                Assert.Equal(Math.Min(peaks.Min(level - 1)[a], peaks.Min(level - 1)[b]), peaks.Min(level)[i]);
                Assert.Equal(Math.Max(peaks.Max(level - 1)[a], peaks.Max(level - 1)[b]), peaks.Max(level)[i]);
            }
        }

        Assert.Equal(4096, peaks.WindowSize(peaks.LevelCount - 1));
        Assert.Equal(1, peaks.WindowCount(peaks.LevelCount - 1));

        // 奇数个窗口时末窗只并自己，尾段不能丢
        var oddPcm = new float[1280];
        for (int i = 0; i < oddPcm.Length; i++) oddPcm[i] = (float)(i * 0.001);   // 每窗数值递增，尾窗合并才可观测
        var odd = WaveformPeaks.Build(oddPcm, 1, 256);   // 5 个 level0 窗口
        Assert.Equal(5, odd.WindowCount(0));
        Assert.Equal(3, odd.WindowCount(1));
        Assert.Equal(odd.Min(0)[4], odd.Min(1)[2]);
        Assert.Equal(odd.Max(0)[4], odd.Max(1)[2]);
    }

    [Fact]
    public void PeaksSelectLevelByPixelWidth()
    {
        var peaks = WaveformPeaks.Build(new float[48000], 1, 256);

        Assert.Equal(0, peaks.SelectLevel(0, 48000, 1000));   // 每像素 48 样本：最细层级就够
        Assert.Equal(1, peaks.SelectLevel(0, 48000, 100));
        Assert.Equal(5, peaks.SelectLevel(0, 48000, 10));     // 每像素 4800 样本：需要 8192 的窗口
        Assert.True(peaks.SelectLevel(0, 48000, 100) <= peaks.SelectLevel(0, 48000, 10));   // 视区越窄层级越粗

        Assert.Equal(0, peaks.SelectLevel(0, 500, 500));      // pixelWidth >= sampleCount
        Assert.Equal(0, peaks.SelectLevel(0, 500, 4000));
    }

    [Fact]
    public void StereoInterleavingAndOddFrameCount()
    {
        const int frames = 1000;                  // 不是 256 的整数倍：末窗按实际样本处理
        var left = new float[frames];
        var right = new float[frames];
        left[300] = 0.7f;
        right[700] = -0.6f;
        left[900] = -0.3f;                        // 末窗内，且最后一个样本也要算进去
        left[frames - 1] = 0.45f;

        var peaks = WaveformPeaks.Build(Interleave(left, right), 2, 256);
        Assert.Equal(2, peaks.Channels);
        Assert.Equal(4, peaks.WindowCount(0));    // ceil(1000/256)
        Assert.Equal(256, peaks.WindowSize(0));
        Assert.Equal(frames, peaks.SampleCount);

        // 交错按声道分离：尖峰只出现在自己那一侧
        Assert.Equal(0.7f, peaks.Max(0)[0 * 4 + 1]);
        Assert.Equal(0f, peaks.Max(0)[1 * 4 + 1]);
        Assert.Equal(-0.6f, peaks.Min(0)[1 * 4 + 2]);
        Assert.Equal(0f, peaks.Min(0)[0 * 4 + 2]);

        // 末窗只覆盖 [768,1000)：按实际样本数取极值，不读越界
        var (mn, mx) = Scan(left, 768, frames);
        Assert.Equal(mn, peaks.Min(0)[0 * 4 + 3]);
        Assert.Equal(mx, peaks.Max(0)[0 * 4 + 3]);

        // 只读可见段，越界范围钳到有效区间
        Assert.True(peaks.TryRead(0, 0, 900, 500, out var visMin, out var visMax));
        Assert.Equal(peaks.Min(0)[3], visMin);
        Assert.Equal(peaks.Max(0)[3], visMax);
        Assert.False(peaks.TryRead(0, 0, 5000, 10, out _, out _));   // 整段在有效范围之外
        Assert.False(peaks.TryRead(0, 0, 0, 0, out _, out _));       // 空段
        Assert.False(peaks.TryRead(0, 5, 0, 100, out _, out _));     // 声道越界
    }

    /// <summary>
    /// 流式构建与一次性 <see cref="WaveformPeaks.Build"/> **逐值一致**（每一级的每个窗口）：
    /// 块边界、跨帧、跨窗口切分都不改变结果，所以装载不必把整曲 PCM 攒下来。
    /// </summary>
    [Fact]
    public void StreamingPeakBuilderMatchesBuildExactly()
    {
        const int channels = 2;

        // 三种长度：末窗不满 / 恰好整窗 / 只有一帧
        foreach (var frames in new[] { 5000, 5120, 1 })
        {
            var pcm = new float[frames * channels];
            var rng = new Random(20261016);
            for (int i = 0; i < pcm.Length; i++) pcm[i] = (float)(rng.NextDouble() * 2 - 1);

            var whole = WaveformPeaks.Build(pcm, channels);

            var builder = WaveformPeaks.CreateBuilder(channels);
            for (int at = 0; at < pcm.Length;)
            {
                int len = Math.Min(1 + at % 997, pcm.Length - at);   // 块长在变，且不整除帧长/窗口长
                builder.Feed(pcm.AsSpan(at, len));
                at += len;
            }
            var streamed = builder.Finish();

            Assert.Equal(whole.Channels, streamed.Channels);
            Assert.Equal(whole.BaseWindow, streamed.BaseWindow);
            Assert.Equal(whole.SampleCount, streamed.SampleCount);
            Assert.Equal(whole.LevelCount, streamed.LevelCount);
            for (int level = 0; level < whole.LevelCount; level++)
            {
                Assert.Equal(whole.WindowCount(level), streamed.WindowCount(level));
                Assert.Equal(whole.Min(level), streamed.Min(level));
                Assert.Equal(whole.Max(level), streamed.Max(level));
            }
        }
    }

    /// <summary>尾部不足一帧的样本与 <see cref="WaveformPeaks.Build"/> 一样丢弃，并且允许切在半帧上喂。</summary>
    [Fact]
    public void StreamingPeakBuilderDropsTrailingPartialFrame()
    {
        var pcm = new[] { 0.1f, 0.2f, 0.3f };   // 3 个样本、2 声道：末帧缺右声道
        var whole = WaveformPeaks.Build(pcm, 2);

        var builder = WaveformPeaks.CreateBuilder(2);
        builder.Feed(pcm.AsSpan(0, 1));         // 第一帧被切成两半喂
        builder.Feed(pcm.AsSpan(1));
        var streamed = builder.Finish();

        Assert.Equal(1, streamed.SampleCount);
        Assert.Equal(whole.SampleCount, streamed.SampleCount);
        Assert.Equal(whole.Min(0), streamed.Min(0));
        Assert.Equal(whole.Max(0), streamed.Max(0));
    }

    /// <summary>整文件装载走的就是流式路径：与「同一份 PCM 换一种块长喂 Builder」逐值一致。</summary>
    [Fact]
    public void WaveformLoaderStreamsTheSamePeaksAsTheBuilder()
    {
        string path = Path.Combine(Path.GetTempPath(), "jz-wave-" + Guid.NewGuid().ToString("N") + ".wav");
        try
        {
            WriteSineWav(path, 3000, 0.8, 0.5);
            var loaded = WaveformLoader.Load(path);

            using var reader = new NAudio.Wave.AudioFileReader(path);
            var samples = (NAudio.Wave.ISampleProvider)reader;
            var builder = WaveformPeaks.CreateBuilder(reader.WaveFormat.Channels);
            var chunk = new float[700];            // 块长刻意不是窗口长/帧长的整数倍
            while (true)
            {
                int read = samples.Read(chunk);
                if (read <= 0) break;
                builder.Feed(chunk.AsSpan(0, read));
            }
            var expected = builder.Finish();

            Assert.Equal(expected.SampleCount, loaded.SampleCount);
            Assert.Equal(expected.LevelCount, loaded.LevelCount);
            for (int level = 0; level < expected.LevelCount; level++)
            {
                Assert.Equal(expected.Min(level), loaded.Min(level));
                Assert.Equal(expected.Max(level), loaded.Max(level));
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    // ---------- 合成辅助 ----------

    /// <summary>写一个单声道 16bit PCM 的 WAV（整文件入口的输入）：解码器与真实素材走同一条路。</summary>
    private static void WriteSineWav(string path, double hz, double amplitude, double seconds)
    {
        var format = new NAudio.Wave.WaveFormat(Rate, 16, 1);
        using var writer = new NAudio.Wave.WaveFileWriter(path, format);

        var samples = (int)(Rate * seconds);
        for (int n = 0; n < samples; n++)
        {
            var value = amplitude * Math.Sin(2 * Math.PI * hz * n / Rate);
            writer.WriteSample((float)value);   // 16bit 格式下 WriteSample 自带换算
        }
    }

    private static float[] SineMono(int frames, double hz, double amplitude, int rate = Rate)
    {
        var channel = new float[frames];
        for (int n = 0; n < frames; n++) channel[n] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * n / rate));
        return channel;
    }

    private static float[] Interleave(params float[][] channels)
    {
        int frames = channels[0].Length;
        var pcm = new float[frames * channels.Length];
        for (int c = 0; c < channels.Length; c++)
            for (int n = 0; n < frames; n++)
                pcm[n * channels.Length + c] = channels[c][n];
        return pcm;
    }

    private static (float Min, float Max) Scan(float[] channel, int from, int to)
    {
        float mn = float.PositiveInfinity, mx = float.NegativeInfinity;
        for (int i = from; i < to; i++)
        {
            if (channel[i] < mn) mn = channel[i];
            if (channel[i] > mx) mx = channel[i];
        }
        return (mn, mx);
    }
}
