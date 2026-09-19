namespace InterviewScribe.Core.Domain;

public enum JobState
{
    Idle,
    WaitingForModel,
    DownloadingModel,
    VerifyingModel,
    ProbingMedia,
    ExtractingAudio,
    ReadyToTranscribe,
    Transcribing,
    ValidatingResult,
    Exporting,
    Completed,
    Cancelling,
    Cancelled,
    Failed,
    Interrupted
}

