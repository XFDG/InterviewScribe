namespace InterviewScribe.Core.Domain;

public sealed record MediaInfo(
    TimeSpan Duration,
    int AudioStreamCount,
    int SelectedAudioStream,
    int SampleRate,
    int Channels,
    string CodecName);

