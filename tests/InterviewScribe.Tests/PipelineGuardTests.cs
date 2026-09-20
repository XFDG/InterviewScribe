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

    [Fact]
    public void CleanupJobAudioArtifacts_RemovesOnlyReproducibleAudioArtifacts()
    {
        using var directory = new TemporaryDirectory();
        var temporaryAudioDirectory = Path.Combine(directory.Path, ".qwen-audio-sdk-test");
        var mossAudioDirectory = Path.Combine(directory.Path, ".moss-audio-chunks");
        var unrelatedDirectory = Path.Combine(directory.Path, "diagnostics");
        Directory.CreateDirectory(temporaryAudioDirectory);
        Directory.CreateDirectory(mossAudioDirectory);
        Directory.CreateDirectory(unrelatedDirectory);
        File.WriteAllText(Path.Combine(directory.Path, "audio.wav"), "source audio");
        File.WriteAllText(Path.Combine(directory.Path, "audio.wav.tmp"), "partial audio");
        File.WriteAllText(Path.Combine(temporaryAudioDirectory, "segment.wav"), "derived audio");
        File.WriteAllText(Path.Combine(mossAudioDirectory, "audio-001.wav"), "derived audio");
        File.WriteAllText(Path.Combine(directory.Path, "qwen.stderr.log"), "diagnostic output");
        File.WriteAllText(Path.Combine(unrelatedDirectory, "details.json"), "{}");

        TranscriptionPipeline.CleanupJobAudioArtifacts(directory.Path);

        Assert.False(File.Exists(Path.Combine(directory.Path, "audio.wav")));
        Assert.False(File.Exists(Path.Combine(directory.Path, "audio.wav.tmp")));
        Assert.False(Directory.Exists(temporaryAudioDirectory));
        Assert.False(Directory.Exists(mossAudioDirectory));
        Assert.True(File.Exists(Path.Combine(directory.Path, "qwen.stderr.log")));
        Assert.True(File.Exists(Path.Combine(unrelatedDirectory, "details.json")));
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
