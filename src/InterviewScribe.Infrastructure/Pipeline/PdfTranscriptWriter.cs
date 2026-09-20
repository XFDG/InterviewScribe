using System.Drawing.Text;
using System.Text;
using InterviewScribe.Core.Domain;
using InterviewScribe.Core.Export;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace InterviewScribe.Infrastructure.Pipeline;

internal static class PdfTranscriptWriter
{
    private const double Margin = 54;
    private const double BodyFontSize = 10.5;
    private const double TitleFontSize = 18;
    private static readonly string[] CjkFontCandidates =
    [
        "Microsoft YaHei UI",
        "Microsoft YaHei",
        "Noto Sans SC",
        "Noto Sans CJK SC",
        "SimHei",
        "DengXian",
        "Microsoft JhengHei UI",
        "Arial Unicode MS",
    ];
    private static readonly string[] LatinFontCandidates = ["Segoe UI", "Arial", "Noto Sans"];
    private static readonly string[] JapaneseFontCandidates = ["Yu Gothic UI", "Yu Gothic", "Noto Sans JP", "Noto Sans CJK JP"];
    private static readonly string[] KoreanFontCandidates = ["Malgun Gothic", "Noto Sans KR", "Noto Sans CJK KR"];

    public static Task WriteAsync(
        string path,
        TranscriptDocument transcript,
        TranscriptFormattingOptions formattingOptions,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var document = new PdfDocument();
        document.Info.Title = $"{transcript.SourceFileName} - 转写";
        document.Info.Author = "MediaScribe";
        document.Info.Creator = "MediaScribe";
        document.Info.CreationDate = transcript.CreatedAt.LocalDateTime;

        var text = TranscriptFormatter.ToTxt(transcript, options: formattingOptions).ReplaceLineEndings("\n");
        var fontOptions = new XPdfFontOptions(PdfFontEncoding.Unicode, PdfFontEmbedding.TryComputeSubset);
        var (bodyFonts, titleFonts) = CreateFontSets(fontOptions, text);
        var pageState = AddPage(document);
        try
        {
            var isFirstLine = true;
            foreach (var originalParagraph in text.Split('\n'))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var paragraph = originalParagraph;
                var fonts = bodyFonts;
                if (isFirstLine && paragraph.StartsWith("# ", StringComparison.Ordinal))
                {
                    paragraph = paragraph[2..];
                    fonts = titleFonts;
                }

                isFirstLine = false;
                if (paragraph.Length == 0)
                {
                    pageState.Y += bodyFonts.LineHeight * 0.45;
                    EnsureVerticalSpace(document, ref pageState, bodyFonts.LineHeight, cancellationToken);
                    continue;
                }

                foreach (var line in WrapLine(pageState.Graphics, paragraph, fonts, pageState.ContentWidth))
                {
                    EnsureVerticalSpace(document, ref pageState, fonts.LineHeight, cancellationToken);
                    DrawTextRuns(pageState.Graphics, line, fonts, Margin, pageState.Y, pageState.ContentWidth);
                    pageState.Y += fonts.LineHeight;
                }

                pageState.Y += bodyFonts.LineHeight * 0.2;
            }
        }
        finally
        {
            pageState.Graphics.Dispose();
        }

        cancellationToken.ThrowIfCancellationRequested();
        document.Save(path);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static (PdfFontSet Body, PdfFontSet Title) CreateFontSets(XPdfFontOptions options, string text)
    {
        using var installedFontCollection = new InstalledFontCollection();
        var installedFamilies = installedFontCollection.Families
            .Select(item => item.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var cjkFamily = FindLoadableFamily(installedFamilies, CjkFontCandidates, options)
            ?? throw new InvalidOperationException(
                "PDF 导出需要可用的中文字体（推荐 Microsoft YaHei UI）。请在 Windows 可选功能中安装中文字体后重试。");
        var latinFamily = FindLoadableFamily(installedFamilies, LatinFontCandidates, options) ?? cjkFamily;
        var japaneseFamily = ContainsScript(text, IsJapaneseKana)
            ? FindLoadableFamily(installedFamilies, JapaneseFontCandidates, options) ?? cjkFamily
            : cjkFamily;
        var koreanFamily = ContainsScript(text, IsHangul)
            ? FindLoadableFamily(installedFamilies, KoreanFontCandidates, options)
                ?? throw new InvalidOperationException(
                    "PDF 中包含韩文，但 Windows 没有可用的韩文字体。请安装“韩语补充字体”后重试。")
            : cjkFamily;

        return (
            CreateFontSet(cjkFamily, latinFamily, japaneseFamily, koreanFamily, BodyFontSize, XFontStyleEx.Regular, options),
            CreateFontSet(cjkFamily, latinFamily, japaneseFamily, koreanFamily, TitleFontSize, XFontStyleEx.Bold, options));
    }

    private static PdfFontSet CreateFontSet(
        string cjkFamily,
        string latinFamily,
        string japaneseFamily,
        string koreanFamily,
        double size,
        XFontStyleEx style,
        XPdfFontOptions options)
    {
        try
        {
            var fonts = new Dictionary<string, XFont>(StringComparer.OrdinalIgnoreCase);
            XFont GetOrCreate(string family)
            {
                if (!fonts.TryGetValue(family, out var font))
                {
                    font = new XFont(family, size, style, options);
                    fonts.Add(family, font);
                }

                return font;
            }

            return new PdfFontSet(
                GetOrCreate(cjkFamily),
                GetOrCreate(latinFamily),
                GetOrCreate(japaneseFamily),
                GetOrCreate(koreanFamily));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            throw new InvalidOperationException(
                "PDF 字体无法加载或嵌入。请修复 Windows 字体后重试。",
                exception);
        }
    }

    private static string? FindLoadableFamily(
        IReadOnlySet<string> installedFamilies,
        IEnumerable<string> candidates,
        XPdfFontOptions options)
    {
        foreach (var family in candidates.Where(installedFamilies.Contains))
        {
            try
            {
                _ = new XFont(family, BodyFontSize, XFontStyleEx.Regular, options);
                _ = new XFont(family, TitleFontSize, XFontStyleEx.Bold, options);
                return family;
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
            {
            }
        }

        return null;
    }

    private static bool ContainsScript(string text, Func<Rune, bool> predicate) =>
        text.EnumerateRunes().Any(predicate);

    private static PageState AddPage(PdfDocument document)
    {
        var page = document.AddPage();
        page.Size = PdfSharp.PageSize.A4;
        var graphics = XGraphics.FromPdfPage(page);
        return new PageState(graphics, Margin, page.Width.Point - (Margin * 2), page.Height.Point - Margin);
    }

    private static void EnsureVerticalSpace(
        PdfDocument document,
        ref PageState state,
        double requiredHeight,
        CancellationToken cancellationToken)
    {
        if (state.Y + requiredHeight <= state.Bottom)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        state.Graphics.Dispose();
        state = AddPage(document);
    }

    private static IEnumerable<string> WrapLine(
        XGraphics graphics,
        string value,
        PdfFontSet fonts,
        double maximumWidth)
    {
        var remaining = value;
        while (remaining.Length > 0)
        {
            var consumed = 0;
            var lastWhitespace = -1;
            var measuredWidth = 0d;
            foreach (var rune in remaining.EnumerateRunes())
            {
                var runeWidth = graphics.MeasureString(rune.ToString(), fonts.ForRune(rune)).Width;
                if (measuredWidth + runeWidth > maximumWidth)
                {
                    break;
                }

                measuredWidth += runeWidth;
                consumed += rune.Utf16SequenceLength;
                if (Rune.IsWhiteSpace(rune))
                {
                    lastWhitespace = consumed;
                }
            }

            if (consumed == remaining.Length)
            {
                yield return remaining;
                yield break;
            }

            if (consumed == 0)
            {
                consumed = remaining.EnumerateRunes().First().Utf16SequenceLength;
            }
            else if (lastWhitespace > 0)
            {
                consumed = lastWhitespace;
            }

            var line = remaining[..consumed].TrimEnd();
            if (line.Length > 0)
            {
                yield return line;
            }

            remaining = remaining[consumed..].TrimStart();
        }
    }

    private static void DrawTextRuns(
        XGraphics graphics,
        string value,
        PdfFontSet fonts,
        double x,
        double y,
        double maximumWidth)
    {
        var run = new StringBuilder();
        XFont? currentFont = null;

        void Flush()
        {
            if (run.Length == 0 || currentFont is null)
            {
                return;
            }

            var text = run.ToString();
            graphics.DrawString(
                text,
                currentFont,
                XBrushes.Black,
                new XRect(x, y, maximumWidth, fonts.LineHeight),
                XStringFormats.TopLeft);
            x += graphics.MeasureString(text, currentFont).Width;
            run.Clear();
        }

        foreach (var rune in value.EnumerateRunes())
        {
            var font = fonts.ForRune(rune);
            if (currentFont is not null && !ReferenceEquals(currentFont, font))
            {
                Flush();
            }

            currentFont = font;
            run.Append(rune.ToString());
        }

        Flush();
    }

    private static bool IsJapaneseKana(Rune rune) =>
        rune.Value is >= 0x3040 and <= 0x30FF or >= 0x31F0 and <= 0x31FF;

    private static bool IsHangul(Rune rune) =>
        rune.Value is >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F or >= 0xA960 and <= 0xA97F
            or >= 0xAC00 and <= 0xD7AF or >= 0xD7B0 and <= 0xD7FF;

    private static bool IsCjk(Rune rune) =>
        rune.Value is >= 0x2E80 and <= 0x303F or >= 0x3100 and <= 0x312F or >= 0x31A0 and <= 0x31EF
            or >= 0x3200 and <= 0x33FF or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF or >= 0xFF00 and <= 0xFFEF or >= 0x20000 and <= 0x323AF;

    private sealed class PdfFontSet(XFont cjk, XFont latin, XFont japanese, XFont korean)
    {
        public double LineHeight { get; } = new[] { cjk, latin, japanese, korean }.Max(item => item.GetHeight()) * 1.25;

        public XFont ForRune(Rune rune)
        {
            if (IsJapaneseKana(rune))
            {
                return japanese;
            }

            if (IsHangul(rune))
            {
                return korean;
            }

            return IsCjk(rune) ? cjk : latin;
        }
    }

    private sealed class PageState(XGraphics graphics, double y, double contentWidth, double bottom)
    {
        public XGraphics Graphics { get; } = graphics;
        public double Y { get; set; } = y;
        public double ContentWidth { get; } = contentWidth;
        public double Bottom { get; } = bottom;
    }
}
