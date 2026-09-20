using InterviewScribe.Core.Export;

namespace InterviewScribe.Infrastructure.Pipeline;

public sealed record TranscriptExportResult
{
    public required string Stem { get; init; }
    public required IReadOnlyDictionary<TranscriptOutputFormat, string> Paths { get; init; }

    public string? TxtPath => GetPath(TranscriptOutputFormat.Txt);
    public string? MarkdownPath => GetPath(TranscriptOutputFormat.Markdown);
    public string? DocxPath => GetPath(TranscriptOutputFormat.Docx);
    public string? PdfPath => GetPath(TranscriptOutputFormat.Pdf);
    public string? SrtPath => GetPath(TranscriptOutputFormat.Srt);
    public string? JsonPath => GetPath(TranscriptOutputFormat.Json);

    public string? GetPath(TranscriptOutputFormat format) =>
        Paths.TryGetValue(format, out var path) ? path : null;

    public string GetRequiredPath(TranscriptOutputFormat format) =>
        GetPath(format) ?? throw new InvalidOperationException($"没有导出格式 {format}。");
}
