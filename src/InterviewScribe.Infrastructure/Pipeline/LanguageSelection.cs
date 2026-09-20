namespace InterviewScribe.Infrastructure.Pipeline;

internal sealed class LanguageSelection
{
    private static readonly string[] SupportedCodes =
    [
        "zh", "en", "yue", "ja", "ko", "fr", "de", "es", "pt", "ru", "it",
    ];

    private LanguageSelection(IReadOnlyList<string> codes)
    {
        Codes = codes;
    }

    public bool IncludesChinese => Codes.Contains("zh", StringComparer.Ordinal);

    public bool IncludesEnglish => Codes.Contains("en", StringComparer.Ordinal);

    public IReadOnlyList<string> Codes { get; }

    public string? NativeLanguageCode => Codes.Count == 1 ? Codes[0] : null;

    public string EngineArgument => NativeLanguageCode ?? "auto";

    // The packaged transcribe.cpp MOSS port currently documents explicit
    // language hints only for Chinese and English. Other selections still use
    // MOSS for its speaker track, but leave language detection on automatic.
    public string MossEngineArgument => NativeLanguageCode is "zh" or "en"
        ? NativeLanguageCode
        : "auto";

    public static LanguageSelection FromCodes(IEnumerable<string> languageCodes)
    {
        ArgumentNullException.ThrowIfNull(languageCodes);

        var selectedCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var languageCode in languageCodes)
        {
            if (languageCode is null)
            {
                throw new ArgumentException("语言选项不能为空。", nameof(languageCodes));
            }

            var normalizedCode = languageCode.Trim().ToLowerInvariant();
            if (normalizedCode.Length == 0)
            {
                throw new ArgumentException("语言选项不能为空。", nameof(languageCodes));
            }

            if (!SupportedCodes.Contains(normalizedCode, StringComparer.Ordinal))
            {
                throw new ArgumentException(
                    $"不支持的语言代码：{normalizedCode}。",
                    nameof(languageCodes));
            }

            selectedCodes.Add(normalizedCode);
        }

        if (selectedCodes.Count == 0)
        {
            throw new ArgumentException("请至少选择一种识别语言。", nameof(languageCodes));
        }

        return new LanguageSelection(
            SupportedCodes.Where(selectedCodes.Contains).ToArray());
    }
}
