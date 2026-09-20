using InterviewScribe.Core.Domain;
using InterviewScribe.Core.Export;

namespace InterviewScribe.Infrastructure.Pipeline;

public sealed record PipelineRequest(string SourcePath, string OutputDirectory)
{
    public IReadOnlyList<string> LanguageCodes { get; init; } = ["zh", "en"];
    public TranscriptFormattingOptions FormattingOptions { get; init; } = TranscriptFormattingOptions.Default;
    public TranscriptOutputFormat OutputFormats { get; init; } = TranscriptOutputFormat.Default;
    public TranscriptionMode Mode { get; init; } = TranscriptionMode.MossLocalFast;

    // Kept in memory for this run only. The UI never persists or logs this value,
    // and the pipeline passes it to the SDK child process through its environment.
    public string? SdkApiKey { get; init; }
}

public sealed record PipelineResult(
    string? TxtPath,
    string? SrtPath,
    string? JsonPath,
    TranscriptDocument Document,
    IReadOnlyList<string> LogMessages,
    string OutputDirectory)
{
    public TranscriptFormattingOptions FormattingOptions { get; init; } = TranscriptFormattingOptions.Default;
    public IReadOnlyDictionary<TranscriptOutputFormat, string> OutputPaths { get; init; } =
        new Dictionary<TranscriptOutputFormat, string>();
}
