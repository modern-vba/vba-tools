using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace VbaTools.Syntax;

/// <summary>Replaces one half-open UTF-16 offset range in an immutable before-text.</summary>
/// <param name="StartOffset">The inclusive offset in the before-text.</param>
/// <param name="EndOffset">The exclusive offset in the before-text.</param>
/// <param name="NewText">The exact replacement text, including any newline characters.</param>
public sealed record VbaSourceTextEdit(int StartOffset, int EndOffset, string NewText);

/// <summary>Relates exact before/after boundaries without mapping arbitrary interior positions.</summary>
public sealed record VbaSourceTextCorrespondence(
    int BeforeStartOffset,
    int BeforeEndOffset,
    int AfterStartOffset,
    int AfterEndOffset)
{
    /// <summary>Gets the change in length of this corresponding span.</summary>
    public int LengthDelta =>
        (AfterEndOffset - AfterStartOffset) - (BeforeEndOffset - BeforeStartOffset);
}

/// <summary>Contains one applied edit collection and its exact source correspondence.</summary>
public sealed class VbaSourceTextEditResult
{
    private VbaSourceTextEditResult(
        VbaSourceText before,
        VbaSourceText after,
        IEnumerable<VbaSourceTextCorrespondence> replacements,
        IEnumerable<VbaSourceTextCorrespondence> unchangedSpans)
    {
        Before = before;
        After = after;
        Replacements = Array.AsReadOnly(replacements.ToArray());
        UnchangedSpans = Array.AsReadOnly(unchangedSpans.ToArray());
    }

    /// <summary>Gets the immutable input snapshot.</summary>
    public VbaSourceText Before { get; }

    /// <summary>Gets the exact snapshot after applying all edits.</summary>
    public VbaSourceText After { get; }

    /// <summary>Gets the exact before/after span for each edit, in source order.</summary>
    public IReadOnlyList<VbaSourceTextCorrespondence> Replacements { get; }

    /// <summary>Gets nonempty unchanged spans in source order.</summary>
    public IReadOnlyList<VbaSourceTextCorrespondence> UnchangedSpans { get; }

    /// <summary>Applies edits against one before-text without choosing or normalizing newlines.</summary>
    /// <remarks>
    /// All ranges refer to the before-text. Offset endpoints may split a newline sequence;
    /// they do not require an exact line-and-character representation.
    /// Adjacent replacements and insertions at a preceding replacement's end are allowed.
    /// Overlapping ranges, multiple insertions at one offset, and any edits sharing a start
    /// offset are rejected independently of input order. The final UTF-16 length is checked
    /// against <see cref="int.MaxValue"/> before allocating the result text.
    /// </remarks>
    /// <param name="before">The immutable snapshot to edit.</param>
    /// <param name="edits">The edit collection, which is copied before validation and application.</param>
    /// <param name="result">The complete applied result on success; otherwise, null.</param>
    /// <returns>
    /// True on success; false for out-of-range, reversed, or conflicting edits, or a final
    /// UTF-16 length that cannot be represented by a nonnegative <see cref="int"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">The before-text or edit collection is null.</exception>
    /// <exception cref="ArgumentException">An edit entry or its replacement text is null.</exception>
    public static bool TryApply(
        VbaSourceText before,
        IEnumerable<VbaSourceTextEdit> edits,
        [NotNullWhen(true)] out VbaSourceTextEditResult? result)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(edits);
        result = null;
        var ordered = edits.ToArray();
        if (ordered.Any(edit => edit is null || edit.NewText is null))
        {
            throw new ArgumentException("Edit entries and replacement text must not be null.", nameof(edits));
        }

        Array.Sort(ordered, static (left, right) => left.StartOffset.CompareTo(right.StartOffset));
        long resultLength = before.Text.Length;
        VbaSourceTextEdit? previous = null;
        foreach (var edit in ordered)
        {
            if (edit.StartOffset < 0 || edit.EndOffset < edit.StartOffset
                || edit.EndOffset > before.Text.Length
                || (previous is not null
                    && (edit.StartOffset < previous.EndOffset || edit.StartOffset == previous.StartOffset)))
            {
                return false;
            }

            resultLength += edit.NewText.Length - (long)(edit.EndOffset - edit.StartOffset);
            previous = edit;
        }

        if (resultLength is < 0 or > int.MaxValue)
        {
            return false;
        }

        var builder = new StringBuilder((int)resultLength);
        var replacements = new List<VbaSourceTextCorrespondence>(ordered.Length);
        var unchangedSpans = new List<VbaSourceTextCorrespondence>();
        var beforeOffset = 0;
        foreach (var edit in ordered)
        {
            if (beforeOffset < edit.StartOffset)
            {
                var unchangedStart = builder.Length;
                builder.Append(before.Text, beforeOffset, edit.StartOffset - beforeOffset);
                unchangedSpans.Add(new VbaSourceTextCorrespondence(
                    beforeOffset, edit.StartOffset, unchangedStart, builder.Length));
            }

            var replacementStart = builder.Length;
            builder.Append(edit.NewText);
            replacements.Add(new VbaSourceTextCorrespondence(
                edit.StartOffset, edit.EndOffset, replacementStart, builder.Length));
            beforeOffset = edit.EndOffset;
        }

        if (beforeOffset < before.Text.Length)
        {
            var unchangedStart = builder.Length;
            builder.Append(before.Text, beforeOffset, before.Text.Length - beforeOffset);
            unchangedSpans.Add(new VbaSourceTextCorrespondence(
                beforeOffset, before.Text.Length, unchangedStart, builder.Length));
        }

        result = new VbaSourceTextEditResult(
            before, VbaSourceText.From(builder.ToString()), replacements, unchangedSpans);
        return true;
    }
}
