using System.Text.Json;
using InterviewScribe.Infrastructure;

namespace InterviewScribe.Tests;

public sealed class RuntimeLocatorTests
{
    [Fact]
    public void FindTranscribeRuntimeDirectory_ListsEveryMissingLockedFile()
    {
        using var directory = new TemporaryDirectory();
        var runtime = Path.Combine(directory.Path, "tools", "transcribe");
        Directory.CreateDirectory(runtime);
        File.WriteAllText(Path.Combine(runtime, "transcribe.dll"), "present");
        WriteLock(directory.Path, ["transcribe.dll", "ggml.dll", "licenses/LICENSE"]);
        var locator = new RuntimeLocator(new AppPaths(
            Path.Combine(directory.Path, "local"),
            directory.Path));

        var exception = Assert.Throws<FileNotFoundException>(() =>
            locator.FindTranscribeRuntimeDirectory());

        Assert.Contains("ggml.dll", exception.Message);
        Assert.Contains("licenses/LICENSE", exception.Message);
        Assert.DoesNotContain("transcribe.dll、", exception.Message);
    }

    [Fact]
    public void FindTranscribeRuntimeDirectory_WhenAllLockedFilesExist_ReturnsRuntime()
    {
        using var directory = new TemporaryDirectory();
        var runtime = Path.Combine(directory.Path, "tools", "transcribe");
        Directory.CreateDirectory(Path.Combine(runtime, "licenses"));
        File.WriteAllText(Path.Combine(runtime, "transcribe.dll"), "present");
        File.WriteAllText(Path.Combine(runtime, "ggml.dll"), "present");
        File.WriteAllText(Path.Combine(runtime, "licenses", "LICENSE"), "present");
        WriteLock(directory.Path, ["transcribe.dll", "ggml.dll", "licenses/LICENSE"]);
        var locator = new RuntimeLocator(new AppPaths(
            Path.Combine(directory.Path, "local"),
            directory.Path));

        var result = locator.FindTranscribeRuntimeDirectory();

        Assert.Equal(runtime, result);
    }

    [Fact]
    public void FindQwenPython_UsesApplicationLocalVirtualEnvironment()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(directory.Path, "local"), directory.Path);
        var python = Path.Combine(paths.QwenVirtualEnvironmentRoot, "Scripts", "python.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(python)!);
        File.WriteAllText(python, "python");
        var locator = new RuntimeLocator(paths);

        var result = locator.FindQwenPython();

        Assert.Equal(python, result);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void FindQwenAsrModelDirectory_RejectsIncompleteModel(bool hasConfig, bool hasWeights)
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(directory.Path, "local"), directory.Path);
        Directory.CreateDirectory(paths.QwenAsrModelRoot);
        if (hasConfig)
        {
            File.WriteAllText(Path.Combine(paths.QwenAsrModelRoot, "config.json"), "{}");
        }

        if (hasWeights)
        {
            File.WriteAllText(Path.Combine(paths.QwenAsrModelRoot, "model.safetensors"), "weights");
        }

        var locator = new RuntimeLocator(paths);

        Assert.Throws<FileNotFoundException>(() => locator.FindQwenAsrModelDirectory());
    }

    [Fact]
    public void FindQwenAsrModelDirectory_WhenPinnedRevisionMatches_ReturnsModelDirectory()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(directory.Path, "local"), directory.Path);
        WriteQwenModel(paths.QwenAsrModelRoot, QwenModelManifest.AsrRevision);
        var locator = new RuntimeLocator(paths);

        var result = locator.FindQwenAsrModelDirectory();

        Assert.Equal(paths.QwenAsrModelRoot, result);
    }

    [Fact]
    public void FindQwenAsrModelDirectory_RejectsUnexpectedRevision()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(directory.Path, "local"), directory.Path);
        WriteQwenModel(paths.QwenAsrModelRoot, "different-revision");
        var locator = new RuntimeLocator(paths);

        var exception = Assert.Throws<InvalidDataException>(() => locator.FindQwenAsrModelDirectory());

        Assert.Contains(QwenModelManifest.AsrRevision, exception.Message);
        Assert.Contains("different-revision", exception.Message);
    }

    [Fact]
    public void FindQwenAsrModelDirectory_RejectsMissingIndexedShard()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(directory.Path, "local"), directory.Path);
        WriteQwenModel(paths.QwenAsrModelRoot, QwenModelManifest.AsrRevision);
        File.WriteAllText(
            Path.Combine(paths.QwenAsrModelRoot, "model.safetensors.index.json"),
            """{"weight_map":{"layer.0":"model-00001-of-00002.safetensors","layer.1":"model-00002-of-00002.safetensors"}}""");
        File.WriteAllText(
            Path.Combine(paths.QwenAsrModelRoot, "model-00001-of-00002.safetensors"),
            "weights");
        var locator = new RuntimeLocator(paths);

        var exception = Assert.Throws<FileNotFoundException>(() => locator.FindQwenAsrModelDirectory());

        Assert.Contains("model-00002-of-00002.safetensors", exception.Message);
    }

    [Fact]
    public void FindQwenAlignerModelDirectory_WhenPinnedRevisionMatches_ReturnsModelDirectory()
    {
        using var directory = new TemporaryDirectory();
        var paths = new AppPaths(Path.Combine(directory.Path, "local"), directory.Path);
        WriteQwenModel(paths.QwenAlignerModelRoot, QwenModelManifest.AlignerRevision);
        var locator = new RuntimeLocator(paths);

        var result = locator.FindQwenAlignerModelDirectory();

        Assert.Equal(paths.QwenAlignerModelRoot, result);
    }

    private static void WriteQwenModel(string modelDirectory, string revision)
    {
        Directory.CreateDirectory(modelDirectory);
        File.WriteAllText(Path.Combine(modelDirectory, "config.json"), "{}");
        File.WriteAllText(Path.Combine(modelDirectory, "model.safetensors"), "weights");
        File.WriteAllText(
            Path.Combine(modelDirectory, QwenModelManifest.RevisionMarkerFileName),
            revision + Environment.NewLine);
    }

    private static void WriteLock(string root, IReadOnlyList<string> requiredFiles)
    {
        var document = new
        {
            nativeDependencies = new
            {
                transcribeCpp = new
                {
                    requiredFiles,
                },
            },
        };
        File.WriteAllText(
            Path.Combine(root, "dependencies.lock.json"),
            JsonSerializer.Serialize(document));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "InterviewScribe.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
