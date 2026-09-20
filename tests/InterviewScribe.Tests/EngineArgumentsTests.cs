using InterviewScribe.EngineHost;

namespace InterviewScribe.Tests;

public sealed class EngineArgumentsTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("auto", null)]
    [InlineData("zh", "zh")]
    [InlineData("en", "en")]
    public void Parse_MapsLanguageToNativeHint(string? languageArgument, string? expectedLanguage)
    {
        using var files = new EngineArgumentFiles();
        var arguments = files.CreateArguments(languageArgument);

        var parsed = Arguments.Parse(arguments);

        Assert.Equal(expectedLanguage, parsed.Language);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("zh,en")]
    [InlineData("fr")]
    [InlineData("ZH")]
    public void Parse_RejectsUnsupportedLanguage(string languageArgument)
    {
        using var files = new EngineArgumentFiles();

        var exception = Assert.Throws<EngineException>(() =>
            Arguments.Parse(files.CreateArguments(languageArgument)));

        Assert.Contains("auto、zh 或 en", exception.Message);
    }

    [Fact]
    public void Parse_WhenLanguageValueIsMissing_ReportsIncompleteArguments()
    {
        using var files = new EngineArgumentFiles();
        var arguments = files.CreateArguments(null).Append("--language").ToArray();

        var exception = Assert.Throws<EngineException>(() => Arguments.Parse(arguments));

        Assert.Contains("启动参数不完整", exception.Message);
    }

    [Fact]
    public void Parse_MapsOptionalContextTokenCap()
    {
        using var files = new EngineArgumentFiles();

        var parsed = Arguments.Parse(files.CreateArguments(null, 65_536));

        Assert.Equal(65_536, parsed.ContextTokens);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1023")]
    [InlineData("131073")]
    [InlineData("not-a-number")]
    public void Parse_RejectsUnsafeContextTokenCap(string value)
    {
        using var files = new EngineArgumentFiles();
        var arguments = files.CreateArguments(null)
            .Concat(["--context-tokens", value])
            .ToArray();

        var exception = Assert.Throws<EngineException>(() => Arguments.Parse(arguments));

        Assert.Contains("1024 到 131072", exception.Message);
    }

    private sealed class EngineArgumentFiles : IDisposable
    {
        public EngineArgumentFiles()
        {
            RootPath = Path.Combine(
                Path.GetTempPath(),
                "InterviewScribe.Tests",
                Guid.NewGuid().ToString("N"));
            RuntimePath = Path.Combine(RootPath, "runtime");
            ModelPath = Path.Combine(RootPath, "model.gguf");
            AudioPath = Path.Combine(RootPath, "audio.wav");
            OutputPath = Path.Combine(RootPath, "result.json");

            Directory.CreateDirectory(RuntimePath);
            File.WriteAllBytes(ModelPath, []);
            File.WriteAllBytes(AudioPath, []);
        }

        public string RootPath { get; }

        public string RuntimePath { get; }

        public string ModelPath { get; }

        public string AudioPath { get; }

        public string OutputPath { get; }

        public string[] CreateArguments(string? languageArgument, int? contextTokens = null)
        {
            var arguments = new List<string>
            {
                "--runtime", RuntimePath,
                "--model", ModelPath,
                "--audio", AudioPath,
                "--output", OutputPath,
            };

            if (languageArgument is not null)
            {
                arguments.Add("--language");
                arguments.Add(languageArgument);
            }

            if (contextTokens is int value)
            {
                arguments.Add("--context-tokens");
                arguments.Add(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }

            return arguments.ToArray();
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(RootPath, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
