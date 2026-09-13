using VbaTools.Syntax;
using Xunit;

namespace VbaTools.Syntax.Tests;

public sealed class VbaSourceTextTests
{
    [Theory]
    [InlineData(-1)]
    [InlineData(5)]
    [InlineData(int.MaxValue)]
    public void Exact_positions_reject_offsets_outside_the_snapshot(int offset)
    {
        var source = VbaSourceText.From("A\r\nB");

        Assert.False(source.TryGetPosition(offset, out var position));
        Assert.Null(position);
    }

    [Fact]
    public void Exact_positions_reject_crlf_interiors_without_changing_general_projection()
    {
        var source = VbaSourceText.From("A\r\nB");

        Assert.False(source.TryGetPosition(2, out var position));
        Assert.Null(position);
        Assert.Equal(new VbaSyntaxPosition(0, 1, 2), source.PositionAt(2));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(0, 4)]
    [InlineData(1, 2)]
    [InlineData(2, 1)]
    [InlineData(4, 1)]
    [InlineData(5, 0)]
    [InlineData(int.MaxValue, 0)]
    [InlineData(1, int.MaxValue)]
    public void Exact_offsets_reject_positions_outside_their_physical_line(int line, int character)
    {
        var source = VbaSourceText.From("A😀\r\nβ\n\rZ\r");

        Assert.False(source.TryGetOffset(line, character, out var offset));
        Assert.Equal(0, offset);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 1, 1)]
    [InlineData(0, 2, 2)]
    [InlineData(0, 3, 3)]
    [InlineData(1, 0, 5)]
    [InlineData(1, 1, 6)]
    [InlineData(2, 0, 7)]
    [InlineData(3, 0, 8)]
    [InlineData(3, 1, 9)]
    [InlineData(4, 0, 10)]
    public void Exact_offsets_use_utf16_coordinates_across_mixed_newlines(
        int line,
        int character,
        int expectedOffset)
    {
        var source = VbaSourceText.From("A😀\r\nβ\n\rZ\r");

        Assert.True(source.TryGetOffset(line, character, out var offset));
        Assert.Equal(expectedOffset, offset);
        Assert.True(source.TryGetPosition(expectedOffset, out var position));
        Assert.Equal(new VbaSyntaxPosition(line, character, expectedOffset), position);
    }

    [Theory]
    [InlineData("", 0, 0)]
    [InlineData("日本😀", 0, 4)]
    [InlineData("日本😀\n", 1, 0)]
    [InlineData("日本😀\r", 1, 0)]
    [InlineData("日本😀\r\n", 1, 0)]
    public void Exact_coordinates_include_eof_with_or_without_a_trailing_newline(
        string text, int line, int character)
    {
        var source = VbaSourceText.From(text);

        Assert.True(source.TryGetOffset(line, character, out var offset));
        Assert.Equal(text.Length, offset);
        Assert.True(source.TryGetPosition(text.Length, out var position));
        Assert.Equal(new VbaSyntaxPosition(line, character, text.Length), position);
    }

    [Fact]
    public void Incremental_source_coordinates_reuse_unchanged_lines_for_a_same_width_line_edit()
    {
        var previous = VbaSourceText.From("First\nValue = 1\nLast");

        var updated = VbaSourceText.Update(
            "First\nValue = 2\nLast",
            previous);

        Assert.Equal("First\nValue = 2\nLast", updated.Text);
        Assert.Same(previous.Lines[0], updated.Lines[0]);
        Assert.NotSame(previous.Lines[1], updated.Lines[1]);
        Assert.Same(previous.Lines[2], updated.Lines[2]);
        Assert.Equal(previous.FullRange, updated.FullRange);
    }

    [Fact]
    public void Incremental_source_coordinates_fall_back_cleanly_when_a_newline_changes()
    {
        var previous = VbaSourceText.From("First\r\nSecond");

        var updated = VbaSourceText.Update(
            "First\n\rSecond",
            previous);

        Assert.Equal(["First", "", "Second"], updated.Lines.Select(line => line.Text));
        Assert.NotSame(previous.Lines[0], updated.Lines[0]);
        Assert.Equal(
            VbaSourceText.From("First\n\rSecond").FullRange,
            updated.FullRange);
    }

    [Fact]
    public void SourceTextCachesBlankPhysicalLineClassification()
    {
        var sourceText = VbaSourceText.From("Code\n \t \n' comment\n");

        Assert.False(sourceText.IsBlankLine(0));
        Assert.True(sourceText.IsBlankLine(1));
        Assert.False(sourceText.IsBlankLine(2));
        Assert.True(sourceText.IsBlankLine(3));
    }

    [Fact]
    public void SourceTextBlankClassificationUsesExactMsVbalWhitespace()
    {
        var sourceText = VbaSourceText.From("\u00a0\n\u0019");

        Assert.False(sourceText.IsBlankLine(0));
        Assert.True(sourceText.IsBlankLine(1));
    }

    [Fact]
    public void Incremental_source_coordinates_refresh_changed_line_blank_classification()
    {
        var previous = VbaSourceText.From("First\nValue\nLast");

        var updated = VbaSourceText.Update(
            "First\n     \nLast",
            previous);

        Assert.False(previous.IsBlankLine(1));
        Assert.True(updated.IsBlankLine(1));
        Assert.False(updated.IsBlankLine(0));
        Assert.False(updated.IsBlankLine(2));
    }

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
