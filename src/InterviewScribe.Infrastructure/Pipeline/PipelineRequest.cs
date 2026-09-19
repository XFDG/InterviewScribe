using InterviewScribe.Core.Domain;
using InterviewScribe.Core.Export;

namespace InterviewScribe.Infrastructure.Pipeline;

public sealed record PipelineRequest(string SourcePath, string OutputDirectory)
{
    public IReadOnlyList<string> LanguageCodes { get; init; } = ["zh", "en"];
    public TranscriptFormattingOptions FormattingOptions { get; init; } = TranscriptFormattingOptions.Default;
}

public sealed record PipelineResult(
    string TxtPath,
    string SrtPath,
    string JsonPath,
    TranscriptDocument Document,
    IReadOnlyList<string> LogMessages,
    string OutputDirectory)
{
    public TranscriptFormattingOptions FormattingOptions { get; init; } = TranscriptFormattingOptions.Default;
}
