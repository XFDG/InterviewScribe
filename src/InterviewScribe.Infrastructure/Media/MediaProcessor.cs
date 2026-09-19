using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterviewScribe.Core.Domain;
using InterviewScribe.Infrastructure.Processes;

namespace InterviewScribe.Infrastructure.Media;

public sealed class MediaProcessor(ManagedProcessRunner processRunner)
{
    public async Task<MediaInfo> ProbeAsync(
        string ffprobePath,
        string sourcePath,
        string logDirectory,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            new ProcessSpec
            {
                FileName = ffprobePath,
                Arguments =
                [
                    "-v", "error",
                    "-select_streams", "a",
                    "-show_entries", "format=duration:stream=index,codec_name,sample_rate,channels,duration,duration_ts,time_base",
                    "-of", "json",
                    sourcePath,
                ],
                StandardErrorLogPath = Path.Combine(logDirectory, "ffprobe.stderr.log"),
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"无法读取视频音轨。{FormatProcessError(result.StandardErrorTail)}");
        }

        var json = string.Join(Environment.NewLine, result.StandardOutputLines);
        return ParseProbeResponse(json);
    }

    internal static MediaInfo ParseProbeResponse(string json)
    {
        FfprobeResult? probe;
        try
        {
            probe = JsonSerializer.Deserialize<FfprobeResult>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("ffprobe 返回了无法解析的媒体信息。", exception);
        }

        if (probe?.Streams is not { Count: > 0 })
        {
            throw new InvalidDataException("这个文件中没有检测到可用的音轨。");
        }

        var durationSeconds = ParsePositiveFiniteSeconds(probe.Format?.Duration)
            ?? probe.Streams
                .Select(GetStreamDurationSeconds)
                .Where(value => value.HasValue)
                .Select(value => value!.Value)
                .DefaultIfEmpty()
                .Max();
        if (!double.IsFinite(durationSeconds) || durationSeconds <= 0)
        {
            throw new InvalidDataException("无法确定音视频时长。");
        }

        var first = probe.Streams[0];
        _ = int.TryParse(first.SampleRate, NumberStyles.Integer, CultureInfo.InvariantCulture, out var sampleRate);
        return new MediaInfo(
            TimeSpan.FromSeconds(durationSeconds),
            probe.Streams.Count,
            0,
            sampleRate,
            first.Channels,
            first.CodecName ?? "unknown");
    }

    private static double? GetStreamDurationSeconds(FfprobeStream stream)
    {
        var directDuration = ParsePositiveFiniteSeconds(stream.Duration);
        if (directDuration.HasValue)
        {
            return directDuration;
        }

        if (!TryGetDurationTimestamp(stream.DurationTs, out var durationTs) ||
            durationTs <= 0 ||
            string.IsNullOrWhiteSpace(stream.TimeBase))
        {
            return null;
        }

        var parts = stream.TimeBase.Split('/', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator) ||
            !double.IsFinite(numerator) ||
            !double.IsFinite(denominator) ||
            numerator <= 0 ||
            denominator <= 0)
        {
            return null;
        }

        var seconds = durationTs * numerator / denominator;
        return double.IsFinite(seconds) && seconds > 0 ? seconds : null;
    }

    private static bool TryGetDurationTimestamp(JsonElement value, out long durationTs)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.TryGetInt64(out durationTs);
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return long.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out durationTs);
        }

        durationTs = 0;
        return false;
    }

    private static double? ParsePositiveFiniteSeconds(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
        double.IsFinite(seconds) &&
        seconds > 0
            ? seconds
            : null;

    public async Task ExtractMonoPcmAsync(
        string ffmpegPath,
        string sourcePath,
        string destinationPath,
        TimeSpan duration,
        int audioStreamCount,
        string logDirectory,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            new ProcessSpec
            {
                FileName = ffmpegPath,
                Arguments = BuildExtractionArguments(
                    sourcePath,
                    destinationPath,
                    audioStreamCount),
                StandardOutputLogPath = Path.Combine(logDirectory, "ffmpeg.progress.log"),
                StandardErrorLogPath = Path.Combine(logDirectory, "ffmpeg.stderr.log"),
            },
            line => ReportFfmpegProgress(line, duration, progress),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidDataException(
                $"提取音轨失败。{FormatProcessError(result.StandardErrorTail)}");
        }

        if (!File.Exists(destinationPath) || new FileInfo(destinationPath).Length <= 44)
        {
            throw new InvalidDataException("提取后的音频为空，未继续进行识别。");
        }
    }

    internal static IReadOnlyList<string> BuildExtractionArguments(
        string sourcePath,
        string destinationPath,
        int audioStreamCount)
    {
        if (audioStreamCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(audioStreamCount),
                "至少需要一条音轨才能提取音频。");
        }

        var arguments = new List<string>
        {
            "-nostdin",
            "-hide_banner",
            "-loglevel", "error",
            "-y",
            "-i", sourcePath,
        };

        if (audioStreamCount == 1)
        {
            arguments.AddRange(["-map", "0:a:0"]);
        }
        else
        {
            // Screen recorders can store the microphone and system sound in separate
            // audio streams.  Mixing every detected stream prevents silently dropping
            // one side of an interview.  amix normalizes the sum to avoid clipping.
            var inputs = string.Concat(
                Enumerable.Range(0, audioStreamCount).Select(index => $"[0:a:{index}]"));
            var filter =
                $"{inputs}amix=inputs={audioStreamCount}:duration=longest:" +
                "dropout_transition=0:normalize=1[aout]";
            arguments.AddRange(["-filter_complex", filter, "-map", "[aout]"]);
        }

        arguments.AddRange(
        [
            "-vn",
            "-sn",
            "-dn",
            "-ac", "1",
            "-ar", "16000",
            "-c:a", "pcm_s16le",
            "-progress", "pipe:1",
            destinationPath,
        ]);
        return arguments;
    }

    private static void ReportFfmpegProgress(
        string line,
        TimeSpan duration,
        IProgress<OperationProgress>? progress)
    {
        if (!line.StartsWith("out_time_us=", StringComparison.Ordinal))
        {
            return;
        }

        var raw = line["out_time_us=".Length..];
        if (!long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var microseconds))
        {
            return;
        }

        var fraction = duration.TotalMilliseconds <= 0
            ? (double?)null
            : Math.Clamp(microseconds / 1000d / duration.TotalMilliseconds, 0, 1);
        progress?.Report(new OperationProgress(
            JobState.ExtractingAudio,
            "正在从视频提取 16 kHz 单声道音频…",
            fraction));
    }

    private static string FormatProcessError(string error)
    {
        var trimmed = error.Trim();
        return trimmed.Length == 0 ? string.Empty : $" 详细信息：{trimmed}";
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed record FfprobeResult(IReadOnlyList<FfprobeStream> Streams, FfprobeFormat? Format);
    private sealed record FfprobeStream(
        int Index,
        [property: JsonPropertyName("codec_name")] string? CodecName,
        [property: JsonPropertyName("sample_rate")] string? SampleRate,
        int Channels,
        string? Duration,
        [property: JsonPropertyName("duration_ts")] JsonElement DurationTs,
        [property: JsonPropertyName("time_base")] string? TimeBase);
    private sealed record FfprobeFormat(string? Duration);
}
