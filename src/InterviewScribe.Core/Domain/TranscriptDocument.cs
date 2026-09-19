namespace InterviewScribe.Core.Domain;

public sealed record TranscriptDocument
{
    public required string SourceFileName { get; init; }
    public required TimeSpan MediaDuration { get; init; }
    public required string ModelName { get; init; }
    public required string ModelRevision { get; init; }
    public required string EngineVersion { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required string FullText { get; init; }
    public required IReadOnlyList<TranscriptSegment> Segments { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool IsPartial { get; init; }
}

