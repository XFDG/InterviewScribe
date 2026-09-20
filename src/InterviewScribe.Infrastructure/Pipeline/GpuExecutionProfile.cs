using System.Diagnostics;
using System.Globalization;

namespace InterviewScribe.Infrastructure.Pipeline;

/// <summary>
/// A deliberately conservative execution profile inferred from the primary
/// NVIDIA adapter.  The native MOSS runtime currently uses Vulkan, so its
/// context window must leave enough VRAM for drivers and desktop composition.
/// The profile is also used to bound Qwen audio chunks instead of assuming one
/// particular laptop GPU.
/// </summary>
internal sealed record GpuExecutionProfile(
    string AdapterName,
    int? DedicatedMemoryMiB,
    int MossContextTokens,
    int QwenChunkSeconds,
    int WhisperBatchSize,
    bool IsDetected)
{
    private const int UnknownMossContextTokens = 12_288;
    private const int UnknownQwenChunkSeconds = 60;

    public static GpuExecutionProfile Detect()
    {
        var overrideMemory = Environment.GetEnvironmentVariable("MEDIASCRIBE_GPU_MEMORY_MIB");
        if (int.TryParse(overrideMemory, NumberStyles.Integer, CultureInfo.InvariantCulture, out var overridden) &&
            overridden is >= 512 and <= 1_048_576)
        {
            return FromMemory("用户指定 GPU", overridden, true);
        }

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi.exe",
                    Arguments = "--query-gpu=name,memory.total --format=csv,noheader,nounits",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };
            if (!process.Start())
            {
                return Unknown();
            }

            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(3_000) || process.ExitCode != 0)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return Unknown();
            }

            // Multi-GPU machines may list several adapters.  Use the first one,
            // which is what the default CUDA/Vulkan device selection uses.
            var first = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(item => item.Trim())
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(first))
            {
                return Unknown();
            }

            var separator = first.LastIndexOf(',');
            if (separator <= 0 ||
                !int.TryParse(first[(separator + 1)..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var memoryMiB) ||
                memoryMiB < 512)
            {
                return new GpuExecutionProfile(first, null, UnknownMossContextTokens, UnknownQwenChunkSeconds, 2, false);
            }

            return FromMemory(first[..separator].Trim(), memoryMiB, true);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Unknown();
        }
    }

    internal static GpuExecutionProfile FromMemory(string adapterName, int memoryMiB, bool isDetected = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterName);
        if (memoryMiB < 512)
        {
            throw new ArgumentOutOfRangeException(nameof(memoryMiB));
        }

        // These caps are intentionally below the theoretical maximum.  MOSS
        // needs room for weights, Vulkan allocations and Windows itself; Qwen
        // runs ASR and forced alignment sequentially but still benefits from
        // keeping the source window short on smaller GPUs.
        var (mossContext, qwenChunkSeconds, whisperBatchSize) = memoryMiB switch
        {
            < 4_096 => (8_192, 45, 1),
            < 6_144 => (12_288, 60, 2),
            < 8_192 => (16_384, 75, 3),
            < 10_240 => (20_480, 90, 4),
            < 16_384 => (32_768, 150, 6),
            < 24_576 => (49_152, 180, 8),
            _ => (65_536, 180, 12),
        };
        return new GpuExecutionProfile(adapterName, memoryMiB, mossContext, qwenChunkSeconds, whisperBatchSize, isDetected);
    }

    private static GpuExecutionProfile Unknown() =>
        new("未检测到可查询的 NVIDIA 显存", null, UnknownMossContextTokens, UnknownQwenChunkSeconds, 2, false);
}
