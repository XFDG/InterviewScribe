using System.Text;
using System.Text.Json;
using InterviewScribe.Core.Domain;

namespace InterviewScribe.Core.Export;

public static class TranscriptFormatter
{
    public static string ToTxt(
        TranscriptDocument document,
        IReadOnlyDictionary<int, string>? speakerNames = null,
        TranscriptFormattingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= TranscriptFormattingOptions.Default;
        var builder = new StringBuilder();
        builder.AppendLine("# 面试录屏转写");
        builder.Append("文件：").AppendLine(document.SourceFileName);
        builder.Append("时长：").AppendLine(FormatClock((long)document.MediaDuration.TotalMilliseconds, includeMilliseconds: false));
        builder.Append("模型：").Append(document.ModelName).Append(" (").Append(document.ModelRevision).AppendLine(")");
        builder.Append("生成时间：").AppendLine(document.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));

        if (document.IsPartial)
        {
            builder.AppendLine("状态：不完整结果，请勿当作完整转写使用");
        }

        if (document.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("注意：");
            foreach (var warning in document.Warnings)
            {
                builder.Append("- ").AppendLine(warning);
            }
        }

        builder.AppendLine();
        foreach (var segment in MergeForReading(document.Segments))
        {
            if (options.IncludeTimestamps)
            {
                builder.Append('[')
                    .Append(FormatClock(segment.StartMs, includeMilliseconds: true))
                    .Append(" - ")
                    .Append(FormatClock(segment.EndMs, includeMilliseconds: true))
                    .Append(']');
            }

            if (options.IncludeSpeakers)
            {
                if (options.IncludeTimestamps)
                {
                    builder.Append(' ');
                }

                builder.Append(ResolveSpeakerName(segment.SpeakerId, speakerNames));
            }

            if (options.IncludeTimestamps || options.IncludeSpeakers)
            {
                builder.AppendLine();
            }

            builder.AppendLine(segment.Text.Trim());
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string ToSrt(
        TranscriptDocument document,
        IReadOnlyDictionary<int, string>? speakerNames = null,
        TranscriptFormattingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= TranscriptFormattingOptions.Default;
        var builder = new StringBuilder();
        var outputIndex = 0;

        foreach (var segment in document.Segments.Where(item => item.EndMs > item.StartMs && !string.IsNullOrWhiteSpace(item.Text)))
        {
            outputIndex++;
            builder.AppendLine(outputIndex.ToString());
            builder.Append(FormatSrtClock(segment.StartMs))
                .Append(" --> ")
                .AppendLine(FormatSrtClock(segment.EndMs));
            if (options.IncludeSpeakers)
            {
                builder.Append('[')
                    .Append(ResolveSpeakerName(segment.SpeakerId, speakerNames))
                    .Append("] ");
            }

            builder.AppendLine(segment.Text.Trim());
            builder.AppendLine();
        }

        return builder.ToString();
    }

    public static string ToMarkdown(
        TranscriptDocument document,
        IReadOnlyDictionary<int, string>? speakerNames = null,
        TranscriptFormattingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= TranscriptFormattingOptions.Default;
        var builder = new StringBuilder();
        builder.AppendLine("# 面试录屏转写");
        builder.AppendLine();
        builder.Append("- **文件：** ").AppendLine(EscapeMarkdown(document.SourceFileName));
        builder.Append("- **时长：** ")
            .AppendLine(FormatClock((long)document.MediaDuration.TotalMilliseconds, includeMilliseconds: false));
        builder.Append("- **模型：** ")
            .Append(EscapeMarkdown(document.ModelName))
            .Append(" (`")
            .Append(document.ModelRevision.Replace("`", "\\`", StringComparison.Ordinal))
            .AppendLine("`)");
        builder.Append("- **生成时间：** ")
            .AppendLine(document.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));

        if (document.IsPartial)
        {
            builder.AppendLine("- **状态：** 不完整结果，请勿当作完整转写使用");
        }

        if (document.Warnings.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 注意");
            builder.AppendLine();
            foreach (var warning in document.Warnings)
            {
                builder.Append("- ").AppendLine(EscapeMarkdown(warning));
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 转写内容");
        builder.AppendLine();
        foreach (var segment in MergeForReading(document.Segments))
        {
            if (options.IncludeTimestamps || options.IncludeSpeakers)
            {
                builder.Append("### ");
                if (options.IncludeTimestamps)
                {
                    builder.Append('[')
                        .Append(FormatClock(segment.StartMs, includeMilliseconds: true))
                        .Append(" - ")
                        .Append(FormatClock(segment.EndMs, includeMilliseconds: true))
                        .Append(']');
                }

                if (options.IncludeSpeakers)
                {
                    if (options.IncludeTimestamps)
                    {
                        builder.Append(' ');
                    }

                    builder.Append(EscapeMarkdown(ResolveSpeakerName(segment.SpeakerId, speakerNames)));
                }

                builder.AppendLine();
                builder.AppendLine();
            }

            builder.AppendLine(segment.Text.Trim());
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    public static string ToJson(TranscriptDocument document) =>
        JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

    public static string FormatClock(long milliseconds, bool includeMilliseconds)
    {
        var value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        var hours = (long)value.TotalHours;
        return includeMilliseconds
            ? $"{hours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}"
            : $"{hours:00}:{value.Minutes:00}:{value.Seconds:00}";
    }

    private static string FormatSrtClock(long milliseconds)
    {
        var value = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        var hours = (long)value.TotalHours;
        return $"{hours:00}:{value.Minutes:00}:{value.Seconds:00},{value.Milliseconds:000}";
    }

    private static string ResolveSpeakerName(int speakerId, IReadOnlyDictionary<int, string>? speakerNames)
    {
        if (speakerNames is not null && speakerNames.TryGetValue(speakerId, out var name) && !string.IsNullOrWhiteSpace(name))
        {
            return name.Trim();
        }

        return speakerId > 0 ? $"说话人 {speakerId}" : "说话人（未识别）";
    }

    private static IEnumerable<TranscriptSegment> MergeForReading(IReadOnlyList<TranscriptSegment> segments)
    {
        TranscriptSegment? pending = null;
        foreach (var current in segments.Where(item => !string.IsNullOrWhiteSpace(item.Text)))
        {
            if (pending is null)
            {
                pending = current;
                continue;
            }

            var gap = current.StartMs - pending.EndMs;
            var combinedLength = pending.Text.Length + current.Text.Length;
            if (pending.SpeakerId == current.SpeakerId && gap is >= 0 and <= 1_500 && combinedLength <= 420)
            {
                pending = pending with
                {
                    EndMs = Math.Max(pending.EndMs, current.EndMs),
                    Text = JoinText(pending.Text, current.Text)
                };
                continue;
            }

            yield return pending;
            pending = current;
        }

        if (pending is not null)
        {
            yield return pending;
        }
    }

    private static string JoinText(string left, string right)
    {
        var trimmedLeft = left.Trim();
        var trimmedRight = right.Trim();
        if (trimmedLeft.Length == 0)
        {
            return trimmedRight;
        }

        if (trimmedRight.Length == 0)
        {
            return trimmedLeft;
        }

        var needsSpace = IsAsciiWordChar(trimmedLeft[^1]) && IsAsciiWordChar(trimmedRight[0]);
        return needsSpace ? $"{trimmedLeft} {trimmedRight}" : trimmedLeft + trimmedRight;
    }

    private static bool IsAsciiWordChar(char value) => value is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static string EscapeMarkdown(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("*", "\\*", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
}
