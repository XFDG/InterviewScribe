namespace InterviewScribe.Core.Domain;

public sealed record OperationProgress(
    JobState State,
    string Message,
    double? Fraction = null,
    TimeSpan? Elapsed = null,
    TimeSpan? EstimatedRemaining = null,
    PipelinePhase Phase = PipelinePhase.None,
    double? PhaseFraction = null);

public enum PipelinePhase
{
    None,
    ProbeMedia,
    PrepareModel,
    ExtractAudio,
    MossDiarization,
    QwenRecognition,
    Validate,
    Export,
    Completed,
}
