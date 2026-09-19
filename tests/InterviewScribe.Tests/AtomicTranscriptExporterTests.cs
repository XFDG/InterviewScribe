using System.Text;
using System.Text.Json;
using InterviewScribe.Core.Export;
using InterviewScribe.Infrastructure.Pipeline;

namespace InterviewScribe.Tests;

public sealed class AtomicTranscriptExporterTests
{
    [Fact]
    public async Task ExportAsync_WritesAllFormatsAsBomlessUtf8WithSafeFileName()
    {
        using var directory = new TemporaryDirectory();
        var document = TestDocumentFactory.Create(sourceFileName: "面试:final?.mp4");

        var result = await AtomicTranscriptExporter.ExportAsync(document, directory.Path, CancellationToken.None);

        Assert.Equal("面试_final__转写.txt", System.IO.Path.GetFileName(result.TxtPath));
        Assert.Equal("面试_final__转写.srt", System.IO.Path.GetFileName(result.SrtPath));
        Assert.Equal("面试_final__转写.json", System.IO.Path.GetFileName(result.JsonPath));
        Assert.True(File.Exists(result.TxtPath));
        Assert.True(File.Exists(result.SrtPath));
        Assert.True(File.Exists(result.JsonPath));

        var txtBytes = await File.ReadAllBytesAsync(result.TxtPath);
        Assert.False(txtBytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.Contains("你好世界", await File.ReadAllTextAsync(result.TxtPath));
        Assert.Contains("00:00:00,000 --> 00:00:01,000", await File.ReadAllTextAsync(result.SrtPath));
        Assert.Contains("\"sourceFileName\"", await File.ReadAllTextAsync(result.JsonPath));
    }

    [Fact]
    public async Task ExportAsync_WhenAnyFormatExists_UsesNumberedStemWithoutOverwriting()
    {
        using var directory = new TemporaryDirectory();
        var existingPath = System.IO.Path.Combine(directory.Path, "技术面试_转写.srt");
        await File.WriteAllTextAsync(existingPath, "keep me");

        var result = await AtomicTranscriptExporter.ExportAsync(
            TestDocumentFactory.Create(),
            directory.Path,
            CancellationToken.None);

        Assert.Equal("技术面试_转写_2.txt", System.IO.Path.GetFileName(result.TxtPath));
        Assert.Equal("keep me", await File.ReadAllTextAsync(existingPath));
    }

    [Fact]
    public async Task ExportAsync_WhenAlreadyCancelled_LeavesNoPartialOutputs()
    {
        using var directory = new TemporaryDirectory();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AtomicTranscriptExporter.ExportAsync(
                TestDocumentFactory.Create(),
                directory.Path,
                cancellation.Token));

        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task ExportAsync_AppliesDisplayOptionsButKeepsStructuredJson()
    {
        using var directory = new TemporaryDirectory();
        var options = new TranscriptFormattingOptions
        {
            IncludeTimestamps = false,
            IncludeSpeakers = false
        };

        var result = await AtomicTranscriptExporter.ExportAsync(
            TestDocumentFactory.Create(),
            directory.Path,
            CancellationToken.None,
            options);

        var txt = await File.ReadAllTextAsync(result.TxtPath);
        var srt = await File.ReadAllTextAsync(result.SrtPath);
        Assert.DoesNotContain("[00:", txt);
        Assert.DoesNotContain("说话人", txt);
        Assert.Contains(" --> ", srt);
        Assert.DoesNotContain("[说话人", srt);

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(result.JsonPath));
        var firstSegment = json.RootElement.GetProperty("segments")[0];
        Assert.True(firstSegment.TryGetProperty("startMs", out _));
        Assert.True(firstSegment.TryGetProperty("endMs", out _));
        Assert.True(firstSegment.TryGetProperty("speakerId", out _));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "InterviewScribe.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
