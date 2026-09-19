namespace InterviewScribe.Core.Domain;

public sealed record TranscriptSegment(
    long StartMs,
    long EndMs,
    int SpeakerId,
    string Text);

