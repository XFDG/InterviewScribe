namespace InterviewScribe.Infrastructure.Pipeline;

/// <summary>
/// Classifies native MOSS failures which are resolved by making the audio
/// segment shorter and retrying on Vulkan.  The native runtime uses one shared
/// context budget for the encoded audio prompt and generated transcript, so an
/// output-token cap is just as recoverable as an input-context cap.
/// </summary>
internal static class MossGpuFailureClassifier
{
    // Native runtime v0.2.3 status values. Keep message matching below as a
    // compatibility path for runtime versions that do not emit a numeric
    // status in their JSON error event.
    private const int InputTooLongNativeStatus = 17;
    private const int OutputTruncatedNativeStatus = 18;

    internal static bool RequiresShorterGpuChunk(string? message, int? nativeStatus = null)
    {
        if (nativeStatus is InputTooLongNativeStatus or OutputTruncatedNativeStatus)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("input audio too long", StringComparison.OrdinalIgnoreCase) ||
            (message.Contains("prompt", StringComparison.OrdinalIgnoreCase) &&
             message.Contains("context", StringComparison.OrdinalIgnoreCase) &&
             message.Contains("exceed", StringComparison.OrdinalIgnoreCase)) ||
            (message.Contains("context", StringComparison.OrdinalIgnoreCase) &&
             message.Contains("too long", StringComparison.OrdinalIgnoreCase)) ||
            // Any native "output truncated" status is a partial MOSS result,
            // not a Vulkan-driver failure.  Current runtimes spell it as
            // "decode hit the context/generation cap before end-of-stream",
            // but matching the stable prefix keeps the retry safe across
            // native runtime wording changes.
            message.Contains("output truncated", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("generation cap", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("模型报告结果被截断", StringComparison.Ordinal);
    }
}
