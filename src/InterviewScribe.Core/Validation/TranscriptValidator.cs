using InterviewScribe.Core.Domain;

namespace InterviewScribe.Core.Validation;

public static class TranscriptValidator
{
    public static TranscriptValidationResult Validate(
        IReadOnlyList<TranscriptSegment>? segments,
        TimeSpan mediaDuration,
        string? rootError = null)
    {
        var errors = new List<string>();
        var warnings = new List<string>();

        if (!string.IsNullOrWhiteSpace(rootError))
        {
            errors.Add($"识别引擎返回错误：{rootError.Trim()}");
        }

        if (segments is null || segments.Count == 0)
        {
            errors.Add("识别结果没有可用的时间轴片段。");
            return new(false, errors, warnings);
        }

        long previousStart = -1;
        var maxAllowed = (long)Math.Ceiling(mediaDuration.TotalMilliseconds) + 5_000;
        var missingSpeakerCount = 0;
        var emptyTextCount = 0;
        var undecodableTextCount = 0;
        var overDurationCount = 0;

        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var label = $"第 {index + 1} 段";

            if (segment.StartMs < 0)
            {
                errors.Add($"{label}的开始时间小于 0。");
            }

            if (segment.EndMs < segment.StartMs)
            {
                errors.Add($"{label}的结束时间早于开始时间。");
            }

            if (segment.EndMs > maxAllowed)
            {
                overDurationCount++;
            }

            if (segment.SpeakerId <= 0)
            {
                missingSpeakerCount++;
            }

            if (string.IsNullOrWhiteSpace(segment.Text))
            {
                emptyTextCount++;
            }

            if (segment.Text.Contains('\uFFFD'))
            {
                undecodableTextCount++;
            }

            if (previousStart > segment.StartMs)
            {
                errors.Add($"{label}的时间轴发生逆序。");
            }

            previousStart = segment.StartMs;
        }

        if (overDurationCount > 0)
        {
            warnings.Add($"{overDurationCount} 个时间段略超出视频时长。");
        }

        if (missingSpeakerCount > 0)
        {
            warnings.Add($"{missingSpeakerCount} 个时间段没有有效的说话人编号。");
        }

        if (emptyTextCount > 0)
        {
            warnings.Add($"{emptyTextCount} 个时间段没有文字。");
        }

        if (undecodableTextCount > 0)
        {
            warnings.Add($"{undecodableTextCount} 个时间段包含无法解码的字符。");
        }

        return new(errors.Count == 0, errors, warnings.Distinct().ToArray());
    }
}
