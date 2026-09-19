using System.Text;
using InterviewScribe.Core.Domain;
using InterviewScribe.Core.Export;

namespace InterviewScribe.Infrastructure.Pipeline;

internal static class AtomicTranscriptExporter
{
    public static async Task<(string TxtPath, string SrtPath, string JsonPath)> ExportAsync(
        TranscriptDocument document,
        string outputDirectory,
        CancellationToken cancellationToken,
        TranscriptFormattingOptions? formattingOptions = null)
    {
        formattingOptions ??= TranscriptFormattingOptions.Default;
        Directory.CreateDirectory(outputDirectory);
        VerifyWritable(outputDirectory);

        var safeBaseName = MakeSafeFileName(Path.GetFileNameWithoutExtension(document.SourceFileName));
        var stem = GetAvailableStem(outputDirectory, $"{safeBaseName}_转写");
        var txtPath = Path.Combine(outputDirectory, stem + ".txt");
        var srtPath = Path.Combine(outputDirectory, stem + ".srt");
        var jsonPath = Path.Combine(outputDirectory, stem + ".json");

        var pending = new List<(string Temporary, string Final)>();
        var committed = new List<string>();
        try
        {
            pending.Add(await WriteTemporaryAsync(
                txtPath,
                TranscriptFormatter.ToTxt(document, options: formattingOptions),
                cancellationToken).ConfigureAwait(false));
            pending.Add(await WriteTemporaryAsync(
                srtPath,
                TranscriptFormatter.ToSrt(document, options: formattingOptions),
                cancellationToken).ConfigureAwait(false));
            pending.Add(await WriteTemporaryAsync(jsonPath, TranscriptFormatter.ToJson(document) + Environment.NewLine, cancellationToken).ConfigureAwait(false));

            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in pending)
            {
                File.Move(item.Temporary, item.Final, false);
                committed.Add(item.Final);
            }

            return (txtPath, srtPath, jsonPath);
        }
        catch
        {
            foreach (var item in pending)
            {
                TryDelete(item.Temporary);
            }

            // Only roll back files created by this export.  If another process won
            // the race for one of the names, its pre-existing file must be kept.
            foreach (var path in committed)
            {
                TryDelete(path);
            }

            throw;
        }
    }

    private static async Task<(string Temporary, string Final)> WriteTemporaryAsync(
        string finalPath,
        string content,
        CancellationToken cancellationToken)
    {
        var temporaryPath = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
        return (temporaryPath, finalPath);
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
        File.Exists(Path.Combine(directory, stem + ".txt")) ||
        File.Exists(Path.Combine(directory, stem + ".srt")) ||
        File.Exists(Path.Combine(directory, stem + ".json"));

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
