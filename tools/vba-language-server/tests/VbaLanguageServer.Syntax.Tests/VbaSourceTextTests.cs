using VbaLanguageServer.Syntax;
using Xunit;

namespace VbaLanguageServer.Syntax.Tests;

public sealed class VbaSourceTextTests
{
    [Fact]
    public void SourceTextIndexesMixedNewlinesWithUtf16HalfOpenRanges()
    {
        const string source = "A😀\r\nβ\n\rZ\r";

        var sourceText = VbaSourceText.From(source);

        Assert.Collection(
            sourceText.Lines,
            line => Assert.Equal(new VbaSourceLine(0, "A😀", 0, 3), line),
            line => Assert.Equal(new VbaSourceLine(1, "β", 5, 6), line),
            line => Assert.Equal(new VbaSourceLine(2, "", 7, 7), line),
            line => Assert.Equal(new VbaSourceLine(3, "Z", 8, 9), line),
            line => Assert.Equal(new VbaSourceLine(4, "", 10, 10), line));
        Assert.Equal(new VbaSyntaxPosition(0, 3, 3), sourceText.PositionAt(3));
        Assert.Equal(new VbaSyntaxPosition(0, 3, 4), sourceText.PositionAt(4));
        Assert.Equal(new VbaSyntaxPosition(1, 0, 5), sourceText.PositionAt(5));
        Assert.Equal(new VbaSyntaxPosition(4, 0, 10), sourceText.FullRange.End);
        Assert.Equal(
            new VbaSyntaxRange(new VbaSyntaxPosition(1, 0, 5), new VbaSyntaxPosition(1, 1, 6)),
            sourceText.RangeForLine(sourceText.Lines[1], 0, 1));
        Assert.Equal(["A😀", "β", "", "Z", ""], VbaSourceText.SplitLines(source));
        Assert.Equal(["A😀", "β", "", "Z", ""], VbaSourceText.SplitLogicalLines(source));
    }

    [Fact]
    public void SourceTextRejectsOffsetsOutsideTheIndexedSnapshot()
    {
        var sourceText = VbaSourceText.From("value");

        Assert.Throws<ArgumentOutOfRangeException>(() => sourceText.PositionAt(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => sourceText.PositionAt(6));
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    [InlineData("A\n")]
    [InlineData("A\r")]
    [InlineData("A\r\n")]
    [InlineData("A😀\r\nβ\n\rZ\r")]
    public void TokenRangesAreContiguousSourceSlices(string source)
    {
        var sourceText = VbaSourceText.From(source);
        var tokens = VbaTokenStream.FromText(source).Tokens;
        var nextOffset = 0;

        foreach (var token in tokens)
        {
            Assert.Equal(nextOffset, token.Range.Start.Offset);
            Assert.Equal(token.Range.Start.Offset + token.Text.Length, token.Range.End.Offset);
            Assert.Equal(token.Text, source[token.Range.Start.Offset..token.Range.End.Offset]);
            Assert.Equal(sourceText.PositionAt(token.Range.Start.Offset), token.Range.Start);
            Assert.Equal(sourceText.PositionAt(token.Range.End.Offset), token.Range.End);
            nextOffset = token.Range.End.Offset;
        }

        Assert.Equal(source.Length, nextOffset);
        Assert.Equal(source.Length, sourceText.FullRange.End.Offset);
    }
}
