using InterviewScribe.Infrastructure.Pipeline;

namespace InterviewScribe.Tests;

public sealed class PipelineGuardTests
{
    [Fact]
    public void ValidateOutputDirectory_CreatesAndRemovesWriteProbe()
    {
        using var directory = new TemporaryDirectory();

        var result = TranscriptionPipeline.ValidateOutputDirectory(directory.Path);

        Assert.Equal(Path.GetFullPath(directory.Path), result);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public void ValidateOutputDirectory_WhenTargetCannotBeCreated_UsesClearChineseError()
    {
        using var directory = new TemporaryDirectory();
        var filePath = Path.Combine(directory.Path, "not-a-directory");
        File.WriteAllText(filePath, "occupied");

        var exception = Assert.Throws<IOException>(() =>
            TranscriptionPipeline.ValidateOutputDirectory(filePath));

        Assert.Contains("结果保存位置无法写入", exception.Message);
        Assert.Contains(Path.GetFullPath(filePath), exception.Message);
        Assert.NotNull(exception.InnerException);
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
