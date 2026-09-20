using InterviewScribe.Infrastructure.Media;

namespace InterviewScribe.Tests;

public sealed class MediaProcessorTests
{
    [Fact]
    public void BuildExtractionArguments_SingleTrack_MapsFirstAudioStream()
    {
        var arguments = MediaProcessor.BuildExtractionArguments("input.mp4", "output.wav", 1);

        Assert.Contains("0:a:0", arguments);
        Assert.DoesNotContain("-filter_complex", arguments);
        Assert.Equal("output.wav", arguments[^1]);
    }

    [Fact]
    public void BuildExtractionArguments_MultipleTracks_MixesEveryAudioStream()
    {
        var arguments = MediaProcessor.BuildExtractionArguments("input.mp4", "output.wav", 3);

        var filterIndex = Array.IndexOf(arguments.ToArray(), "-filter_complex");
        Assert.True(filterIndex >= 0);
        Assert.Equal(
            "[0:a:0][0:a:1][0:a:2]amix=inputs=3:duration=longest:" +
            "dropout_transition=0:normalize=1[aout]",
            arguments[filterIndex + 1]);
        Assert.Contains("[aout]", arguments);
        Assert.DoesNotContain("0:a:0", arguments);
    }

    [Fact]
    public void BuildExtractionArguments_NoTracks_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MediaProcessor.BuildExtractionArguments("input.mp4", "output.wav", 0));
    }

    [Fact]
    public void BuildSegmentExtractionArguments_UsesBoundedPrecisePcmWindow()
    {
        var arguments = MediaProcessor.BuildSegmentExtractionArguments(
            "whole.wav",
            "chunk.wav",
            TimeSpan.FromMinutes(24.5),
            TimeSpan.FromMinutes(25));

        Assert.Equal(
            [
                "-nostdin",
                "-hide_banner",
                "-loglevel", "error",
                "-y",
                "-ss", "1470",
                "-i", "whole.wav",
                "-t", "1500",
                "-map", "0:a:0",
                "-vn",
                "-sn",
                "-dn",
                "-ac", "1",
                "-ar", "16000",
                "-c:a", "pcm_s16le",
                "chunk.wav",
            ],
            arguments);
    }

    [Fact]
    public void BuildSegmentExtractionArguments_RejectsWindowOverGpuLimit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MediaProcessor.BuildSegmentExtractionArguments(
                "whole.wav",
                "chunk.wav",
                TimeSpan.Zero,
                TimeSpan.FromMinutes(25) + TimeSpan.FromMilliseconds(1)));
    }
}
