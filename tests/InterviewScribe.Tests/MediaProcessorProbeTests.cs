using InterviewScribe.Infrastructure.Media;

namespace InterviewScribe.Tests;

public sealed class MediaProcessorProbeTests
{
    [Fact]
    public void ParseProbeResponse_WhenFormatDurationMissing_UsesAudioStreamDuration()
    {
        const string json = """
            {
              "streams": [{
                "index": 1,
                "codec_name": "aac",
                "sample_rate": "48000",
                "channels": 2,
                "duration": "12.75"
              }],
              "format": {}
            }
            """;

        var result = MediaProcessor.ParseProbeResponse(json);

        Assert.Equal(TimeSpan.FromSeconds(12.75), result.Duration);
        Assert.Equal("aac", result.CodecName);
        Assert.Equal(48_000, result.SampleRate);
    }

    [Fact]
    public void ParseProbeResponse_WhenDurationsMissing_UsesDurationTsAndTimeBase()
    {
        const string json = """
            {
              "streams": [{
                "index": 0,
                "codec_name": "opus",
                "sample_rate": "48000",
                "channels": 1,
                "duration_ts": 144000,
                "time_base": "1/48000"
              }]
            }
            """;

        var result = MediaProcessor.ParseProbeResponse(json);

        Assert.Equal(TimeSpan.FromSeconds(3), result.Duration);
        Assert.Equal("opus", result.CodecName);
    }

    [Fact]
    public void ParseProbeResponse_PrefersFormatDuration()
    {
        const string json = """
            {
              "streams": [{
                "index": 0,
                "codec_name": "aac",
                "sample_rate": "44100",
                "channels": 2,
                "duration": "9"
              }],
              "format": { "duration": "10.5" }
            }
            """;

        var result = MediaProcessor.ParseProbeResponse(json);

        Assert.Equal(TimeSpan.FromSeconds(10.5), result.Duration);
    }
}
