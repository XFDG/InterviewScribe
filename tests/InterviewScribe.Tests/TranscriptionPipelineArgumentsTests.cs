using InterviewScribe.Core.Domain;
using InterviewScribe.Infrastructure.Pipeline;

namespace InterviewScribe.Tests;

public sealed class TranscriptionPipelineArgumentsTests
{
    [Theory]
    [InlineData("vulkan", "zh", null, "zh")]
    [InlineData("cpu", "zh", null, "zh")]
    [InlineData("vulkan", "en", null, "en")]
    [InlineData("cpu", "en", null, "en")]
    [InlineData("vulkan", "zh", "en", "auto")]
    [InlineData("cpu", "zh", "en", "auto")]
    [InlineData("vulkan", "ja", null, "auto")]
    [InlineData("cpu", "fr", null, "auto")]
    public void BuildEngineArguments_UsesSafeLanguageValueForEveryBackend(
        string backend,
        string firstLanguage,
        string? secondLanguage,
        string expectedEngineLanguage)
    {
        string[] languageCodes = secondLanguage is null
            ? [firstLanguage]
            : [firstLanguage, secondLanguage];
        var selection = LanguageSelection.FromCodes(languageCodes);

        var arguments = TranscriptionPipeline.BuildEngineArguments(
            "runtime",
            "model.gguf",
            "audio.wav",
            "result.json",
            backend,
            selection);

        Assert.Equal(
            [
                "--runtime", "runtime",
                "--model", "model.gguf",
                "--audio", "audio.wav",
                "--output", "result.json",
                "--backend", backend,
                "--language", expectedEngineLanguage,
            ],
            arguments);
        Assert.Equal(1, arguments.Count(argument => argument == "--language"));
        Assert.DoesNotContain("zh,en", arguments);
    }

    [Theory]
    [InlineData("ja")]
    [InlineData("fr")]
    [InlineData("yue")]
    public void BuildQwenArguments_LocalMode_PreservesSupportedExtendedLanguage(string languageCode)
    {
        var arguments = TranscriptionPipeline.BuildQwenArguments(
            "qwen_sidecar.py",
            "asr-model",
            "aligner-model",
            "audio.wav",
            "result.json",
            LanguageSelection.FromCodes([languageCode]),
            90);

        var languageIndex = Array.IndexOf(arguments.ToArray(), "--language");
        Assert.True(languageIndex >= 0);
        Assert.Equal(languageCode, arguments[languageIndex + 1]);
    }

    [Fact]
    public void BuildEngineArguments_VulkanContextCapIsExplicitAndCpuCanUseFullContext()
    {
        var language = LanguageSelection.FromCodes(["zh", "en"]);

        var vulkanArguments = TranscriptionPipeline.BuildEngineArguments(
            "runtime",
            "model.gguf",
            "audio.wav",
            "result.json",
            "vulkan",
            language,
            TranscriptionPipeline.VulkanProtectedContextTokens);
        var cpuArguments = TranscriptionPipeline.BuildEngineArguments(
            "runtime",
            "model.gguf",
            "audio.wav",
            "result.json",
            "cpu",
            language);

        Assert.Contains("--context-tokens", vulkanArguments);
        Assert.Contains("16384", vulkanArguments);
        Assert.DoesNotContain("--context-tokens", cpuArguments);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(17, true)]
    [InlineData(18, false)]
    public void GetVulkanContextTokenCap_OnlyProtectsGpuSizedChunks(
        int minutes,
        bool expectedCap)
    {
        var result = TranscriptionPipeline.GetVulkanContextTokenCap(TimeSpan.FromMinutes(minutes));

        Assert.Equal(expectedCap, result.HasValue);
    }

    [Fact]
    public void GetEffectiveMediaDuration_UsesLongerExtractedAudioDuration()
    {
        var effective = TranscriptionPipeline.GetEffectiveMediaDuration(
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(31));

        Assert.Equal(TimeSpan.FromMinutes(31), effective);
    }

    [Fact]
    public void GetEffectiveMediaDuration_RejectsExtractedAudioOverEightHours()
    {
        Assert.Throws<NotSupportedException>(() =>
            TranscriptionPipeline.GetEffectiveMediaDuration(
                TimeSpan.FromHours(1),
                TimeSpan.FromHours(8) + TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void BuildQwenArguments_LocalMode_UsesPinnedLocalDirectoriesAndAutoLanguage()
    {
        var arguments = TranscriptionPipeline.BuildQwenArguments(
            "qwen_sidecar.py",
            "asr-model",
            "aligner-model",
            "audio.wav",
            "result.json",
            LanguageSelection.FromCodes(["zh", "en"]),
            90);

        Assert.Equal(
            [
                "-X", "utf8",
                "-I", "qwen_sidecar.py",
                "--mode", "local",
                "--audio", "audio.wav",
                "--output", "result.json",
                "--language", "auto",
                "--model-dir", "asr-model",
                "--aligner-dir", "aligner-model",
                "--device", "auto",
                "--chunk-seconds", "90",
            ],
            arguments);
    }

    [Fact]
    public void BuildQwenArguments_RejectsUnsafeChunkLength()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            TranscriptionPipeline.BuildQwenArguments(
                "qwen_sidecar.py",
                "asr-model",
                "aligner-model",
                "audio.wav",
                "result.json",
                LanguageSelection.FromCodes(["en"]),
                29));
    }

    [Fact]
    public void BuildWhisperArguments_UsesIsolatedLocalCudaContract()
    {
        var arguments = TranscriptionPipeline.BuildWhisperArguments(
            "whisper_sidecar.py",
            "whisper-model",
            "audio.wav",
            "result.json",
            LanguageSelection.FromCodes(["zh", "en"]),
            "cuda",
            "int8_float16",
            3);

        Assert.Equal(
            [
                "-X", "utf8",
                "-I", "whisper_sidecar.py",
                "--audio", "audio.wav",
                "--output", "result.json",
                "--model-dir", "whisper-model",
                "--language", "auto",
                "--device", "cuda",
                "--compute-type", "int8_float16",
                "--batch-size", "3",
            ],
            arguments);
    }

    [Theory]
    [InlineData(8_151, 16_384, 75, 3)]
    [InlineData(12_000, 32_768, 150, 6)]
    [InlineData(24_000, 49_152, 180, 8)]
    public void GpuExecutionProfile_UsesConservativeMemoryAwareCaps(
        int memoryMiB,
        int expectedContext,
        int expectedQwenSeconds,
        int expectedWhisperBatch)
    {
        var profile = GpuExecutionProfile.FromMemory("test GPU", memoryMiB);

        Assert.Equal(expectedContext, profile.MossContextTokens);
        Assert.Equal(expectedQwenSeconds, profile.QwenChunkSeconds);
        Assert.Equal(expectedWhisperBatch, profile.WhisperBatchSize);
    }
}
