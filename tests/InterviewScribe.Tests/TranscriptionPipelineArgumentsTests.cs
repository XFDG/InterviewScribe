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
}
