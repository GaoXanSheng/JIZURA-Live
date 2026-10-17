using JizuraLive.Engine;

namespace JizuraLive.Pv;

public sealed class LyricStyleSpan
{
    public double Start { get; set; }
    public double End { get; set; }
    public string Style { get; set; } = "";
}

public static class LyricStyleSpans
{
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
