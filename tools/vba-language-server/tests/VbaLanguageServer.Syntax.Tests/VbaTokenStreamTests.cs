using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaTokenStreamTests
{
    [Fact]
    public void TokenStreamClassifiesCompleteSourceForLexicalHighlighting()
    {
        var source = string.Join('\n', [
            "#Const VBA7 = True",
            "Attribute VB_Name = \"Worker\"",
            "Option Explicit",
            "Public Sub Run()",
            "    value = 42 + Len(\"abc\") _",
            "        ' trailing comment",
            "End Sub"
        ]);

        var stream = VbaTokenStream.FromText(source);

        Assert.Contains(stream.Tokens, token => token.Kind == VbaTokenKind.PreprocessorDirective && token.Text == "#Const VBA7 = True");
        Assert.Contains(stream.Tokens, token => token.Kind == VbaTokenKind.Keyword && token.Text == "Public");
        Assert.Contains(stream.Tokens, token => token.Kind == VbaTokenKind.Identifier && token.Text == "Run");
        Assert.Contains(stream.Tokens, token => token.Kind == VbaTokenKind.NumericLiteral && token.Text == "42");
        Assert.Contains(stream.Tokens, token => token.Kind == VbaTokenKind.StringLiteral && token.Text == "\"abc\"");
        Assert.Contains(stream.Tokens, token => token.Kind == VbaTokenKind.Operator && token.Text == "+");
        Assert.Contains(stream.Tokens, token => token.Kind == VbaTokenKind.Punctuation && token.Text == "(");
        Assert.Contains(stream.Tokens, token => token.Kind == VbaTokenKind.Comment && token.Text == "' trailing comment");
        Assert.Contains(stream.Tokens, token => token.Kind == VbaTokenKind.Whitespace && token.Text == "    ");
        Assert.Contains(stream.Tokens, token => token.Kind == VbaTokenKind.NewLine && token.Text == "\n");
        Assert.Contains(stream.Tokens, token => token.Kind == VbaTokenKind.LineContinuation && token.Text == "_");
    }

    [Fact]
    public void TokenStreamPreservesSourceRangesForMalformedSource()
    {
        var source = string.Join('\n', [
            "Public Sub Run()",
            "    value = \"unterminated",
            "End Sub"
        ]);

        var stream = VbaTokenStream.FromText(source);

        var stringToken = Assert.Single(stream.Tokens, token => token.Kind == VbaTokenKind.StringLiteral);
        Assert.Equal("\"unterminated", stringToken.Text);
        Assert.Equal(new VbaSyntaxRange(new VbaSyntaxPosition(1, 12, 29), new VbaSyntaxPosition(1, 25, 42)), stringToken.Range);
        Assert.Contains(stream.Tokens, token => token.Kind == VbaTokenKind.Keyword && token.Text == "End");
    }

    [Fact]
    public void TokenStreamPreservesLineStartsAfterCrLf()
    {
        var source = string.Join("\r\n", [
            "Option Explicit",
            "'* @details",
            "Public Sub Run()",
            "End Sub"
        ]);

        var stream = VbaTokenStream.FromText(source);

        var firstNewLine = stream.Tokens.First(token => token.Kind == VbaTokenKind.NewLine);
        Assert.Equal("\r\n", firstNewLine.Text);
        Assert.Equal(
            new VbaSyntaxRange(new VbaSyntaxPosition(0, 15, 15), new VbaSyntaxPosition(1, 0, 17)),
            firstNewLine.Range);

        var comment = Assert.Single(stream.Tokens, token => token.Kind == VbaTokenKind.Comment);
        Assert.Equal("'* @details", comment.Text);
        Assert.Equal(
            new VbaSyntaxRange(new VbaSyntaxPosition(1, 0, 17), new VbaSyntaxPosition(1, 11, 28)),
            comment.Range);

        var publicKeyword = Assert.Single(
            stream.Tokens,
            token => token.Kind == VbaTokenKind.Keyword && token.Text == "Public");
        Assert.Equal(new VbaSyntaxPosition(2, 0, 30), publicKeyword.Range.Start);
    }
}
