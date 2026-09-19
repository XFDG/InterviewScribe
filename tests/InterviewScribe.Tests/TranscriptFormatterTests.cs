using System.Text.Json;
using InterviewScribe.Core.Domain;
using InterviewScribe.Core.Export;

namespace InterviewScribe.Tests;

public sealed class TranscriptFormatterTests
{
    [Fact]
    public void ToTxt_FormatsMetadataWarningsAndReadableMergedSegments()
    {
        var document = TestDocumentFactory.Create(
            warnings: ["说话人判断仅供参考"],
            isPartial: true);
        var speakerNames = new Dictionary<int, string>
        {
            [1] = " 候选人 ",
            [2] = "面试官"
        };

        var text = TranscriptFormatter.ToTxt(document, speakerNames);

        Assert.Contains("# 面试录屏转写", text);
        Assert.Contains("文件：技术面试.mp4", text);
        Assert.Contains("模型：MOSS-Transcribe-Diarize-Q8_0 (test-revision)", text);
        Assert.Contains("状态：不完整结果", text);
        Assert.Contains("- 说话人判断仅供参考", text);
        Assert.Contains("[00:00:00.000 - 00:00:02.000] 候选人", text);
        Assert.Contains("Hello world", text);
        Assert.Contains("[00:00:04.000 - 00:00:06.000] 面试官", text);
        Assert.Contains("你好世界", text);
        Assert.EndsWith(Environment.NewLine, text);
    }

    [Fact]
    public void ToSrt_FiltersInvalidSegmentsAndUsesFallbackSpeakerName()
    {
        TranscriptSegment[] segments =
        [
            new(0, 1_250, 0, "  中文 and English  "),
            new(2_000, 2_000, 1, "zero duration"),
            new(3_000, 4_000, 2, "   ")
        ];

        var srt = TranscriptFormatter.ToSrt(TestDocumentFactory.Create(segments));

        Assert.Contains("00:00:00,000 --> 00:00:01,250", srt);
        Assert.Contains("[说话人（未识别）] 中文 and English", srt);
        Assert.DoesNotContain("zero duration", srt);
        Assert.DoesNotContain("00:00:03,000", srt);
        Assert.DoesNotContain($"{Environment.NewLine}2{Environment.NewLine}", srt);
    }

    [Fact]
    public void ToJson_UsesCamelCaseAndKeepsUnicodeData()
    {
        var document = TestDocumentFactory.Create();

        var json = TranscriptFormatter.ToJson(document);
        using var parsed = JsonDocument.Parse(json);

        var root = parsed.RootElement;
        Assert.Equal("技术面试.mp4", root.GetProperty("sourceFileName").GetString());
        Assert.Equal("Hello world你好世界", root.GetProperty("fullText").GetString());
        Assert.Equal(4, root.GetProperty("segments").GetArrayLength());
        Assert.False(root.TryGetProperty("SourceFileName", out _));
    }

    [Theory]
    [InlineData(-1L, true, "00:00:00.000")]
    [InlineData(3_723_004L, true, "01:02:03.004")]
    [InlineData(90_061_000L, false, "25:01:01")]
    public void FormatClock_ClampsAndFormatsDurations(long milliseconds, bool millisecondsIncluded, string expected)
    {
        Assert.Equal(expected, TranscriptFormatter.FormatClock(milliseconds, millisecondsIncluded));
    }

    [Fact]
    public void Formatters_RejectNullDocument()
    {
        Assert.Throws<ArgumentNullException>(() => TranscriptFormatter.ToTxt(null!));
        Assert.Throws<ArgumentNullException>(() => TranscriptFormatter.ToSrt(null!));
    }
}
