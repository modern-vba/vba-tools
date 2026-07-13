using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaLexicalFactsTests
{
    [Fact]
    public void TryGetCodeIdentifierAtSkipsStringsAndApostropheComments()
    {
        var source = string.Join('\n', [
            "Public Sub Run()",
            "    value = \"Run\" ' commentValue",
            "End Sub"
        ]);
        var facts = VbaLexicalFacts.FromText(source);

        var findsCodeIdentifier = facts.TryGetCodeIdentifierAt(1, "    val".Length, out var codeIdentifier);
        var skipsStringIdentifier = facts.TryGetCodeIdentifierAt(1, "    value = \"R".Length, out _);
        var skipsCommentIdentifier = facts.TryGetCodeIdentifierAt(1, "    value = \"Run\" ' comment".Length, out _);

        Assert.True(findsCodeIdentifier);
        Assert.Equal("value", codeIdentifier.Name);
        Assert.False(skipsStringIdentifier);
        Assert.False(skipsCommentIdentifier);
    }

    [Fact]
    public void TryGetLogicalPrefixUsesContinuedPhysicalLines()
    {
        var source = string.Join('\n', [
            "Public Sub Run()",
            "    Application _",
            "        .Workbooks _",
            "        .Open(",
            "End Sub"
        ]);
        var facts = VbaLexicalFacts.FromText(source);

        var found = facts.TryGetLogicalPrefix(3, "        .Open(".Length, out var logicalPrefix);

        Assert.True(found);
        Assert.Equal("    Application          .Workbooks          .Open(", logicalPrefix);
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
