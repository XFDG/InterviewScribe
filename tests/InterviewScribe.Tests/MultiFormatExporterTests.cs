using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using InterviewScribe.Core.Export;
using InterviewScribe.Infrastructure.Pipeline;
using PdfSharp.Pdf.IO;

namespace InterviewScribe.Tests;

public sealed class MultiFormatExporterTests
{
    [Fact]
    public async Task ExportAsync_WritesOnlySelectedFormatsWithSharedStem()
    {
        using var directory = new TemporaryDirectory();

        var result = await AtomicTranscriptExporter.ExportAsync(
            TestDocumentFactory.Create(),
            directory.Path,
            TranscriptOutputFormat.Markdown | TranscriptOutputFormat.Docx,
            CancellationToken.None);

        Assert.Equal(2, result.Paths.Count);
        Assert.NotNull(result.MarkdownPath);
        Assert.NotNull(result.DocxPath);
        Assert.Null(result.TxtPath);
        Assert.Null(result.PdfPath);
        Assert.Null(result.SrtPath);
        Assert.Null(result.JsonPath);
        Assert.Equal(result.Stem + ".md", Path.GetFileName(result.MarkdownPath));
        Assert.Equal(result.Stem + ".docx", Path.GetFileName(result.DocxPath));
        Assert.Equal(2, Directory.GetFiles(directory.Path).Length);
    }

    [Fact]
    public async Task ExportAsync_DocxIsValidOpenXmlPackageAndKeepsUnicodeLabels()
    {
        using var directory = new TemporaryDirectory();

        var result = await AtomicTranscriptExporter.ExportAsync(
            TestDocumentFactory.Create(),
            directory.Path,
            TranscriptOutputFormat.Docx,
            CancellationToken.None);

        using var archive = ZipFile.OpenRead(result.GetRequiredPath(TranscriptOutputFormat.Docx));
        Assert.NotNull(archive.GetEntry("[Content_Types].xml"));
        Assert.NotNull(archive.GetEntry("_rels/.rels"));
        Assert.NotNull(archive.GetEntry("word/styles.xml"));
        Assert.NotNull(archive.GetEntry("word/_rels/document.xml.rels"));
        var documentEntry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("word/document.xml"));
        using var reader = new StreamReader(documentEntry.Open(), Encoding.UTF8);
        var xml = await reader.ReadToEndAsync(CancellationToken.None);
        _ = XDocument.Parse(xml);
        Assert.Contains("面试录屏转写", xml);
        Assert.Contains("[00:00:00.000 - 00:00:02.000]", xml);
        Assert.Contains("说话人 1", xml);
        Assert.Contains("Hello world", xml);
        Assert.Contains("你好世界", xml);
    }

    [Fact]
    public async Task ExportAsync_PdfCanBeReopenedAndContainsEmbeddedUnicodeFont()
    {
        using var directory = new TemporaryDirectory();

        var result = await AtomicTranscriptExporter.ExportAsync(
            TestDocumentFactory.Create(),
            directory.Path,
            TranscriptOutputFormat.Pdf,
            CancellationToken.None);
        var pdfPath = result.GetRequiredPath(TranscriptOutputFormat.Pdf);

        var prefix = new byte[5];
        await using (var stream = File.OpenRead(pdfPath))
        {
            Assert.Equal(prefix.Length, await stream.ReadAsync(prefix, CancellationToken.None));
        }

        Assert.Equal("%PDF-", Encoding.ASCII.GetString(prefix));
        using var pdf = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
        Assert.Single(pdf.Pages);
        Assert.True(new FileInfo(pdfPath).Length > 10_000);
        var pdfStructure = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(pdfPath));
        Assert.Contains("/ToUnicode", pdfStructure);
        Assert.Contains("/FontFile2", pdfStructure);
    }

    [Fact]
    public async Task ExportAsync_PdfUsesScriptSpecificFontsForSupportedLanguages()
    {
        using var directory = new TemporaryDirectory();
        var document = TestDocumentFactory.Create(
        [
            new(0, 1_000, 1, "中文 English Français Deutsch Español Português Italiano Русский"),
            new(1_100, 2_000, 2, "日本語のテスト 한국어 테스트"),
        ]);

        var result = await AtomicTranscriptExporter.ExportAsync(
            document,
            directory.Path,
            TranscriptOutputFormat.Pdf,
            CancellationToken.None);
        var pdfPath = result.GetRequiredPath(TranscriptOutputFormat.Pdf);

        using var pdf = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
        Assert.Single(pdf.Pages);
        var pdfStructure = Encoding.Latin1.GetString(await File.ReadAllBytesAsync(pdfPath));
        Assert.True(pdfStructure.Split("/FontFile2", StringSplitOptions.None).Length >= 4);
        Assert.True(pdfStructure.Split("/ToUnicode", StringSplitOptions.None).Length >= 4);
    }

    [Fact]
    public async Task ExportAsync_PdfPaginatesLongTranscripts()
    {
        using var directory = new TemporaryDirectory();
        var segments = Enumerable.Range(0, 180)
            .Select(index => new InterviewScribe.Core.Domain.TranscriptSegment(
                index * 1_100L,
                (index * 1_100L) + 1_000,
                (index % 2) + 1,
                $"第 {index + 1} 段访谈内容：这是用于验证长时间录屏 PDF 换页的中英文 mixed transcript paragraph."))
            .ToArray();

        var result = await AtomicTranscriptExporter.ExportAsync(
            TestDocumentFactory.Create(segments, mediaDuration: TimeSpan.FromMinutes(5)),
            directory.Path,
            TranscriptOutputFormat.Pdf,
            CancellationToken.None);

        using var pdf = PdfReader.Open(
            result.GetRequiredPath(TranscriptOutputFormat.Pdf),
            PdfDocumentOpenMode.Import);
        Assert.True(pdf.PageCount > 1);
    }

    [Fact]
    public async Task ExportAsync_AppliesFormattingOptionsToMarkdownAndDocx()
    {
        using var directory = new TemporaryDirectory();
        var options = new TranscriptFormattingOptions
        {
            IncludeTimestamps = false,
            IncludeSpeakers = false,
        };

        var result = await AtomicTranscriptExporter.ExportAsync(
            TestDocumentFactory.Create(),
            directory.Path,
            TranscriptOutputFormat.Markdown | TranscriptOutputFormat.Docx,
            CancellationToken.None,
            options);

        var markdown = await File.ReadAllTextAsync(result.GetRequiredPath(TranscriptOutputFormat.Markdown));
        Assert.DoesNotContain("[00:", markdown);
        Assert.DoesNotContain("说话人", markdown);

        using var archive = ZipFile.OpenRead(result.GetRequiredPath(TranscriptOutputFormat.Docx));
        var entry = Assert.IsType<ZipArchiveEntry>(archive.GetEntry("word/document.xml"));
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        var xml = await reader.ReadToEndAsync(CancellationToken.None);
        Assert.DoesNotContain("[00:", xml);
        Assert.DoesNotContain("说话人", xml);
    }

    [Fact]
    public async Task ExportAsync_RejectsEmptyOrUnknownFormatWithoutCreatingFiles()
    {
        using var directory = new TemporaryDirectory();
        var document = TestDocumentFactory.Create();

        await Assert.ThrowsAsync<ArgumentException>(() => AtomicTranscriptExporter.ExportAsync(
            document,
            directory.Path,
            TranscriptOutputFormat.None,
            CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => AtomicTranscriptExporter.ExportAsync(
            document,
            directory.Path,
            (TranscriptOutputFormat)(1 << 20),
            CancellationToken.None));

        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task ExportAsync_AnySupportedExtensionReservesStem()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(Path.Combine(directory.Path, "技术面试_转写.pdf"), "keep");

        var result = await AtomicTranscriptExporter.ExportAsync(
            TestDocumentFactory.Create(),
            directory.Path,
            TranscriptOutputFormat.Txt,
            CancellationToken.None);

        Assert.Equal("技术面试_转写_2.txt", Path.GetFileName(result.TxtPath));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "InterviewScribe.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
