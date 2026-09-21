namespace InterviewScribe.Infrastructure.Pipeline;

internal sealed record MossChunkPlan(int Index, long StartMs, long DurationMs)
{
    public long EndMs => checked(StartMs + DurationMs);
}

internal static class MossChunkPlanner
{
    // This is the hard upper bound supported by the bundled native MOSS
    // runtime.  It is not necessarily safe for every GPU: the usable duration
    // must also fit that adapter's context window.
    internal static readonly TimeSpan MaximumChunkDuration = TimeSpan.FromMinutes(25);
    internal static readonly TimeSpan BoundaryOverlap = TimeSpan.FromSeconds(30);

    // The native runtime expands audio into prompt tokens before generation.
    // The observed rate is about 13.2 tokens/sec.  Its context window is also
    // shared with generated text and speaker tags: a dense conversational
    // segment can need several thousand output tokens after its prompt has
    // been prefetched.  Reserve 4k tokens up front rather than planning a
    // segment which inevitably hits the native output/context cap and then
    // falls back to CPU.
    private const double ConservativePromptTokensPerSecond = 15d;
    private const int GenerationAndSafetyReserveTokens = 4_096;

    internal static TimeSpan GetGpuSafeMaximumChunkDuration(int contextTokens)
    {
        if (contextTokens <= GenerationAndSafetyReserveTokens)
        {
            throw new ArgumentOutOfRangeException(nameof(contextTokens));
        }

        var safeSeconds = Math.Floor(
            (contextTokens - GenerationAndSafetyReserveTokens) /
            ConservativePromptTokensPerSecond);
        if (safeSeconds <= BoundaryOverlap.TotalSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contextTokens),
                "MOSS 上下文不足以生成带边界重叠的安全 GPU 分段。");
        }

        return TimeSpan.FromSeconds(
            Math.Min(MaximumChunkDuration.TotalSeconds, safeSeconds));
    }

    internal static IReadOnlyList<MossChunkPlan> Create(TimeSpan mediaDuration)
        => Create(mediaDuration, MaximumChunkDuration);

    internal static IReadOnlyList<MossChunkPlan> Create(
        TimeSpan mediaDuration,
        TimeSpan maximumChunkDuration)
    {
        if (mediaDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(mediaDuration));
        }

        if (maximumChunkDuration <= BoundaryOverlap ||
            maximumChunkDuration > MaximumChunkDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumChunkDuration),
                $"分段上限必须大于 {BoundaryOverlap.TotalSeconds:0} 秒且不超过 " +
                $"{MaximumChunkDuration.TotalMinutes:0} 分钟。");
        }

        var totalMs = checked((long)Math.Ceiling(mediaDuration.TotalMilliseconds));
        var maximumMs = checked((long)Math.Floor(maximumChunkDuration.TotalMilliseconds));
        var overlapMs = checked((long)BoundaryOverlap.TotalMilliseconds);
        if (totalMs <= maximumMs)
        {
            return [new MossChunkPlan(0, 0, totalMs)];
        }

        // For N chunks of length D with O milliseconds of overlap, total coverage is
        // N*D-(N-1)*O. Pick the smallest N that keeps every chunk at or below the
        // configured duration, then distribute the duration evenly. This avoids a tiny final
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
