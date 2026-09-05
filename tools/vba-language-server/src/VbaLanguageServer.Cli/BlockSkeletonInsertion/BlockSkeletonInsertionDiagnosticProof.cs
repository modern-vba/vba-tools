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
                proofCase.Replacement)
            && MultisetsEqual(expected, prospective);
    }

    private static bool HasConsistentSourceEvidence(BlockSkeletonInsertionDiagnosticProofCase proofCase)
    {
        var original = proofCase.Original.Source;
        var prospective = proofCase.Prospective.Source;
        var replacement = proofCase.Replacement;
        if (replacement.StartOffset < 0
            || replacement.EndOffset < replacement.StartOffset
            || replacement.EndOffset > original.Text.Length
            || replacement.ProspectiveEndOffset < replacement.StartOffset
            || replacement.ProspectiveEndOffset > prospective.Text.Length
            || original.Text.Length + (long)replacement.ProspectiveEndOffset - replacement.EndOffset
                != prospective.Text.Length
            || !original.Text.Equals(proofCase.AllowedRemovals.Source.Text, StringComparison.Ordinal)
            || !original.Text.AsSpan(0, replacement.StartOffset)
                .SequenceEqual(prospective.Text.AsSpan(0, replacement.StartOffset))
            || !original.Text.AsSpan(replacement.EndOffset)
                .SequenceEqual(prospective.Text.AsSpan(replacement.ProspectiveEndOffset)))
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
        BlockSkeletonInsertionDiagnosticReplacement? replacement = null)
    {
        result = new();
        replacement ??= new(0, 0, 0);
        var delta = (long)replacement.ProspectiveEndOffset - replacement.EndOffset;
        foreach (var diagnostic in evidence.SyntaxDiagnostics
            .Where(diagnostic => IsError(diagnostic.Severity)))
        {
            if (!TryCreateNormalizedFingerprint(
                "syntax",
                diagnostic,
                evidence.Source,
                replacement.StartOffset,
                replacement.EndOffset,
                replacement.ProspectiveEndOffset,
                delta,
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
                replacement.StartOffset,
                replacement.EndOffset,
                replacement.ProspectiveEndOffset,
                delta,
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
        int insertionStartOffset,
        int insertionEndOffset,
        int replacementEndOffset,
        long delta,
        out DiagnosticFingerprint fingerprint)
        => TryCreateNormalizedFingerprint(
            category,
            diagnostic.Source,
            diagnostic.Severity,
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.Range,
            source,
            insertionStartOffset,
            insertionEndOffset,
            replacementEndOffset,
            delta,
            out fingerprint);

    private static bool TryCreateNormalizedFingerprint(
        string category,
        VbaValidationDiagnostic diagnostic,
        VbaSourceText source,
        int insertionStartOffset,
        int insertionEndOffset,
        int replacementEndOffset,
        long delta,
        out DiagnosticFingerprint fingerprint)
        => TryCreateNormalizedFingerprint(
            category,
            diagnostic.Source,
            diagnostic.Severity,
            diagnostic.Code,
            diagnostic.Message,
            diagnostic.Range,
            source,
            insertionStartOffset,
            insertionEndOffset,
            replacementEndOffset,
            delta,
            out fingerprint);

    private static bool TryCreateNormalizedFingerprint(
        string category,
        string sourceName,
        string severity,
        string code,
        string message,
        VbaRange range,
        VbaSourceText source,
        int insertionStartOffset,
        int insertionEndOffset,
        int replacementEndOffset,
        long delta,
        out DiagnosticFingerprint fingerprint)
    {
        fingerprint = default!;
        if (!TryToOffset(source, range.Start, out var startOffset)
            || !TryToOffset(source, range.End, out var endOffset)
            || !TryMapRangeToOriginal(
                startOffset,
                endOffset,
                insertionStartOffset,
                insertionEndOffset,
                replacementEndOffset,
                delta,
                out var originalStart,
                out var originalEnd))
        {
            return false;
        }

        if (originalEnd > source.Text.Length - delta)
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

    private static bool TryMapRangeToOriginal(
        int prospectiveStartOffset,
        int prospectiveEndOffset,
        int insertionStartOffset,
        int insertionEndOffset,
        int replacementEndOffset,
        long delta,
        out int originalStartOffset,
        out int originalEndOffset)
    {
        if (prospectiveEndOffset < prospectiveStartOffset)
        {
            originalStartOffset = 0;
            originalEndOffset = 0;
            return false;
        }

        if (prospectiveEndOffset <= insertionStartOffset)
        {
            originalStartOffset = prospectiveStartOffset;
            originalEndOffset = prospectiveEndOffset;
            return true;
        }

        if (prospectiveStartOffset >= replacementEndOffset)
        {
            var mappedStart = prospectiveStartOffset - delta;
            var mappedEnd = prospectiveEndOffset - delta;
            if (mappedStart >= insertionEndOffset && mappedEnd <= int.MaxValue)
            {
                originalStartOffset = (int)mappedStart;
                originalEndOffset = (int)mappedEnd;
                return true;
            }
        }

        originalStartOffset = 0;
        originalEndOffset = 0;
        return false;
    }

    private static bool IsError(string severity)
        => severity.Equals("error", StringComparison.OrdinalIgnoreCase);

    private static bool TryToOffset(VbaSourceText source, VbaPosition position, out int offset)
    {
        offset = 0;
        if (position.Line < 0 || position.Line >= source.Lines.Count)
        {
            return false;
        }

        var line = source.Lines[position.Line];
        if (position.Character < 0 || position.Character > line.Text.Length)
        {
            return false;
        }

        offset = line.StartOffset + position.Character;
        return true;
    }

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
