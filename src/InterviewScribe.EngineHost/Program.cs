using System.Runtime.InteropServices;
using System.Text.Json;

namespace InterviewScribe.EngineHost;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static int Main(string[] args)
    {
        try
        {
            var options = Arguments.Parse(args);
            Emit("progress", "正在读取音频", 0.02);
            var audio = WavReader.ReadMono16Khz(options.AudioPath);

            NativeMethods.Configure(options.RuntimeDirectory);
            NativeMethods.InstallLogCallback();
            Check(NativeMethods.TranscribeInitBackends(options.RuntimeDirectory), "初始化推理后端");
            NativeMethods.VerifyAbi();

            Emit("progress", "正在加载 MOSS 模型", 0.05);
            var loadParams = default(NativeMethods.ModelLoadParams);
            NativeMethods.TranscribeModelLoadParamsInit(ref loadParams);
            loadParams.Backend = options.Backend;

            var sessionParams = default(NativeMethods.SessionParams);
            NativeMethods.TranscribeSessionParamsInit(ref sessionParams);
            if (options.ContextTokens is int contextTokens)
            {
                sessionParams.ContextTokens = contextTokens;
                EmitDiagnostic(
                    $"已启用 {contextTokens:N0}-token 显存保护；若无法完整识别，主程序会自动改用 CPU 完整重试。");
            }

            Check(
                NativeMethods.TranscribeOpen(options.ModelPath, ref loadParams, ref sessionParams, out var session),
                "加载模型");

            try
            {
                var model = NativeMethods.TranscribeGetModel(session);
                var backend = NativeMethods.Utf8(NativeMethods.TranscribeModelBackend(model));
                var engineVersion = NativeMethods.Utf8(NativeMethods.TranscribeVersion());
                Emit("progress", $"模型已加载，正在使用 {backend} 识别", 0.08);

                var runParams = default(NativeMethods.RunParams);
                NativeMethods.TranscribeRunParamsInit(ref runParams);
                runParams.Task = NativeMethods.TranscribeTask.Transcribe;
                runParams.Timestamps = NativeMethods.TimestampKind.Segment;
                runParams.Diarize = NativeMethods.DiarizeMode.On;

                var languagePointer = IntPtr.Zero;
                NativeMethods.Status status;
                try
                {
                    if (options.Language is not null)
                    {
                        languagePointer = Marshal.StringToCoTaskMemUTF8(options.Language);
                        runParams.Language = languagePointer;
                    }

                    status = NativeMethods.TranscribeRun(
                        session,
                        audio.Samples,
                        audio.Samples.Length,
                        ref runParams);
                }
                finally
                {
                    if (languagePointer != IntPtr.Zero)
                    {
                        Marshal.FreeCoTaskMem(languagePointer);
                    }
                }

                if (status != NativeMethods.Status.Ok)
                {
                    throw new EngineException(
                        $"语音识别失败：{NativeMethods.StatusText(status)}",
                        (int)status);
                }

                Emit("progress", "正在整理说话人和时间轴", 0.96);
                var result = ReadResult(
                    session,
                    options.AudioPath,
                    audio.DurationMs,
                    backend,
                    engineVersion);

                var outputDirectory = Path.GetDirectoryName(options.OutputPath);
                if (!string.IsNullOrEmpty(outputDirectory))
                {
                    Directory.CreateDirectory(outputDirectory);
                }

                var temporaryPath = options.OutputPath + ".tmp";
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(result, JsonOptions));
                File.Move(temporaryPath, options.OutputPath, true);
                Emit("progress", "识别完成", 1.0);
                return 0;
            }
            finally
            {
                NativeMethods.TranscribeSessionFree(session);
            }
        }
        catch (Exception ex)
        {
            var code = ex is EngineException engineException ? engineException.NativeStatus : null;
            Console.Error.WriteLine(JsonSerializer.Serialize(new
            {
                type = "error",
                message = ex.Message,
                nativeStatus = code,
            }));
            return 1;
        }
    }

    private static EngineResult ReadResult(
        IntPtr session,
        string audioPath,
        long durationMs,
        string backend,
        string engineVersion)
    {
        var segments = new List<EngineSegment>();
        var count = NativeMethods.TranscribeSegmentCount(session);
        for (var index = 0; index < count; index++)
        {
            var segment = default(NativeMethods.Segment);
            NativeMethods.TranscribeSegmentInit(ref segment);
            Check(NativeMethods.TranscribeGetSegment(session, index, ref segment), $"读取第 {index + 1} 个文本片段");
            var text = NativeMethods.Utf8(segment.Text).Trim();
            if (text.Length == 0)
            {
                continue;
            }

            segments.Add(new EngineSegment(
                Math.Max(0, segment.StartMs),
                Math.Max(segment.StartMs, segment.EndMs),
                segment.SpeakerId,
                text));
        }

        var warnings = new List<string>();
        if (segments.Count == 0)
        {
            warnings.Add("模型未返回带时间轴的文本片段。");
        }
        else if (segments.All(segment => segment.SpeakerId == 0))
        {
            warnings.Add("本次结果未能区分说话人。");
        }

        return new EngineResult(
            Path.GetFileName(audioPath),
            durationMs,
            "MOSS-Transcribe-Diarize-Q8_0",
            engineVersion,
            backend,
            NativeMethods.Utf8(NativeMethods.TranscribeDetectedLanguage(session)),
            NativeMethods.Utf8(NativeMethods.TranscribeFullText(session)),
            NativeMethods.Utf8(NativeMethods.TranscribeRawText(session)),
            segments,
            warnings,
            NativeMethods.TranscribeWasTruncated(session));
    }

    private static void Check(NativeMethods.Status status, string operation)
    {
        if (status != NativeMethods.Status.Ok)
        {
            throw new EngineException($"{operation}失败：{NativeMethods.StatusText(status)}", (int)status);
        }
    }

    private static void Emit(string type, string message, double fraction)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { type, message, fraction }));
    }

    private static void EmitDiagnostic(string message)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { type = "native", message }));
    }
}

internal sealed class EngineException(string message, int? nativeStatus = null) : Exception(message)
{
    public int? NativeStatus { get; } = nativeStatus;
}

internal sealed record EngineResult(
    string SourceFileName,
    long DurationMs,
    string Model,
    string EngineVersion,
    string Backend,
    string Language,
    string FullText,
    string RawText,
    IReadOnlyList<EngineSegment> Segments,
    IReadOnlyList<string> Warnings,
    bool IsPartial);

internal sealed record EngineSegment(long StartMs, long EndMs, int SpeakerId, string Text);

internal sealed record Arguments(
    string RuntimeDirectory,
    string ModelPath,
    string AudioPath,
    string OutputPath,
    NativeMethods.BackendRequest Backend,
    string? Language,
    int? ContextTokens)
{
    public static Arguments Parse(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            throw new EngineException(
                "用法：InterviewScribe.EngineHost --runtime <dir> --model <gguf> --audio <wav> --output <json> " +
                "[--backend auto|cuda|vulkan|cpu] [--language auto|zh|en] [--context-tokens 32768]");
        }

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length || !args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new EngineException("启动参数不完整。");
            }

            values[args[index]] = args[index + 1];
        }

        var runtime = Required(values, "--runtime");
        var model = Required(values, "--model");
        var audio = Required(values, "--audio");
        var output = Required(values, "--output");
        if (!Directory.Exists(runtime))
        {
            throw new EngineException($"推理库目录不存在：{runtime}");
        }

        if (!File.Exists(model))
        {
            throw new EngineException($"模型文件不存在：{model}");
        }

        if (!File.Exists(audio))
        {
            throw new EngineException($"音频文件不存在：{audio}");
        }

        var backendName = values.GetValueOrDefault("--backend", "auto");
        var backend = backendName.ToLowerInvariant() switch
        {
            "auto" => NativeMethods.BackendRequest.Auto,
            "cuda" => NativeMethods.BackendRequest.Cuda,
            "vulkan" => NativeMethods.BackendRequest.Vulkan,
            "cpu" => NativeMethods.BackendRequest.Cpu,
            _ => throw new EngineException($"不支持的推理后端：{backendName}"),
        };
        var language = ResolveNativeLanguage(values.GetValueOrDefault("--language", "auto"));
        var contextTokens = ResolveContextTokens(values.GetValueOrDefault("--context-tokens"));

        return new Arguments(
            Path.GetFullPath(runtime),
            Path.GetFullPath(model),
            Path.GetFullPath(audio),
            Path.GetFullPath(output),
            backend,
            language,
            contextTokens);
    }

    internal static string? ResolveNativeLanguage(string languageName) =>
        languageName switch
        {
            "auto" => null,
            "zh" => "zh",
            "en" => "en",
            _ => throw new EngineException(
                $"不支持的识别语言：{languageName}。请使用 auto、zh 或 en。"),
        };

    internal static int? ResolveContextTokens(string? value)
    {
        if (value is null)
        {
            return null;
        }

        if (!int.TryParse(value, out var contextTokens) || contextTokens is < 1024 or > 131072)
        {
            throw new EngineException("上下文 token 上限必须是 1024 到 131072 之间的整数。");
        }

        return contextTokens;
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new EngineException($"缺少参数 {name}。");
}
