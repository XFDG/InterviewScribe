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
    private static readonly TimeSpan MaximumMediaDuration = TimeSpan.FromHours(8);
    private static readonly TimeSpan MinimumMossGpuChunkDuration = TimeSpan.FromMinutes(2);
    private const int MaximumMossGpuContextReplans = 2;
    internal const int VulkanProtectedContextTokens = 16_384;
    private const string WhisperTurboDisplayName = "Whisper large-v3-turbo + Faster-Whisper";
    private const string QwenLocalDisplayName = "Qwen3-ASR 1.7B + ForcedAligner";

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
        CancellationToken cancellationToken = default,
        IProgress<string>? diagnostics = null)
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
        if (!Enum.IsDefined(request.Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(request), "不支持所选的识别模式。");
        }
        if (request.OutputFormats == TranscriptOutputFormat.None ||
            (request.OutputFormats & ~TranscriptOutputFormat.All) != 0)
        {
            throw new ArgumentException("请至少选择一种受支持的输出格式。", nameof(request));
        }

        var useMoss = request.Mode == TranscriptionMode.MossLocalFast || request.EnableSpeakerDiarization;
        var progressCoordinator = new PipelineProgressCoordinator(request.Mode, useMoss, progress);

        _paths.EnsureCreated();
        CleanupOldJobs();

        // Resolve every packaged executable before a potentially long model download.
        // A damaged installation should fail immediately with an actionable message.
        var ffmpegPath = _runtimeLocator.FindFfmpeg();
        var ffprobePath = _runtimeLocator.FindFfprobe();
        string? engineHostPath = null;
        string? transcribeRuntimeDirectory = null;
        if (useMoss)
        {
            engineHostPath = _runtimeLocator.FindEngineHost();
            transcribeRuntimeDirectory = _runtimeLocator.FindTranscribeRuntimeDirectory();
        }

        var qwenRuntime = ResolveQwenRuntime(request.Mode);
        var whisperRuntime = ResolveWhisperRuntime(request.Mode);
        var gpuProfile = GpuExecutionProfile.Detect();

        var jobDirectory = _paths.CreateJobDirectory();
        var wavPath = Path.Combine(jobDirectory, "audio.wav");
        var mossResultPath = Path.Combine(jobDirectory, "moss-result.json");
        var qwenResultPath = Path.Combine(jobDirectory, "qwen-result.json");
        var whisperResultPath = Path.Combine(jobDirectory, "whisper-result.json");
        var logMessages = new List<string>();
        var succeeded = false;

        try
        {
            progressCoordinator.Report(
                PipelinePhase.ProbeMedia,
                JobState.ProbingMedia,
                "正在读取视频时长和音轨…",
                0);
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

            progressCoordinator.Report(
                PipelinePhase.ProbeMedia,
                JobState.ProbingMedia,
                "视频信息读取完成。",
                1);
            AddLog(
                logMessages,
                $"已检测音轨：{mediaInfo.AudioStreamCount} 路，{mediaInfo.CodecName}，{mediaInfo.SampleRate} Hz，{mediaInfo.Channels} 声道，时长 {FormatDuration(mediaInfo.Duration)}。",
                diagnostics);
            if (mediaInfo.AudioStreamCount > 1)
            {
                AddLog(logMessages, $"已合并 {mediaInfo.AudioStreamCount} 路音轨，避免遗漏麦克风或系统声音。", diagnostics);
            }

            // Validate the media before a first-run model download. Bad, silent or
            // over-limit input should fail quickly without consuming model data.
            string? mossModelPath = null;
            if (useMoss)
            {
                progressCoordinator.Report(
                    PipelinePhase.PrepareModel,
                    JobState.WaitingForModel,
                    "正在检查 MOSS 本地模型…",
                    0);
                using var modelStore = new ModelStore();
                mossModelPath = await modelStore.EnsureAsync(
                    _paths,
                    progressCoordinator.ForPhase(PipelinePhase.PrepareModel),
                    cancellationToken).ConfigureAwait(false);
                progressCoordinator.Report(
                    PipelinePhase.PrepareModel,
                    JobState.VerifyingModel,
                    "MOSS 本地模型已校验。",
                    1);
                AddLog(logMessages, $"MOSS 模型已校验：{Path.GetFileName(mossModelPath)}", diagnostics);
            }
            else
            {
                var selectedModel = request.Mode switch
                {
                    TranscriptionMode.WhisperTurboFast => "Whisper large-v3-turbo 本地模型",
                    TranscriptionMode.QwenLocalHighAccuracy => "Qwen3-ASR 与 ForcedAligner 本地模型",
                    _ => throw new ArgumentOutOfRangeException(nameof(request.Mode)),
                };
                progressCoordinator.Report(
                    PipelinePhase.PrepareModel,
                    JobState.VerifyingModel,
                    $"已检查 {selectedModel}。",
                    1);
                AddLog(logMessages, $"已检查 {selectedModel}；未启用 MOSS 说话人后处理。", diagnostics);
            }
            var memoryLabel = gpuProfile.DedicatedMemoryMiB is int memory
                ? $"{memory:N0} MiB"
                : "未能查询";
            AddLog(
                logMessages,
                $"硬件自适应：{gpuProfile.AdapterName}，显存 {memoryLabel}；" +
                $"MOSS 上下文 {gpuProfile.MossContextTokens:N0} token，" +
                $"Qwen 分块 {gpuProfile.QwenChunkSeconds} 秒，Whisper 批大小 {gpuProfile.WhisperBatchSize}。",
                diagnostics);
            cancellationToken.ThrowIfCancellationRequested();

            progressCoordinator.Report(
                PipelinePhase.ExtractAudio,
                JobState.ExtractingAudio,
                "正在从视频提取 16 kHz 单声道音频…",
                0);
            await _mediaProcessor.ExtractMonoPcmAsync(
                ffmpegPath,
                sourcePath,
                wavPath,
                mediaInfo.Duration,
                mediaInfo.AudioStreamCount,
                jobDirectory,
                progressCoordinator.ForPhase(PipelinePhase.ExtractAudio),
                cancellationToken).ConfigureAwait(false);

            progressCoordinator.Report(
                PipelinePhase.ExtractAudio,
                JobState.ExtractingAudio,
                "音轨提取完成。",
                1);
            AddLog(logMessages, "音轨提取完成；原视频未被修改。", diagnostics);
            cancellationToken.ThrowIfCancellationRequested();

            // Container/video duration can be longer than its audio stream. Chunk
            // the WAV that will actually be transcribed, otherwise a silent video
            // tail could produce an empty final chunk and fail the entire job.
            var extractedAudioInfo = await _mediaProcessor.ProbeAsync(
                ffprobePath,
                wavPath,
                jobDirectory,
                cancellationToken,
                "ffprobe-extracted-audio.stderr.log").ConfigureAwait(false);
            var recognitionDuration = extractedAudioInfo.Duration;
            var documentDuration = GetEffectiveMediaDuration(mediaInfo.Duration, recognitionDuration);
            var durationDifference = mediaInfo.Duration - recognitionDuration;
            if (durationDifference > TimeSpan.FromSeconds(2))
            {
                AddLog(
                    logMessages,
                    $"视频比实际音轨长 {FormatDuration(durationDifference)}；" +
                    "将按实际音轨时长分段，不会在结尾生成空音频。",
                    diagnostics);
            }

            EngineResultDto? mossResult = null;
            if (useMoss)
            {
                if (request.EnableSpeakerDiarization && request.Mode != TranscriptionMode.MossLocalFast)
                {
                    AddLog(
                        logMessages,
                        "已启用说话人区分：将先生成额外的本地 MOSS 说话人轨道；" +
                        "Whisper/Qwen 的文字识别将在该轨道完成后开始。",
                        diagnostics);
                }
                progressCoordinator.Report(
                    PipelinePhase.MossDiarization,
                    JobState.ReadyToTranscribe,
                    request.Mode == TranscriptionMode.MossLocalFast
                        ? "正在初始化 MOSS 显卡推理…"
                        : "正在生成可选的本地说话人轨道…",
                    0);
                await RunMossAsync(
                    ffmpegPath,
                    languageSelection,
                    engineHostPath ?? throw new InvalidOperationException("MOSS 引擎宿主未初始化。"),
                    transcribeRuntimeDirectory ?? throw new InvalidOperationException("MOSS 运行库未初始化。"),
                    mossModelPath ?? throw new InvalidOperationException("MOSS 模型未初始化。"),
                    wavPath,
                    mossResultPath,
                    sourcePath,
                    recognitionDuration,
                    gpuProfile,
                    jobDirectory,
                    progressCoordinator.ForPhase(PipelinePhase.MossDiarization),
                    progressCoordinator.ResetEta,
                    () => progressCoordinator.BeginPhaseRetry(PipelinePhase.MossDiarization),
                    logMessages,
                    diagnostics,
                    cancellationToken).ConfigureAwait(false);
                progressCoordinator.Report(
                    PipelinePhase.MossDiarization,
                    JobState.Transcribing,
                    "正在检查 MOSS 说话人轨道…",
                    1);
                mossResult = await ReadEngineResultAsync(mossResultPath, cancellationToken).ConfigureAwait(false);
                _ = CreateValidatedDocument(
                    mossResult,
                    sourcePath,
                    documentDuration,
                    ModelStore.MossQ8.DisplayName,
                    ModelStore.MossQ8.Revision);
            }

            EngineResultDto recognitionResult;
            string modelName;
            string modelRevision;
            switch (request.Mode)
            {
                case TranscriptionMode.MossLocalFast:
                    recognitionResult = mossResult ?? throw new InvalidOperationException("MOSS 结果未生成。");
                    modelName = ModelStore.MossQ8.DisplayName;
                    modelRevision = ModelStore.MossQ8.Revision;
                    break;

                case TranscriptionMode.WhisperTurboFast:
                {
                    var run = await RunWhisperAsync(
                        whisperRuntime ?? throw new InvalidOperationException("Whisper 运行环境未初始化。"),
                        languageSelection,
                        wavPath,
                        whisperResultPath,
                        gpuProfile,
                        jobDirectory,
                        progressCoordinator.ForPhase(PipelinePhase.QwenRecognition),
                        diagnostics,
                        cancellationToken).ConfigureAwait(false);
                    AddLog(logMessages, $"Whisper Turbo 识别完成（耗时 {FormatDuration(run.Elapsed)}）。", diagnostics);
                    var whisperResult = await ReadEngineResultAsync(whisperResultPath, cancellationToken).ConfigureAwait(false);
                    recognitionResult = MergeRecognitionWithOptionalMoss(
                        whisperResult,
                        mossResult,
                        "Whisper 的文字与时间轴；说话人标签来自本地 MOSS 轨道并按时间重叠合并。",
                        "未启用说话人区分；Whisper 结果保留时间轴和文字。"
                    );
                    modelName = WhisperTurboDisplayName;
                    modelRevision = FasterWhisperModelManifest.Revision;
                    break;
                }

                case TranscriptionMode.QwenLocalHighAccuracy:
                {
                    var run = await RunQwenAsync(
                        qwenRuntime ?? throw new InvalidOperationException("Qwen 运行环境未初始化。"),
                        languageSelection,
                        wavPath,
                        qwenResultPath,
                        gpuProfile.QwenChunkSeconds,
                        jobDirectory,
                        progressCoordinator.ForPhase(PipelinePhase.QwenRecognition),
                        diagnostics,
                        cancellationToken).ConfigureAwait(false);
                    AddLog(logMessages, $"Qwen 高精度识别完成（耗时 {FormatDuration(run.Elapsed)}）。", diagnostics);
                    var qwenResult = await ReadEngineResultAsync(qwenResultPath, cancellationToken).ConfigureAwait(false);
                    recognitionResult = MergeRecognitionWithOptionalMoss(
                        qwenResult,
                        mossResult,
                        "Qwen 本地模型生成文字与逐词对齐；说话人标签来自本地 MOSS 轨道并按时间重叠合并。",
                        "未启用说话人区分；Qwen 的文字和逐词时间轴完全来自本地模型。"
                    );
                    (modelName, modelRevision) = GetHighAccuracyModelIdentity();
                    break;
                }

                default:
                    throw new ArgumentOutOfRangeException(nameof(request.Mode));
            }
            var document = CreateValidatedDocument(
                recognitionResult,
                sourcePath,
                documentDuration,
                modelName,
                modelRevision);

            progressCoordinator.Report(
                PipelinePhase.Validate,
                JobState.ValidatingResult,
                "正在检查说话人、时间轴和结果完整性…",
                0);
            AddLog(
                logMessages,
                $"识别完成：{document.Segments.Count} 个时间段，{document.Segments.Where(item => item.SpeakerId > 0).Select(item => item.SpeakerId).Distinct().Count()} 位说话人。",
                diagnostics);
            progressCoordinator.Report(
                PipelinePhase.Validate,
                JobState.ValidatingResult,
                "结果完整性检查通过。",
                1);

            progressCoordinator.Report(
                PipelinePhase.Export,
                JobState.Exporting,
                "正在安全写入所选输出格式…",
                0);
            var exported = await AtomicTranscriptExporter.ExportAsync(
                document,
                outputDirectory,
                request.OutputFormats,
                cancellationToken,
                formattingOptions).ConfigureAwait(false);

            progressCoordinator.Report(
                PipelinePhase.Export,
                JobState.Exporting,
                "所选输出文件已安全保存。",
                1);
            foreach (var path in exported.Paths.Values)
            {
                AddLog(logMessages, $"结果已保存：{path}", diagnostics);
            }
            succeeded = true;
            progressCoordinator.Report(
                PipelinePhase.Completed,
                JobState.Completed,
                "转写与导出完成。",
                1);
            return new PipelineResult(
                exported.TxtPath,
                exported.SrtPath,
                exported.JsonPath,
                document,
                logMessages.ToArray(),
                outputDirectory)
            {
                FormattingOptions = formattingOptions,
                OutputPaths = exported.Paths,
            };
        }
        finally
        {
            // Extracted WAV can be large and is always reproducible from the user's
            // untouched source. Keep diagnostic text/JSON after failures, but not
            // the source WAV or derived MOSS/Qwen audio chunks.
            CleanupJobAudioArtifacts(jobDirectory);
            if (succeeded)
            {
                TryDeleteDirectory(jobDirectory);
            }
        }
    }

    private async Task RunMossAsync(
        string ffmpegPath,
        LanguageSelection languageSelection,
        string engineHostPath,
        string runtimeDirectory,
        string modelPath,
        string sourceWavPath,
        string finalResultPath,
        string sourcePath,
        TimeSpan audioDuration,
        GpuExecutionProfile gpuProfile,
        string jobDirectory,
        IProgress<OperationProgress>? progress,
        Action resetEta,
        Action restartMossProgress,
        ICollection<string> logMessages,
        IProgress<string>? diagnostics,
        CancellationToken cancellationToken)
    {
        var maximumChunkDuration = MossChunkPlanner.GetGpuSafeMaximumChunkDuration(
            gpuProfile.MossContextTokens);

        for (var planAttempt = 0; ; planAttempt++)
        {
            var chunks = MossChunkPlanner.Create(audioDuration, maximumChunkDuration);
            try
            {
                await RunMossPlanAsync(
                    ffmpegPath,
                    languageSelection,
                    engineHostPath,
                    runtimeDirectory,
                    modelPath,
                    sourceWavPath,
                    finalResultPath,
                    sourcePath,
                    audioDuration,
                    gpuProfile,
                    maximumChunkDuration,
                    planAttempt,
                    chunks,
                    jobDirectory,
                    progress,
                    resetEta,
                    logMessages,
                    diagnostics,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (MossGpuChunkSizeException exception) when (
                !cancellationToken.IsCancellationRequested &&
                planAttempt < MaximumMossGpuContextReplans &&
                maximumChunkDuration > MinimumMossGpuChunkDuration)
            {
                var reducedMaximum = ReduceMossGpuChunkDuration(maximumChunkDuration);
                if (reducedMaximum >= maximumChunkDuration)
                {
                    throw;
                }

                TryDelete(finalResultPath);
                TryDelete(finalResultPath + ".tmp");
                AddLog(
                    logMessages,
                    $"MOSS GPU 分段仍触及上下文：{exception.Message} 已将最长分段从 " +
                    $"{FormatDuration(maximumChunkDuration)} 缩短为 {FormatDuration(reducedMaximum)}，" +
                    "继续使用 Vulkan GPU，不会整段改用 CPU。",
                    diagnostics);
                restartMossProgress();
                progress?.Report(new OperationProgress(
                    JobState.ReadyToTranscribe,
                    "MOSS 正在缩短音频分段后继续使用 GPU；剩余时间将重新估算。",
                    0));
                maximumChunkDuration = reducedMaximum;
            }
            catch (MossGpuChunkSizeException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    "MOSS 已多次缩短 GPU 音频分段，但仍无法在当前显卡上下文中输出完整结果。" +
                    "为避免把整段录音改用极慢的 CPU，本次已停止；可取消“说话人区分”后使用 Whisper/Qwen 转写，" +
                    $"或查看诊断文件：{jobDirectory}",
                    exception);
            }
        }
    }

    private async Task RunMossPlanAsync(
        string ffmpegPath,
        LanguageSelection languageSelection,
        string engineHostPath,
        string runtimeDirectory,
        string modelPath,
        string sourceWavPath,
        string finalResultPath,
        string sourcePath,
        TimeSpan audioDuration,
        GpuExecutionProfile gpuProfile,
        TimeSpan maximumChunkDuration,
        int planAttempt,
        IReadOnlyList<MossChunkPlan> chunks,
        string jobDirectory,
        IProgress<OperationProgress>? progress,
        Action resetEta,
        ICollection<string> logMessages,
        IProgress<string>? diagnostics,
        CancellationToken cancellationToken)
    {
        var chunkResults = new List<MossChunkEngineResult>(chunks.Count);
        var temporaryResultPaths = new List<string>();
        var audioDirectory = Path.Combine(jobDirectory, $".moss-audio-chunks-plan-{planAttempt:00}");
        var totalInferenceTime = TimeSpan.Zero;

        if (chunks.Count > 1)
        {
            Directory.CreateDirectory(audioDirectory);
            AddLog(
                logMessages,
                $"长录音将拆成 {chunks.Count} 段（每段不超过 " +
                $"{FormatDuration(maximumChunkDuration)}，边界重叠 " +
                $"{MossChunkPlanner.BoundaryOverlap.TotalSeconds:0} 秒）；每段优先使用 Vulkan GPU。",
                diagnostics);
        }

        AddLog(
            logMessages,
            $"GPU 优先：{gpuProfile.AdapterName} 将使用 {gpuProfile.MossContextTokens:N0}-token 保守显存窗口；" +
            $"根据该窗口，单段最长 {FormatDuration(maximumChunkDuration)}。上下文长度不足时会先自动缩短分段并继续使用 GPU。",
            diagnostics);

        try
        {
            for (var index = 0; index < chunks.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chunk = chunks[index];
                var chunkNumber = index + 1;
                var runLabel = chunks.Count == 1
                    ? planAttempt == 0 ? null : $"retry-{planAttempt:00}"
                    : $"plan-{planAttempt:00}-chunk-{chunkNumber:000}";
                var displayPrefix = chunks.Count == 1 ? string.Empty : $"第 {chunkNumber}/{chunks.Count} 段：";
                var chunkAudioPath = chunks.Count == 1
                    ? sourceWavPath
                    : Path.Combine(audioDirectory, $"audio-{chunkNumber:000}.wav");
                var chunkResultPath = chunks.Count == 1
                    ? finalResultPath
                    : Path.Combine(jobDirectory, $"moss-result.plan-{planAttempt:00}.chunk-{chunkNumber:000}.json");
                if (chunks.Count > 1)
                {
                    temporaryResultPaths.Add(chunkResultPath);
                }

                var chunkProgress = new ChunkOperationProgress(
                    progress,
                    index,
                    chunks.Count,
                    displayPrefix,
                    chunks.Count == 1 ? 1 : 0.99);
                try
                {
                    if (chunks.Count > 1)
                    {
                        chunkProgress.Report(new OperationProgress(
                            JobState.Transcribing,
                            "正在准备临时音频…",
                            0));
                        await _mediaProcessor.ExtractMonoPcmSegmentAsync(
                            ffmpegPath,
                            sourceWavPath,
                            chunkAudioPath,
                            TimeSpan.FromMilliseconds(chunk.StartMs),
                            TimeSpan.FromMilliseconds(chunk.DurationMs),
                            jobDirectory,
                            runLabel!,
                            cancellationToken).ConfigureAwait(false);
                    }

                    var attempt = await RunMossChunkWithFallbackAsync(
                        languageSelection,
                        engineHostPath,
                        runtimeDirectory,
                        modelPath,
                        chunkAudioPath,
                        chunkResultPath,
                        jobDirectory,
                        runLabel,
                        chunk,
                        chunks.Count,
                        gpuProfile,
                        chunkProgress,
                        resetEta,
                        logMessages,
                        diagnostics,
                        cancellationToken).ConfigureAwait(false);
                    totalInferenceTime += attempt.Outcome.Elapsed;
                    chunkResults.Add(new MossChunkEngineResult(chunk, attempt.Result));
                    chunkProgress.Report(new OperationProgress(
                        JobState.Transcribing,
                        "识别完成",
                        1));
                }
                finally
                {
                    if (chunks.Count > 1)
                    {
                        TryDelete(chunkAudioPath);
                        TryDelete(chunkAudioPath + ".tmp");
                    }
                }
            }

            if (chunks.Count > 1)
            {
                progress?.Report(new OperationProgress(
                    JobState.Transcribing,
                    "正在合并分段时间轴、边界文字和说话人标签…",
                    0.995));
                var merged = MossChunkResultMerger.Merge(
                    chunkResults,
                    Path.GetFileName(sourcePath),
                    audioDuration);
                await WriteEngineResultAsync(finalResultPath, merged, cancellationToken).ConfigureAwait(false);
                progress?.Report(new OperationProgress(
                    JobState.Transcribing,
                    "分段时间轴和说话人标签已合并。",
                    1));
                AddLog(
                    logMessages,
                    $"{chunks.Count} 段 MOSS 结果已合并（纯推理耗时 " +
                    $"{FormatDuration(totalInferenceTime)}）；临时分段已清理，原视频未修改。",
                    diagnostics);
            }
        }
        finally
        {
            foreach (var path in temporaryResultPaths)
            {
                TryDelete(path);
                TryDelete(path + ".tmp");
            }

            if (chunks.Count > 1)
            {
                TryDeleteDirectory(audioDirectory);
            }
        }
    }

    private async Task<MossChunkAttempt> RunMossChunkWithFallbackAsync(
        LanguageSelection languageSelection,
        string engineHostPath,
        string runtimeDirectory,
        string modelPath,
        string wavPath,
        string resultPath,
        string jobDirectory,
        string? runLabel,
        MossChunkPlan chunk,
        int chunkCount,
        GpuExecutionProfile gpuProfile,
        IProgress<OperationProgress>? progress,
        Action resetEta,
        ICollection<string> logMessages,
        IProgress<string>? diagnostics,
        CancellationToken cancellationToken)
    {
        var label = chunkCount == 1 ? string.Empty : $"第 {chunk.Index + 1}/{chunkCount} 段";
        try
        {
            var attempt = await RunMossAttemptAsync(
                "vulkan",
                languageSelection,
                engineHostPath,
                runtimeDirectory,
                modelPath,
                wavPath,
                resultPath,
                jobDirectory,
                runLabel,
                GetVulkanContextTokenCap(TimeSpan.FromMilliseconds(chunk.DurationMs), gpuProfile),
                progress,
                diagnostics,
                cancellationToken).ConfigureAwait(false);
            AddLog(
                logMessages,
                $"{label}识别后端：Vulkan GPU（耗时 {FormatDuration(attempt.Outcome.Elapsed)}）。",
                diagnostics);
            return attempt;
        }
        catch (EngineRunException vulkanException) when (
            !cancellationToken.IsCancellationRequested &&
            IsMossGpuChunkSizeFailure(vulkanException))
        {
            TryDelete(resultPath);
            TryDelete(resultPath + ".tmp");
            AddLog(
                logMessages,
                $"{label}Vulkan GPU 段超过当前上下文，准备缩短分段后继续使用 GPU：" +
                vulkanException.CleanMessage,
                diagnostics);
            throw new MossGpuChunkSizeException(vulkanException.CleanMessage, vulkanException);
        }
        catch (EngineRunException vulkanException) when (!cancellationToken.IsCancellationRequested)
        {
            AddLog(
                logMessages,
                $"{label}Vulkan GPU 未能完成识别：{vulkanException.CleanMessage}",
                diagnostics);
            AddLog(logMessages, $"{label}已自动切换到 CPU 后端重试（不是音频上下文长度问题）。", diagnostics);
            resetEta();
            progress?.Report(new OperationProgress(
                JobState.ReadyToTranscribe,
                "显卡推理不可用，正在切换到 CPU；剩余时间将重新估算。",
                0));

            TryDelete(resultPath);
            TryDelete(resultPath + ".tmp");
            try
            {
                var attempt = await RunMossAttemptAsync(
                    "cpu",
                    languageSelection,
                    engineHostPath,
                    runtimeDirectory,
                    modelPath,
                    wavPath,
                    resultPath,
                    jobDirectory,
                    runLabel,
                    null,
                    progress,
                    diagnostics,
                    cancellationToken).ConfigureAwait(false);
                AddLog(
                    logMessages,
                    $"{label}识别后端：CPU 回退（耗时 {FormatDuration(attempt.Outcome.Elapsed)}）。",
                    diagnostics);
                return attempt;
            }
            catch (EngineRunException cpuException)
            {
                throw new InvalidOperationException(
                    $"{label}显卡和 CPU 识别都未能完成。CPU 详细信息：{cpuException.CleanMessage} " +
                    $"诊断文件保存在：{jobDirectory}",
                    cpuException);
            }
        }
    }

    private async Task<MossChunkAttempt> RunMossAttemptAsync(
        string backend,
        LanguageSelection languageSelection,
        string engineHostPath,
        string runtimeDirectory,
        string modelPath,
        string wavPath,
        string resultPath,
        string jobDirectory,
        string? runLabel,
        int? contextTokens,
        IProgress<OperationProgress>? progress,
        IProgress<string>? diagnostics,
        CancellationToken cancellationToken)
    {
        var outcome = await RunEngineAsync(
            backend,
            languageSelection,
            engineHostPath,
            runtimeDirectory,
            modelPath,
            wavPath,
            resultPath,
            jobDirectory,
            runLabel,
            contextTokens,
            progress,
            diagnostics,
            cancellationToken).ConfigureAwait(false);

        EngineResultDto result;
        try
        {
            result = await ReadEngineResultAsync(resultPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException)
        {
            throw new EngineRunException($"识别结果无法读取：{exception.Message}", 0);
        }

        if (result.IsPartial)
        {
            throw new EngineRunException("模型报告结果被截断，不能当作完整输出（通常是上下文长度不足）。", 0);
        }

        if (result.Segments is null)
        {
            throw new EngineRunException("识别引擎没有返回 segments 字段。", 0);
        }

        return new MossChunkAttempt(outcome, result);
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
        string? runLabel,
        int? contextTokens,
        IProgress<OperationProgress>? progress,
        IProgress<string>? diagnostics,
        CancellationToken cancellationToken)
    {
        progress?.Report(new OperationProgress(
            JobState.Transcribing,
            backend == "vulkan"
                ? "正在本地识别语音并区分说话人…"
                : "正在使用 CPU 识别，速度会慢一些…"));

        var logPrefix = backend == "vulkan" ? "engine-vulkan" : "engine-cpu";
        if (!string.IsNullOrWhiteSpace(runLabel))
        {
            logPrefix += $"-{runLabel}";
        }
        string? lastProgressMessage = null;
        double? lastProgressFraction = null;
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
                    languageSelection,
                    contextTokens),
                StandardOutputLogPath = Path.Combine(jobDirectory, logPrefix + ".stdout.log"),
                StandardErrorLogPath = Path.Combine(jobDirectory, logPrefix + ".stderr.log"),
            },
            onStandardError: line =>
            {
                var engineEvent = TryParseEngineEvent(line);
                if (engineEvent is { Type: "native" } && !string.IsNullOrWhiteSpace(engineEvent.Message))
                {
                    var diagnosticLabel = string.IsNullOrWhiteSpace(runLabel)
                        ? string.Empty
                        : $"{runLabel} ";
                    diagnostics?.Report($"MOSS {diagnosticLabel}原生运行库：{engineEvent.Message.Trim()}");
                    return;
                }

                if (engineEvent is not { Type: "progress" } || string.IsNullOrWhiteSpace(engineEvent.Message))
                {
                    return;
                }

                if (string.Equals(lastProgressMessage, engineEvent.Message, StringComparison.Ordinal) &&
                    Nullable.Equals(lastProgressFraction, engineEvent.Fraction))
                {
                    return;
                }

                lastProgressMessage = engineEvent.Message;
                lastProgressFraction = engineEvent.Fraction;
                progress?.Report(new OperationProgress(
                    JobState.Transcribing,
                    engineEvent.Message.Trim(),
                    engineEvent.Fraction));
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

    private QwenRuntime? ResolveQwenRuntime(TranscriptionMode mode)
    {
        if (mode != TranscriptionMode.QwenLocalHighAccuracy)
        {
            return null;
        }

        var pythonPath = _runtimeLocator.FindQwenPython();
        var sidecarPath = _runtimeLocator.FindQwenSidecar();
        return new QwenRuntime(
            pythonPath,
            sidecarPath,
            _runtimeLocator.FindQwenAsrModelDirectory(),
            _runtimeLocator.FindQwenAlignerModelDirectory());
    }

    private WhisperRuntime? ResolveWhisperRuntime(TranscriptionMode mode)
    {
        if (mode != TranscriptionMode.WhisperTurboFast)
        {
            return null;
        }

        return new WhisperRuntime(
            _runtimeLocator.FindWhisperPython(),
            _runtimeLocator.FindWhisperSidecar(),
            _runtimeLocator.FindWhisperModelDirectory());
    }

    private async Task<EngineRunOutcome> RunQwenAsync(
        QwenRuntime runtime,
        LanguageSelection languageSelection,
        string wavPath,
        string resultPath,
        int chunkSeconds,
        string jobDirectory,
        IProgress<OperationProgress>? progress,
        IProgress<string>? diagnostics,
        CancellationToken cancellationToken)
    {
        progress?.Report(new OperationProgress(
            JobState.Transcribing,
            "正在用 Windows CUDA / 标准 PyTorch 运行本地 Qwen3-ASR 高精度识别…"));

        var environment = new Dictionary<string, string?>
        {
            ["PYTHONUTF8"] = "1",
            ["PYTHONIOENCODING"] = "utf-8",
        };
        // The local mode is intentionally strict: no silent network fallback
        // after installation, even if a Hugging Face token is available.
        environment["HF_HUB_OFFLINE"] = "1";
        environment["TRANSFORMERS_OFFLINE"] = "1";

        string? lastProgressMessage = null;
        double? lastProgressFraction = null;
        var result = await _processRunner.RunAsync(
            new ProcessSpec
            {
                FileName = runtime.PythonPath,
                WorkingDirectory = Path.GetDirectoryName(runtime.SidecarPath),
                Arguments = BuildQwenArguments(
                    runtime.SidecarPath,
                    runtime.AsrModelDirectory,
                    runtime.AlignerModelDirectory,
                    wavPath,
                    resultPath,
                    languageSelection,
                    chunkSeconds),
                EnvironmentVariables = environment,
                StandardOutputLogPath = Path.Combine(jobDirectory, "qwen.stdout.log"),
                StandardErrorLogPath = Path.Combine(jobDirectory, "qwen.stderr.log"),
            },
            onStandardError: line =>
            {
                var engineEvent = TryParseEngineEvent(line);
                if (engineEvent is not { Type: "progress" } || string.IsNullOrWhiteSpace(engineEvent.Message))
                {
                    return;
                }

                if (string.Equals(lastProgressMessage, engineEvent.Message, StringComparison.Ordinal) &&
                    Nullable.Equals(lastProgressFraction, engineEvent.Fraction))
                {
                    return;
                }

                lastProgressMessage = engineEvent.Message;
                lastProgressFraction = engineEvent.Fraction;
                progress?.Report(new OperationProgress(
                    JobState.Transcribing,
                    engineEvent.Message.Trim(),
                    engineEvent.Fraction));
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Qwen 高精度识别失败：{ExtractEngineError(result.StandardErrorTail)} " +
                $"诊断文件保存在：{jobDirectory}");
        }

        if (!File.Exists(resultPath) || new FileInfo(resultPath).Length == 0)
        {
            throw new InvalidOperationException(
                $"Qwen 识别进程已结束，但没有生成结果文件。诊断文件保存在：{jobDirectory}");
        }

        return new EngineRunOutcome(result.Elapsed);
    }

    private async Task<EngineRunOutcome> RunWhisperAsync(
        WhisperRuntime runtime,
        LanguageSelection languageSelection,
        string wavPath,
        string resultPath,
        GpuExecutionProfile gpuProfile,
        string jobDirectory,
        IProgress<OperationProgress>? progress,
        IProgress<string>? diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RunWhisperAttemptAsync(
                runtime,
                languageSelection,
                wavPath,
                resultPath,
                "cuda",
                "int8_float16",
                gpuProfile.WhisperBatchSize,
                jobDirectory,
                progress,
                diagnostics,
                cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException gpuException) when (!cancellationToken.IsCancellationRequested)
        {
            diagnostics?.Report(
                "Faster-Whisper Windows CUDA 未能完成；正在自动改用 CPU int8。" +
                $" 原因：{gpuException.Message}");
            TryDelete(resultPath);
            TryDelete(resultPath + ".tmp");
            progress?.Report(new OperationProgress(
                JobState.ReadyToTranscribe,
                "Whisper CUDA 不可用，正在改用 CPU；剩余时间将重新估算。",
                0));
            return await RunWhisperAttemptAsync(
                runtime,
                languageSelection,
                wavPath,
                resultPath,
                "cpu",
                "int8",
                1,
                jobDirectory,
                progress,
                diagnostics,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<EngineRunOutcome> RunWhisperAttemptAsync(
        WhisperRuntime runtime,
        LanguageSelection languageSelection,
        string wavPath,
        string resultPath,
        string device,
        string computeType,
        int batchSize,
        string jobDirectory,
        IProgress<OperationProgress>? progress,
        IProgress<string>? diagnostics,
        CancellationToken cancellationToken)
    {
        progress?.Report(new OperationProgress(
            JobState.Transcribing,
            device == "cuda"
                ? "正在用 Faster-Whisper / Windows CUDA 识别语音…"
                : "正在用 Faster-Whisper CPU 识别语音，速度会慢一些…",
            0));
        var environment = new Dictionary<string, string?>
        {
            ["PYTHONUTF8"] = "1",
            ["PYTHONIOENCODING"] = "utf-8",
            ["HF_HUB_OFFLINE"] = "1",
            ["HF_HUB_DISABLE_TELEMETRY"] = "1",
            ["DO_NOT_TRACK"] = "1",
        };
        string? lastProgressMessage = null;
        double? lastProgressFraction = null;
        var result = await _processRunner.RunAsync(
            new ProcessSpec
            {
                FileName = runtime.PythonPath,
                WorkingDirectory = Path.GetDirectoryName(runtime.SidecarPath),
                Arguments = BuildWhisperArguments(
                    runtime.SidecarPath,
                    runtime.ModelDirectory,
                    wavPath,
                    resultPath,
                    languageSelection,
                    device,
                    computeType,
                    batchSize),
                EnvironmentVariables = environment,
                StandardOutputLogPath = Path.Combine(jobDirectory, $"whisper-{device}.stdout.log"),
                StandardErrorLogPath = Path.Combine(jobDirectory, $"whisper-{device}.stderr.log"),
            },
            onStandardError: line =>
            {
                var engineEvent = TryParseEngineEvent(line);
                if (engineEvent is not { Type: "progress" } || string.IsNullOrWhiteSpace(engineEvent.Message))
                {
                    return;
                }
                if (string.Equals(lastProgressMessage, engineEvent.Message, StringComparison.Ordinal) &&
                    Nullable.Equals(lastProgressFraction, engineEvent.Fraction))
                {
                    return;
                }
                lastProgressMessage = engineEvent.Message;
                lastProgressFraction = engineEvent.Fraction;
                progress?.Report(new OperationProgress(
                    JobState.Transcribing,
                    engineEvent.Message.Trim(),
                    engineEvent.Fraction));
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Faster-Whisper {device} 推理失败：{ExtractEngineError(result.StandardErrorTail)} " +
                $"诊断文件保存在：{jobDirectory}");
        }
        if (!File.Exists(resultPath) || new FileInfo(resultPath).Length == 0)
        {
            throw new InvalidOperationException(
                $"Faster-Whisper {device} 已结束但没有生成结果文件。诊断文件保存在：{jobDirectory}");
        }
        return new EngineRunOutcome(result.Elapsed);
    }

    internal static IReadOnlyList<string> BuildWhisperArguments(
        string sidecarPath,
        string modelDirectory,
        string wavPath,
        string resultPath,
        LanguageSelection languageSelection,
        string device,
        string computeType,
        int batchSize)
    {
        ArgumentNullException.ThrowIfNull(languageSelection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sidecarPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        if (device is not ("cuda" or "cpu"))
        {
            throw new ArgumentOutOfRangeException(nameof(device));
        }
        if ((device == "cuda" && computeType != "int8_float16") ||
            (device == "cpu" && computeType != "int8"))
        {
            throw new ArgumentException("Whisper 后端与计算类型不匹配。", nameof(computeType));
        }
        if (batchSize is < 1 or > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }
        return
        [
            "-X", "utf8",
            "-I", sidecarPath,
            "--audio", wavPath,
            "--output", resultPath,
            "--model-dir", modelDirectory,
            "--language", languageSelection.EngineArgument,
            "--device", device,
            "--compute-type", computeType,
            "--batch-size", batchSize.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ];
    }

    internal static IReadOnlyList<string> BuildQwenArguments(
        string sidecarPath,
        string asrModelDirectory,
        string alignerModelDirectory,
        string wavPath,
        string resultPath,
        LanguageSelection languageSelection,
        int chunkSeconds)
    {
        ArgumentNullException.ThrowIfNull(languageSelection);
        ArgumentException.ThrowIfNullOrWhiteSpace(sidecarPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(asrModelDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(alignerModelDirectory);
        if (chunkSeconds is < 30 or > 180)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkSeconds), "Qwen 音频分块必须在 30–180 秒之间。");
        }

        var arguments = new List<string>
        {
            "-X", "utf8",
            "-I",
            sidecarPath,
            "--mode", "local",
            "--audio", wavPath,
            "--output", resultPath,
            "--language", languageSelection.EngineArgument,
            "--model-dir", asrModelDirectory,
            "--aligner-dir", alignerModelDirectory,
            "--device", "auto",
            "--chunk-seconds", chunkSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        return arguments;
    }

    private static EngineResultDto MergeRecognitionWithOptionalMoss(
        EngineResultDto recognitionResult,
        EngineResultDto? mossResult,
        string speakerEnabledWarning,
        string speakerDisabledWarning)
    {
        if (recognitionResult.Segments is null)
        {
            throw new InvalidDataException("识别结果没有 segments 字段。");
        }

        var warnings = new List<string>();
        if (recognitionResult.Warnings is not null)
        {
            warnings.AddRange(recognitionResult.Warnings);
        }

        if (mossResult is null)
        {
            warnings.Add(speakerDisabledWarning);
            return recognitionResult with
            {
                Warnings = warnings.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal).ToArray(),
            };
        }

        if (mossResult.Segments is null)
        {
            throw new InvalidDataException("MOSS 说话人结果没有 segments 字段。");
        }
        if (mossResult.Warnings is not null)
        {
            warnings.AddRange(mossResult.Warnings);
        }
        warnings.Add(speakerEnabledWarning);
        var mergedSegments = TimelineSpeakerMerger.Merge(recognitionResult.Segments, mossResult.Segments)
            .Select(segment => new EngineSegmentDto(segment.StartMs, segment.EndMs, segment.SpeakerId, segment.Text))
            .ToArray();
        return new EngineResultDto(
            recognitionResult.SourceFileName,
            recognitionResult.DurationMs > 0 ? recognitionResult.DurationMs : mossResult.DurationMs,
            recognitionResult.Model,
            $"{recognitionResult.EngineVersion} + MOSS {mossResult.EngineVersion}",
            $"{recognitionResult.Backend} + MOSS {mossResult.Backend}",
            recognitionResult.Language,
            recognitionResult.FullText,
            recognitionResult.RawText,
            mergedSegments,
            warnings.Where(item => !string.IsNullOrWhiteSpace(item)).Distinct(StringComparer.Ordinal).ToArray(),
            recognitionResult.IsPartial || mossResult.IsPartial);
    }

    private static (string Name, string Revision) GetHighAccuracyModelIdentity() =>
        (QwenLocalDisplayName, $"ASR {QwenModelManifest.AsrRevision}; Aligner {QwenModelManifest.AlignerRevision}");

    internal static IReadOnlyList<string> BuildEngineArguments(
        string runtimeDirectory,
        string modelPath,
        string wavPath,
        string resultPath,
        string backend,
        LanguageSelection languageSelection,
        int? contextTokens = null)
    {
        ArgumentNullException.ThrowIfNull(languageSelection);

        var arguments = new List<string>
        {
            "--runtime", runtimeDirectory,
            "--model", modelPath,
            "--audio", wavPath,
            "--output", resultPath,
            "--backend", backend,
            "--language", languageSelection.MossEngineArgument,
        };

        if (contextTokens is int value)
        {
            if (value is < 1024 or > 131072)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(contextTokens),
                    "上下文 token 上限必须在 1024 到 131072 之间。");
            }

            arguments.Add("--context-tokens");
            arguments.Add(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return arguments;
    }

    internal static int? GetVulkanContextTokenCap(TimeSpan mediaDuration) =>
        GetVulkanContextTokenCap(
            mediaDuration,
            GpuExecutionProfile.FromMemory("默认保守配置", 6_144, isDetected: false));

    internal static int? GetVulkanContextTokenCap(
        TimeSpan mediaDuration,
        GpuExecutionProfile gpuProfile)
    {
        ArgumentNullException.ThrowIfNull(gpuProfile);
        var maximumChunkDuration = MossChunkPlanner.GetGpuSafeMaximumChunkDuration(
            gpuProfile.MossContextTokens);
        return mediaDuration > TimeSpan.Zero && mediaDuration <= maximumChunkDuration
            ? gpuProfile.MossContextTokens
            : null;
    }

    private static TimeSpan ReduceMossGpuChunkDuration(TimeSpan maximumChunkDuration)
    {
        var reducedSeconds = Math.Floor(maximumChunkDuration.TotalSeconds / 2d);
        return TimeSpan.FromSeconds(Math.Max(
            MinimumMossGpuChunkDuration.TotalSeconds,
            reducedSeconds));
    }

    private static bool IsMossGpuChunkSizeFailure(EngineRunException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = exception.CleanMessage;
        return message.Contains("input audio too long", StringComparison.OrdinalIgnoreCase) ||
            (message.Contains("prompt", StringComparison.OrdinalIgnoreCase) &&
             message.Contains("context", StringComparison.OrdinalIgnoreCase) &&
             message.Contains("exceed", StringComparison.OrdinalIgnoreCase)) ||
            (message.Contains("context", StringComparison.OrdinalIgnoreCase) &&
             message.Contains("too long", StringComparison.OrdinalIgnoreCase)) ||
            message.Contains("模型报告结果被截断", StringComparison.Ordinal);
    }

    internal static TimeSpan GetEffectiveMediaDuration(
        TimeSpan probedMediaDuration,
        TimeSpan extractedAudioDuration)
    {
        if (probedMediaDuration <= TimeSpan.Zero || extractedAudioDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(extractedAudioDuration),
                "媒体和提取音轨时长必须大于 0。");
        }

        var effectiveDuration = probedMediaDuration >= extractedAudioDuration
            ? probedMediaDuration
            : extractedAudioDuration;
        if (effectiveDuration > MaximumMediaDuration)
        {
            throw new NotSupportedException(
                $"当前版本一次支持最长 {MaximumMediaDuration.TotalHours:0} 小时的录屏。" +
                $"实际可识别音轨与媒体的最长时长为 {FormatDuration(effectiveDuration)}。");
        }

        return effectiveDuration;
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

    private static async Task WriteEngineResultAsync(
        string resultPath,
        EngineResultDto result,
        CancellationToken cancellationToken)
    {
        var temporaryPath = resultPath + ".tmp";
        TryDelete(temporaryPath);
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    result,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, resultPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private static TranscriptDocument CreateValidatedDocument(
        EngineResultDto result,
        string sourcePath,
        TimeSpan mediaDuration,
        string modelName,
        string modelRevision)
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
            ModelName = modelName,
            ModelRevision = modelRevision,
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
                    // The application is single-instance. Anything left before a
                    // new job starts belongs to a cancelled or crashed older run.
                    CleanupJobAudioArtifacts(directory);
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

    internal static void CleanupJobAudioArtifacts(string jobDirectory)
    {
        TryDelete(Path.Combine(jobDirectory, "audio.wav"));
        TryDelete(Path.Combine(jobDirectory, "audio.wav.tmp"));
        if (!Directory.Exists(jobDirectory))
        {
            return;
        }

        try
        {
            foreach (var pattern in new[] { ".qwen-audio-*", ".moss-audio-*" })
            {
                foreach (var directory in Directory.EnumerateDirectories(
                             jobDirectory,
                             pattern,
                             SearchOption.TopDirectoryOnly))
                {
                    TryDeleteDirectory(directory);
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

    private static void AddLog(
        ICollection<string> messages,
        string message,
        IProgress<string>? diagnostics)
    {
        messages.Add(message);
        diagnostics?.Report(message);
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
    private sealed record MossChunkAttempt(EngineRunOutcome Outcome, EngineResultDto Result);
    private sealed record QwenRuntime(
        string PythonPath,
        string SidecarPath,
        string AsrModelDirectory,
        string AlignerModelDirectory);
    private sealed record WhisperRuntime(
        string PythonPath,
        string SidecarPath,
        string ModelDirectory);

    private sealed class EngineRunException(string message, int exitCode) : Exception(message)
    {
        public int ExitCode { get; } = exitCode;
        public string CleanMessage { get; } = message;
    }

    private sealed class MossGpuChunkSizeException(string message, Exception innerException)
        : Exception(message, innerException);

    private sealed class ChunkOperationProgress(
        IProgress<OperationProgress>? target,
        int chunkIndex,
        int chunkCount,
        string messagePrefix,
        double completionFraction) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var localFraction = value.Fraction is double fraction && double.IsFinite(fraction)
                ? Math.Clamp(fraction, 0, 1)
                : 0;
            var overallFraction = ((chunkIndex + localFraction) / chunkCount) * completionFraction;
            target?.Report(value with
            {
                Message = messagePrefix + value.Message,
                Fraction = overallFraction,
            });
        }
    }
}
