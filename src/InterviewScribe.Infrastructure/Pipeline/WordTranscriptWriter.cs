using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using InterviewScribe.Core.Domain;
using InterviewScribe.Core.Export;

namespace InterviewScribe.Infrastructure.Pipeline;

internal static class WordTranscriptWriter
{
    private static readonly XNamespace Word = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly XNamespace Relationships = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace ContentTypes = "http://schemas.openxmlformats.org/package/2006/content-types";
    private static readonly XNamespace DublinCore = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace DublinCoreTerms = "http://purl.org/dc/terms/";
    private static readonly XNamespace CoreProperties = "http://schemas.openxmlformats.org/package/2006/metadata/core-properties";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";
    private static readonly XNamespace ExtendedProperties = "http://schemas.openxmlformats.org/officeDocument/2006/extended-properties";
    private static readonly XNamespace VTypes = "http://schemas.openxmlformats.org/officeDocument/2006/docPropsVTypes";

    public static async Task WriteAsync(
        string path,
        TranscriptDocument document,
        TranscriptFormattingOptions formattingOptions,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false, Encoding.UTF8);

        await WriteEntryAsync(archive, "[Content_Types].xml", CreateContentTypes(), cancellationToken).ConfigureAwait(false);
        await WriteEntryAsync(archive, "_rels/.rels", CreatePackageRelationships(), cancellationToken).ConfigureAwait(false);
        await WriteEntryAsync(archive, "word/document.xml", CreateDocument(document, formattingOptions), cancellationToken).ConfigureAwait(false);
        await WriteEntryAsync(archive, "word/styles.xml", CreateStyles(), cancellationToken).ConfigureAwait(false);
        await WriteEntryAsync(
            archive,
            "word/_rels/document.xml.rels",
            CreateDocumentRelationships(),
            cancellationToken).ConfigureAwait(false);
        await WriteEntryAsync(archive, "docProps/core.xml", CreateCoreProperties(document), cancellationToken).ConfigureAwait(false);
        await WriteEntryAsync(archive, "docProps/app.xml", CreateExtendedProperties(), cancellationToken).ConfigureAwait(false);
    }

    private static XDocument CreateContentTypes() => new(
        new XElement(ContentTypes + "Types",
            new XElement(ContentTypes + "Default",
                new XAttribute("Extension", "rels"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
            new XElement(ContentTypes + "Default",
                new XAttribute("Extension", "xml"),
                new XAttribute("ContentType", "application/xml")),
            CreateOverride("/word/document.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"),
            CreateOverride("/word/styles.xml", "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml"),
            CreateOverride("/docProps/core.xml", "application/vnd.openxmlformats-package.core-properties+xml"),
            CreateOverride("/docProps/app.xml", "application/vnd.openxmlformats-officedocument.extended-properties+xml")));

    private static XElement CreateOverride(string partName, string contentType) =>
        new(ContentTypes + "Override", new XAttribute("PartName", partName), new XAttribute("ContentType", contentType));

    private static XDocument CreatePackageRelationships() => new(
        new XElement(Relationships + "Relationships",
            CreateRelationship("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument", "word/document.xml"),
            CreateRelationship("rId2", "http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties", "docProps/core.xml"),
            CreateRelationship("rId3", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/extended-properties", "docProps/app.xml")));

    private static XElement CreateRelationship(string id, string type, string target) =>
        new(Relationships + "Relationship", new XAttribute("Id", id), new XAttribute("Type", type), new XAttribute("Target", target));

    private static XDocument CreateDocumentRelationships() => new(
        new XElement(Relationships + "Relationships",
            CreateRelationship("rId1", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles", "styles.xml")));

    private static XDocument CreateDocument(TranscriptDocument document, TranscriptFormattingOptions formattingOptions)
    {
        var lines = TranscriptFormatter.ToTxt(document, options: formattingOptions)
            .ReplaceLineEndings("\n")
            .Split('\n');
        var body = new XElement(Word + "body");

        foreach (var originalLine in lines)
        {
            var line = originalLine;
            var style = "Normal";
            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                line = line[2..];
                style = "Title";
            }
            else if (line == "注意：")
            {
                style = "Heading1";
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                line = "• " + line[2..];
            }

            body.Add(CreateParagraph(line, style));
        }

        body.Add(new XElement(Word + "sectPr",
            new XElement(Word + "pgSz", new XAttribute(Word + "w", "11906"), new XAttribute(Word + "h", "16838")),
            new XElement(Word + "pgMar",
                new XAttribute(Word + "top", "1440"),
                new XAttribute(Word + "right", "1440"),
                new XAttribute(Word + "bottom", "1440"),
                new XAttribute(Word + "left", "1440"),
                new XAttribute(Word + "header", "720"),
                new XAttribute(Word + "footer", "720"),
                new XAttribute(Word + "gutter", "0"))));

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(Word + "document", new XAttribute(XNamespace.Xmlns + "w", Word), body));
    }

    private static XElement CreateParagraph(string text, string style)
    {
        var paragraph = new XElement(Word + "p");
        if (style != "Normal")
        {
            paragraph.Add(new XElement(Word + "pPr", new XElement(Word + "pStyle", new XAttribute(Word + "val", style))));
        }

        if (text.Length > 0)
        {
            paragraph.Add(new XElement(Word + "r",
                new XElement(Word + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text)));
        }

        return paragraph;
    }

    private static XDocument CreateStyles() => new(
        new XDeclaration("1.0", "UTF-8", "yes"),
        new XElement(Word + "styles",
            new XAttribute(XNamespace.Xmlns + "w", Word),
            new XElement(Word + "docDefaults",
                new XElement(Word + "rPrDefault",
                    new XElement(Word + "rPr",
                        CreateFonts(),
                        new XElement(Word + "sz", new XAttribute(Word + "val", "22")),
                        new XElement(Word + "szCs", new XAttribute(Word + "val", "22"))))),
            CreateStyle("Normal", "正文", null, 22, bold: false),
            CreateStyle("Title", "标题", "Normal", 36, bold: true),
            CreateStyle("Heading1", "标题 1", "Normal", 28, bold: true)));

    private static XElement CreateStyle(string id, string name, string? basedOn, int size, bool bold)
    {
        var style = new XElement(Word + "style",
            new XAttribute(Word + "type", "paragraph"),
            new XAttribute(Word + "styleId", id),
            new XElement(Word + "name", new XAttribute(Word + "val", name)));
        if (basedOn is not null)
        {
            style.Add(new XElement(Word + "basedOn", new XAttribute(Word + "val", basedOn)));
        }

        var runProperties = new XElement(Word + "rPr", CreateFonts());
        if (bold)
        {
            runProperties.Add(new XElement(Word + "b"), new XElement(Word + "bCs"));
        }

        runProperties.Add(
            new XElement(Word + "sz", new XAttribute(Word + "val", size)),
            new XElement(Word + "szCs", new XAttribute(Word + "val", size)));
        style.Add(runProperties);
        return style;
    }

    private static XElement CreateFonts() => new(Word + "rFonts",
        new XAttribute(Word + "ascii", "Microsoft YaHei"),
        new XAttribute(Word + "hAnsi", "Microsoft YaHei"),
        new XAttribute(Word + "eastAsia", "Microsoft YaHei"),
        new XAttribute(Word + "cs", "Microsoft YaHei"));

    private static XDocument CreateCoreProperties(TranscriptDocument document)
    {
        var created = document.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(CoreProperties + "coreProperties",
                new XAttribute(XNamespace.Xmlns + "cp", CoreProperties),
                new XAttribute(XNamespace.Xmlns + "dc", DublinCore),
                new XAttribute(XNamespace.Xmlns + "dcterms", DublinCoreTerms),
                new XAttribute(XNamespace.Xmlns + "xsi", Xsi),
                new XElement(DublinCore + "title", $"{document.SourceFileName} - 转写"),
                new XElement(DublinCore + "creator", "MediaScribe"),
                new XElement(CoreProperties + "lastModifiedBy", "MediaScribe"),
                new XElement(DublinCoreTerms + "created", new XAttribute(Xsi + "type", "dcterms:W3CDTF"), created),
                new XElement(DublinCoreTerms + "modified", new XAttribute(Xsi + "type", "dcterms:W3CDTF"), created)));
    }

    private static XDocument CreateExtendedProperties() => new(
        new XDeclaration("1.0", "UTF-8", "yes"),
        new XElement(ExtendedProperties + "Properties",
            new XAttribute(XNamespace.Xmlns + "vt", VTypes),
            new XElement(ExtendedProperties + "Application", "MediaScribe")));

    private static async Task WriteEntryAsync(
        ZipArchive archive,
        string name,
        XDocument document,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 16 * 1024, leaveOpen: false);
        await writer.WriteAsync(document.ToString(SaveOptions.DisableFormatting).AsMemory(), cancellationToken).ConfigureAwait(false);
    }
}
