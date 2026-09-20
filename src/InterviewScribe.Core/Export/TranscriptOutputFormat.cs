namespace InterviewScribe.Core.Export;

[Flags]
public enum TranscriptOutputFormat
{
    None = 0,
    Txt = 1 << 0,
    Markdown = 1 << 1,
    Docx = 1 << 2,
    Pdf = 1 << 3,
    Srt = 1 << 4,
    Json = 1 << 5,

    Default = Txt | Srt | Json,
    All = Txt | Markdown | Docx | Pdf | Srt | Json,
}
