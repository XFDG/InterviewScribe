namespace InterviewScribe.Core.Domain;

public enum TranscriptionMode
{
    /// <summary>Whisper large-v3-turbo through Faster-Whisper/CTranslate2.</summary>
    WhisperTurboFast = 0,

    /// <summary>
    /// The existing all-in-one MOSS engine.  Kept as a fully local compatibility
    /// route and as the optional speaker-timeline provider for other modes.
    /// </summary>
    MossLocalFast = 1,

    /// <summary>Qwen3-ASR plus Qwen3 ForcedAligner via local Windows PyTorch CUDA.</summary>
    QwenLocalHighAccuracy = 2,
}
