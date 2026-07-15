using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaLexicalFactsTests
{
    [Fact]
    public void SplitCodeAndCommentPreservesApostrophesInsideStrings()
    {
        const string line = "value = \"that's ok\" ' comment";

        var parts = VbaLexicalFacts.SplitCodeAndComment(line);

        Assert.Equal("value = \"that's ok\" ", parts.CodePart);
        Assert.Equal("' comment", parts.CommentPart);
    }

}
