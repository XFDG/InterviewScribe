namespace InterviewScribe.Infrastructure.Pipeline;

internal sealed record MossChunkPlan(int Index, long StartMs, long DurationMs)
{
    public long EndMs => checked(StartMs + DurationMs);
}

internal static class MossChunkPlanner
{
    internal static readonly TimeSpan MaximumChunkDuration = TimeSpan.FromMinutes(25);
    internal static readonly TimeSpan BoundaryOverlap = TimeSpan.FromSeconds(30);

    internal static IReadOnlyList<MossChunkPlan> Create(TimeSpan mediaDuration)
    {
        if (mediaDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(mediaDuration));
        }

        var totalMs = checked((long)Math.Ceiling(mediaDuration.TotalMilliseconds));
        var maximumMs = checked((long)MaximumChunkDuration.TotalMilliseconds);
        var overlapMs = checked((long)BoundaryOverlap.TotalMilliseconds);
        if (totalMs <= maximumMs)
        {
            return [new MossChunkPlan(0, 0, totalMs)];
        }

        // For N chunks of length D with O milliseconds of overlap, total coverage is
        // N*D-(N-1)*O. Pick the smallest N that keeps every chunk at or below 25
        // minutes, then distribute the duration evenly. This avoids a tiny final
        // chunk when the recording is only slightly longer than an exact multiple.
        var maximumStrideMs = maximumMs - overlapMs;
        var chunkCount = checked((int)Math.Max(
            2,
            DivideRoundUp(totalMs - overlapMs, maximumStrideMs)));
        var chunkDurationMs = DivideRoundUp(
            totalMs + ((long)chunkCount - 1) * overlapMs,
            chunkCount);
        if (chunkDurationMs > maximumMs)
        {
            throw new InvalidOperationException("无法生成安全的 MOSS 分段计划。");
        }

        var strideMs = chunkDurationMs - overlapMs;
        var chunks = new List<MossChunkPlan>(chunkCount);
        for (var index = 0; index < chunkCount; index++)
        {
            var startMs = checked(index * strideMs);
            var durationMs = Math.Min(chunkDurationMs, totalMs - startMs);
            if (durationMs <= 0)
            {
                break;
            }

            chunks.Add(new MossChunkPlan(index, startMs, durationMs));
        }

        if (chunks.Count != chunkCount || chunks[^1].EndMs != totalMs)
        {
            throw new InvalidOperationException("MOSS 分段计划未覆盖完整录音。");
        }

        return chunks;
    }

    private static long DivideRoundUp(long numerator, long denominator) =>
        checked((numerator + denominator - 1) / denominator);
}
