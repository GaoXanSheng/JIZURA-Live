// 移植自 frontend/src/engine/10_audio.js（仅 J.beatGrid，源码 L296–302；其余音频分析见 C# AudioAnalyzer）
namespace JizuraLive.Engine;

public static class JAudio
{
    /// <summary>
    /// J.beatGrid（10_audio.js L296）：按用户 BPM + 首拍偏移重建节拍网格。
    /// bpm &lt;= 0 返回空；步长 p = 60/bpm，从 offset 起累加到 duration + 0.01 前，
    /// 只保留 t &gt;= 0 的拍点，每个拍点四舍五入到 4 位小数（JS 的 +t.toFixed(4)）。
    /// </summary>
    public static double[] BeatGrid(double bpm, double offset, double duration)
    {
        var outList = new List<double>();
        if (!(bpm > 0)) return outList.ToArray();
        var p = 60 / bpm;
        for (var t = offset; t < duration + 0.01; t += p)
            if (t >= 0)
                outList.Add(Math.Round(t, 4, MidpointRounding.AwayFromZero));
        return outList.ToArray();
    }
}
