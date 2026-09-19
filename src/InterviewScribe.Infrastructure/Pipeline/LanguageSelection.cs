namespace InterviewScribe.Infrastructure.Pipeline;

internal sealed class LanguageSelection
{
    private const string ChineseCode = "zh";
    private const string EnglishCode = "en";

    private LanguageSelection(bool includesChinese, bool includesEnglish)
    {
        IncludesChinese = includesChinese;
        IncludesEnglish = includesEnglish;
        Codes = includesChinese
            ? includesEnglish
                ? [ChineseCode, EnglishCode]
                : [ChineseCode]
            : [EnglishCode];
    }

    public bool IncludesChinese { get; }

    public bool IncludesEnglish { get; }

    public IReadOnlyList<string> Codes { get; }

    public string? NativeLanguageCode =>
        IncludesChinese == IncludesEnglish
            ? null
            : IncludesChinese
                ? ChineseCode
                : EnglishCode;

    public string EngineArgument => NativeLanguageCode ?? "auto";

    public static LanguageSelection FromCodes(IEnumerable<string> languageCodes)
    {
        ArgumentNullException.ThrowIfNull(languageCodes);

        var includesChinese = false;
        var includesEnglish = false;
        foreach (var languageCode in languageCodes)
        {
            if (languageCode is null)
            {
                throw new ArgumentException("语言选项不能为空。", nameof(languageCodes));
            }

            var normalizedCode = languageCode.Trim().ToLowerInvariant();
            switch (normalizedCode)
            {
                case ChineseCode:
                    includesChinese = true;
                    break;
                case EnglishCode:
                    includesEnglish = true;
                    break;
                case "":
                    throw new ArgumentException("语言选项不能为空。", nameof(languageCodes));
                default:
                    throw new ArgumentException(
                        $"不支持的语言代码：{normalizedCode}。当前仅支持 zh 和 en。",
                        nameof(languageCodes));
            }
        }

        if (!includesChinese && !includesEnglish)
        {
            throw new ArgumentException("请至少选择中文或英文。", nameof(languageCodes));
        }

        return new LanguageSelection(includesChinese, includesEnglish);
    }
}
