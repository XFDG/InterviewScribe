using System.Text.Json;
using InterviewScribe.Core.Domain;
using InterviewScribe.Core.Export;
using InterviewScribe.Core.Validation;
using InterviewScribe.Infrastructure.Media;
using InterviewScribe.Infrastructure.Models;
using InterviewScribe.Infrastructure.Processes;

namespace InterviewScribe.Infrastructure.Pipeline;

public sealed class TranscriptionPipeline
{
    private static readonly TimeSpan MaximumMediaDuration = TimeSpan.FromHours(2);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppPaths _paths;
    private readonly RuntimeLocator _runtimeLocator;
    private readonly ManagedProcessRunner _processRunner;
    private readonly MediaProcessor _mediaProcessor;

    public TranscriptionPipeline()
        : this(new AppPaths(), new ManagedProcessRunner())
    {
    }

    public TranscriptionPipeline(AppPaths paths, ManagedProcessRunner processRunner)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(processRunner);

        _paths = paths;
        _runtimeLocator = new RuntimeLocator(paths);
        _processRunner = processRunner;
        _mediaProcessor = new MediaProcessor(processRunner);
    }

    public async Task<PipelineResult> RunAsync(
        PipelineRequest request,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var languageSelection = LanguageSelection.FromCodes(request.LanguageCodes);
        var requestedFormattingOptions = request.FormattingOptions
            ?? throw new ArgumentException("转写格式选项不能为空。", nameof(request));
        var formattingOptions = new TranscriptFormattingOptions
        {
            IncludeTimestamps = requestedFormattingOptions.IncludeTimestamps,
            IncludeSpeakers = requestedFormattingOptions.IncludeSpeakers,
        };
        var sourcePath = ValidateSourcePath(request.SourcePath);
        var outputDirectory = ValidateOutputDirectory(request.OutputDirectory);

        _paths.EnsureCreated();
        CleanupOldJobs();

        // Resolve every packaged executable before a potentially long model download.
        // A damaged installation should fail immediately with an actionable message.
        var ffmpegPath = _runtimeLocator.FindFfmpeg();
        var ffprobePath = _runtimeLocator.FindFfprobe();
        var engineHostPath = _runtimeLocator.FindEngineHost();
        var transcribeRuntimeDirectory = _runtimeLocator.FindTranscribeRuntimeDirectory();

        var jobDirectory = _paths.CreateJobDirectory();
        var wavPath = Path.Combine(jobDirectory, "audio.wav");
        var engineResultPath = Path.Combine(jobDirectory, "engine-result.json");
        var logMessages = new List<string>();
        var succeeded = false;

        try
        {
            progress?.Report(new OperationProgress(
                JobState.ProbingMedia,
                "正在读取视频时长和音轨…"));
            var mediaInfo = await _mediaProcessor.ProbeAsync(
                ffprobePath,
                sourcePath,
                jobDirectory,
                cancellationToken).ConfigureAwait(false);

            if (mediaInfo.Duration > MaximumMediaDuration)
            {
                throw new NotSupportedException(
                    $"当前版本一次支持最长 {MaximumMediaDuration.TotalHours:0} 小时的录屏。" +
                    $"所选文件时长为 {FormatDuration(mediaInfo.Duration)}，请先分段后再识别，以避免内存耗尽。");
            }

            AddLog(
                logMessages,
                $"已检测音轨：{mediaInfo.AudioStreamCount} 路，{mediaInfo.CodecName}，{mediaInfo.SampleRate} Hz，{mediaInfo.Channels} 声道，时长 {FormatDuration(mediaInfo.Duration)}。");
            if (mediaInfo.AudioStreamCount > 1)
            {
                AddLog(logMessages, $"已合并 {mediaInfo.AudioStreamCount} 路音轨，避免遗漏麦克风或系统声音。");
            }

            // Validate the media before a first-run model download. Bad, silent or
            // over-limit input should fail quickly without consuming ~1 GB of data.
            progress?.Report(new OperationProgress(
                JobState.WaitingForModel,
                "正在检查 MOSS 本地模型…"));

            string modelPath;
            using (var modelStore = new ModelStore())
            {
                modelPath = await modelStore.EnsureAsync(_paths, progress, cancellationToken).ConfigureAwait(false);
            }

            AddLog(logMessages, $"模型已校验：{Path.GetFileName(modelPath)}");
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new OperationProgress(
                JobState.ExtractingAudio,
                "正在从视频提取 16 kHz 单声道音频…",
                0));
            await _mediaProcessor.ExtractMonoPcmAsync(
                ffmpegPath,
                sourcePath,
                wavPath,
                mediaInfo.Duration,
                mediaInfo.AudioStreamCount,
                jobDirectory,
                progress,
                cancellationToken).ConfigureAwait(false);

            AddLog(logMessages, "音轨提取完成；原视频未被修改。");
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new OperationProgress(
                JobState.ReadyToTranscribe,
                "正在初始化显卡推理…"));

            EngineRunOutcome engineRun;
            try
            {
                engineRun = await RunEngineAsync(
                    "vulkan",
                    languageSelection,
                    engineHostPath,
                    transcribeRuntimeDirectory,
                    modelPath,
                    wavPath,
                    engineResultPath,
                    jobDirectory,
                    progress,
                    cancellationToken).ConfigureAwait(false);
                AddLog(logMessages, $"识别后端：Vulkan（耗时 {FormatDuration(engineRun.Elapsed)}）。");
            }
            catch (EngineRunException vulkanException) when (!cancellationToken.IsCancellationRequested)
            {
                AddLog(logMessages, $"Vulkan 未能完成识别：{vulkanException.CleanMessage}");
                AddLog(logMessages, "已自动切换到 CPU 后端重试。");
                progress?.Report(new OperationProgress(
                    JobState.ReadyToTranscribe,
                    "显卡推理不可用，正在切换到 CPU…"));

                TryDelete(engineResultPath);
                TryDelete(engineResultPath + ".tmp");
                try
                {
                    engineRun = await RunEngineAsync(
                        "cpu",
                        languageSelection,
                        engineHostPath,
                        transcribeRuntimeDirectory,
                        modelPath,
                        wavPath,
                        engineResultPath,
                        jobDirectory,
                        progress,
                        cancellationToken).ConfigureAwait(false);
                    AddLog(logMessages, $"识别后端：CPU（耗时 {FormatDuration(engineRun.Elapsed)}）。");
                }
                catch (EngineRunException cpuException)
                {
                    throw new InvalidOperationException(
                        $"显卡和 CPU 识别都未能完成。CPU 详细信息：{cpuException.CleanMessage} " +
                        $"诊断文件保存在：{jobDirectory}",
                        cpuException);
                }
            }

            progress?.Report(new OperationProgress(
                JobState.ValidatingResult,
                "正在检查说话人、时间轴和结果完整性…"));
            var engineResult = await ReadEngineResultAsync(engineResultPath, cancellationToken).ConfigureAwait(false);
            var document = CreateValidatedDocument(engineResult, sourcePath, mediaInfo.Duration);

            AddLog(
                logMessages,
                $"识别完成：{document.Segments.Count} 个时间段，{document.Segments.Where(item => item.SpeakerId > 0).Select(item => item.SpeakerId).Distinct().Count()} 位说话人。");

            progress?.Report(new OperationProgress(
                JobState.Exporting,
                "正在安全写入 TXT、SRT 和 JSON…"));
            var exported = await AtomicTranscriptExporter.ExportAsync(
                document,
                outputDirectory,
                cancellationToken,
                formattingOptions).ConfigureAwait(false);

            AddLog(logMessages, $"结果已保存：{exported.TxtPath}");
            succeeded = true;
            return new PipelineResult(
                exported.TxtPath,
                exported.SrtPath,
                exported.JsonPath,
                document,
                logMessages.ToArray(),
                outputDirectory)
            {
                FormattingOptions = formattingOptions,
            };
        }
        finally
        {
            // Extracted WAV can be large and is always reproducible from the user's
            // untouched source.  Keep diagnostic text/JSON after failures, but not WAV.
            TryDelete(wavPath);
            TryDelete(wavPath + ".tmp");
            if (succeeded)
            {
                TryDeleteDirectory(jobDirectory);
            }
        }
    }

    private async Task<EngineRunOutcome> RunEngineAsync(
        string backend,
        LanguageSelection languageSelection,
        string engineHostPath,
        string runtimeDirectory,
        string modelPath,
        string wavPath,
        string resultPath,
        string jobDirectory,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new OperationProgress(
            JobState.Transcribing,
            backend == "vulkan"
                ? "正在本地识别中英文并区分说话人…"
                : "正在使用 CPU 识别，速度会慢一些…"));

        var logPrefix = backend == "vulkan" ? "engine-vulkan" : "engine-cpu";
        string? lastProgressMessage = null;
        var result = await _processRunner.RunAsync(
            new ProcessSpec
            {
                FileName = engineHostPath,
                WorkingDirectory = Path.GetDirectoryName(engineHostPath),
                Arguments = BuildEngineArguments(
                    runtimeDirectory,
                    modelPath,
                    wavPath,
                    resultPath,
                    backend,
                    languageSelection),
                StandardOutputLogPath = Path.Combine(jobDirectory, logPrefix + ".stdout.log"),
                StandardErrorLogPath = Path.Combine(jobDirectory, logPrefix + ".stderr.log"),
            },
            onStandardError: line =>
            {
                var engineEvent = TryParseEngineEvent(line);
                if (engineEvent is not { Type: "progress" } ||
                    string.IsNullOrWhiteSpace(engineEvent.Message) ||
                    string.Equals(lastProgressMessage, engineEvent.Message, StringComparison.Ordinal))
                {
                    return;
                }

                lastProgressMessage = engineEvent.Message;
                progress?.Report(new OperationProgress(
                    JobState.Transcribing,
                    engineEvent.Message.Trim()));
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new EngineRunException(ExtractEngineError(result.StandardErrorTail), result.ExitCode);
        }

        if (!File.Exists(resultPath) || new FileInfo(resultPath).Length == 0)
        {
            throw new EngineRunException("识别进程已结束，但没有生成结果文件。", result.ExitCode);
        }

        return new EngineRunOutcome(result.Elapsed);
    }

    internal static IReadOnlyList<string> BuildEngineArguments(
        string runtimeDirectory,
        string modelPath,
        string wavPath,
        string resultPath,
        string backend,
        LanguageSelection languageSelection)
    {
        ArgumentNullException.ThrowIfNull(languageSelection);

        return
        [
            "--runtime", runtimeDirectory,
            "--model", modelPath,
            "--audio", wavPath,
            "--output", resultPath,
            "--backend", backend,
            "--language", languageSelection.EngineArgument,
        ];
    }

    private static async Task<EngineResultDto> ReadEngineResultAsync(
        string resultPath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                resultPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var result = await JsonSerializer.DeserializeAsync<EngineResultDto>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            return result ?? throw new InvalidDataException("识别引擎返回了空结果。");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("识别结果 JSON 损坏或版本不兼容。", exception);
        }
    }

    private static TranscriptDocument CreateValidatedDocument(
        EngineResultDto result,
        string sourcePath,
        TimeSpan mediaDuration)
    {
        if (result.IsPartial)
        {
            throw new InvalidDataException("模型报告结果被截断，已拒绝生成看似完整的 TXT。");
        }

        if (result.Segments is null)
        {
            throw new InvalidDataException("识别引擎没有返回 segments 字段。");
        }

        var segments = result.Segments
            .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Text))
            .Select(item => new TranscriptSegment(
                Math.Max(0, item.StartMs),
                Math.Max(Math.Max(0, item.StartMs), item.EndMs),
                item.SpeakerId,
                item.Text.Trim()))
            .OrderBy(item => item.StartMs)
            .ThenBy(item => item.EndMs)
            .ToArray();

        var validation = TranscriptValidator.Validate(segments, mediaDuration);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join("；", validation.Errors));
        }

        var warnings = new List<string>();
        if (result.Warnings is not null)
        {
            warnings.AddRange(result.Warnings.Where(item => !string.IsNullOrWhiteSpace(item)).Select(item => item.Trim()));
        }

        warnings.AddRange(validation.Warnings);
        AddCoverageWarning(warnings, segments, mediaDuration);

        var fullText = string.IsNullOrWhiteSpace(result.FullText)
            ? string.Join(Environment.NewLine, segments.Select(item => item.Text))
            : result.FullText.Trim();

        return new TranscriptDocument
        {
            SourceFileName = Path.GetFileName(sourcePath),
            MediaDuration = mediaDuration,
            ModelName = ModelStore.MossQ8.DisplayName,
            ModelRevision = ModelStore.MossQ8.Revision,
            EngineVersion = string.IsNullOrWhiteSpace(result.EngineVersion)
                ? result.Backend
                : $"{result.EngineVersion} / {result.Backend}",
            CreatedAt = DateTimeOffset.Now,
            FullText = fullText,
            Segments = segments,
            Warnings = warnings.Distinct(StringComparer.Ordinal).ToArray(),
            IsPartial = false,
        };
    }

    private static void AddCoverageWarning(
        ICollection<string> warnings,
        IReadOnlyList<TranscriptSegment> segments,
        TimeSpan mediaDuration)
    {
        if (segments.Count == 0)
        {
            return;
        }

        var trailingSilenceMs = mediaDuration.TotalMilliseconds - segments.Max(item => item.EndMs);
        var warningThresholdMs = Math.Max(30_000, mediaDuration.TotalMilliseconds * 0.2);
        if (trailingSilenceMs > warningThresholdMs)
        {
            warnings.Add("最后一段识别文字与视频结尾相差较大；结尾可能是静音，也建议人工确认。");
        }
    }

    private static EngineEvent? TryParseEngineEvent(string line)
    {
        if (string.IsNullOrWhiteSpace(line) || line[0] != '{')
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<EngineEvent>(line, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ExtractEngineError(string stderr)
    {
        var lines = stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var index = lines.Length - 1; index >= 0; index--)
        {
            var engineEvent = TryParseEngineEvent(lines[index]);
            if (engineEvent is { Type: "error" } && !string.IsNullOrWhiteSpace(engineEvent.Message))
            {
                return engineEvent.Message.Trim();
            }
        }

        var fallback = lines.LastOrDefault();
        if (string.IsNullOrWhiteSpace(fallback))
        {
            return "识别进程异常退出，未返回详细原因。";
        }

        return fallback.Length <= 800 ? fallback : fallback[^800..];
    }

    private void CleanupOldJobs()
    {
        if (!Directory.Exists(_paths.JobsRoot))
        {
            return;
        }

        var cutoff = DateTime.UtcNow - TimeSpan.FromDays(7);
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(_paths.JobsRoot))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                    {
                        Directory.Delete(directory, recursive: true);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string ValidateSourcePath(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("请先选择一个视频或音频文件。", nameof(sourcePath));
        }

        var fullPath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("找不到所选视频，它可能已被移动。", fullPath);
        }

        return fullPath;
    }

    internal static string ValidateOutputDirectory(string outputDirectory)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("请先选择结果保存位置。", nameof(outputDirectory));
        }

        var fullPath = Path.GetFullPath(outputDirectory);
        var probePath = Path.Combine(fullPath, $".interviewscribe-write-test-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(fullPath);
            using (var stream = new FileStream(
                probePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 1,
                FileOptions.WriteThrough))
            {
                stream.WriteByte(0);
                stream.Flush(flushToDisk: true);
            }

            File.Delete(probePath);
            return fullPath;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            TryDelete(probePath);
            throw new IOException(
                $"结果保存位置无法写入：{fullPath}。请选择有写入权限且磁盘空间充足的文件夹。",
                exception);
        }
    }

    private static void AddLog(ICollection<string> messages, string message)
    {
        messages.Add(message);
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
        }

        return $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    private sealed record EngineEvent(string Type, string Message, double? Fraction, int? NativeStatus);
    private sealed record EngineRunOutcome(TimeSpan Elapsed);

    private sealed class EngineRunException(string message, int exitCode) : Exception(message)
    {
        public int ExitCode { get; } = exitCode;
        public string CleanMessage { get; } = message;
    }
}
