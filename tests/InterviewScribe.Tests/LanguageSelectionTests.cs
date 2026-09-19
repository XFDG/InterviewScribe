using InterviewScribe.Infrastructure.Pipeline;

namespace InterviewScribe.Tests;

public sealed class LanguageSelectionTests
{
    [Theory]
    [InlineData(" zh ", "zh", true, false)]
    [InlineData("EN", "en", false, true)]
    public void FromCodes_WithOneLanguage_UsesExactNativeCode(
        string languageCode,
        string expectedCode,
        bool includesChinese,
        bool includesEnglish)
    {
        var selection = LanguageSelection.FromCodes([languageCode]);

        Assert.Equal(expectedCode, selection.NativeLanguageCode);
        Assert.Equal(expectedCode, selection.EngineArgument);
        Assert.Equal([expectedCode], selection.Codes);
        Assert.Equal(includesChinese, selection.IncludesChinese);
        Assert.Equal(includesEnglish, selection.IncludesEnglish);
    }

    [Theory]
    [InlineData("zh", "en")]
    [InlineData(" EN ", "ZH")]
    public void FromCodes_WithBothLanguages_UsesAutoInsteadOfCombinedNativeCode(
        string first,
        string second)
    {
        var selection = LanguageSelection.FromCodes([first, second]);

        Assert.Null(selection.NativeLanguageCode);
        Assert.Equal("auto", selection.EngineArgument);
        Assert.DoesNotContain(',', selection.EngineArgument);
        Assert.True(selection.IncludesChinese);
        Assert.True(selection.IncludesEnglish);
        Assert.Equal(["zh", "en"], selection.Codes);
    }

    [Fact]
    public void FromCodes_WithNullCollection_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => LanguageSelection.FromCodes(null!));
    }

    [Fact]
    public void FromCodes_WithNoLanguages_Throws()
    {
        Assert.Throws<ArgumentException>(() => LanguageSelection.FromCodes([]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("auto")]
    [InlineData("zh,en")]
    [InlineData("fr")]
    [InlineData("zh-CN")]
    public void FromCodes_WithUnsupportedLanguage_Throws(string? languageCode)
    {
        Assert.Throws<ArgumentException>(() =>
            LanguageSelection.FromCodes([languageCode!]));
    }

    [Theory]
    [InlineData("zh")]
    [InlineData("en")]
    public void FromCodes_WithDuplicates_DeduplicatesInFixedOrder(string languageCode)
    {
        var selection = LanguageSelection.FromCodes([languageCode, languageCode]);

        Assert.Equal([languageCode], selection.Codes);
    }

    [Fact]
    public void FromCodes_WithMixedDuplicates_UsesFixedChineseThenEnglishOrder()
    {
        var selection = LanguageSelection.FromCodes(["en", "ZH", " en ", "zh"]);

        Assert.Equal(["zh", "en"], selection.Codes);
        Assert.Null(selection.NativeLanguageCode);
        Assert.Equal("auto", selection.EngineArgument);
    }
}
