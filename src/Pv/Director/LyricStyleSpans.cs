using JizuraLive.Engine;

namespace JizuraLive.Pv;

/// <summary>一段时间内的歌词样式预设名（LyricStyle 预设：Classic/Neon/Outline/Minimal/Karaoke/Pop/Typewriter）。</summary>
public sealed class LyricStyleSpan
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Style { get; set; } = "";
}

public static class LyricStyleSpans
{
    /// <summary>按句首时刻落在哪个区间定样式；不在任何区间里的句子 Style=null（用全局样式）。</summary>
    public static IReadOnlyList<LyricClip> Apply(IReadOnlyList<LyricClip> clips, IReadOnlyList<LyricStyleSpan>? spans)
    {
        var result = new List<LyricClip>(clips.Count);
        foreach (var clip in clips)
        {
            string? style = null;
            if (spans is not null)
            {
                var t = clip.StartMs / 1000.0;
                foreach (var s in spans)
                    if (t >= s.Start && t < s.End) { style = s.Style; break; }
            }
            result.Add(clip with { Style = style });
        }
        return result;
    }
}
