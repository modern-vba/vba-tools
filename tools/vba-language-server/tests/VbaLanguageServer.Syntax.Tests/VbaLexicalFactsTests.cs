using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaLexicalFactsTests
{
    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("' regular comment")]
    [InlineData("    '* @details")]
    [InlineData("Rem")]
    [InlineData("  rEm existing comment")]
    [InlineData("Rem first: app.Work")]
    public void Blank_or_comment_only_line_recognizes_supported_boundary_trivia(string line)
    {
        Assert.True(VbaLexicalFacts.IsBlankOrCommentOnlyLine(line));
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
    [InlineData("Rem\tcomment")]
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
