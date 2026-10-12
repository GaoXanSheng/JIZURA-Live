using JizuraLive.Engine;
using NAudio.Wave;
using Newtonsoft.Json.Linq;
using Xunit;

namespace JizuraLive.Engine.Tests;

/// <summary>
/// AudioFx 清洗与建链的行为红线（预览 PreviewEngine 与导出 AudioPrep 共用同一条 Build）：
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
