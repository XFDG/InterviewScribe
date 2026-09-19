using System.Net;
using InterviewScribe.Core.Domain;
using InterviewScribe.Infrastructure;
using InterviewScribe.Infrastructure.Models;

namespace InterviewScribe.Tests;

public sealed class ModelStoreTests
{
    [Fact]
    public async Task EnsureAsync_AfterThirdFailure_ReportsExactAttemptCountAndPreservesCause()
    {
        using var directory = new TemporaryDirectory();
        var handler = new AlwaysFailingHandler();
        using var client = new HttpClient(handler);
        var delays = new List<TimeSpan>();
        var progress = new ProgressCollector();
        using var store = new ModelStore(client, (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });
        var previousOverride = Environment.GetEnvironmentVariable("INTERVIEWSCRIBE_MODEL_PATH");
        Environment.SetEnvironmentVariable("INTERVIEWSCRIBE_MODEL_PATH", null);

        try
        {
            var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
                store.EnsureAsync(
                    new AppPaths(directory.Path, directory.Path),
                    progress,
                    CancellationToken.None));

            Assert.Equal(3, handler.RequestCount);
            Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)], delays);
            Assert.Contains("全部 3 次尝试", exception.Message);
            Assert.Contains("最后一次错误", exception.Message);
            Assert.IsType<HttpRequestException>(exception.InnerException);
            Assert.Contains(progress.Items, item => item.Message.Contains("第 1/3 次失败"));
            Assert.Contains(progress.Items, item => item.Message.Contains("第 2/3 次失败"));
            Assert.Contains(progress.Items, item => item.Message.Contains("第 3/3 次失败，已停止自动重试"));
            Assert.DoesNotContain(progress.Items, item => item.Message.Contains("第 4/3 次尝试"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("INTERVIEWSCRIBE_MODEL_PATH", previousOverride);
        }
    }

    private sealed class AlwaysFailingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromException<HttpResponseMessage>(new HttpRequestException("simulated network failure"));
        }
    }

    private sealed class ProgressCollector : IProgress<OperationProgress>
    {
        public List<OperationProgress> Items { get; } = [];
        public void Report(OperationProgress value) => Items.Add(value);
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
