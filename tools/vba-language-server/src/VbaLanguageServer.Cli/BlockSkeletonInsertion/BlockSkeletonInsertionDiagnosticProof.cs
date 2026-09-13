using VbaLanguageServer.Diagnostics;
using VbaTools.Syntax;
using PublishedSyntaxDiagnostic = VbaLanguageServer.Diagnostics.VbaSyntaxDiagnostic;

namespace VbaLanguageServer.BlockSkeletonInsertion;

/// <summary>
/// Compares caller-selected diagnostic evidence without parsing or deciding insertion eligibility.
/// </summary>
internal static class BlockSkeletonInsertionDiagnosticProof
{
    public static bool IsSafe(BlockSkeletonInsertionDiagnosticProofCase proofCase)
    {
        if (!HasConsistentSourceEvidence(proofCase)
            || !TryCreateErrorMultiset(proofCase.Original, out var expected)
            || !TryCreateErrorMultiset(proofCase.AllowedRemovals, out var allowed))
        {
            return false;
        }

        if (proofCase.Control is { } controlEvidence)
        {
            if (!TryCreateErrorMultiset(controlEvidence, out var control)
                || !TrySubtract(expected, control)
                || !expected.All(pair => allowed.TryGetValue(pair.Key, out var count)
                    && pair.Value <= count))
            {
                return false;
            }

            expected = control;
        }
        else if (!TrySubtract(expected, allowed))
        {
            return false;
        }

        return TryCreateErrorMultiset(
                proofCase.Prospective,
                out var prospective,
                proofCase.Edit)
            && MultisetsEqual(expected, prospective);
    }

    private static bool HasConsistentSourceEvidence(BlockSkeletonInsertionDiagnosticProofCase proofCase)
    {
        var original = proofCase.Original.Source;
        var edit = proofCase.Edit;
        if (edit.Replacements.Count > 1
            || !original.Text.Equals(edit.Before.Text, StringComparison.Ordinal)
            || !proofCase.Prospective.Source.Text.Equals(edit.After.Text, StringComparison.Ordinal)
            || !original.Text.Equals(proofCase.AllowedRemovals.Source.Text, StringComparison.Ordinal))
        {
            return false;
        }

        if (proofCase.Control is not { } control)
        {
            return true;
        }

        // Neutralizing a header changes text, but not its physical source coordinates.
        if (control.Source.Text.Length != original.Text.Length
            || control.Source.Lines.Count != original.Lines.Count)
        {
            return false;
        }

        for (var index = 0; index < original.Lines.Count; index++)
        {
            if (control.Source.Lines[index].StartOffset != original.Lines[index].StartOffset
                || control.Source.Lines[index].EndOffset != original.Lines[index].EndOffset)
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryCreateErrorMultiset(
        BlockSkeletonInsertionDiagnosticEvidence evidence,
        out Dictionary<DiagnosticFingerprint, int> result,
        VbaSourceTextEditResult? edit = null)
    {
        result = new();
        foreach (var diagnostic in evidence.SyntaxDiagnostics
            .Where(diagnostic => IsError(diagnostic.Severity)))
        {
            if (!TryCreateNormalizedFingerprint(
                "syntax",
                diagnostic,
                evidence.Source,
                edit,
                out var fingerprint))
            {
                return false;
            }

            Add(result, fingerprint);
        }

        foreach (var diagnostic in evidence.DocumentValidationDiagnostics
            .Where(diagnostic => IsError(diagnostic.Severity)))
        {
            if (!TryCreateNormalizedFingerprint(
                "validation",
                diagnostic,
                evidence.Source,
                edit,
                out var fingerprint))
            {
                return false;
            }

            Add(result, fingerprint);
        }

        return true;
    }

    private static bool TrySubtract(
        Dictionary<DiagnosticFingerprint, int> original,
        IReadOnlyDictionary<DiagnosticFingerprint, int> removed)
    {
        foreach (var pair in removed)
        {
            if (!original.TryGetValue(pair.Key, out var count) || count < pair.Value)
            {
                return false;
            }

            if (count == pair.Value)
            {
                original.Remove(pair.Key);
            }
            else
            {
                original[pair.Key] = count - pair.Value;
            }
        }

        return true;
    }

    private static bool MultisetsEqual(
        IReadOnlyDictionary<DiagnosticFingerprint, int> left,
        IReadOnlyDictionary<DiagnosticFingerprint, int> right)
        => left.Count == right.Count
            && left.All(pair => right.TryGetValue(pair.Key, out var count)
                && count == pair.Value);

    private static bool TryCreateNormalizedFingerprint(
        string category,
        PublishedSyntaxDiagnostic diagnostic,
        VbaSourceText source,
        VbaSourceTextEditResult? edit,
        out DiagnosticFingerprint fingerprint)
        => TryCreateNormalizedFingerprint(
            category,
            diagnostic.Source,
            diagnostic.Severity,
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.Range,
            source,
            edit,
            out fingerprint);

    private static bool TryCreateNormalizedFingerprint(
        string category,
        VbaValidationDiagnostic diagnostic,
        VbaSourceText source,
        VbaSourceTextEditResult? edit,
        out DiagnosticFingerprint fingerprint)
        => TryCreateNormalizedFingerprint(
            category,
            diagnostic.Source,
            diagnostic.Severity,
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.Range,
            source,
            edit,
            out fingerprint);

    private static bool TryCreateNormalizedFingerprint(
        string category,
        string sourceName,
        string severity,
        string code,
        string message,
        VbaRange range,
        VbaSourceText source,
        VbaSourceTextEditResult? edit,
        out DiagnosticFingerprint fingerprint)
    {
        fingerprint = default!;
        if (!source.TryGetOffset(range.Start.Line, range.Start.Character, out var startOffset)
            || !source.TryGetOffset(range.End.Line, range.End.Character, out var endOffset)
            || endOffset < startOffset)
        {
            return false;
        }

        var originalStart = startOffset;
        var originalEnd = endOffset;
        if (edit is not null
            && !TryMapDiagnosticRangeToOriginal(
                startOffset, endOffset, edit, out originalStart, out originalEnd))
        {
            return false;
        }

        fingerprint = new DiagnosticFingerprint(
            category,
            sourceName,
            severity,
            code,
            message,
            originalStart,
            originalEnd);
        return true;
    }

    private static bool TryMapDiagnosticRangeToOriginal(
        int prospectiveStartOffset,
        int prospectiveEndOffset,
        VbaSourceTextEditResult edit,
        out int originalStartOffset,
        out int originalEndOffset)
    {
        originalStartOffset = 0;
        originalEndOffset = 0;
        if (edit.Replacements.Count == 0)
        {
            originalStartOffset = prospectiveStartOffset;
            originalEndOffset = prospectiveEndOffset;
            return true;
        }

        var replacement = edit.Replacements[0];
        // Zero-width adjacency is allowed, including at EOF with no nonempty unchanged span.
        // If deletion collapses both endpoints, the preceding side retains precedence.
        if (prospectiveStartOffset == prospectiveEndOffset)
        {
            if (prospectiveStartOffset == replacement.AfterStartOffset)
            {
                originalStartOffset = originalEndOffset = replacement.BeforeStartOffset;
                return true;
            }

            if (prospectiveStartOffset == replacement.AfterEndOffset)
            {
                originalStartOffset = originalEndOffset = replacement.BeforeEndOffset;
                return true;
            }
        }

        // Diagnostics must be wholly unchanged; enclosing a replacement is not correspondence.
        foreach (var span in edit.UnchangedSpans)
        {
            if (span.AfterStartOffset <= prospectiveStartOffset
                && prospectiveEndOffset <= span.AfterEndOffset)
            {
                originalStartOffset = span.BeforeStartOffset + (prospectiveStartOffset - span.AfterStartOffset);
                originalEndOffset = span.BeforeStartOffset + (prospectiveEndOffset - span.AfterStartOffset);
                return true;
            }
        }

        return false;
    }

    private static bool IsError(string severity)
        => severity.Equals("error", StringComparison.OrdinalIgnoreCase);

    private static void Add(
        IDictionary<DiagnosticFingerprint, int> counts,
        DiagnosticFingerprint fingerprint)
        => counts[fingerprint] = counts.TryGetValue(fingerprint, out var count)
            ? count + 1
            : 1;

    private sealed record DiagnosticFingerprint(
        string Category,
        string Source,
        string Severity,
        string Code,
        string Message,
        int StartOffset,
        int EndOffset);
}
