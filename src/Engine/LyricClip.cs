namespace JizuraLive.Engine;

public sealed record LyricClip(
    string Id, long StartMs, long EndMs, string Text, bool OutOfRange,
    IReadOnlyList<KrcWord>? Words = null, string? Style = null);

public readonly record struct LyricSample(
    string ClipId, string Text, long StartMs, long EndMs, bool OutOfRange)
{
    public long LocalMs(double timeSeconds) => (long)(timeSeconds * 1000.0) - StartMs;

    public double ProgressAt(double timeSeconds)
    {
        if (EndMs <= StartMs) return 1.0;
        return Math.Clamp((double)LocalMs(timeSeconds) / (EndMs - StartMs), 0.0, 1.0);
    }
}

public sealed record LrcImportIssue(int LineNumber, string Reason);

public sealed record LrcImportResult(
    IReadOnlyList<LyricClip> Clips, IReadOnlyList<LrcImportIssue> Issues);
