using System.Collections.ObjectModel;
using System.Text;
using InterviewScribe.Core.Domain;
using InterviewScribe.Core.Export;

namespace InterviewScribe.Infrastructure.Pipeline;

internal static class AtomicTranscriptExporter
{
    private static readonly (TranscriptOutputFormat Format, string Extension)[] SupportedFormats =
    [
        (TranscriptOutputFormat.Txt, ".txt"),
        (TranscriptOutputFormat.Markdown, ".md"),
        (TranscriptOutputFormat.Docx, ".docx"),
        (TranscriptOutputFormat.Pdf, ".pdf"),
        (TranscriptOutputFormat.Srt, ".srt"),
        (TranscriptOutputFormat.Json, ".json"),
    ];

    public static async Task<(string TxtPath, string SrtPath, string JsonPath)> ExportAsync(
        TranscriptDocument document,
        string outputDirectory,
        CancellationToken cancellationToken,
        TranscriptFormattingOptions? formattingOptions = null)
    {
        var result = await ExportAsync(
            document,
            outputDirectory,
            TranscriptOutputFormat.Default,
            cancellationToken,
            formattingOptions).ConfigureAwait(false);
        return (
            result.GetRequiredPath(TranscriptOutputFormat.Txt),
            result.GetRequiredPath(TranscriptOutputFormat.Srt),
            result.GetRequiredPath(TranscriptOutputFormat.Json));
    }

    public static async Task<TranscriptExportResult> ExportAsync(
        TranscriptDocument document,
        string outputDirectory,
        TranscriptOutputFormat formats,
        CancellationToken cancellationToken,
        TranscriptFormattingOptions? formattingOptions = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ValidateFormats(formats);
        formattingOptions ??= TranscriptFormattingOptions.Default;
        Directory.CreateDirectory(outputDirectory);
        VerifyWritable(outputDirectory);

        var safeBaseName = MakeSafeFileName(Path.GetFileNameWithoutExtension(document.SourceFileName));
        var stem = GetAvailableStem(outputDirectory, $"{safeBaseName}_转写");
        var finalPaths = SupportedFormats
            .Where(item => formats.HasFlag(item.Format))
            .ToDictionary(item => item.Format, item => Path.Combine(outputDirectory, stem + item.Extension));

        var pending = new List<(string Temporary, string Final)>();
        var committed = new List<string>();
        try
        {
            foreach (var (format, _) in SupportedFormats.Where(item => formats.HasFlag(item.Format)))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var finalPath = finalPaths[format];
                var temporaryPath = CreateTemporaryPath(finalPath);
                pending.Add((temporaryPath, finalPath));
                await WriteTemporaryAsync(
                    format,
                    temporaryPath,
                    document,
                    formattingOptions,
                    cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in pending)
            {
                File.Move(item.Temporary, item.Final, false);
                committed.Add(item.Final);
            }

            return new TranscriptExportResult
            {
                Stem = stem,
                Paths = new ReadOnlyDictionary<TranscriptOutputFormat, string>(finalPaths),
            };
        }
        catch
        {
            foreach (var item in pending)
            {
                TryDelete(item.Temporary);
            }

            // Only roll back files created by this export. If another process won
            // the race for one of the names, its pre-existing file must be kept.
            foreach (var path in committed)
            {
                TryDelete(path);
            }

            throw;
        }
    }

    private static async Task WriteTemporaryAsync(
        TranscriptOutputFormat format,
        string temporaryPath,
        TranscriptDocument document,
        TranscriptFormattingOptions formattingOptions,
        CancellationToken cancellationToken)
    {
        switch (format)
        {
            case TranscriptOutputFormat.Txt:
                await WriteTextAsync(
                    temporaryPath,
                    TranscriptFormatter.ToTxt(document, options: formattingOptions),
                    cancellationToken).ConfigureAwait(false);
                break;
            case TranscriptOutputFormat.Markdown:
                await WriteTextAsync(
                    temporaryPath,
                    TranscriptFormatter.ToMarkdown(document, options: formattingOptions),
                    cancellationToken).ConfigureAwait(false);
                break;
            case TranscriptOutputFormat.Docx:
                await WordTranscriptWriter.WriteAsync(temporaryPath, document, formattingOptions, cancellationToken).ConfigureAwait(false);
                break;
            case TranscriptOutputFormat.Pdf:
                await PdfTranscriptWriter.WriteAsync(temporaryPath, document, formattingOptions, cancellationToken).ConfigureAwait(false);
                break;
            case TranscriptOutputFormat.Srt:
                await WriteTextAsync(
                    temporaryPath,
                    TranscriptFormatter.ToSrt(document, options: formattingOptions),
                    cancellationToken).ConfigureAwait(false);
                break;
            case TranscriptOutputFormat.Json:
                await WriteTextAsync(
                    temporaryPath,
                    TranscriptFormatter.ToJson(document) + Environment.NewLine,
                    cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, "不支持的导出格式。");
        }
    }

    private static async Task WriteTextAsync(string path, string content, CancellationToken cancellationToken) =>
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

    private static string CreateTemporaryPath(string finalPath) =>
        finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

    private static void ValidateFormats(TranscriptOutputFormat formats)
    {
        if (formats == TranscriptOutputFormat.None)
        {
            throw new ArgumentException("请至少选择一种输出格式。", nameof(formats));
        }

        if ((formats & ~TranscriptOutputFormat.All) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(formats), formats, "包含不支持的输出格式。");
        }
    }

    private static void VerifyWritable(string outputDirectory)
    {
        var probePath = Path.Combine(outputDirectory, $".interviewscribe-write-{Guid.NewGuid():N}.tmp");
        try
        {
            using var stream = new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
            stream.WriteByte(0);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new UnauthorizedAccessException($"无法写入输出文件夹：{outputDirectory}", exception);
        }
        finally
        {
            TryDelete(probePath);
        }
    }

    private static string GetAvailableStem(string directory, string baseStem)
    {
        var candidate = baseStem;
        for (var index = 2; index < 10_000; index++)
        {
            if (!HasAnyOutput(directory, candidate))
            {
                return candidate;
            }

            candidate = $"{baseStem}_{index}";
        }

        return $"{baseStem}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}";
    }

    private static bool HasAnyOutput(string directory, string stem) =>
        SupportedFormats.Any(item => File.Exists(Path.Combine(directory, stem + item.Extension)));

    private static string MakeSafeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Trim())
        {
            builder.Append(invalid.Contains(character) ? '_' : character);
        }

        var result = builder.ToString().Trim().TrimEnd('.');
        return result.Length == 0 ? "转写结果" : result;
    }

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
}
