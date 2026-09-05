using VbaTools.Syntax;
using Xunit;

namespace VbaTools.Syntax.Tests;

public sealed class VbaLexicalFactsTests
{
    [Fact]
    public void IntrinsicTypeLookupReturnsCanonicalVocabularyCasing()
    {
        Assert.True(VbaLanguageVocabulary.TryGetCanonicalTypeName("sTrInG", out var name));
        Assert.Equal("String", name);
        Assert.False(VbaLanguageVocabulary.TryGetCanonicalTypeName("Widget", out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("' regular comment")]
    [InlineData("    '* @details")]
    [InlineData("Rem")]
    [InlineData("  rEm existing comment")]
    [InlineData("Rem first: app.Work")]
    [InlineData("Rem\tcomment")]
    public void Blank_or_comment_only_line_recognizes_supported_boundary_trivia(string line)
    {
        Assert.True(VbaLexicalFacts.IsBlankOrCommentOnlyLine(line));
    }

    [Fact]
    public void RemCommentRecognizesExactMsVbalWhitespace()
    {
        Assert.True(VbaLexicalFacts.IsBlankOrCommentOnlyLine("\u0019Rem\u0019comment"));
        Assert.False(VbaLexicalFacts.IsBlankOrCommentOnlyLine("Rem\u00A0comment"));
    }

    [Theory]
    [InlineData("Debug.Print 1 ' inline comment")]
    [InlineData("Public Sub B() ' inline comment")]
    [InlineData("Label: ' comment")]
    [InlineData("Label: Rem comment")]
    [InlineData("Call Work: Rem comment")]
    [InlineData(": Rem comment")]
    [InlineData("value = \"' not a comment\"")]
    [InlineData("Remember = True")]
    [InlineData("Rem: Debug.Print 1")]
    [InlineData("Rem\"unterminated")]
    [InlineData("#If VBA7 Then ' comment")]
    [InlineData("_ ' comment")]
    public void Blank_or_comment_only_line_rejects_code_bearing_lines(string line)
    {
        Assert.False(VbaLexicalFacts.IsBlankOrCommentOnlyLine(line));
    }

    [Fact]
    public void SplitCodeAndCommentPreservesApostrophesInsideStrings()
    {
        const string line = "value = \"that's ok\" ' comment";

        var parts = VbaLexicalFacts.SplitCodeAndComment(line);

        Assert.Equal("value = \"that's ok\" ", parts.CodePart);
        Assert.Equal("' comment", parts.CommentPart);
    }

}
