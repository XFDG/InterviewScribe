namespace InterviewScribe.Core.Domain;

public sealed record OperationProgress(
    JobState State,
    string Message,
    double? Fraction = null,
    TimeSpan? Elapsed = null);

