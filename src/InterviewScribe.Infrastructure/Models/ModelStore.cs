using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using InterviewScribe.Core.Domain;

namespace InterviewScribe.Infrastructure.Models;

public sealed class ModelStore : IDisposable
{
    public static readonly ModelDescriptor MossQ8 = new(
        "MOSS-Transcribe-Diarize Q8_0",
        "MOSS-Transcribe-Diarize-Q8_0.gguf",
        "bfa3d24438711391d8713c6ab0efd6264527757c",
        new Uri("https://huggingface.co/handy-computer/moss-transcribe-diarize-gguf/resolve/bfa3d24438711391d8713c6ab0efd6264527757c/MOSS-Transcribe-Diarize-Q8_0.gguf"),
        986_899_616,
        "64ec654dc6ffcfdfe180422dffce1d33422b0c30959b7edfd131bad77ee35039");

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _retryDelay;

    public ModelStore(HttpClient? httpClient = null)
        : this(httpClient, Task.Delay)
    {
    }

    internal ModelStore(
        HttpClient? httpClient,
        Func<TimeSpan, CancellationToken, Task> retryDelay)
    {
        ArgumentNullException.ThrowIfNull(retryDelay);
        _ownsClient = httpClient is null;
        _httpClient = httpClient ?? new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
        _retryDelay = retryDelay;
        var assemblyVersion = typeof(ModelStore).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        _httpClient.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("InterviewScribe", assemblyVersion));
    }

    public async Task<string> EnsureAsync(
        AppPaths paths,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        paths.EnsureCreated();

        var overridePath = Environment.GetEnvironmentVariable("INTERVIEWSCRIBE_MODEL_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var fullOverridePath = Path.GetFullPath(overridePath);
            progress?.Report(new OperationProgress(JobState.VerifyingModel, "正在校验指定的本地模型…"));
            await VerifyAsync(fullOverridePath, MossQ8, progress, cancellationToken).ConfigureAwait(false);
            return fullOverridePath;
        }

        var finalPath = Path.Combine(paths.ModelsRoot, MossQ8.FileName);
        if (File.Exists(finalPath))
        {
            progress?.Report(new OperationProgress(JobState.VerifyingModel, "正在校验本地模型完整性…"));
            try
            {
                await VerifyAsync(finalPath, MossQ8, progress, cancellationToken).ConfigureAwait(false);
                return finalPath;
            }
            catch (InvalidDataException)
            {
                TryDelete(finalPath);
            }
        }

        var partialPath = finalPath + ".part";
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DownloadAsync(partialPath, MossQ8, progress, cancellationToken).ConfigureAwait(false);
                progress?.Report(new OperationProgress(JobState.VerifyingModel, "下载完成，正在校验 SHA-256…"));
                await VerifyAsync(partialPath, MossQ8, progress, cancellationToken).ConfigureAwait(false);
                File.Move(partialPath, finalPath, true);
                return finalPath;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (IsRetryableDownloadFailure(exception))
            {
                lastFailure = exception;
                if (exception is InvalidDataException)
                {
                    TryDelete(partialPath);
                }

                if (attempt < 3)
                {
                    var delay = exception is HttpRequestException
                        ? TimeSpan.FromSeconds(attempt * 2)
                        : TimeSpan.FromSeconds(attempt);
                    progress?.Report(new OperationProgress(
                        JobState.DownloadingModel,
                        $"模型下载或校验第 {attempt}/3 次失败，{delay.TotalSeconds:0} 秒后将进行第 {attempt + 1}/3 次尝试…"));
                    await _retryDelay(delay, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    progress?.Report(new OperationProgress(
                        JobState.DownloadingModel,
                        "模型下载或校验第 3/3 次失败，已停止自动重试。"));
                }
            }
        }

        var detail = string.IsNullOrWhiteSpace(lastFailure?.Message)
            ? string.Empty
            : $"最后一次错误：{lastFailure.Message} ";
        throw new HttpRequestException(
            $"模型下载或校验已完成全部 3 次尝试，仍然失败。{detail}" +
            "如存在可续传的部分文件，程序会在下次自动继续；请检查网络和磁盘空间后重试。",
            lastFailure);
    }

    private static bool IsRetryableDownloadFailure(Exception exception) =>
        exception is InvalidDataException or HttpRequestException or IOException;

    private async Task DownloadAsync(
        string partialPath,
        ModelDescriptor descriptor,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(partialPath) ?? throw new InvalidOperationException("模型目录无效。"));
        var existingBytes = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (existingBytes < 0 || existingBytes > descriptor.ExpectedBytes)
        {
            TryDelete(partialPath);
            existingBytes = 0;
        }

        if (existingBytes == descriptor.ExpectedBytes)
        {
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, descriptor.DownloadUri);
        if (existingBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);
        }

        progress?.Report(new OperationProgress(
            JobState.DownloadingModel,
            existingBytes > 0 ? "正在续传识别模型…" : "首次使用，正在下载约 941 MB 的本地模型…",
            (double)existingBytes / descriptor.ExpectedBytes));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingBytes == descriptor.ExpectedBytes)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
        var append = existingBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (!append)
        {
            existingBytes = 0;
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(
            partialPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var buffer = ArrayPool<byte>.Shared.Rent(1024 * 1024);
        try
        {
            long totalBytes = existingBytes;
            long lastReportedBytes = totalBytes;
            var lastReportAt = DateTime.UtcNow;
            while (true)
            {
                var read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                totalBytes += read;
                if (totalBytes > descriptor.ExpectedBytes)
                {
                    throw new InvalidDataException("模型文件大小超出预期，下载源可能已变更。");
                }

                var now = DateTime.UtcNow;
                if (totalBytes - lastReportedBytes >= 4 * 1024 * 1024 || now - lastReportAt >= TimeSpan.FromSeconds(1))
                {
                    progress?.Report(new OperationProgress(
                        JobState.DownloadingModel,
                        $"正在下载本地模型… {FormatBytes(totalBytes)} / {FormatBytes(descriptor.ExpectedBytes)}",
                        (double)totalBytes / descriptor.ExpectedBytes));
                    lastReportedBytes = totalBytes;
                    lastReportAt = now;
                }
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var actualBytes = new FileInfo(partialPath).Length;
        if (actualBytes != descriptor.ExpectedBytes)
        {
            throw new HttpRequestException(
                $"模型下载未完成：已收到 {FormatBytes(actualBytes)}，应为 {FormatBytes(descriptor.ExpectedBytes)}。");
        }
    }

    private static async Task VerifyAsync(
        string path,
        ModelDescriptor descriptor,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("找不到本地模型文件。", path);
        }

        var length = new FileInfo(path).Length;
        if (length != descriptor.ExpectedBytes)
        {
            throw new InvalidDataException(
                $"模型文件大小不正确：{FormatBytes(length)}，应为 {FormatBytes(descriptor.ExpectedBytes)}。");
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4 * 1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(4 * 1024 * 1024);
        try
        {
            long totalRead = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hasher.AppendData(buffer, 0, read);
                totalRead += read;
                if (totalRead % (64L * 1024 * 1024) < buffer.Length)
                {
                    progress?.Report(new OperationProgress(
                        JobState.VerifyingModel,
                        "正在校验模型完整性…",
                        (double)totalRead / descriptor.ExpectedBytes));
                }
            }

            var actualHash = Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant();
            if (!string.Equals(actualHash, descriptor.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"模型 SHA-256 校验失败。实际为 {actualHash}，期望为 {descriptor.Sha256}。");
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string FormatBytes(long bytes) => $"{bytes / 1024d / 1024d:0.0} MB";

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
