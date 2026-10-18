namespace JizuraLive.Engine;

public sealed record LyricClip(
    string Id, long StartMs, long EndMs, string Text, bool OutOfRange,
    IReadOnlyList<KrcWord>? Words = null, string? Style = null);

public readonly record struct LyricSample(
    string ClipId, string Text, long StartMs, long EndMs, bool OutOfRange)
{

}

public sealed record LrcImportIssue(int LineNumber, string Reason);

public sealed record LrcImportResult(
    IReadOnlyList<LyricClip> Clips, IReadOnlyList<LrcImportIssue> Issues);
