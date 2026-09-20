using System.Text;

namespace InterviewScribe.Infrastructure.Pipeline;

internal sealed record MossChunkEngineResult(MossChunkPlan Chunk, EngineResultDto Result);

internal static class MossChunkResultMerger
{
    private const long DuplicateMaximumGapMs = 2_000;

    internal static EngineResultDto Merge(
        IReadOnlyList<MossChunkEngineResult> chunkResults,
        string sourceFileName,
        TimeSpan mediaDuration)
    {
        ArgumentNullException.ThrowIfNull(chunkResults);
        if (chunkResults.Count == 0)
        {
            throw new ArgumentException("至少需要一段 MOSS 识别结果。", nameof(chunkResults));
        }

        var ordered = chunkResults.OrderBy(item => item.Chunk.Index).ToArray();
        var totalMs = checked((long)Math.Ceiling(mediaDuration.TotalMilliseconds));
        var history = new List<MappedSegment>();
        var selected = new List<MappedSegment>();
        var deferredOverlapSegments = new List<MappedSegment>();
        var mergeWarnings = new List<string>();
        var nextSpeakerId = 1;

        for (var position = 0; position < ordered.Length; position++)
        {
            var item = ordered[position];
            var localSegments = (item.Result.Segments ?? [])
                .Where(segment => segment is not null && !string.IsNullOrWhiteSpace(segment.Text))
                .ToArray();
            var speakerMapping = MapSpeakers(localSegments, item.Chunk, history, ref nextSpeakerId);
            if (position > 0 && speakerMapping.NewSpeakerCount > 0)
            {
                mergeWarnings.Add(
                    $"第 {position + 1} 段有 {speakerMapping.NewSpeakerCount} 个说话人无法从重叠区可靠关联，" +
                    "已分配新的全局说话人编号。");
            }

            var globalSegments = localSegments
                .Select(segment => ToGlobalSegment(segment, item.Chunk, totalMs, speakerMapping.Mapping))
                .Where(segment => segment is not null)
                .Select(segment => segment!)
                .ToArray();

            var ownershipStart = position == 0
                ? 0
                : Midpoint(ordered[position - 1].Chunk.EndMs, item.Chunk.StartMs);
            var ownershipEnd = position == ordered.Length - 1
                ? totalMs
                : Midpoint(item.Chunk.EndMs, ordered[position + 1].Chunk.StartMs);

            foreach (var segment in globalSegments)
            {
                var midpoint = Midpoint(segment.Segment.StartMs, segment.Segment.EndMs);
                if (midpoint >= ownershipStart &&
                    (position == ordered.Length - 1 ? midpoint <= ownershipEnd : midpoint < ownershipEnd))
                {
                    selected.Add(segment);
                }
                else
                {
                    deferredOverlapSegments.Add(segment);
                }
            }

            // Keep the complete overlapped result as mapping evidence for the next
            // chunk, not merely the half selected for final output.
            history.AddRange(globalSegments);
        }

        // Timestamp jitter can put the left copy just to the right of an ownership
        // boundary and the right copy just to its left. In that case a strict
        // midpoint rule would discard both. Recover a deferred segment only when
        // another chunk has not already selected the same time neighbourhood.
        // Comparing time as well as text prevents two slightly different ASR
        // renderings of the same boundary speech from being written twice.
        foreach (var deferred in deferredOverlapSegments
                     .OrderBy(item => item.Segment.StartMs)
                     .ThenBy(item => item.Segment.EndMs))
        {
            if (!selected.Any(existing =>
                    existing.ChunkIndex != deferred.ChunkIndex &&
                    (AreBoundaryDuplicates(existing.Segment, deferred.Segment) ||
                     (existing.Segment.SpeakerId > 0 &&
                      existing.Segment.SpeakerId == deferred.Segment.SpeakerId &&
                      HasSubstantialTimeOverlap(existing.Segment, deferred.Segment)))))
            {
                selected.Add(deferred);
            }
        }

        var mergedSegments = RemoveBoundaryDuplicates(selected)
            .Select(item => item.Segment)
            .ToArray();
        var warnings = ordered
            .SelectMany(item => item.Result.Warnings ?? [])
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        warnings.AddRange(mergeWarnings);
        warnings.Add(
            $"长录音已拆分为 {ordered.Length} 段本地识别；边界使用 " +
            $"{MossChunkPlanner.BoundaryOverlap.TotalSeconds:0} 秒重叠区合并时间轴和说话人标签。");

        var fullText = string.Join(Environment.NewLine, mergedSegments.Select(segment => segment.Text));
        return new EngineResultDto(
            sourceFileName,
            totalMs,
            JoinDistinct(ordered.Select(item => item.Result.Model)),
            JoinDistinct(ordered.Select(item => item.Result.EngineVersion)),
            JoinDistinct(ordered.Select(item => item.Result.Backend)),
            JoinDistinct(ordered.Select(item => item.Result.Language)),
            fullText,
            fullText,
            mergedSegments,
            warnings,
            ordered.Any(item => item.Result.IsPartial));
    }

    private static SpeakerMapping MapSpeakers(
        IReadOnlyList<EngineSegmentDto> localSegments,
        MossChunkPlan chunk,
        IReadOnlyList<MappedSegment> history,
        ref int nextSpeakerId)
    {
        var localSpeakerIds = localSegments
            .Where(segment => segment.SpeakerId > 0)
            .Select(segment => segment.SpeakerId)
            .Distinct()
            .OrderBy(id => id)
            .ToArray();
        var mapping = new Dictionary<int, int>();
        if (localSpeakerIds.Length == 0)
        {
            return new SpeakerMapping(mapping, 0);
        }

        var overlapHistory = history
            .Where(segment => segment.Segment.EndMs > chunk.StartMs && segment.Segment.StartMs < chunk.EndMs)
            .ToArray();
        var scores = new List<SpeakerScore>();
        foreach (var localSpeakerId in localSpeakerIds)
        {
            foreach (var globalSpeakerId in overlapHistory
                         .Select(segment => segment.Segment.SpeakerId)
                         .Where(id => id > 0)
                         .Distinct())
            {
                long score = 0;
                foreach (var local in localSegments.Where(segment => segment.SpeakerId == localSpeakerId))
                {
                    var localStart = chunk.StartMs + Math.Max(0, local.StartMs);
                    var localEnd = chunk.StartMs + Math.Max(local.StartMs, local.EndMs);
                    foreach (var previous in overlapHistory.Where(
                                 segment => segment.Segment.SpeakerId == globalSpeakerId))
                    {
                        score += Math.Max(
                            0,
                            Math.Min(localEnd, previous.Segment.EndMs) -
                            Math.Max(localStart, previous.Segment.StartMs));
                    }
                }

                if (score > 0)
                {
                    scores.Add(new SpeakerScore(localSpeakerId, globalSpeakerId, score));
                }
            }
        }

        var usedGlobalSpeakerIds = new HashSet<int>();
        foreach (var score in scores
                     .OrderByDescending(item => item.OverlapMs)
                     .ThenBy(item => item.LocalSpeakerId)
                     .ThenBy(item => item.GlobalSpeakerId))
        {
            if (mapping.ContainsKey(score.LocalSpeakerId) ||
                !usedGlobalSpeakerIds.Add(score.GlobalSpeakerId))
            {
                continue;
            }

            mapping.Add(score.LocalSpeakerId, score.GlobalSpeakerId);
        }

        var newSpeakerCount = 0;
        foreach (var localSpeakerId in localSpeakerIds)
        {
            if (!mapping.ContainsKey(localSpeakerId))
            {
                mapping.Add(localSpeakerId, nextSpeakerId++);
                newSpeakerCount++;
            }
        }

        if (mapping.Count > 0)
        {
            nextSpeakerId = Math.Max(nextSpeakerId, mapping.Values.Max() + 1);
        }

        return new SpeakerMapping(mapping, newSpeakerCount);
    }

    private static MappedSegment? ToGlobalSegment(
        EngineSegmentDto segment,
        MossChunkPlan chunk,
        long totalMs,
        IReadOnlyDictionary<int, int> speakerMap)
    {
        var chunkEndMs = Math.Min(totalMs, chunk.EndMs);
        var startMs = Math.Clamp(
            chunk.StartMs + Math.Max(0, segment.StartMs),
            0,
            chunkEndMs);
        var endMs = Math.Clamp(
            chunk.StartMs + Math.Max(segment.StartMs, segment.EndMs),
            startMs,
            chunkEndMs);
        if (endMs <= startMs || string.IsNullOrWhiteSpace(segment.Text))
        {
            return null;
        }

        var speakerId = segment.SpeakerId > 0 && speakerMap.TryGetValue(segment.SpeakerId, out var mapped)
            ? mapped
            : 0;
        return new MappedSegment(
            new EngineSegmentDto(startMs, endMs, speakerId, segment.Text.Trim()),
            chunk.Index);
    }

    private static IReadOnlyList<MappedSegment> RemoveBoundaryDuplicates(
        IEnumerable<MappedSegment> segments)
    {
        var ordered = segments
            .OrderBy(item => item.Segment.StartMs)
            .ThenBy(item => item.Segment.EndMs)
            .ToArray();
        var result = new List<MappedSegment>(ordered.Length);
        foreach (var current in ordered)
        {
            if (result.Count == 0)
            {
                result.Add(current);
                continue;
            }

            // A short interjection from another speaker can sort between the two
            // copies of a boundary sentence. Search the accumulated boundary
            // window instead of comparing only with the immediately previous row.
            var duplicateIndex = result.FindLastIndex(candidate =>
                candidate.ChunkIndex != current.ChunkIndex &&
                AreBoundaryDuplicates(candidate.Segment, current.Segment));
            if (duplicateIndex >= 0)
            {
                var previous = result[duplicateIndex];
                var text = current.Segment.Text.Length > previous.Segment.Text.Length
                    ? current.Segment.Text
                    : previous.Segment.Text;
                var speakerId = previous.Segment.SpeakerId > 0
                    ? previous.Segment.SpeakerId
                    : current.Segment.SpeakerId;
                result[duplicateIndex] = previous with
                {
                    Segment = new EngineSegmentDto(
                        Math.Min(previous.Segment.StartMs, current.Segment.StartMs),
                        Math.Max(previous.Segment.EndMs, current.Segment.EndMs),
                        speakerId,
                        text),
                };
                continue;
            }

            result.Add(current);
        }

        return result
            .OrderBy(item => item.Segment.StartMs)
            .ThenBy(item => item.Segment.EndMs)
            .ToArray();
    }

    private static bool AreBoundaryDuplicates(EngineSegmentDto first, EngineSegmentDto second)
    {
        var gapMs = IntervalGapMs(first, second);
        if (gapMs > DuplicateMaximumGapMs)
        {
            return false;
        }

        var firstText = NormalizeText(first.Text);
        var secondText = NormalizeText(second.Text);
        if (firstText.Length == 0 || secondText.Length == 0)
        {
            return false;
        }

        if (string.Equals(firstText, secondText, StringComparison.Ordinal))
        {
            var overlapMs = Math.Min(first.EndMs, second.EndMs) -
                            Math.Max(first.StartMs, second.StartMs);
            return overlapMs > 0 || firstText.Length >= 4;
        }

        var shorter = firstText.Length <= secondText.Length ? firstText : secondText;
        var longer = firstText.Length > secondText.Length ? firstText : secondText;
        return shorter.Length >= 4 &&
               longer.Contains(shorter, StringComparison.Ordinal) &&
               shorter.Length >= longer.Length * 0.7;
    }

    private static long IntervalGapMs(EngineSegmentDto first, EngineSegmentDto second)
    {
        if (first.EndMs < second.StartMs)
        {
            return second.StartMs - first.EndMs;
        }

        if (second.EndMs < first.StartMs)
        {
            return first.StartMs - second.EndMs;
        }

        return 0;
    }

    private static bool HasSubstantialTimeOverlap(EngineSegmentDto first, EngineSegmentDto second)
    {
        var overlapMs = Math.Min(first.EndMs, second.EndMs) -
                        Math.Max(first.StartMs, second.StartMs);
        if (overlapMs <= 0)
        {
            return false;
        }

        var shorterDurationMs = Math.Min(
            first.EndMs - first.StartMs,
            second.EndMs - second.StartMs);
        return shorterDurationMs > 0 && overlapMs * 2 >= shorterDurationMs;
    }

    private static string NormalizeText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static string JoinDistinct(IEnumerable<string?> values) =>
        string.Join(
            " + ",
            values
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.Ordinal));

    private static long Midpoint(long first, long second) => first + ((second - first) / 2);

    private sealed record MappedSegment(EngineSegmentDto Segment, int ChunkIndex);
    private sealed record SpeakerScore(int LocalSpeakerId, int GlobalSpeakerId, long OverlapMs);
    private sealed record SpeakerMapping(IReadOnlyDictionary<int, int> Mapping, int NewSpeakerCount);
}
