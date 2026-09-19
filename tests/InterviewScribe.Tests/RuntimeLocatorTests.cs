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
