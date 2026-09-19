namespace InterviewScribe.Infrastructure.Pipeline;

internal sealed record EngineResultDto(
    string SourceFileName,
    long DurationMs,
    string Model,
    string EngineVersion,
    string Backend,
    string Language,
    string FullText,
    string RawText,
    IReadOnlyList<EngineSegmentDto> Segments,
    IReadOnlyList<string> Warnings,
    bool IsPartial);

internal sealed record EngineSegmentDto(long StartMs, long EndMs, int SpeakerId, string Text);
