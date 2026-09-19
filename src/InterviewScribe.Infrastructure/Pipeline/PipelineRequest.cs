using InterviewScribe.Core.Domain;

namespace InterviewScribe.Infrastructure.Pipeline;

public sealed record PipelineRequest(string SourcePath, string OutputDirectory);

public sealed record PipelineResult(
    string TxtPath,
    string SrtPath,
    string JsonPath,
    TranscriptDocument Document,
    IReadOnlyList<string> LogMessages,
    string OutputDirectory);
