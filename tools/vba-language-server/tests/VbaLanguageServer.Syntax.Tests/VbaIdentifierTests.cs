using System.Globalization;
using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaIdentifierTests
{
    [Theory]
    [InlineData("Alpha_1", VbaIdentifierForm.Latin)]
    [InlineData("\u00a0value", VbaIdentifierForm.CodePage)]
    [InlineData("集計", VbaIdentifierForm.Japanese)]
    [InlineData("한글", VbaIdentifierForm.Korean)]
    [InlineData("汉字", VbaIdentifierForm.SimplifiedChinese)]
    [InlineData("臺灣", VbaIdentifierForm.TraditionalChinese)]
    public void GetFormsRecognizesEveryMsVbalLexIdentifierForm(
        string value,
        VbaIdentifierForm expectedForm)
    {
        var forms = VbaIdentifier.GetForms(value);

        Assert.True(forms.HasFlag(expectedForm), $"{value} was recognized as {forms}.");
    }

    [Fact]
    public void GetFormsIntersectsFormsAcrossTheWholeName()
    {
        Assert.NotEqual(VbaIdentifierForm.None, VbaIdentifier.GetForms("亜"));
        Assert.NotEqual(VbaIdentifierForm.None, VbaIdentifier.GetForms("ㄱ"));

        Assert.Equal(VbaIdentifierForm.None, VbaIdentifier.GetForms("亜ㄱ"));
    }

    [Theory]
    [InlineData('_')]
    [InlineData('7')]
    public void SubsequentOnlyCharactersRemainWordCharactersButCannotStartAName(char value)
    {
        Assert.True(VbaIdentifier.IsWordCharacter(value));
        Assert.False(VbaIdentifier.IsInitialCharacter(value));
        Assert.False(VbaIdentifier.IsLexIdentifier(value.ToString()));
    }

    [Fact]
    public void WhitespaceMatchesTheExactMsVbalWscCodePoints()
    {
        int[] expected =
        [
            0x0009, 0x0019, 0x0020, 0x1680, 0x180e,
            0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005,
            0x2006, 0x2007, 0x2008, 0x2009, 0x200a,
            0x202f, 0x205f, 0x3000
        ];

        Assert.All(expected, value => Assert.True(VbaIdentifier.IsWhitespace((char)value)));
        Assert.False(VbaIdentifier.IsWhitespace('\u000b'));
        Assert.False(VbaIdentifier.IsWhitespace('\u00a0'));
        Assert.False(VbaIdentifier.IsWhitespace('\u2028'));
    }

    [Theory]
    [InlineData("Name%")]
    [InlineData("Name&")]
    [InlineData("Name^")]
    [InlineData("Name!")]
    [InlineData("Name#")]
    [InlineData("Name@")]
    [InlineData("Name$")]
    [InlineData("[Name]")]
    public void BaseIdentifierExcludesTypedSuffixesAndForeignNames(string value)
    {
        Assert.False(VbaIdentifier.IsLexIdentifier(value));
        Assert.False(VbaIdentifier.IsIdentifier(value));
    }

    [Theory]
    [InlineData("、")]
    [InlineData("。")]
    [InlineData("·")]
    [InlineData("ˉ")]
    [InlineData("ˇ")]
    [InlineData("¨")]
    [InlineData("〃")]
    [InlineData("々")]
    [InlineData("—")]
    public void SimplifiedChineseFormAppliesTheCp936A1A2ThroughA1AaCorrection(string value)
    {
        Assert.True(
            VbaIdentifier.GetForms(value).HasFlag(VbaIdentifierForm.SimplifiedChinese));
    }

    [Fact]
    public void SimplifiedChineseSubsequentCharactersIncludeCp936InitialCharacters()
    {
        Assert.True(
            VbaIdentifier.GetForms("汉、").HasFlag(VbaIdentifierForm.SimplifiedChinese));
    }

    [Fact]
    public void IdentifierRejectsEveryCaseOfACompleteReservedProductionName()
    {
        Assert.True(VbaIdentifier.IsLexIdentifier("cDeCl"));
        Assert.True(VbaIdentifier.IsReservedIdentifier("cDeCl"));
        Assert.False(VbaIdentifier.IsIdentifier("cDeCl"));
    }

    [Theory]
    [InlineData("AddressOf")]
    [InlineData("Print")]
    [InlineData("VB_VarProcData")]
    public void IdentifierRejectsReservedNamesFromEveryReservedProductionFamily(string value)
    {
        Assert.True(VbaIdentifier.IsReservedIdentifier(value));
        Assert.False(VbaIdentifier.IsIdentifier(value));
    }

    [Theory]
    [InlineData("Alias")]
    [InlineData("Explicit")]
    [InlineData("Lib")]
    [InlineData("Object")]
    [InlineData("Property")]
    [InlineData("PtrSafe")]
    public void ContextualVocabularyThatIsNotReservedRemainsAnIdentifier(string value)
    {
        Assert.False(VbaIdentifier.IsReservedIdentifier(value));
        Assert.True(VbaIdentifier.IsIdentifier(value));
    }

    [Fact]
    public void ConformanceMetadataPinsTheSpecificationAndForwardMappingSources()
    {
        Assert.Equal("MS-VBAL 2.4 (2025-05-20)", VbaIdentifier.SpecificationRevision);
        Assert.Contains(
            "forward MBTABLE/DBCSTABLE",
            VbaIdentifier.CodePageMappingProvenance,
            StringComparison.Ordinal);
        Assert.Equal(14, VbaIdentifier.CodePageMappingSha256.Count);
        Assert.Equal(
            "e5070a2d6ad26619f5872ddbe64d3381c11620af5adbb04cda0f0abb1a91fdae",
            VbaIdentifier.CodePageMappingSha256[936]);
    }

    [Fact]
    public void PublicMappingHashesCannotMutateThePinnedConformanceData()
    {
        var dictionary = Assert.IsAssignableFrom<IDictionary<int, string>>(
            VbaIdentifier.CodePageMappingSha256);
        var expected = dictionary[936];

        Assert.True(dictionary.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => dictionary[936] = "mutated");
        Assert.Equal(expected, VbaIdentifier.CodePageMappingSha256[936]);
    }

    [Theory]
    [InlineData("tr-TR")]
    [InlineData("ja-JP")]
    public void IdentifierRecognitionDoesNotDependOnTheCurrentCulture(string cultureName)
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);

            Assert.True(VbaIdentifier.IsIdentifier("集計"));
            Assert.True(VbaIdentifier.IsReservedIdentifier("cDeCl"));
            Assert.Equal(VbaIdentifierForm.None, VbaIdentifier.GetForms("亜ㄱ"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void JapaneseFormUsesEveryAllowedForwardMicrosoftCodePageMapping()
    {
        var forms = VbaIdentifier.GetForms("・");

        Assert.True(forms.HasFlag(VbaIdentifierForm.Japanese));
    }

    [Theory]
    [InlineData("\u0080")]
    [InlineData("\uf8f7")]
    public void KoreanFormExcludesSingleByteCp949Mappings(string value)
    {
        var forms = VbaIdentifier.GetForms(value);

        Assert.False(forms.HasFlag(VbaIdentifierForm.Korean));
    }
}
