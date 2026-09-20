using InterviewScribe.Infrastructure.Pipeline;

namespace InterviewScribe.Tests;

public sealed class MossChunkingTests
{
    [Fact]
    public void Planner_RecordingAtLimit_UsesOriginalAsSingleChunk()
    {
        var chunks = MossChunkPlanner.Create(TimeSpan.FromMinutes(25));

        var chunk = Assert.Single(chunks);
        Assert.Equal(0, chunk.StartMs);
        Assert.Equal(TimeSpan.FromMinutes(25).TotalMilliseconds, chunk.DurationMs);
    }

    [Fact]
    public void Planner_LongRecording_CoversWholeTimelineWithBoundedOverlappingChunks()
    {
        var duration = TimeSpan.FromMinutes(55);

        var chunks = MossChunkPlanner.Create(duration);

        Assert.True(chunks.Count > 1);
        Assert.Equal(0, chunks[0].StartMs);
        Assert.Equal((long)duration.TotalMilliseconds, chunks[^1].EndMs);
        Assert.All(chunks, chunk => Assert.InRange(
            chunk.DurationMs,
            1,
            (long)MossChunkPlanner.MaximumChunkDuration.TotalMilliseconds));
        for (var index = 1; index < chunks.Count; index++)
        {
            Assert.Equal(
                (long)MossChunkPlanner.BoundaryOverlap.TotalMilliseconds,
                chunks[index - 1].EndMs - chunks[index].StartMs);
        }
    }

    [Fact]
    public void Planner_TwoHourRecording_DoesNotCreateTinyTailChunk()
    {
        var chunks = MossChunkPlanner.Create(TimeSpan.FromHours(2));

        Assert.Equal((long)TimeSpan.FromHours(2).TotalMilliseconds, chunks[^1].EndMs);
        Assert.All(chunks, chunk => Assert.True(chunk.DurationMs <= TimeSpan.FromMinutes(25).TotalMilliseconds));
        Assert.True(chunks[^1].DurationMs >= TimeSpan.FromMinutes(15).TotalMilliseconds);
    }

    [Fact]
    public void Merger_OffsetsTimeline_DeduplicatesBoundary_AndMapsSpeakersFromOverlap()
    {
        var first = new MossChunkPlan(0, 0, 120_000);
        var second = new MossChunkPlan(1, 90_000, 120_000);
        var firstResult = Result(
            "Vulkan0",
            new EngineSegmentDto(10_000, 20_000, 1, "introduction"),
            new EngineSegmentDto(96_000, 104_000, 2, "speaker two overlap"),
            new EngineSegmentDto(101_000, 106_000, 1, "duplicate boundary"));
        var secondResult = Result(
            "Vulkan0",
            new EngineSegmentDto(6_000, 14_000, 8, "speaker two overlap"),
            new EngineSegmentDto(14_000, 20_000, 7, "duplicate boundary"),
            new EngineSegmentDto(20_000, 30_000, 8, "after boundary"));

        var merged = MossChunkResultMerger.Merge(
            [new(first, firstResult), new(second, secondResult)],
            "meeting.mp4",
            TimeSpan.FromSeconds(210));

        Assert.False(merged.IsPartial);
        Assert.Equal("meeting.mp4", merged.SourceFileName);
        Assert.Equal(4, merged.Segments.Count);
        Assert.Equal(1, merged.Segments.Count(segment => segment.Text == "duplicate boundary"));
        var afterBoundary = Assert.Single(
            merged.Segments,
            segment => segment.Text == "after boundary");
        Assert.Equal(110_000, afterBoundary.StartMs);
        Assert.Equal(120_000, afterBoundary.EndMs);
        Assert.Equal(2, afterBoundary.SpeakerId);
        Assert.Contains(merged.Warnings, warning => warning.Contains("2 段", StringComparison.Ordinal));
    }

    [Fact]
    public void Merger_PropagatesPartialFlag()
    {
        var chunk = new MossChunkPlan(0, 0, 60_000);
        var result = Result(
            "Vulkan0",
            new EngineSegmentDto(0, 1_000, 1, "text")) with
        {
            IsPartial = true,
        };

        var merged = MossChunkResultMerger.Merge(
            [new(chunk, result)],
            "meeting.mp4",
            TimeSpan.FromMinutes(1));

        Assert.True(merged.IsPartial);
    }

    [Fact]
    public void Merger_TimestampJitterAcrossOwnershipBoundary_DoesNotDropBothCopies()
    {
        var first = new MossChunkPlan(0, 0, 120_000);
        var second = new MossChunkPlan(1, 90_000, 120_000);
        var firstResult = Result(
            "Vulkan0",
            new EngineSegmentDto(104_000, 108_000, 1, "must not disappear"));
        var secondResult = Result(
            "Vulkan0",
            new EngineSegmentDto(12_000, 16_000, 9, "must not disappear"));

        var merged = MossChunkResultMerger.Merge(
            [new(first, firstResult), new(second, secondResult)],
            "meeting.mp4",
            TimeSpan.FromSeconds(210));

        Assert.Single(merged.Segments, segment => segment.Text == "must not disappear");
    }

    [Fact]
    public void Merger_DeferredBoundaryText_IsNotDroppedByFarLaterRepeatedText()
    {
        var first = new MossChunkPlan(0, 0, 120_000);
        var second = new MossChunkPlan(1, 90_000, 120_000);
        var firstResult = Result(
            "Vulkan0",
            new EngineSegmentDto(108_000, 110_000, 1, "好的"));
        var secondResult = Result(
            "Vulkan0",
            new EngineSegmentDto(100_000, 101_000, 7, "好的"));

        var merged = MossChunkResultMerger.Merge(
            [new(first, firstResult), new(second, secondResult)],
            "meeting.mp4",
            TimeSpan.FromSeconds(210));

        Assert.Equal(2, merged.Segments.Count(segment => segment.Text == "好的"));
    }

    [Fact]
    public void Merger_DifferentBoundaryRenderings_KeepOnlyOwnedTimelineCopy()
    {
        var first = new MossChunkPlan(0, 0, 120_000);
        var second = new MossChunkPlan(1, 90_000, 120_000);
        var firstResult = Result(
            "Vulkan0",
            new EngineSegmentDto(104_000, 108_000, 1, "Kubernetes controller"));
        var secondResult = Result(
            "Vulkan0",
            new EngineSegmentDto(14_000, 19_000, 7, "K8s controller"));

        var merged = MossChunkResultMerger.Merge(
            [new(first, firstResult), new(second, secondResult)],
            "meeting.mp4",
            TimeSpan.FromSeconds(210));

        Assert.Single(
            merged.Segments,
            segment => segment.Text is "Kubernetes controller" or "K8s controller");
        Assert.Contains(merged.Segments, segment => segment.Text == "K8s controller");
        Assert.DoesNotContain(merged.Segments, segment => segment.Text == "Kubernetes controller");
    }

    [Fact]
    public void Merger_AdjacentDifferentBoundaryUtterance_IsNotDiscardedAsCoverage()
    {
        var first = new MossChunkPlan(0, 0, 120_000);
        var second = new MossChunkPlan(1, 90_000, 120_000);
        var firstResult = Result(
            "Vulkan0",
            new EngineSegmentDto(104_000, 106_000, 1, "first utterance"),
            new EngineSegmentDto(106_000, 108_000, 1, "second utterance"));
        var secondResult = Result(
            "Vulkan0",
            new EngineSegmentDto(16_000, 18_000, 7, "second utterance"));

        var merged = MossChunkResultMerger.Merge(
            [new(first, firstResult), new(second, secondResult)],
            "meeting.mp4",
            TimeSpan.FromSeconds(210));

        Assert.Contains(merged.Segments, segment => segment.Text == "first utterance");
        Assert.Single(merged.Segments, segment => segment.Text == "second utterance");
    }

    [Fact]
    public void Merger_DeduplicatesBoundaryCopiesSeparatedByAnotherSegment()
    {
        var first = new MossChunkPlan(0, 0, 120_000);
        var second = new MossChunkPlan(1, 90_000, 120_000);
        var firstResult = Result(
            "Vulkan0",
            new EngineSegmentDto(100_000, 104_000, 1, "shared boundary sentence"));
        var secondResult = Result(
            "Vulkan0",
            new EngineSegmentDto(15_000, 16_000, 8, "short interjection"),
            new EngineSegmentDto(16_000, 110_000 - 90_000, 7, "shared boundary sentence"));

        var merged = MossChunkResultMerger.Merge(
            [new(first, firstResult), new(second, secondResult)],
            "meeting.mp4",
            TimeSpan.FromSeconds(210));

        Assert.Single(
            merged.Segments,
            segment => segment.Text == "shared boundary sentence");
        Assert.Contains(merged.Segments, segment => segment.Text == "short interjection");
    }

    [Fact]
    public void Merger_SpeakerWithoutOverlapEvidence_GetsNewIdAndWarning()
    {
        var first = new MossChunkPlan(0, 0, 120_000);
        var second = new MossChunkPlan(1, 90_000, 120_000);
        var firstResult = Result(
            "Vulkan0",
            new EngineSegmentDto(10_000, 20_000, 1, "first speaker"));
        var secondResult = Result(
            "Vulkan0",
            new EngineSegmentDto(20_000, 30_000, 7, "new speaker after overlap"));

        var merged = MossChunkResultMerger.Merge(
            [new(first, firstResult), new(second, secondResult)],
            "meeting.mp4",
            TimeSpan.FromSeconds(210));

        Assert.Equal(2, merged.Segments[^1].SpeakerId);
        Assert.Contains(
            merged.Warnings,
            warning => warning.Contains("无法从重叠区可靠关联", StringComparison.Ordinal));
    }

    private static EngineResultDto Result(string backend, params EngineSegmentDto[] segments) =>
        new(
            "chunk.wav",
            120_000,
            "MOSS",
            "test-engine",
            backend,
            "zh",
            string.Join(' ', segments.Select(segment => segment.Text)),
            string.Empty,
            segments,
            [],
            false);
}
