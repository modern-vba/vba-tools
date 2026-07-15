using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Tests;

public sealed class VbaLanguageVocabularyTests
{
    [Fact]
    public void CanonicalKeywordsCoverParserVocabularyAndWordOperators()
    {
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Eqv"] = "Eqv",
            ["GoSub"] = "GoSub",
            ["GoTo"] = "GoTo",
            ["Imp"] = "Imp",
            ["Is"] = "Is",
            ["Like"] = "Like",
            ["Mod"] = "Mod",
            ["Optional"] = "Optional",
            ["ParamArray"] = "ParamArray",
            ["Preserve"] = "Preserve",
            ["PtrSafe"] = "PtrSafe",
            ["RaiseEvent"] = "RaiseEvent",
            ["ReDim"] = "ReDim",
            ["Rem"] = "Rem",
            ["Resume"] = "Resume",
            ["Select"] = "Select"
        };

        foreach (var (word, canonicalWord) in expected)
        {
            Assert.True(VbaLanguageVocabulary.CanonicalKeywords.TryGetValue(word, out var actualWord));
            Assert.Equal(canonicalWord, actualWord);
        }
    }

    [Fact]
    public void CompletionWordSetsAreImmutableOrderedAndContextSpecific()
    {
        AssertOrderedSet(VbaLanguageVocabulary.ModuleDeclarationWords);
        AssertOrderedSet(VbaLanguageVocabulary.ProcedureStatementWords);
        AssertOrderedSet(VbaLanguageVocabulary.ExpressionValueWords);
        AssertOrderedSet(VbaLanguageVocabulary.TypeNames);

        Assert.Equal(
            ["Empty", "False", "Me", "New", "Not", "Nothing", "Null", "True"],
            VbaLanguageVocabulary.ExpressionValueWords);
        Assert.DoesNotContain("Public", VbaLanguageVocabulary.ExpressionValueWords);
        Assert.DoesNotContain("Function", VbaLanguageVocabulary.ProcedureStatementWords);
        Assert.DoesNotContain("If", VbaLanguageVocabulary.ModuleDeclarationWords);
        Assert.Contains("Debug", VbaLanguageVocabulary.ProcedureStatementWords);
        Assert.Contains("Exit", VbaLanguageVocabulary.ProcedureStatementWords);
        Assert.Contains("On", VbaLanguageVocabulary.ProcedureStatementWords);
    }

    [Fact]
    public void ContextWordSetsOnlyContainCanonicalKeywords()
    {
        var contextWords = VbaLanguageVocabulary.ModuleDeclarationWords
            .Concat(VbaLanguageVocabulary.ProcedureStatementWords)
            .Concat(VbaLanguageVocabulary.ExpressionValueWords)
            .Concat(VbaLanguageVocabulary.TypeNames);

        Assert.All(contextWords, word =>
            Assert.Equal(word, VbaLanguageVocabulary.CanonicalKeywords[word]));
    }

    [Theory]
    [InlineData("String", true)]
    [InlineData("Date", true)]
    [InlineData("If", false)]
    [InlineData("And", false)]
    [InlineData("Not", false)]
    public void BareKeywordCallCapabilityDistinguishesIntrinsicsFromGroupingWords(
        string word,
        bool expected)
    {
        Assert.Equal(expected, VbaLanguageVocabulary.CanBeBareCallTarget(word));
    }

    private static void AssertOrderedSet(IReadOnlyList<string> words)
    {
        Assert.Equal(
            words.OrderBy(word => word, StringComparer.OrdinalIgnoreCase),
            words);
        Assert.Equal(
            words.Count,
            words.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        var mutableView = Assert.IsAssignableFrom<IList<string>>(words);
        Assert.Throws<NotSupportedException>(() => mutableView.Add("Injected"));
    }
}
