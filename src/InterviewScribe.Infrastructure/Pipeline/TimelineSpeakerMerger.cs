using InterviewScribe.Core.Domain;

namespace InterviewScribe.Infrastructure.Pipeline;

internal static class TimelineSpeakerMerger
{
    private const long MaximumGapMs = 1_200;
    private const long MaximumCombinedDurationMs = 15_000;

    public static IReadOnlyList<TranscriptSegment> Merge(
        IReadOnlyList<EngineSegmentDto> qwenSegments,
        IReadOnlyList<EngineSegmentDto> mossSegments)
    {
        ArgumentNullException.ThrowIfNull(qwenSegments);
        ArgumentNullException.ThrowIfNull(mossSegments);

        var speakerTimeline = mossSegments
            .Where(segment => segment is not null && segment.SpeakerId > 0)
            .OrderBy(segment => segment.StartMs)
            .ThenBy(segment => segment.EndMs)
            .ToArray();

        var assigned = qwenSegments
            .Where(segment => segment is not null && !string.IsNullOrWhiteSpace(segment.Text))
            .OrderBy(segment => segment.StartMs)
            .ThenBy(segment => segment.EndMs)
            .Select(segment => new TranscriptSegment(
                Math.Max(0, segment.StartMs),
                Math.Max(Math.Max(0, segment.StartMs), segment.EndMs),
                segment.SpeakerId > 0 ? segment.SpeakerId : SelectSpeaker(segment, speakerTimeline),
                segment.Text.Trim()))
            .ToArray();

        return Coalesce(assigned);
    }

    private static int SelectSpeaker(
        EngineSegmentDto segment,
        IReadOnlyList<EngineSegmentDto> speakerTimeline)
    {
        if (speakerTimeline.Count == 0)
        {
            return 0;
        }

        var start = Math.Max(0, segment.StartMs);
        var end = Math.Max(start + 1, segment.EndMs);
        var center = start + ((end - start) / 2d);
        var scores = new Dictionary<int, (long Overlap, double NearestDistance)>();

        foreach (var speakerSegment in speakerTimeline)
        {
            var speakerStart = Math.Max(0, speakerSegment.StartMs);
            var speakerEnd = Math.Max(speakerStart + 1, speakerSegment.EndMs);
            var overlap = Math.Max(0, Math.Min(end, speakerEnd) - Math.Max(start, speakerStart));
            var speakerCenter = speakerStart + ((speakerEnd - speakerStart) / 2d);
            var distance = Math.Abs(center - speakerCenter);

            if (scores.TryGetValue(speakerSegment.SpeakerId, out var current))
            {
                scores[speakerSegment.SpeakerId] = (
                    current.Overlap + overlap,
                    Math.Min(current.NearestDistance, distance));
            }
            else
            {
                scores[speakerSegment.SpeakerId] = (overlap, distance);
            }
        }

        return scores
            .OrderByDescending(item => item.Value.Overlap)
            .ThenBy(item => item.Value.NearestDistance)
            .ThenBy(item => item.Key)
            .First()
            .Key;
    }

    private static IReadOnlyList<TranscriptSegment> Coalesce(
        IReadOnlyList<TranscriptSegment> segments)
    {
        if (segments.Count < 2)
        {
            return segments;
        }

        var result = new List<TranscriptSegment>(segments.Count);
        var current = segments[0];
        for (var index = 1; index < segments.Count; index++)
        {
            var next = segments[index];
            var gap = next.StartMs - current.EndMs;
            var combinedDuration = Math.Max(current.EndMs, next.EndMs) - current.StartMs;
            var canCombine = current.SpeakerId == next.SpeakerId &&
                gap >= 0 &&
                gap <= MaximumGapMs &&
                combinedDuration <= MaximumCombinedDurationMs &&
                !EndsSentence(current.Text);

            if (!canCombine)
            {
                result.Add(current);
                current = next;
                continue;
            }

            current = current with
            {
                EndMs = Math.Max(current.EndMs, next.EndMs),
                Text = JoinText(current.Text, next.Text),
            };
        }

        result.Add(current);
        return result;
    }

    private static string JoinText(string first, string second)
    {
        var left = first.TrimEnd();
        var right = second.TrimStart();
        if (left.Length == 0)
        {
            return right;
        }

        if (right.Length == 0)
        {
            return left;
        }

        var addSpace = IsAsciiWordCharacter(left[^1]) && IsAsciiWordCharacter(right[0]);
        return addSpace ? $"{left} {right}" : left + right;
    }

    private static bool IsAsciiWordCharacter(char value) =>
        value <= 0x7f && (char.IsLetterOrDigit(value) || value is '\'' or '_');

    private static bool EndsSentence(string text)
    {
        var trimmed = text.TrimEnd();
        return trimmed.Length > 0 && trimmed[^1] is '.' or '?' or '!' or '。' or '？' or '！' or ';' or '；';
    }
}
