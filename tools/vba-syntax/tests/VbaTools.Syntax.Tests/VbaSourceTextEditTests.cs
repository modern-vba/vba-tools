using VbaTools.Syntax;
using Xunit;

namespace VbaTools.Syntax.Tests;

public sealed class VbaSourceTextEditTests
{
    [Fact]
    public void Null_snapshots_and_edit_collections_are_argument_errors()
    {
        var nullBefore = Assert.Throws<ArgumentNullException>(() =>
            VbaSourceTextEditResult.TryApply(null!, [], out _));
        var nullEdits = Assert.Throws<ArgumentNullException>(() =>
            VbaSourceTextEditResult.TryApply(VbaSourceText.From("abc"), null!, out _));

        Assert.Equal("before", nullBefore.ParamName);
        Assert.Equal("edits", nullEdits.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Null_edit_entries_or_replacement_text_are_argument_errors(bool nullReplacement)
    {
        var before = VbaSourceText.From("abc");
        VbaSourceTextEdit[] edits = [new(0, 1, "A"), nullReplacement ? new(1, 2, null!) : null!];

        var exception = Assert.Throws<ArgumentException>(() =>
            VbaSourceTextEditResult.TryApply(before, edits, out _));

        Assert.Equal("edits", exception.ParamName);
        Assert.Equal("abc", before.Text);
    }

    [Fact]
    public void Adjacent_replacements_and_unambiguous_boundary_insertions_preserve_order()
    {
        var before = VbaSourceText.From("abcdef");
        VbaSourceTextEdit[] edits = [new(3, 3, "!"), new(1, 3, "BC"), new(0, 1, "A")];

        Assert.True(VbaSourceTextEditResult.TryApply(before, edits, out var result));

        Assert.Equal("ABC!def", result.After.Text);
        Assert.Equal(
            [new(0, 1, 0, 1), new(1, 3, 1, 3), new VbaSourceTextCorrespondence(3, 3, 3, 4)],
            result.Replacements);
        Assert.Equal([new VbaSourceTextCorrespondence(3, 6, 4, 7)], result.UnchangedSpans);
    }

    [Theory]
    [InlineData("")]
    [InlineData("日本😀")]
    [InlineData("A\r\nB\rC\n")]
    public void An_empty_edit_collection_preserves_the_exact_snapshot(string text)
    {
        var before = VbaSourceText.From(text);

        Assert.True(VbaSourceTextEditResult.TryApply(before, [], out var result));

        Assert.Equal(text, result.After.Text);
        Assert.Empty(result.Replacements);
        Assert.Equal(
            text.Length == 0 ? [] : new[] { new VbaSourceTextCorrespondence(0, text.Length, 0, text.Length) },
            result.UnchangedSpans);
    }

    [Theory]
    [InlineData("", 0, 0, "日本😀\r", "日本😀\r")]
    [InlineData("A\r\n", 3, 3, "Z", "A\r\nZ")]
    [InlineData("A", 0, 1, "", "")]
    [InlineData("A\r\nB", 1, 2, "", "A\nB")]
    public void Offset_edits_preserve_requested_text_and_exact_boundary_facts(
        string text, int start, int end, string replacement, string expected)
    {
        var before = VbaSourceText.From(text);

        Assert.True(VbaSourceTextEditResult.TryApply(before, [new(start, end, replacement)], out var result));

        Assert.Equal(expected, result.After.Text);
        Assert.Equal(new VbaSourceTextCorrespondence(start, end, start, start + replacement.Length),
            Assert.Single(result.Replacements));
    }

    [Fact]
    public void Applied_correspondence_is_immutable_and_does_not_borrow_the_edit_collection()
    {
        var before = VbaSourceText.From("abc");
        var edits = new List<VbaSourceTextEdit> { new(1, 2, "XY") };

        Assert.True(VbaSourceTextEditResult.TryApply(before, edits, out var result));
        edits[0] = new VbaSourceTextEdit(0, 3, "changed");
        edits.Clear();

        Assert.Equal("aXYc", result.After.Text);
        Assert.Equal(new VbaSourceTextCorrespondence(1, 2, 1, 3), Assert.Single(result.Replacements));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<VbaSourceTextCorrespondence>)result.Replacements)[0] = new(0, 0, 0, 0));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<VbaSourceTextCorrespondence>)result.UnchangedSpans).Clear());
    }

    [Fact]
    public void Unrepresentable_result_length_rejects_before_allocating_the_result()
    {
        var before = VbaSourceText.From(new string('x', 2048));
        var replacement = new string('y', 1024 * 1024);
        var edits = Enumerable.Range(0, 2048)
            .Select(offset => new VbaSourceTextEdit(offset, offset + 1, replacement));

        Assert.False(VbaSourceTextEditResult.TryApply(before, edits, out var result));
        Assert.Null(result);
        Assert.Equal(2048, before.Text.Length);
    }

    [Theory]
    [InlineData(0, 3, 2, 4)]
    [InlineData(0, 4, 1, 2)]
    [InlineData(1, 3, 1, 3)]
    [InlineData(2, 2, 2, 2)]
    [InlineData(1, 3, 1, 1)]
    [InlineData(0, 4, 2, 2)]
    public void Conflicts_reject_the_collection_independently_of_input_order(
        int firstStart, int firstEnd, int secondStart, int secondEnd)
    {
        var before = VbaSourceText.From("abcdef");
        VbaSourceTextEdit[] edits = [new(firstStart, firstEnd, "X"), new(secondStart, secondEnd, "Y")];

        Assert.False(VbaSourceTextEditResult.TryApply(before, edits, out var result));
        Assert.Null(result);
        Assert.False(VbaSourceTextEditResult.TryApply(before, edits.Reverse(), out result));
        Assert.Null(result);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    [InlineData(3, 2)]
    [InlineData(0, 7)]
    [InlineData(7, 7)]
    [InlineData(1, int.MaxValue)]
    public void Invalid_ranges_reject_the_complete_collection(int start, int end)
    {
        var before = VbaSourceText.From("abcdef");
        VbaSourceTextEdit[] edits = [new(0, 1, "A"), new(start, end, "X")];

        Assert.False(VbaSourceTextEditResult.TryApply(before, edits, out var result));
        Assert.Null(result);
        Assert.Equal("abcdef", before.Text);
    }

    [Fact]
    public void Edits_apply_to_one_before_text_and_report_exact_utf16_correspondence()
    {
        var before = VbaSourceText.From("A😀\r\nitem\r日本\nlast");
        VbaSourceTextEdit[] edits = [new(13, 17, "tail!"), new(5, 9, "count")];

        Assert.True(VbaSourceTextEditResult.TryApply(before, edits, out var result));

        Assert.Same(before, result.Before);
        Assert.Equal("A😀\r\ncount\r日本\ntail!", result.After.Text);
        Assert.Equal("A😀\r\nitem\r日本\nlast", before.Text);
        Assert.Equal(
            [new(5, 9, 5, 10), new VbaSourceTextCorrespondence(13, 17, 14, 19)],
            result.Replacements);
        Assert.Equal([1, 1], result.Replacements.Select(span => span.LengthDelta));
        Assert.Equal(
            [new(0, 5, 0, 5), new VbaSourceTextCorrespondence(9, 13, 10, 14)],
            result.UnchangedSpans);
        foreach (var span in result.UnchangedSpans)
        {
            Assert.Equal(
                before.Text[span.BeforeStartOffset..span.BeforeEndOffset],
                result.After.Text[span.AfterStartOffset..span.AfterEndOffset]);
            Assert.Equal(0, span.LengthDelta);
        }
    }
}
