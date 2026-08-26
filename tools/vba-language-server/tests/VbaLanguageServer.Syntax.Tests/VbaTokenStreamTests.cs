using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaTokenStreamTests
{
    [Fact]
    public void TokenStreamClassifiesJapaneseProcedureNameAsOneIdentifier()
    {
        var stream = VbaTokenStream.FromText("Public Sub 集計()\nEnd Sub");

        Assert.Contains(
            stream.Tokens,
            token => token.Kind == VbaTokenKind.Identifier && token.Text == "集計");
    }

    [Fact]
    public void TokenStreamTreatsCp2NoBreakSpaceAsIdentifierCharacterNotWhitespace()
    {
        var stream = VbaTokenStream.FromText("\u00a0value = 1");

        Assert.Contains(
            stream.Tokens,
            token => token.Kind == VbaTokenKind.Identifier && token.Text == "\u00a0value");
    }

    [Fact]
    public void TokenStreamDoesNotSplitOneMixedFormCandidateIntoAdjacentIdentifiers()
    {
        var stream = VbaTokenStream.FromText("亜ㄱ");

        var token = Assert.Single(stream.Tokens);
        Assert.Equal(VbaTokenKind.Punctuation, token.Kind);
        Assert.Equal("亜ㄱ", token.Text);
    }

    [Fact]
    public void TokenStreamDoesNotExposeAReservedSuffixInsideAnInvalidWordCandidate()
    {
        var stream = VbaTokenStream.FromText("_If");

        Assert.DoesNotContain(
            stream.Tokens,
            token => token.Kind == VbaTokenKind.Keyword && token.Text == "If");
        Assert.Contains(
            stream.Tokens,
            token => token.Kind == VbaTokenKind.Punctuation && token.Text == "_If");
    }

    [Fact]
    public void TokenStreamKeepsTypedSuffixAndForeignNameSyntaxOutsideTheBaseIdentifier()
    {
        var tokens = VbaTokenStream.FromText("Name$ [Name]").Tokens
            .Where(token => token.Kind != VbaTokenKind.Whitespace)
            .ToArray();

        Assert.Collection(
            tokens,
            token => Assert.Equal((VbaTokenKind.Identifier, "Name"), (token.Kind, token.Text)),
            token => Assert.Equal((VbaTokenKind.Punctuation, "$"), (token.Kind, token.Text)),
            token => Assert.Equal((VbaTokenKind.Punctuation, "["), (token.Kind, token.Text)),
            token => Assert.Equal((VbaTokenKind.Identifier, "Name"), (token.Kind, token.Text)),
            token => Assert.Equal((VbaTokenKind.Punctuation, "]"), (token.Kind, token.Text)));
    }

    [Theory]
    [InlineData("\u0019", true)]
    [InlineData("\u000b", false)]
    public void TokenStreamUsesMsVbalWhitespaceForExplicitLineContinuations(
        string separator,
        bool expected)
    {
        var stream = VbaTokenStream.FromText($"value{separator}_\n nextValue");

        Assert.Equal(
            expected,
            stream.Tokens.Any(token => token.Kind == VbaTokenKind.LineContinuation));
    }

    [Fact]
    public void TokenStreamRequiresTheLineTerminatorImmediatelyAfterTheContinuationMarker()
    {
        var stream = VbaTokenStream.FromText("value _ \nnextValue");

        Assert.DoesNotContain(
            stream.Tokens,
            token => token.Kind == VbaTokenKind.LineContinuation);
    }

    [Fact]
    public void TokenStreamUsesMsVbalWhitespaceForDirectiveLineContinuations()
    {
        var stream = VbaTokenStream.FromText("#If True Then\u3000_\nDebug.Print\n#End If");

        Assert.Contains(
            stream.Tokens,
            token => token.Kind == VbaTokenKind.PreprocessorDirective
                && token.Text == "#If True Then\u3000_\nDebug.Print");
    }

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
