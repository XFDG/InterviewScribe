using InterviewScribe.Infrastructure.Pipeline;

namespace InterviewScribe.Tests;

public sealed class TimelineSpeakerMergerTests
{
    [Fact]
    public void Merge_AssignsSpeakerWithLargestOverlap()
    {
        EngineSegmentDto[] qwen =
        [
            new(800, 2_200, 0, "你好"),
        ];
        EngineSegmentDto[] moss =
        [
            new(0, 1_000, 1, "ignored"),
            new(1_000, 3_000, 2, "ignored"),
        ];

        var result = TimelineSpeakerMerger.Merge(qwen, moss);

        var segment = Assert.Single(result);
        Assert.Equal(2, segment.SpeakerId);
        Assert.Equal(800, segment.StartMs);
        Assert.Equal(2_200, segment.EndMs);
        Assert.Equal("你好", segment.Text);
    }

    [Fact]
    public void Merge_WhenThereIsNoOverlap_UsesNearestSpeaker()
    {
        EngineSegmentDto[] qwen =
        [
            new(2_000, 2_500, 0, "hello"),
        ];
        EngineSegmentDto[] moss =
        [
            new(0, 800, 1, "ignored"),
            new(3_000, 3_600, 2, "ignored"),
        ];

        var result = TimelineSpeakerMerger.Merge(qwen, moss);

        Assert.Equal(2, Assert.Single(result).SpeakerId);
    }

    [Fact]
    public void Merge_CoalescesAdjacentWordsForSameSpeaker()
    {
        EngineSegmentDto[] qwen =
        [
            new(0, 400, 0, "Hello"),
            new(450, 900, 0, "world"),
            new(1_000, 1_300, 0, "你"),
            new(1_350, 1_700, 0, "好"),
        ];
        EngineSegmentDto[] moss =
        [
            new(0, 2_000, 1, "ignored"),
        ];

        var result = TimelineSpeakerMerger.Merge(qwen, moss);

        var segment = Assert.Single(result);
        Assert.Equal("Hello world你好", segment.Text);
        Assert.Equal(1, segment.SpeakerId);
        Assert.Equal(0, segment.StartMs);
        Assert.Equal(1_700, segment.EndMs);
    }

    [Fact]
    public void Merge_DoesNotCoalesceAcrossSpeakerBoundaryOrSentenceEnd()
    {
        EngineSegmentDto[] qwen =
        [
            new(0, 500, 0, "First."),
            new(550, 900, 0, "Second"),
            new(1_050, 1_400, 0, "Third"),
        ];
        EngineSegmentDto[] moss =
        [
            new(0, 1_000, 1, "ignored"),
            new(1_000, 2_000, 2, "ignored"),
        ];

        var result = TimelineSpeakerMerger.Merge(qwen, moss);

        Assert.Collection(
            result,
            item =>
            {
                Assert.Equal(1, item.SpeakerId);
                Assert.Equal("First.", item.Text);
            },
            item =>
            {
                Assert.Equal(1, item.SpeakerId);
                Assert.Equal("Second", item.Text);
            },
            item =>
            {
                Assert.Equal(2, item.SpeakerId);
                Assert.Equal("Third", item.Text);
            });
    }

    [Fact]
    public void Merge_WithoutSpeakerTimeline_UsesUnknownSpeaker()
    {
        EngineSegmentDto[] qwen =
        [
            new(0, 500, 0, "text"),
        ];

        var result = TimelineSpeakerMerger.Merge(qwen, []);

        Assert.Equal(0, Assert.Single(result).SpeakerId);
    }

    [Fact]
    public void Merge_DoesNotInventTextSplitForCoarseSegment()
    {
        EngineSegmentDto[] qwen =
        [
            new(0, 2_000, 0, "Hello there"),
        ];
        EngineSegmentDto[] moss =
        [
            new(0, 1_000, 1, "ignored"),
            new(1_000, 2_000, 2, "ignored"),
        ];

        var result = TimelineSpeakerMerger.Merge(qwen, moss);

        var segment = Assert.Single(result);
        Assert.Equal(1, segment.SpeakerId);
        Assert.Equal(0, segment.StartMs);
        Assert.Equal(2_000, segment.EndMs);
        Assert.Equal("Hello there", segment.Text);
    }

    [Fact]
    public void Merge_WhenWordOnlyBrushesSpeakerBoundary_DoesNotInventTextSplit()
    {
        EngineSegmentDto[] qwen =
        [
            new(800, 1_200, 0, "hello"),
        ];
        EngineSegmentDto[] moss =
        [
            new(0, 1_000, 1, "ignored"),
            new(1_000, 2_000, 2, "ignored"),
        ];

        var result = TimelineSpeakerMerger.Merge(qwen, moss);

        Assert.Equal("hello", Assert.Single(result).Text);
    }

    [Fact]
    public void Merge_PreservesSpeakerAssignedBySdkSidecar()
    {
        EngineSegmentDto[] qwen =
        [
            new(900, 1_600, 2, "short answer"),
        ];
        EngineSegmentDto[] moss =
        [
            new(0, 1_200, 1, "ignored"),
            new(1_200, 2_000, 2, "ignored"),
        ];

        var result = TimelineSpeakerMerger.Merge(qwen, moss);

        Assert.Equal(2, Assert.Single(result).SpeakerId);
    }

    [Fact]
    public void Merge_DoesNotCoalesceOverlappingSegments()
    {
        EngineSegmentDto[] qwen =
        [
            new(0, 900, 1, "first"),
            new(800, 1_300, 1, "second"),
        ];

        var result = TimelineSpeakerMerger.Merge(qwen, []);

        Assert.Equal(2, result.Count);
    }
}
