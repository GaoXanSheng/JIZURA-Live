using JizuraLive.App;
using JizuraLive.Engine;
using NAudio.Wave;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// AudioFx 清洗与建链的行为红线（预览 PreviewEngine 播放链的 Build）：
/// 工程/settings 里写进来的裸 JObject 是不可信输入——清洗必须丢未知/重复单元、clamp 越界参数、
/// on 缺省为真；建链必须跳过 on:false 的单元、空链返回 null（不建图直通）。
/// </summary>
public class AudioFxChainTests
{
    /// <summary>本测试用的脉冲源：第 0 帧 1.0、其余静音（测链路的冲激响应/能量增益）。</summary>
    private sealed class ImpulseSource : ISampleProvider
    {
        private int _pos;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public int Read(Span<float> buffer)
        {
            for (var i = 0; i < buffer.Length; i += 2)
            {
                var imp = _pos == 0 ? 1f : 0f;
                buffer[i] = imp;
                buffer[i + 1] = imp;
                _pos++;
            }

            return buffer.Length;
        }
    }

    /// <summary>按样本生成函数产出的测试源（频率/脉冲串测试用）。</summary>
    private sealed class FnSource(Func<int, double> gen, double seconds) : ISampleProvider
    {
        private int _pos;
        private readonly int _total = (int)(48000 * seconds);

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public int Read(Span<float> buffer)
        {
            for (var i = 0; i < buffer.Length; i += 2)
            {
                var v = _pos >= _total ? 0f : (float)gen(_pos);
                buffer[i] = v;
                buffer[i + 1] = v;
                _pos++;
            }

            return buffer.Length;
        }
    }

    private static double[] RunPitch(int semitones, Func<int, double> gen, double seconds)
    {
        var cfg = JObject.Parse(
            "{\"units\":[{\"id\":\"pitch\",\"on\":true,\"params\":{\"semitones\":" + semitones + "}}]}");
        var g = AudioFx.Build(cfg, new FxBuildOpts { SampleRate = 48000 })!;
        g.Input.Source = new FnSource(gen, seconds);
        var outl = new List<double>((int)(48000 * seconds));
        var buf = new float[4096];
        while (outl.Count < 48000 * seconds)
        {
            var n = g.Output.Read(buf.AsSpan());
            if (n <= 0) break;
            for (var i = 0; i < n; i += 2) outl.Add(buf[i]);
        }

        return outl.ToArray();
    }

    /// <summary>Goertzel 单频功率（归一化到样本数）。</summary>
    private static double Goertzel(ReadOnlySpan<double> x, double f)
    {
        var w = 2 * Math.PI * f / 48000;
        double c = 2 * Math.Cos(w), s1 = 0, s2 = 0;
        foreach (var v in x)
        {
            var s0 = v + c * s1 - s2;
            s2 = s1;
            s1 = s0;
        }

        return (s1 * s1 + s2 * s2 - c * s1 * s2) / x.Length;
    }

    /// <summary>
    /// 移调器（pitch 单元的 WSOLA 两级实现）的红线：音高 ×2^(n/12) 精确、时长严格不变（1:1）。
    /// 回归背景：旧"延迟往复"实现平均音高不变（只是合唱颤音）、首版两级前身单级架构在极端档
    /// 退化成纯重采样（内容半速 2× + 环形越界相位跳）。判据：输出期望频率功率远大于原频（无干声残留），
    /// 且脉冲串（每 0.5s 一个）的输出间隔仍 ≈0.5s（时长/内容完整性）。
    /// </summary>
    [Fact]
    public void 移调器音高精确且时长不变()
    {
        // 音高：440Hz 正弦 → 各档主频 = 440·2^(n/12)，原频泄漏必须可忽略
        foreach (var s in new[] { -7, -2, 2, 7 })
        {
            var x = RunPitch(s, n => 0.5 * Math.Sin(2 * Math.PI * 440 * n / 48000), 2.0);
            var seg = x.AsSpan(24000, 72000); // 跳过启动（128ms 预填）
            var expect = 440 * Math.Pow(2, s / 12.0);
            var pe = Goertzel(seg, expect);
            var p440 = Goertzel(seg, 440);
            Assert.True(pe > 100 * p440, $"semitones={s}: 期望主频功率 {pe:F3} 应远大于原频 {p440:F3}");
        }

        // 时长：脉冲串每 0.5s 一个 → 输出间隔仍 ≈0.5s（音高改动不得改变内容节奏）
        var pulse = RunPitch(-2, n => n % 24000 < 24 ? 0.9 * Math.Sin(2 * Math.PI * 1000 * n / 48000) : 0, 5.0);
        var peaks = new List<int>();
        for (var i = 1; i < pulse.Length - 1; i++)
            if (Math.Abs(pulse[i]) > 0.35 && Math.Abs(pulse[i]) >= Math.Abs(pulse[i - 1])
                                           && Math.Abs(pulse[i]) > Math.Abs(pulse[i + 1])
                                           && (peaks.Count == 0 || i - peaks[^1] > 5000))
                peaks.Add(i);
        Assert.True(peaks.Count >= 8, $"脉冲检出 {peaks.Count} 个（应 ≈9）");
        for (var i = 1; i < peaks.Count; i++)
        {
            var gap = (peaks[i] - peaks[i - 1]) / 48000.0;
            Assert.InRange(gap, 0.47, 0.53);
        }
    }

    /// <summary>源读到尾后返回 0 的短读源（测补零包装）。</summary>
    private sealed class DrainedSource : ISampleProvider
    {
        private int _left = 4; // 先给 4 帧（恰好一次读满 8 个 float），之后永远 0（模拟读到文件尾）
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

        public int Read(Span<float> buffer)
        {
            var n = Math.Min(buffer.Length, _left * 2);
            buffer[..n].Fill(1f);
            _left -= n / 2;
            return n;
        }
    }

    /// <summary>
    /// 尾后补零包装（ZeroPadSampleProvider）的红线：源短读/返回 0 时必须补零到满长——
    /// 下游 NAudio WDL 重采样器在"源不足一帧"时 Array.Copy 越界抛 ArgumentException，
    /// 异常打在 WaveOut 回调线程上把设备停机成"幽灵播放"（引擎以为在播、实际永久无声，
    /// 用户实测：「进度条到底后从零播放，一点声音都没有」）。EOF 必须对重采样器不可见。
    /// </summary>
    [Fact]
    public void 尾后补零包装对下游不可见()
    {
        var p = new ZeroPadSampleProvider(new DrainedSource());
        var buf = new float[8];

        Assert.Equal(8, p.Read(buf.AsSpan()));   // 源有数据：原样透传
        Assert.All(buf, v => Assert.Equal(1f, v));

        Array.Fill(buf, 1f);
        Assert.Equal(8, p.Read(buf.AsSpan()));   // 源读到尾（返回 0）：补零、仍满长
        Assert.All(buf, v => Assert.Equal(0f, v));

        Array.Fill(buf, 1f);
        Assert.Equal(8, p.Read(buf.AsSpan()));   // EOF 后持续满长零（永不短读）
        Assert.All(buf, v => Assert.Equal(0f, v));
    }

    /// <summary>
    /// 混响 IR 必须做单位能量归一化（对齐浏览器 ConvolverNode normalize=true，1/√(Σh²)）。
    /// 回归背景：移植时这层缩放属于 WebAudio 节点、不在 makeIR 代码里，首版漏掉后湿声功率增益
    /// 实测 ≈43×（wet=0.25 默认档仍有 10.7×）——混响淹没干声并削波轰鸣。
    /// 判据：白噪声输入下湿声 RMS ≈ 输入 RMS（√能量 ≈ 1；湿声低通会削掉部分高频能量，落在 0.5~1.5 合理）。
    /// </summary>
    [Fact]
    public void 混响IR单位能量归一化()
    {
        var cfg = JObject.Parse("""{"units":[{"id":"reverb","on":true,"params":{"size":2.2,"damp":0.5,"wet":1}}]}""");
        var g = AudioFx.Build(cfg, new FxBuildOpts { SampleRate = 48000 })!;
        g.Input.Source = new ImpulseSource();
        var buf = new float[4096];
        double energy = 0;
        long done = 0;
        var limit = 48000L * 3; // 3s：覆盖 2.2s IR + 一个块延迟
        while (done < limit)
        {
            var n = g.Output.Read(buf.AsSpan());
            if (n <= 0) break;
            for (var i = 0; i < n; i += 2)
                if (done + i > 4) // 跳过干声脉冲本身，只统计湿声尾
                    energy += (double)buf[i] * buf[i];
            done += n;
        }

        var gain = Math.Sqrt(energy);
        Assert.InRange(gain, 0.2, 2.5); // 未归一化时 ≈43：立刻抓住回归
    }
    [Fact]
    public void 清洗丢弃未知与重复单元并保留顺序()
    {
        var fx = JObject.Parse("""
        {"speed": 3,
         "units": [
            {"id": "mono", "params": {}},
            {"id": "nope", "params": {}},
            {"id": "mono", "params": {}},
            {"id": "bass", "on": false, "params": {"gain": 99, "freq": -5}}
         ]}
        """);
        var s = AudioFx.SanitizeAudioFx(fx);
        var units = (JArray)s["units"]!;
        Assert.Equal(2, units.Count);                       // 未知 id 丢弃、重复 id 只留首个
        Assert.Equal("mono", units[0]!["id"]!.ToString());
        Assert.Equal("bass", units[1]!["id"]!.ToString());   // 链顺序保留
        Assert.True(units[0]!["on"]!.Value<bool>());        // on 缺省 = true
        Assert.False(units[1]!["on"]!.Value<bool>());
        Assert.Equal(12d, units[1]!["params"]!["gain"]!.Value<double>()); // gain 99 → clamp 12
        Assert.Equal(60d, units[1]!["params"]!["freq"]!.Value<double>()); // freq -5 → clamp 60
        Assert.Equal(2d, s["speed"]!.Value<double>());       // speed 3 → clamp 2
    }

    [Fact]
    public void 建链跳过关闭的单元空链返回直通()
    {
        Assert.Null(AudioFx.Build(new JObject { ["units"] = new JArray() })); // 空链：null = 直通

        var off = AudioFx.Build(JObject.Parse("""{"units":[{"id":"mono","on":false,"params":{}}]}"""));
        Assert.NotNull(off);                                 // on:false 的单元跳过但图仍在
        Assert.Same(off!.Input, off.Output);                 // 全关：输出直连输入

        var on = AudioFx.Build(JObject.Parse("""{"units":[{"id":"mono","params":{}}]}"""));
        Assert.NotNull(on);
        Assert.NotSame(on!.Input, on.Output);                // 单元已入链
    }
}
