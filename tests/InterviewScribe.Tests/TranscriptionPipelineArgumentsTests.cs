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

    [Fact]
    public void BuildQwenArguments_LocalMode_UsesPinnedLocalDirectoriesAndAutoLanguage()
    {
        var arguments = TranscriptionPipeline.BuildQwenArguments(
            TranscriptionMode.QwenLocalHighAccuracy,
            "qwen_sidecar.py",
            "asr-model",
            "aligner-model",
            "audio.wav",
            "result.json",
            null,
            LanguageSelection.FromCodes(["zh", "en"]));

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
            ],
            arguments);
    }

    [Fact]
    public void BuildQwenArguments_SdkMode_DoesNotPutSecretOrLocalPathsOnCommandLine()
    {
        var arguments = TranscriptionPipeline.BuildQwenArguments(
            TranscriptionMode.QwenSdkHighAccuracy,
            "qwen_sidecar.py",
            null,
            null,
            "audio.wav",
            "result.json",
            "moss-result.json",
            LanguageSelection.FromCodes(["en"]));

        Assert.Equal(
            [
                "-X", "utf8",
                "-I", "qwen_sidecar.py",
                "--mode", "sdk",
                "--audio", "audio.wav",
                "--output", "result.json",
                "--language", "en",
                "--sdk-model", "qwen3-asr-flash",
                "--speaker-timeline", "moss-result.json",
            ],
            arguments);
        Assert.DoesNotContain(arguments, item => item.Contains("key", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("--model-dir", arguments);
        Assert.DoesNotContain("--aligner-dir", arguments);
    }

    [Fact]
    public void BuildQwenArguments_SdkMode_RequiresSpeakerTimeline()
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            TranscriptionPipeline.BuildQwenArguments(
                TranscriptionMode.QwenSdkHighAccuracy,
                "qwen_sidecar.py",
                null,
                null,
                "audio.wav",
                "result.json",
                null,
                LanguageSelection.FromCodes(["zh"])));

        Assert.Contains("MOSS", exception.Message, StringComparison.Ordinal);
    }
}
