using InterviewScribe.Core.Domain;

namespace InterviewScribe.Tests;

internal static class TestDocumentFactory
{
    public static TranscriptDocument Create(
        IReadOnlyList<TranscriptSegment>? segments = null,
        string sourceFileName = "技术面试.mp4",
        TimeSpan? mediaDuration = null,
        IReadOnlyList<string>? warnings = null,
        bool isPartial = false) =>
        new()
        {
            SourceFileName = sourceFileName,
            MediaDuration = mediaDuration ?? TimeSpan.FromMinutes(2),
            ModelName = "MOSS-Transcribe-Diarize-Q8_0",
            ModelRevision = "test-revision",
            EngineVersion = "test-engine",
            CreatedAt = new DateTimeOffset(2026, 9, 19, 12, 30, 0, TimeSpan.Zero),
            FullText = "Hello world你好世界",
            Segments = segments ??
            [
                new TranscriptSegment(0, 1_000, 1, "Hello"),
                new TranscriptSegment(1_200, 2_000, 1, "world"),
                new TranscriptSegment(4_000, 5_000, 2, "你好"),
                new TranscriptSegment(5_200, 6_000, 2, "世界")
            ],
            Warnings = warnings ?? [],
            IsPartial = isPartial
        };
}
