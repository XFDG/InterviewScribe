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

            Check(
                NativeMethods.TranscribeOpen(options.ModelPath, ref loadParams, IntPtr.Zero, out var session),
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

                var status = NativeMethods.TranscribeRun(session, audio.Samples, audio.Samples.Length, ref runParams);
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
    NativeMethods.BackendRequest Backend)
{
    public static Arguments Parse(string[] args)
    {
        if (args.Length == 1 && args[0] is "--help" or "-h")
        {
            throw new EngineException(
                "用法：InterviewScribe.EngineHost --runtime <dir> --model <gguf> --audio <wav> --output <json> [--backend auto|cuda|vulkan|cpu]");
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

        return new Arguments(
            Path.GetFullPath(runtime),
            Path.GetFullPath(model),
            Path.GetFullPath(audio),
            Path.GetFullPath(output),
            backend);
    }

    private static string Required(IReadOnlyDictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new EngineException($"缺少参数 {name}。");
}
