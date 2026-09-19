using InterviewScribe.Core.Domain;
using InterviewScribe.Core.Validation;

namespace InterviewScribe.Tests;

public sealed class TranscriptValidatorTests
{
    [Fact]
    public void Validate_AcceptsOrderedUsefulSegments()
    {
        TranscriptSegment[] segments =
        [
            new(0, 1_000, 1, "你好"),
            new(1_000, 64_999, 2, "Hello")
        ];

        var result = TranscriptValidator.Validate(segments, TimeSpan.FromMinutes(1));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Validate_ReportsEngineAndEmptyTimelineErrors()
    {
        var result = TranscriptValidator.Validate([], TimeSpan.FromMinutes(1), "  out of memory  ");

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains("识别引擎返回错误：out of memory", result.Errors);
        Assert.Contains("识别结果没有可用的时间轴片段。", result.Errors);
    }

    [Fact]
    public void Validate_DistinguishesFatalTimelineErrorsFromQualityWarnings()
    {
        TranscriptSegment[] segments =
        [
            new(-10, 100, 0, "bad\uFFFDtext"),
            new(300, 200, 1, "  "),
            new(150, 70_000, 2, "late")
        ];

        var result = TranscriptValidator.Validate(segments, TimeSpan.FromMinutes(1));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("开始时间小于 0", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("结束时间早于开始时间", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("时间轴发生逆序", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("没有有效的说话人编号", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("包含无法解码的字符", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("没有文字", StringComparison.Ordinal));
        Assert.Contains(result.Warnings, warning => warning.Contains("略超出视频时长", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AllowsQualityWarningsWithoutMarkingResultInvalid()
    {
        TranscriptSegment[] segments = [new(0, 1_000, 0, "")];

        var result = TranscriptValidator.Validate(segments, TimeSpan.FromSeconds(1));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
        Assert.Equal(2, result.Warnings.Count);
    }
}
