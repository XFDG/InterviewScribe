namespace InterviewScribe.Core.Validation;

public sealed record TranscriptValidationResult(
    bool IsValid,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings);

