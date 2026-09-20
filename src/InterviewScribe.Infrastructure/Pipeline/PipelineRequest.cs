using InterviewScribe.Core.Domain;
using InterviewScribe.Core.Export;

namespace InterviewScribe.Infrastructure.Pipeline;

public sealed record PipelineRequest(string SourcePath, string OutputDirectory)
{
    public IReadOnlyList<string> LanguageCodes { get; init; } = ["zh", "en"];
    public TranscriptFormattingOptions FormattingOptions { get; init; } = TranscriptFormattingOptions.Default;
    public TranscriptOutputFormat OutputFormats { get; init; } = TranscriptOutputFormat.Default;
    public TranscriptionMode Mode { get; init; } = TranscriptionMode.WhisperTurboFast;

    /// <summary>
    /// Requests the retained local MOSS speaker track as a second pass.  It is
    /// independent from whether speaker labels are printed in a chosen export:
    /// callers can deliberately compute a structured JSON track without showing
    /// labels in TXT/MD/PDF.
    /// </summary>
    public bool EnableSpeakerDiarization { get; init; }

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
