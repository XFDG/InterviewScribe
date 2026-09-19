namespace InterviewScribe.Core.Export;

public sealed record TranscriptFormattingOptions
{
    public static TranscriptFormattingOptions Default { get; } = new();

    public bool IncludeTimestamps { get; init; } = true;
    public bool IncludeSpeakers { get; init; } = true;
}
